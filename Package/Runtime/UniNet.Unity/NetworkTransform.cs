using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Movement prediction component (ADR-0013 · ADR-0038) — server-authoritative replication of the transform
    /// position with full client-side prediction &amp; reconciliation on the owning client and interpolation on remotes.
    /// Runs as an independent slot (SubId) alongside other NetworkBehaviours on the same object (ADR-0010).
    /// The game injects a <see cref="INetworkMoveSimulation"/> (typically <see cref="MoveSimulation{TInput,TState}"/> with
    /// its own input/state structs and a pure step rule) and feeds input every frame via SetInput. The same rule runs
    /// on the server (authority) and the owner (prediction) at a fixed step, so stateful movement — jumps (gravity),
    /// dashes/rolls (timed windows, cooldowns), teleports (state reset) — reconciles exactly under latency and loss.
    /// When Simulation is null the component degrades to position replication only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class NetworkTransform : NetworkBehaviour
    {
        /// <summary>The game's movement simulation — inject before spawn (required for prediction; null = position replication only).</summary>
        public INetworkMoveSimulation Simulation;

        /// <summary>Simulation gate — when false, neither the server nor the predictor steps (death, cutscenes). Controlled by your game.</summary>
        public bool SimulationEnabled = true;

        /// <summary>Remote render delay (seconds) — remotes interpolate this far behind server time (smooths over step gaps).</summary>
        public float InterpolationDelay = 0.12f;

        /// <summary>
        /// Reconciliation trigger — predicted-vs-server position divergence (squared distance) beyond this rolls the
        /// simulation back to the server state and re-simulates unacknowledged inputs; smaller values correct harder,
        /// larger values tolerate float noise.
        /// </summary>
        public float RollbackThresholdSq = 0.01f;

        /// <summary>Teleport detection — with no pending inputs, a server position jump (squared distance) beyond this snaps the prediction to it (respawn, teleport).</summary>
        public float SnapThreshold = 0.5f;

        /// <summary>Fixed-step catch-up cap per frame — bounds recovery after a stall (the accumulator is clamped when hit).</summary>
        private const int MaxStepsPerFrame = 8;

        [Replicated(Notify = nameof(OnNetXChanged))] private float _px;
        [Replicated(Notify = nameof(OnNetYChanged))] private float _py;
        [Replicated(Notify = nameof(OnNetZChanged))] private float _pz;
        // declaration order is wire-apply order within one delta (ADR-0037 precedent): position, then the ack state
        // the rollback restores, then the ack sequence whose notify consumes both. A NEW envelope instance per
        // changed state is required — the delta detector compares references, so mutating Data in place never resends.
        // Non-null initializer: full-state writes (spawn/catch-up) serialize message fields unconditionally and
        // MessageSerializer.SerializeToWriter throws on null.
        [Replicated(ReplicateCondition.OwnerOnly)] private UniNetBlobEnvelope _ackState = new UniNetBlobEnvelope { Data = new byte[0] };
        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnAckSeqChanged))] private uint _ackSeq;

        private float _accumulator;      // fixed-step remainder (seconds) since the last executed step
        private float _predX;            // predicted position (owning client)
        private float _predY;
        private float _predZ;
        private float _prevPredX;        // position before the latest predicted step — render interpolation baseline
        private float _prevPredY;
        private float _prevPredZ;
        private uint _clientSeq;         // owner input sequence
        private readonly UniNetBlobEnvelope _env = new UniNetBlobEnvelope();   // reused wire envelope — the payload array it references is swapped per send (encode is synchronous)

        private readonly SnapshotBuffer<float> _bufX = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufY = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufZ = new(32, LerpF);

        private static float LerpF(float a, float b, double t) => a + (b - a) * (float)t;

        private void Awake()
        {
            var p = transform.position;
            _px = p.x;
            _py = p.y;
            _pz = p.z;   // seed the initial replicated state from the spawn transform — placement before spawn becomes the spawn state
            _predX = p.x; _predY = p.y; _predZ = p.z;   // prediction starts aligned with the spawn (owner and server instantiate the same transform)
        }

        /// <summary>
        /// Server-only — sets the networked position; it replicates to all clients on the next tick (the transform
        /// updates immediately too). A teleport/respawn resets the movement simulation state (a jump arc must not
        /// resume at the destination) — the owner re-anchors on receipt.
        /// </summary>
        public void SetNetworkPosition(Vector3 position)
        {
            _px = position.x;
            _py = position.y;
            _pz = position.z;
            transform.position = position;
            Simulation?.OnTeleport();
        }

        private void Update()
        {
            var sim = Simulation;
            if (sim == null)
            {
                if (IsServer || !IsOwner || !IsClient)
                    transform.position = new Vector3(_px, _py, _pz);   // replication-only contract — render the authoritative position
                else if (IsClient)
                    InterpolateRemote();
                return;
            }

            if (!SimulationEnabled)
            {
                _accumulator = 0f;   // frozen — do not accumulate stall debt while disabled
                if (IsOwner && IsClient && !IsServer)
                    transform.position = new Vector3(_px, _py, _pz);   // gated owner (death) renders the authoritative position
                else if (!IsServer && IsClient)
                    InterpolateRemote();
                else
                    transform.position = new Vector3(_px, _py, _pz);
                return;
            }

            float interval = sim.StepIntervalSeconds;
            _accumulator += Time.deltaTime;
            int steps = 0;
            while (_accumulator >= interval && steps < MaxStepsPerFrame)
            {
                _accumulator -= interval;
                steps++;
                if (IsServer)
                {
                    sim.TickServer(ref _px, ref _py, ref _pz);
                    uint ack = sim.LastAdoptedSeq;
                    if (ack != _ackSeq)
                    {
                        _ackSeq = ack;
                        _ackState = new UniNetBlobEnvelope { Data = sim.WriteState() };   // fresh instance — reference change is the delta trigger
                    }
                }
                else if (IsOwner && IsClient)
                {
                    _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;
                    uint seq = ++_clientSeq;
                    byte[] payload = sim.PredictStep(seq, ref _predX, ref _predY, ref _predZ);
                    _env.Data = payload;
                    RpcSubmitMove(seq, _env);
                }
            }
            if (steps == MaxStepsPerFrame)
                _accumulator = Mathf.Min(_accumulator, interval * 0.5f);   // spiral-of-death guard — shed accumulated debt after a stall

            if (IsServer)
                transform.position = new Vector3(_px, _py, _pz);
            else if (IsOwner && IsClient)
            {
                // Teleport re-anchor (ADR-0037 lineage) — no pending inputs + server position jumped: the ack path
                // never fires (no further adoptions while the sender is idle/gated), so adopt the server state here.
                if (sim.PendingCount == 0)
                {
                    float ddx = _predX - _px, ddy = _predY - _py, ddz = _predZ - _pz;
                    if (ddx * ddx + ddy * ddy + ddz * ddz > SnapThreshold * SnapThreshold)
                    {
                        sim.OnTeleport();
                        _predX = _px; _predY = _py; _predZ = _pz;
                        _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;
                    }
                }
                // Render between the last two predicted steps — smooth at any frame rate while the simulation stays fixed-rate
                float frac = interval > 0f ? Mathf.Clamp01(_accumulator / interval) : 1f;
                transform.position = new Vector3(
                    Mathf.Lerp(_prevPredX, _predX, frac),
                    Mathf.Lerp(_prevPredY, _predY, frac),
                    Mathf.Lerp(_prevPredZ, _predZ, frac));
            }
            else if (IsClient)
                InterpolateRemote();
        }

        /// <summary>Remote — render a past moment from the interpolation buffer (delay controls when updates are applied). Keep the current position while the buffer is empty.</summary>
        private void InterpolateRemote()
        {
            double renderTime = UniNetTime.Now - InterpolationDelay;
            if (_bufX.TrySample(renderTime, out var x) && _bufY.TrySample(renderTime, out var y) && _bufZ.TrySample(renderTime, out var z))
                transform.position = new Vector3(x, y, z);
        }

        /// <summary>Networked X received — ignored by the server (authority) and the predicting owner (reconciliation happens on ack), buffered by remotes.</summary>
        private void OnNetXChanged(float prev)
        {
            if (IsServer || IsOwner) return;   // authority keeps its own; the owner reconciles on ack (ADR-0038)
            _bufX.Add(UniNetTime.Now, _px);
        }

        /// <summary>Networked Y received — same handling as the X axis.</summary>
        private void OnNetYChanged(float prev)
        {
            if (IsServer || IsOwner) return;
            _bufY.Add(UniNetTime.Now, _py);
        }

        /// <summary>Networked Z received — same handling as the X axis.</summary>
        private void OnNetZChanged(float prev)
        {
            if (IsServer || IsOwner) return;
            _bufZ.Add(UniNetTime.Now, _pz);
        }

        /// <summary>
        /// Server acknowledged an input sequence (ADR-0038) — compares the position predicted for that sequence
        /// against the server's echo; on divergence beyond <see cref="RollbackThresholdSq"/> rolls the state back to
        /// the server's (position, state) and re-simulates every unacknowledged input. An ack outside the history
        /// window (extreme latency) hard-snaps to the server state.
        /// </summary>
        private void OnAckSeqChanged(uint prev)
        {
            if (IsServer || !IsOwner || !IsClient) return;
            var sim = Simulation;
            if (sim == null) return;

            if (sim.TryGetPredicted(_ackSeq, out float hx, out float hy, out float hz))
            {
                float dx = hx - _px, dy = hy - _py, dz = hz - _pz;
                if (dx * dx + dy * dy + dz * dz <= RollbackThresholdSq)
                {
                    sim.DiscardThrough(_ackSeq);
                    return;
                }
            }
            sim.Rollback(_ackSeq, _ackState?.Data, _px, _py, _pz, ref _predX, ref _predY, ref _predZ);
            _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;   // reset the render baseline — never lerp from the pre-rollback position
        }

        // ---- Input ServerRpc (ADR-0038 — sequenced move stream; ownership auto-enforced per ADR-0016) ----

        /// <summary>Client-to-server movement input — one sequenced payload per simulation step.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcSubmitMove(uint seq, UniNetBlobEnvelope envelope);

        private Task<bool> RpcSubmitMove_Validate(uint seq, UniNetBlobEnvelope envelope)
            => Task.FromResult(Simulation != null
                               && envelope != null
                               && envelope.Data != null
                               && envelope.Data.Length <= Simulation.InputCapacityBytes);   // cap — oversized payloads are rejected before parsing

        private void RpcSubmitMove_Implementation(uint seq, UniNetBlobEnvelope envelope)
        {
            Simulation?.AdoptInput(seq, envelope?.Data);
        }
    }
}
