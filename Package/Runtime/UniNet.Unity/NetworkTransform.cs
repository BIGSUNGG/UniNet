using System;
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

        /// <summary>
        /// Maximum remote render delay (seconds) — remotes render at most this far behind the received stream.
        /// The effective delay shrinks adaptively when snapshots arrive densely and steadily (a 60 Hz stream renders
        /// ~3 arrival gaps behind instead of the full delay), so remote motion tracks closer to live without stutter.
        /// </summary>
        public float InterpolationDelay = 0.12f;

        /// <summary>
        /// Reconciliation trigger — predicted-vs-server position divergence (squared distance) beyond this rolls the
        /// simulation back to the server state and re-simulates unacknowledged inputs; smaller values correct harder,
        /// larger values tolerate float noise.
        /// </summary>
        public float RollbackThresholdSq = 0.01f;

        /// <summary>Teleport detection — a position jump (per axis, per snapshot) beyond this is a discontinuity: the owner re-anchors its prediction, remotes clear the interpolation buffer so the jump never renders as a slide. Also doubles as the ANGULAR snap floor (degrees) for <see cref="SyncRotation"/>.</summary>
        public float SnapThreshold = 0.5f;

        /// <summary>
        /// Opt-in (TRS-001) — replicate <see cref="Transform.rotation"/> continuously (turrets, vehicles). Default off:
        /// the replicated rotation fields ride the spawn state either way (Awake seeds them from the spawn pose so a
        /// zero quaternion never reaches the wire), but stay constant — enabling this only adds the per-tick rotation
        /// deltas. The flag gates what the SERVER samples; receivers apply whatever arrives (owner directly — rotation
        /// is not predicted; remotes interpolated on the same adaptive delay as position).
        /// </summary>
        public bool SyncRotation = false;

        /// <summary>Opt-in (TRS-001) — replicate <see cref="Transform.localScale"/>. Same contract as <see cref="SyncRotation"/>; applied immediately on receipt (a scale change is discrete — no interpolation).</summary>
        public bool SyncScale = false;

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
        // MOV-001 movement-ownership epoch — server-authoritative, replicated to the owner only. Bumped on every
        // tenure→tenure owner change (direct transfer, or release) and paired with the input-stream reset; the owner
        // stamps it onto every move input so a stale in-flight packet from a PREVIOUS tenure is rejected even when
        // its sequence number would pass. Adoption from unowned (0 → conn) is a baseline: no bump, no reset.
        // After _ackSeq: its notify is independent of the ack fields (declaration order is wire-apply order within a delta).
        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnMoveEpochChanged))] private uint _moveEpoch;
        // TRS-001 — persistent rotation/scale replication, opt-in via SyncRotation/SyncScale. Awake seeds them from the
        // spawn transform so the full-state spawn/catch-up wire carries the current pose (and a (0,0,0,0) quaternion is
        // impossible). Declaration order is wire-apply order within one delta (ADR-0037 precedent); the four rotation
        // notifies of one delta land µs apart, so the quaternion is consumed ONCE per delta via _rotPending — reading it
        // mid-apply would see half-updated components (an invalid, non-unit quaternion).
        [Replicated(Notify = nameof(OnNetRotComponentChanged))] private float _rx;
        [Replicated(Notify = nameof(OnNetRotComponentChanged))] private float _ry;
        [Replicated(Notify = nameof(OnNetRotComponentChanged))] private float _rz;
        [Replicated(Notify = nameof(OnNetRotComponentChanged))] private float _rw;
        [Replicated(Notify = nameof(OnNetScaleComponentChanged))] private float _sx;
        [Replicated(Notify = nameof(OnNetScaleComponentChanged))] private float _sy;
        [Replicated(Notify = nameof(OnNetScaleComponentChanged))] private float _sz;

        private float _accumulator;      // fixed-step remainder (seconds) since the last executed step
        private float _predX;            // predicted position (owning client)
        private float _predY;
        private float _predZ;
        private float _prevPredX;        // position before the latest predicted step — render interpolation baseline
        private float _prevPredY;
        private float _prevPredZ;
        private uint _clientSeq;         // owner input sequence
        private uint _clientEpoch;       // epoch of the input stream this owner is currently sending (MOV-001)
        private bool _prevGate = true;   // MOV-002 — the predictor's last observed SimulationEnabled (death-gate edge detection)
        private long _watchedOwner;      // server — owner value at the last watch observation (tenure watch, MOV-001)
        private long _epochMismatches;   // server — inputs dropped because their embedded epoch is stale (MOV-001 observability)
        private NetworkClient _subscribedClient;   // client instance currently subscribed for ownership notices (replaced on reconnect)
        private readonly UniNetBlobEnvelope _env = new UniNetBlobEnvelope();   // reused wire envelope — the payload array it references is swapped per send (encode is synchronous)

        private readonly SnapshotBuffer<float> _bufX = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufY = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufZ = new(32, LerpF);
        private readonly SnapshotBuffer<Quaternion> _bufRot = new(32, SlerpQ);   // TRS-001 — same buffer pattern as position (adaptive delay + arrival pacing reused)

        // ---- remote rendering (ADR-0038 review follow-up) ----
        private bool _hasSeen;                 // any remote sample buffered yet
        private bool _rotPending;              // TRS-001 — a rotation delta arrived; the quaternion is consumed once per delta in Update (per-component notifies land µs apart)
        private bool _hasSeenRot;              // any remote rotation buffered yet
        private Quaternion _seenRot;           // last buffered remote rotation — angular discontinuity detection
        private float _rotDispEma;             // per-delta angular displacement EMA (degrees) — the snap tolerance follows the object's turn rate
        private float _seenX, _seenY, _seenZ;  // last buffered remote sample — per-axis teleport discontinuity detection
        private float _dispEmaX, _dispEmaY, _dispEmaZ;   // per-axis sample-to-sample displacement — the discontinuity tolerance follows the object's motion scale
        private double _lastSampleAt;          // arrival time of the last paced sample (UniNetTime domain)
        private double _arrivalGapEma = 0.05;  // smoothed inter-sample arrival gap (seconds)
        private const double ArrivalGapEmaAlpha = 0.1;
        private const double AdaptiveDelayMinSeconds = 0.03;
        private const double AdaptiveGapMultiplier = 3.0;
        private const double SameDeltaArrivalSeconds = 0.002;   // X/Y/Z notifies of one delta land µs apart — not a stream gap
        private const float DisplacementEmaAlpha = 0.1f;
        private const float DisplacementEmaMultiplier = 3f;

        /// <summary>Current remote render delay — <see cref="InterpolationDelay"/> capped, shrunk to ~3 arrival gaps for a dense steady stream (test/debug observer).</summary>
        internal double EffectiveInterpolationDelay
            => Math.Min(InterpolationDelay, Math.Max(AdaptiveDelayMinSeconds, _arrivalGapEma * AdaptiveGapMultiplier));

        private static float LerpF(float a, float b, double t) => a + (b - a) * (float)t;

        private static Quaternion SlerpQ(Quaternion a, Quaternion b, double t) => Quaternion.Slerp(a, b, (float)t);

        private void Awake()
        {
            // Position/ack stream rides the unreliable (Sequenced) batch — a lost delta is superseded by the next
            // one 16 ms later; a lost final state is recovered by the server's quiescence resend.
            NetworkReplicationDelivery = Delivery.Unreliable;

            var p = transform.position;
            _px = p.x;
            _py = p.y;
            _pz = p.z;   // seed the initial replicated state from the spawn transform — placement before spawn becomes the spawn state
            _predX = p.x; _predY = p.y; _predZ = p.z;   // prediction starts aligned with the spawn (owner and server instantiate the same transform)

            // TRS-001 — seed the rotation/scale fields from the spawn pose unconditionally: the full-state spawn wire
            // serializes them either way, and a zero quaternion (default float fields) is not a valid rotation — late
            // joiners must start at the actual pose. With the opt-in flags off these never change afterward (the server
            // does not sample), so the per-tick bandwidth cost is zero.
            var r = transform.rotation;
            _rx = r.x; _ry = r.y; _rz = r.z; _rw = r.w;
            var s = transform.localScale;
            _sx = s.x; _sy = s.y; _sz = s.z;
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

        /// <summary>
        /// Server-only — sets the networked rotation; it replicates to all clients on the next tick (the transform
        /// updates immediately too). Position-only teleports keep using <see cref="SetNetworkPosition"/> — rotation is
        /// deliberately not part of it, and no simulation reset happens here: rotation is not a predicted quantity
        /// (TRS-001), so there is no prediction to re-anchor. Remotes render the jump as a snap (angular-displacement
        /// discontinuity detection clears the rotation buffer).
        /// </summary>
        public void SetNetworkRotation(Quaternion rotation)
        {
            _rx = rotation.x;
            _ry = rotation.y;
            _rz = rotation.z;
            _rw = rotation.w;
            transform.rotation = rotation;
        }

        private void Update()
        {
            var sim = Simulation;
            bool isAuthority = IsServer;
            bool isPredictor = !IsServer && IsOwner && IsClient;

            if (isAuthority)
                WatchServerOwnership();   // MOV-01 — bump the epoch + reset the stream BEFORE this frame's steps so stale-tenure inputs die at the gate
            else
                BindOwnershipEvent();     // MOV-01 — client instance: hook ownership notices (dispatch-thread event, marshalled inside)

            if (isAuthority || isPredictor)
            {
                if (isPredictor && sim != null)
                    TrackGateEdge(sim);   // MOV-002 — before the step block: the entry edge drops pending history, the exit edge re-anchors so this frame's step starts from the respawn position (r17 ISSUE-1)

                if (sim == null)
                {
                    // replication-only contract (ADR-0013) — nothing to step; render branches below apply the position
                }
                else if (!SimulationEnabled)
                {
                    _accumulator = 0f;   // frozen — do not accumulate stall debt while disabled
                }
                else
                {
                    StepSimulation(sim);
                }
            }

            if (isAuthority)
            {
                // TRS-001 — the game rotates/scales the transform directly (rotation has no simulation slot); sample
                // it into the replicated fields so the delta detector ships changes. Flag-off instances never write,
                // so the fields stay at their Awake seed and cost zero per-tick bandwidth.
                if (SyncRotation)
                {
                    var r = transform.rotation;
                    _rx = r.x; _ry = r.y; _rz = r.z; _rw = r.w;
                }
                if (SyncScale)
                {
                    var s = transform.localScale;
                    _sx = s.x; _sy = s.y; _sz = s.z;
                }
                transform.position = new Vector3(_px, _py, _pz);
            }
            else if (isPredictor)
            {
                ApplyPendingRotationDirect();   // TRS-001 — before the early return below: the owner applies the server-driven rotation directly (not predicted, no buffer)

                if (sim == null || !SimulationEnabled)
                {
                    transform.position = new Vector3(_px, _py, _pz);   // owner without a simulation / gated (death) — render the authoritative position
                    return;
                }

                // Teleport re-anchor (ADR-0037 lineage) — no pending inputs + server position jumped: the ack path
                // never fires (no further adoptions while the sender is idle/gated), so adopt the server state here.
                TryReAnchor(sim);

                // Render between the last two predicted steps — smooth at any frame rate while the simulation stays fixed-rate
                float interval = sim.StepIntervalSeconds;
                float frac = interval > 0f ? Mathf.Clamp01(_accumulator / interval) : 1f;
                transform.position = new Vector3(
                    Mathf.Lerp(_prevPredX, _predX, frac),
                    Mathf.Lerp(_prevPredY, _predY, frac),
                    Mathf.Lerp(_prevPredZ, _predZ, frac));
            }
            else if (IsClient)
            {
                BufferPendingRotation();   // TRS-001 — intake the pending quaternion BEFORE sampling the buffers
                InterpolateRemote();   // remote — always interpolated, with or without a simulation
            }
            else
            {
                transform.position = new Vector3(_px, _py, _pz);   // unregistered/stale instance — follow the authoritative position
            }
        }

        /// <summary>Fixed-step driver — server authority and owner prediction advance by whole simulation steps (spiral-of-death guarded).</summary>
        private void StepSimulation(INetworkMoveSimulation sim)
        {
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
                else
                {
                    _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;
                    uint seq = ++_clientSeq;
                    byte[] payload = sim.PredictStep(seq, ref _predX, ref _predY, ref _predZ);
                    _env.Data = EncodeMoveInput(_clientEpoch, payload);   // MOV-01 — epoch-prefixed envelope payload
                    RpcSubmitMove(seq, _env);
                }
            }
            if (steps == MaxStepsPerFrame)
                _accumulator = Mathf.Min(_accumulator, interval * 0.5f);   // spiral-of-death guard — shed accumulated debt after a stall
        }

        /// <summary>Remote — render a past moment from the interpolation buffer at the adaptive delay. Keep the current position while the buffer is empty.</summary>
        private void InterpolateRemote()
        {
            double renderTime = UniNetTime.Now - EffectiveInterpolationDelay;
            if (_bufX.TrySample(renderTime, out var x) && _bufY.TrySample(renderTime, out var y) && _bufZ.TrySample(renderTime, out var z))
                transform.position = new Vector3(x, y, z);
            if (_bufRot.TrySample(renderTime, out var rot))
                transform.rotation = rot;   // TRS-001 — unreachable with SyncRotation off (no deltas → empty buffer), so the flag needs no re-check here
        }

        /// <summary>Networked X received — ignored by the server (authority) and the predicting owner (reconciliation happens on ack), buffered by remotes.</summary>
        private void OnNetXChanged(float prev)
        {
            if (IsServer || IsOwner) return;   // authority keeps its own; the owner reconciles on ack (ADR-0038)
            TrackArrival(UniNetTime.Now);
            AddRemoteSample(ref _seenX, ref _dispEmaX, _px, _bufX);
        }

        /// <summary>Networked Y received — same handling as the X axis.</summary>
        private void OnNetYChanged(float prev)
        {
            if (IsServer || IsOwner) return;
            TrackArrival(UniNetTime.Now);
            AddRemoteSample(ref _seenY, ref _dispEmaY, _py, _bufY);
        }

        /// <summary>Networked Z received — same handling as the X axis.</summary>
        private void OnNetZChanged(float prev)
        {
            if (IsServer || IsOwner) return;
            TrackArrival(UniNetTime.Now);
            AddRemoteSample(ref _seenZ, ref _dispEmaZ, _pz, _bufZ);
        }

        // ---- persistent rotation/scale sync (TRS-001, opt-in) ----

        /// <summary>A rotation component (x/y/z/w) was received — one shared handler for all four fields. The server
        /// ignores (its transform is the sampling source); the owner and remotes mark the quaternion pending and
        /// consume it once per delta in Update: reading the quaternion here would see half-updated components, because
        /// a notify fires before the delta's other components are applied (same per-axis caveat as position).
        /// Owner: applied directly (rotation is not predicted — deliberate difference from the position notify's
        /// IsOwner guard). Remote: buffered + Slerp-interpolated, feeding the shared adaptive-delay pacing.</summary>
        private void OnNetRotComponentChanged(float prev)
        {
            if (IsServer) return;   // authority — it samples its own transform instead
            if (IsOwner)
            {
                _rotPending = true;
                return;
            }
            TrackArrival(UniNetTime.Now);   // rotation-only deltas must pace the shared adaptive delay too (same-delta duplicates suppressed)
            _rotPending = true;
        }

        /// <summary>A scale component (x/y/z) was received — applied immediately (owner and remote alike): a scale
        /// change is discrete and un-interpolated. The three per-component notifies land µs apart inside one apply
        /// pass, so the transient mid-pass vectors are never rendered and the final write completes the vector.</summary>
        private void OnNetScaleComponentChanged(float prev)
        {
            if (IsServer) return;   // authority — it samples its own transform instead
            transform.localScale = new Vector3(_sx, _sy, _sz);
        }

        /// <summary>Remote — consumes the pending quaternion into the interpolation buffer. A jump beyond
        /// max(SnapThreshold, 3× the angular-displacement EMA) clears the buffer (the fresh single sample holds
        /// immediately — a spin-up renders as a snap, not a spin-through). Two deliberate deviations from the position
        /// analog, both forced by SnapThreshold (units) doubling as an ANGULAR floor (degrees) that sits BELOW every
        /// ordinary per-sample step: (1) the judgment arms only once the EMA has observed motion — a cold EMA would
        /// classify the second sample of any steady spin as a teleport; (2) the EMA keeps updating across snaps — a
        /// frozen-at-zero EMA would make every subsequent step snap forever. The cold-start window needs no snap
        /// anyway: with ≤ 2 buffered samples the renderer holds/lerps at most one delay window.</summary>
        private void BufferPendingRotation()
        {
            if (!_rotPending) return;
            _rotPending = false;
            var q = new Quaternion(_rx, _ry, _rz, _rw);
            float angle = _hasSeenRot ? Quaternion.Angle(_seenRot, q) : 0f;
            bool snap = _hasSeenRot && _rotDispEma > 0f
                        && angle > Mathf.Max(SnapThreshold, _rotDispEma * DisplacementEmaMultiplier);
            if (snap)
                _bufRot.Clear();   // the discontinuity itself must not render as a slide; bounded by 180°, so it may still inform the EMA below
            if (_hasSeenRot)
                _rotDispEma += (angle - _rotDispEma) * DisplacementEmaAlpha;
            _bufRot.Add(UniNetTime.Now, q);
            _seenRot = q;
            _hasSeenRot = true;
        }

        /// <summary>Owner — rotation is not a predicted quantity (the server drives it), so the replicated value
        /// applies directly, one consume per delta. Runs before the gated early-return so a death/cutscene window
        /// still tracks the server's rotation.</summary>
        private void ApplyPendingRotationDirect()
        {
            if (!_rotPending) return;
            _rotPending = false;
            transform.rotation = new Quaternion(_rx, _ry, _rz, _rw);
        }

        /// <summary>Smooths the inter-delta arrival gap. Any axis may pace the stream (a falling or in-place-jumping
        /// object only changes Y) — the three notifies of one delta land µs apart and are suppressed as duplicates.</summary>
        private void TrackArrival(double now)
        {
            if (_lastSampleAt > 0)
            {
                double gap = now - _lastSampleAt;
                if (gap < SameDeltaArrivalSeconds) return;   // same-delta duplicate (X/Y/Z of one apply pass)
                _arrivalGapEma += (gap - _arrivalGapEma) * ArrivalGapEmaAlpha;
            }
            _lastSampleAt = now;
        }

        /// <summary>
        /// Buffers one remote axis sample. A jump beyond max(SnapThreshold, 3× the axis's displacement EMA) is a
        /// teleport: the axis buffer is cleared so the renderer never lerps through the gap (the fresh single sample
        /// holds immediately — a discontinuity renders as a snap, not a slide). The motion-scaled term keeps fast
        /// movement honest: a 20 u/s dash with two sim ticks merged into one delta (0.67u) stays below 3× its normal
        /// per-sample displacement (~0.33u) and interpolates normally. Deliberately per-axis (a notify fires before
        /// the delta's other axes are applied, so the full vector is unavailable here) — unlike the owner re-anchor,
        /// which judges the 3D distance; sub-threshold diagonal jumps may still render as a short slide.
        /// </summary>
        private void AddRemoteSample(ref float seen, ref float dispEma, float value, SnapshotBuffer<float> buffer)
        {
            float delta = _hasSeen ? Mathf.Abs(value - seen) : 0f;
            bool teleport = _hasSeen && delta > Mathf.Max(SnapThreshold, dispEma * DisplacementEmaMultiplier);
            if (teleport)
            {
                buffer.Clear();   // the teleport sample itself does not pollute the motion-scale EMA
            }
            else if (_hasSeen)
            {
                dispEma += (delta - dispEma) * DisplacementEmaAlpha;
            }
            buffer.Add(UniNetTime.Now, value);
            seen = value;
            _hasSeen = true;
        }

        /// <summary>
        /// Server acknowledged an input sequence (ADR-0038) — compares the position predicted for that sequence
        /// against the server's echo; on divergence beyond <see cref="RollbackThresholdSq"/> rolls the state back to
        /// the server's (position, state) and re-simulates every unacknowledged input. An ack outside the history
        /// window (extreme latency) hard-snaps to the server state. Even when positions agree, the state predicted
        /// for the acknowledged sequence is reconciled against the server's echoed state (v0.23.0) — game state
        /// (cooldowns, speeds, movement modes) converges to the authority even while standing still, at the cost of
        /// one comparison per ack and a re-simulation only on actual divergence.
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
                    // FR-05 — positions agree within tolerance, but the authoritative game state may still differ
                    // (a dash cooldown the server never adopted, an input-expiry step the prediction never ran).
                    // ReconcileState compares the state predicted for this sequence against the server echo EXACTLY
                    // and re-simulates the unacknowledged inputs only on divergence; an agreeing ack costs one
                    // comparison and no replay. Custom simulations without the hook keep the legacy discard-only path.
                    if (sim.ReconcileState(_ackSeq, _ackState?.Data, _px, _py, _pz, ref _predX, ref _predY, ref _predZ))
                    {
                        // the replay moved the prediction — never lerp from the pre-reconcile position (all three axes;
                        // an agreeing ack must leave the render baseline untouched entirely)
                        _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;
                    }
                    sim.DiscardThrough(_ackSeq);
                    return;
                }
            }
            sim.Rollback(_ackSeq, _ackState?.Data, _px, _py, _pz, ref _predX, ref _predY, ref _predZ);
            _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;   // reset the render baseline — never lerp from the pre-rollback position
        }

        // ---- simulation gate (MOV-002: death-gate pending-history cleanup) ----

        /// <summary>
        /// Owner-client death-gate edge detection — on SimulationEnabled turning OFF (death, cutscene), drops the
        /// pending prediction history: acks arriving during the death window miss the empty ring and hard-snap the
        /// predictor onto the authoritative position (self-alignment), and pending==0 on re-enable lets the
        /// UNCHANGED teleport re-anchor adopt the respawn position. Edge-triggered deliberately — a level check
        /// would keep re-clearing a game that legitimately predicts inside the gate. Whether state/input survive
        /// the gate is the simulation's choice (<see cref="INetworkMoveSimulation.DropPendingHistory"/>).
        /// </summary>
        private void TrackGateEdge(INetworkMoveSimulation sim)
        {
            if (_prevGate == SimulationEnabled) return;
            bool wasEnabled = _prevGate;
            _prevGate = SimulationEnabled;
            if (wasEnabled && !SimulationEnabled)
                sim?.DropPendingHistory();
            else if (!wasEnabled && SimulationEnabled)
                TryReAnchor(sim);   // revive: re-anchor before the revive frame's step (r17 ISSUE-1)
        }

        /// <summary>Teleport re-anchor (ADR-0037 lineage): with no pending inputs and a server position
        /// beyond the snap threshold, adopt the server state — the ack path never fires while the sender
        /// is idle/gated. Called mid-life and on the death-gate exit edge (MOV-002).</summary>
        private void TryReAnchor(INetworkMoveSimulation sim)
        {
            if (sim.PendingCount != 0) return;
            float ddx = _predX - _px, ddy = _predY - _py, ddz = _predZ - _pz;
            if (ddx * ddx + ddy * ddy + ddz * ddz > SnapThreshold * SnapThreshold)
            {
                sim.OnTeleport();
                _predX = _px; _predY = _py; _predZ = _pz;
                _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;
            }
        }

        // ---- movement ownership tenure (MOV-001: stream reset + epoch gate) ----

        /// <summary>
        /// Server-side ownership watch — bumps the movement epoch and resets the input stream only on a TENURE→TENURE
        /// change (the previously observed owner was nonzero and differs: a direct transfer, or a release). Adoption
        /// from unowned (0 → conn) is a baseline — no bump, no reset: an initial spawn's stream is virgin, and after a
        /// release the release-time bump already cut the boundary (the ownership gate blocks senders while unowned).
        /// This rule is timing-independent: whatever owner the first observation sees is a baseline, so a dynamic
        /// spawn whose owning connection attaches AFTER the first Update no longer bumps on its initial adoption.
        /// Frame-granular; a same-frame A→B→A flip is observed as no change — documented ceiling in ADR-0042.
        /// </summary>
        private void WatchServerOwnership()
        {
            long owner = UniNetEnvironment.Server?.GetOwner(NetId) ?? 0;
            if (owner == _watchedOwner) return;
            long prev = _watchedOwner;
            _watchedOwner = owner;
            if (prev == 0) return;   // adoption baseline (includes the very first observation — _watchedOwner starts at 0)
            _moveEpoch++;
            Simulation?.ResetOwnershipStream();
        }

        /// <summary>Subscribes this client instance to ownership notices — lazy: the environment client appears after
        /// component enable and is replaced on reconnect. A stale subscription to a dead client is inert (its
        /// connection is gone), and a fresh client's first assignment is a baseline (no event), so no ownership
        /// transition can be lost in the swap.</summary>
        private void BindOwnershipEvent()
        {
            var client = UniNetEnvironment.Client;
            if (client == null || ReferenceEquals(client, _subscribedClient)) return;
            if (_subscribedClient != null) _subscribedClient.OwnershipChanged -= OnClientOwnershipChanged;
            _subscribedClient = client;
            client.OwnershipChanged += OnClientOwnershipChanged;
        }

        /// <summary>Ownership notice arrived (dispatch thread — marshalled above). Only a GAIN by the local
        /// connection rebinds this instance; the authority instance (host) skips — the server-side watch already
        /// owns its stream and a client-side reset here would clear the server queue (the double-reset guard).
        /// Notices for other netIds (including objects this client never registered) drop at the netId guard.</summary>
        private void OnClientOwnershipChanged(ulong netId, long prevOwner, long newOwner)
        {
            UniNetEnvironment.QueueOnMain(() =>
            {
                if (this == null || IsServer || netId != NetId) return;
                var client = UniNetEnvironment.Client;
                if (client == null || newOwner == 0 || newOwner != client.LocalConnId) return;
                ResetOwnerStream();
            });
        }

        /// <summary>Replicated movement epoch changed (owner client) — the server bumped it at an ownership boundary:
        /// adopt it as this stream's session epoch and rebind. Idempotent per value: catch-up/visibility baselines
        /// re-apply the same epoch without resetting. Also the self-healing backstop for a missed ownership event —
        /// the epoch delta reaches the CURRENT owner (unreliable + quiescence resend) even when the notice raced
        /// component enable; handoff-window inputs sent with the stale epoch are rejected until it arrives.</summary>
        private void OnMoveEpochChanged(uint prev)
        {
            if (IsServer || !IsOwner || !IsClient) return;
            if (_moveEpoch == _clientEpoch) return;
            _clientEpoch = _moveEpoch;
            ResetOwnerStream();
        }

        /// <summary>Owner-client rebinding — restarts the local input stream at seq 1 and re-anchors on the
        /// authoritative replicated position. The predicted game state is deliberately NOT restored here: there is
        /// no state echo for the new tenure yet, so prediction converges through the first ack rollback (a hard snap
        /// when that ack predates the new history).</summary>
        private void ResetOwnerStream()
        {
            _clientSeq = 0;
            _accumulator = 0f;
            _predX = _px; _predY = _py; _predZ = _pz;
            _prevPredX = _predX; _prevPredY = _predY; _prevPredZ = _predZ;
            Simulation?.ResetOwnershipStream();
        }

        /// <summary>Server-side diagnostic (MOV-01) — movement inputs dropped because their embedded epoch did not
        /// match the current ownership epoch. Includes the brief handoff window where a legitimate new owner has not
        /// yet received the bumped epoch (its stale-epoch inputs land here until the OwnerOnly delta arrives).</summary>
        public long MoveEpochMismatches => System.Threading.Interlocked.Read(ref _epochMismatches);

        private void OnDestroy()
        {
            if (_subscribedClient != null)   // null-conditional -= is illegal on events (CS0079) — event field access is not a value
                _subscribedClient.OwnershipChanged -= OnClientOwnershipChanged;
        }

        // ---- Input ServerRpc (ADR-0038 — sequenced move stream; ownership auto-enforced per ADR-0016) ----

        /// <summary>Client-to-server movement input — one sequenced payload per simulation step.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcSubmitMove(uint seq, UniNetBlobEnvelope envelope);

        /// <summary>Movement-input envelope layout (MOV-01, breaking envelope change): [uint epoch][int length][bytes input]
        /// — the epoch prefixes the game payload inside UniNetBlobEnvelope.Data. The RPC signature, the envelope
        /// message type, and all system messages are unchanged (no generator rebuild needed). Standard MessageProtocol
        /// codecs, so endianness matches the rest of the wire.</summary>
        private static byte[] EncodeMoveInput(uint epoch, byte[] input)
        {
            // one allocation per send — the synchronous prefix copy right before the generated encoder call is the
            // floor (a reusable scratch buffer would violate _env.Data's array-lives-until-send contract).
            // Little-endian verified against MessageProtocol's reader (WriteUInt32 0x01020304 → 04 03 02 01).
            var data = new byte[8 + input.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), epoch);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), input.Length);
            Buffer.BlockCopy(input, 0, data, 8, input.Length);
            return data;
        }

        private Task<bool> RpcSubmitMove_Validate(uint seq, UniNetBlobEnvelope envelope)
            => Task.FromResult(Simulation != null
                               && envelope != null
                               && envelope.Data != null
                               && envelope.Data.Length <= sizeof(uint) + sizeof(int) + Simulation.InputCapacityBytes);   // envelope prefix overhead (epoch + length); the input payload itself is capped again at adoption

        private void RpcSubmitMove_Implementation(uint seq, UniNetBlobEnvelope envelope)
        {
            var sim = Simulation;
            var data = envelope?.Data;
            if (sim == null || data == null) return;
            uint epoch;
            byte[] input;
            try
            {
                var r = new MessageProtocol.Serialize.MessageBufferReader(data);
                epoch = r.ReadUInt32();
                input = r.ReadBytes(r.ReadInt32()).ToArray();
            }
            catch (Exception ex)
            {
                UniNetEnvironment.LogFault(ex);   // malformed envelope — dropped, observable (same containment contract as AdoptInput)
                return;
            }
            if (epoch != _moveEpoch)
            {
                System.Threading.Interlocked.Increment(ref _epochMismatches);   // quiet drop — the counter is the only observable
                return;
            }
            sim.AdoptInput(seq, input);
        }
    }
}
