using System;
using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// P4 movement prediction component — server-authoritative replication of the transform position,
    /// with prediction and reconciliation on the owning client and interpolation on remotes (See ADR-0013).
    /// Runs as an independent slot (SubId) alongside other NetworkBehaviours on the same object (See ADR-0010 multi-component).
    /// Provide the movement rule via <see cref="MovementRule"/> (injected by your game), input via <see cref="SubmitMove"/>,
    /// and the simulation gate via <see cref="SimulationEnabled"/> — the server (authority) and the owning client (prediction)
    /// must share the same rule, otherwise prediction error becomes a constant offset.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class NetworkTransform : NetworkBehaviour
    {
        /// <summary>Movement step — applied identically by the server (authority) and the predicting client. When null, simulation stops (position replication only). Injected by your game (DIP).</summary>
        public delegate void MovementStep(ref float x, ref float y, ref float z, float inX, float inY, float inZ, float dt);

        /// <summary>Your game's movement rule — when null, only position replication runs (assign on the server, and on predicting clients).</summary>
        public MovementStep? MovementRule;

        /// <summary>Simulation gate — when false, neither the server nor the predictor moves (death, movement disabled). Controlled by your game.</summary>
        public bool SimulationEnabled = true;

        /// <summary>Remote render delay (seconds) — remotes interpolate this far behind server time (smooths over tick gaps).</summary>
        public float InterpolationDelay = 0.12f;

        /// <summary>Prediction reconciliation — snaps to the server value when the error exceeds SnapThreshold², otherwise absorbs SoftRate of the remaining error.</summary>
        public float SnapThreshold = 0.5f;

        /// <summary>Soft correction rate — the fraction of remaining prediction error absorbed immediately on each server state update.</summary>
        public float SoftRate = 0.15f;

        /// <summary>Owning-client movement prediction (default true) — when false, the owner also renders the authoritative position.</summary>
        public bool PredictOwner = true;

        /// <summary>
        /// Full CSP&R reconciliation (ADR-0037) — when true, the owner client uses rollback + re-simulation instead of
        /// soft/snap correction: the server echoes the last processed input sequence (_ackSeq), and on mismatch the client
        /// rolls back to the server position and re-simulates all unacknowledged inputs. More accurate than soft/snap
        /// under latency spikes and input loss. Requires input sequencing (built-in when true).
        /// </summary>
        public bool FullReconciliation = false;

        /// <summary>Rollback mismatch threshold (squared distance) — server state beyond this triggers rollback+re-sim (ADR-0037).</summary>
        public float RollbackThresholdSq = 0.01f;

        [Replicated(Notify = nameof(OnNetXChanged))] private float _px;
        [Replicated(Notify = nameof(OnNetYChanged))] private float _py;
        [Replicated(Notify = nameof(OnNetZChanged))] private float _pz;
        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnAckSeqChanged))] private uint _ackSeq;   // ADR-0037 — declared after _px/_py/_pz (same-delta position apply precedes ack compare)

        private float _inX;   // authoritative input (server — updated via the SubmitMove RPC)
        private float _inY;
        private float _inZ;
        private uint _clientSeq;   // ADR-0037 — client input sequence counter (owner only)
        private float _predX;         // predicted position (owning client)
        private float _predY;
        private float _predZ;
        private bool _predInit;       // suppress prediction until the first server state arrives

        // ADR-0037 — input history ring buffer for rollback + re-simulation (owner client, full CSP&R mode)
        private struct InputHistoryEntry { public uint Seq; public float InX, InY, InZ; public float PredX, PredY, PredZ; }
        private readonly InputHistoryEntry[] _history = new InputHistoryEntry[128];
        private int _historyHead;   // next write index (ring)
        private int _historyCount;
        private uint _lastRollbackSeq;   // test observer — the ackSeq at the most recent rollback
        private readonly SnapshotBuffer<float> _bufX = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufY = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufZ = new(32, LerpF);

        /// <summary>Test observer — number of samples buffered for remote interpolation (X axis).</summary>
        internal int BufferedSampleCount => _bufX.SampleCount;

        /// <summary>Test observer — the ackSeq at the most recent rollback (ADR-0037; 0 = no rollback yet).</summary>
        internal uint LastRollbackSeq => _lastRollbackSeq;

        /// <summary>Test observer — current input history count (ADR-0037).</summary>
        internal int HistoryCount => _historyCount;

        private static float LerpF(float a, float b, double t) => a + (b - a) * (float)t;

        private void Awake()
        {
            var p = transform.position;
            _px = p.x;
            _py = p.y;
            _pz = p.z;   // seed the initial replicated state from the spawn transform — placement before spawn becomes the spawn state
        }

        /// <summary>
        /// Submits local input — applied to prediction immediately on the owning client and sent to the server
        /// (on server/host it becomes the authoritative input directly). Values are clamped to [-1, 1]
        /// (trust boundary — defense in depth alongside the server-side Validate).
        /// </summary>
        public void SubmitMove(float inX, float inY, float inZ = 0f)
        {
            inX = Mathf.Clamp(inX, -1f, 1f);
            inY = Mathf.Clamp(inY, -1f, 1f);
            inZ = Mathf.Clamp(inZ, -1f, 1f);
            _inX = inX;   // owning client — feed prediction immediately (local movement follows input before any server reply)
            _inY = inY;
            _inZ = inZ;
            if (IsServer)
            {
                return;   // server/host — already the authoritative input, no RPC needed
            }
            if (FullReconciliation)
            {
                _clientSeq++;
                RpcSubmitMoveSequenced(_clientSeq, inX, inY, inZ);
                return;
            }
            RpcSubmitMove(inX, inY, inZ);
        }

        /// <summary>Server-only — sets the networked position; it replicates to all clients on the next tick (the transform updates immediately too).</summary>
        public void SetNetworkPosition(Vector3 position)
        {
            _px = position.x;
            _py = position.y;
            _pz = position.z;
            transform.position = position;
        }

        private void Update()
        {
            if (IsServer)
            {
                if (SimulationEnabled && MovementRule != null)
                    MovementRule(ref _px, ref _py, ref _pz, _inX, _inY, _inZ, Time.deltaTime);
                transform.position = new Vector3(_px, _py, _pz);   // apply the authoritative position
            }
            else if (IsOwner && PredictOwner)
            {
                if (!_predInit || !SimulationEnabled || MovementRule == null)
                {
                    transform.position = new Vector3(_px, _py, _pz);   // no server state yet or gate off — render the authoritative position
                    return;
                }
                MovementRule(ref _predX, ref _predY, ref _predZ, _inX, _inY, _inZ, Time.deltaTime);
                if (FullReconciliation)
                {
                    // Teleport re-anchor (ADR-0037 review round 2) — check BEFORE RecordHistory: when there are no
                    // pending unacknowledged inputs and the server position jumped (respawn etc.), the ack path
                    // never fires — re-anchor the prediction here so the client renders the server state.
                    if (_historyCount == 0)
                    {
                        float ddx = _predX - _px, ddy = _predY - _py, ddz = _predZ - _pz;
                        if (ddx * ddx + ddy * ddy + ddz * ddz > SnapThreshold * SnapThreshold)
                        {
                            _predX = _px; _predY = _py; _predZ = _pz;
                            _historyHead = 0; _historyCount = 0;   // clear stale history
                        }
                    }
                    RecordHistory();   // ADR-0037 — record the input + result for rollback re-simulation
                }
                transform.position = new Vector3(_predX, _predY, _predZ);
            }
            else if (IsOwner)
            {
                transform.position = new Vector3(_px, _py, _pz);   // owner without prediction — render the authoritative position
            }
            else if (IsClient)
            {
                // Remote — render a past moment from the interpolation buffer (delay controls when updates are applied). Keep the current position while the buffer is empty
                double renderTime = UniNetTime.Now - InterpolationDelay;
                if (_bufX.TrySample(renderTime, out var x) && _bufY.TrySample(renderTime, out var y) && _bufZ.TrySample(renderTime, out var z))
                    transform.position = new Vector3(x, y, z);
            }
        }

        /// <summary>Networked X received — ignored on the server (authority keeps its own), reconciled on the owning client, buffered on remotes. The first reception anchors the 3-axis prediction baseline.</summary>
        private void OnNetXChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit)
                {
                    _predInit = true;   // first state — snap all 3 axes to set the prediction baseline
                    _predX = _px;
                    _predY = _py;
                    _predZ = _pz;
                    return;
                }
                if (FullReconciliation) return;   // ADR-0037 — rollback+re-sim drives reconciliation (OnAckSeqChanged)
                _predX = ReconcileAxis(_predX, _px, SnapThreshold, SoftRate);
                return;
            }
            _bufX.Add(UniNetTime.Now, _px);
        }

        /// <summary>Networked Y received — same handling as the X axis.</summary>
        private void OnNetYChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit) { _predY = _py; return; }
                if (FullReconciliation) return;   // ADR-0037
                _predY = ReconcileAxis(_predY, _py, SnapThreshold, SoftRate);
                return;
            }
            _bufY.Add(UniNetTime.Now, _py);
        }

        /// <summary>Networked Z received — same handling as the X axis.</summary>
        private void OnNetZChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit)
                {
                    _predInit = true;   // Z may be the last axis to arrive — complete the 3-axis baseline here
                    _predX = _px;
                    _predY = _py;
                    _predZ = _pz;
                    return;
                }
                if (FullReconciliation) return;   // ADR-0037
                _predZ = ReconcileAxis(_predZ, _pz, SnapThreshold, SoftRate);
                return;
            }
            _bufZ.Add(UniNetTime.Now, _pz);
        }

        /// <summary>
        /// Reconciles one prediction axis — snaps to the server value (hard correction) when the error exceeds
        /// snapThreshold, otherwise absorbs softRate of the remaining error immediately (soft correction) —
        /// a simplified UE ClientAdjustPosition.
        /// ADR-0037: the FullReconciliation option adds input sequencing + rollback + re-simulation (UE-grade). This soft/snap path is the default (approximate) reconciliation.
        /// </summary>
        internal static float ReconcileAxis(float predicted, float server, float snapThreshold, float softRate)
        {
            float error = server - predicted;
            if (error * error > snapThreshold * snapThreshold)
                return server;
            return predicted + error * softRate;
        }

        // ---- Input ServerRpc (owned by this component — exercises base-class RPC scanning and derived-instance routing) ----

        /// <summary>Client-to-server movement input (float vector, [-1, 1]). The server keeps it as the authoritative input.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcSubmitMove(float inX, float inY, float inZ);

        [ServerRpc(Validate = true)]
        private partial void RpcSubmitMoveSequenced(uint seq, float inX, float inY, float inZ);

        private Task<bool> RpcSubmitMove_Validate(float inX, float inY, float inZ)
            => Task.FromResult(!float.IsNaN(inX) && !float.IsInfinity(inX)
                               && !float.IsNaN(inY) && !float.IsInfinity(inY)
                               && !float.IsNaN(inZ) && !float.IsInfinity(inZ)
                               && Math.Abs(inX) <= 1.01f && Math.Abs(inY) <= 1.01f && Math.Abs(inZ) <= 1.01f);

        private void RpcSubmitMove_Implementation(float inX, float inY, float inZ)
        {
            _inX = inX;
            _inY = inY;
            _inZ = inZ;
        }

        private Task<bool> RpcSubmitMoveSequenced_Validate(uint seq, float inX, float inY, float inZ)
            => Task.FromResult(!float.IsNaN(inX) && !float.IsInfinity(inX)
                               && !float.IsNaN(inY) && !float.IsInfinity(inY)
                               && !float.IsNaN(inZ) && !float.IsInfinity(inZ)
                               && Math.Abs(inX) <= 1.01f && Math.Abs(inY) <= 1.01f && Math.Abs(inZ) <= 1.01f);

        private void RpcSubmitMoveSequenced_Implementation(uint seq, float inX, float inY, float inZ)
        {
            _inX = inX;
            _inY = inY;
            _inZ = inZ;
            _ackSeq = seq;   // ADR-0037 — echo the last processed sequence (replicates to the owner via OwnerOnly)
        }

        // ---- ADR-0037 — Full CSP&R: history recording + rollback + re-simulation ----

        /// <summary>Records the current input and predicted position into the history ring buffer (owner, full CSP&R).</summary>
        private void RecordHistory()
        {
            _history[_historyHead] = new InputHistoryEntry
            {
                Seq = _clientSeq,
                InX = _inX, InY = _inY, InZ = _inZ,
                PredX = _predX, PredY = _predY, PredZ = _predZ,
            };
            _historyHead = (_historyHead + 1) % _history.Length;
            if (_historyCount < _history.Length) _historyCount++;
        }

        /// <summary>
        /// Server echoed an acknowledgment sequence (ADR-0037 — fires on the owner client when _ackSeq changes).
        /// Compares the predicted position at that sequence with the server's authoritative position;
        /// on mismatch beyond RollbackThresholdSq, rolls back to the server position and re-simulates all
        /// unacknowledged inputs. Discards acknowledged history on match.
        /// </summary>
        private void OnAckSeqChanged(uint prev)
        {
            if (IsServer || !IsOwner || !FullReconciliation || !_predInit || MovementRule == null) return;

            // Find the history entry matching the server's ackSeq — search backward (recent first)
            int matchIdx = -1;
            int tail = (_historyHead - 1 + _history.Length) % _history.Length;
            for (int i = 0; i < _historyCount; i++)
            {
                int idx = (tail - i + _history.Length * 2) % _history.Length;
                if (_history[idx].Seq == _ackSeq) { matchIdx = idx; break; }
                if (_history[idx].Seq < _ackSeq && _history[idx].Seq != 0) break;   // older than ack — stop searching
            }

            if (matchIdx < 0)
            {
                // AckSeq not in history (too old or never recorded) — soft-correct as fallback
                _predX = ReconcileAxis(_predX, _px, SnapThreshold, SoftRate);
                _predY = ReconcileAxis(_predY, _py, SnapThreshold, SoftRate);
                _predZ = ReconcileAxis(_predZ, _pz, SnapThreshold, SoftRate);
                return;
            }

            var entry = _history[matchIdx];
            float dx = entry.PredX - _px, dy = entry.PredY - _py, dz = entry.PredZ - _pz;
            bool mismatch = dx * dx + dy * dy + dz * dz > RollbackThresholdSq;

            if (mismatch)
            {
                // Rollback: start from the server's authoritative position at ackSeq
                _lastRollbackSeq = _ackSeq;
                _predX = _px; _predY = _py; _predZ = _pz;

                // Re-simulate: apply all inputs AFTER ackSeq from the history (oldest first)
                float dt = Time.deltaTime > 0 ? Time.deltaTime : 1f / 60f;   // use a reasonable fixed step
                int reSimCount = 0;
                for (int i = 0; i < _historyCount; i++)
                {
                    int idx = (matchIdx + 1 + i) % _history.Length;
                    if (idx == _historyHead) break;   // wrapped to head — no more entries
                    var e = _history[idx];
                    if (e.Seq <= _ackSeq) continue;   // already acknowledged
                    MovementRule(ref _predX, ref _predY, ref _predZ, e.InX, e.InY, e.InZ, dt);
                    reSimCount++;
                    if (reSimCount > 120) break;   // safety cap — avoid runaway re-simulation
                }
            }

            // Discard acknowledged history — single-pass in-place compaction (ADR-0037 review round 3):
            // write starts at the oldest live index; the read cursor starts after matchIdx — the write cursor
            // can never overtake the read cursor (write = oldest + newCount ≤ oldest + i < read).
            {
                int oldest = (_historyHead - _historyCount + _history.Length) % _history.Length;
                int write = oldest;
                int newCount = 0;
                for (int i = 0; i < _historyCount; i++)
                {
                    int idx = (matchIdx + 1 + i) % _history.Length;
                    if (idx == _historyHead) break;
                    if (_history[idx].Seq > _ackSeq)
                    {
                        if (write != idx) _history[write] = _history[idx];
                        write = (write + 1) % _history.Length;
                        newCount++;
                    }
                }
                _historyHead = write;
                _historyCount = newCount;
            }
        }
    }
}
