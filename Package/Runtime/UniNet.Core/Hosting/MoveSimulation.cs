using System;
using MessageProtocol.Serialize;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Generic fixed-step movement simulation over game-defined input/state structs (ADR-0038) — the standard
    /// implementation of <see cref="INetworkMoveSimulation"/>. The game injects five static functions:
    /// a pure step rule and the ADR-0020-style Write/Read codecs for input and state. Structs may contain any
    /// value fields (Unity types included) because they never cross assembly boundaries — only their serialized
    /// bytes do; keep them free of reference-type fields so struct copies stay independent.
    /// Determinism contract: Step must depend on (state, position, input, dt) only. Step is called with the SAME
    /// fixed dt on the server and during rollback re-simulation, so stateful rules (gravity/jump velocity, dash
    /// cooldowns, roll windows) reconcile exactly instead of drifting.
    /// </summary>
    public sealed class MoveSimulation<TInput, TState> : INetworkMoveSimulation
        where TInput : struct
        where TState : struct
    {
        /// <summary>One simulation step — advances the movement state and position. Must be a pure function of its arguments (no Time.*, no randomness).</summary>
        public delegate void StepFn(ref TState state, ref float x, ref float y, ref float z, in TInput input, float dt);

        /// <summary>Serializes an input (custom serializer contract — static Write, ADR-0020 shape).</summary>
        public delegate void WriteInputFn(ref MessageBufferWriter w, in TInput input);

        /// <summary>Deserializes an input (custom serializer contract — static Read, ADR-0020 shape). Malformed bytes must throw — the engine drops the input and continues.</summary>
        public delegate TInput ReadInputFn(ref MessageBufferReader r);

        /// <summary>Serializes a state (custom serializer contract — static Write, ADR-0020 shape).</summary>
        public delegate void WriteStateFn(ref MessageBufferWriter w, in TState state);

        /// <summary>Deserializes a state (custom serializer contract — static Read, ADR-0020 shape).</summary>
        public delegate TState ReadStateFn(ref MessageBufferReader r);

        /// <summary>History ring capacity — at the default 60 Hz this covers ~2.1 s of unacknowledged inputs; beyond it reconciliation degrades to a hard snap.</summary>
        private const int HistoryCapacity = 128;

        /// <summary>Server input queue capacity — the client sends one input per step; a deeper backlog means the server is starving and the oldest inputs are dropped (the client converges back via rollback).</summary>
        private const int ServerQueueCapacity = 8;

        private readonly StepFn _step;
        private readonly WriteInputFn _writeInput;
        private readonly ReadInputFn _readInput;
        private readonly WriteStateFn _writeState;
        private readonly ReadStateFn _readState;

        private TInput _input;      // current input — set locally (predicting owner / listen-server host) or adopted from the queue (dedicated server)
        private TState _state;      // authoritative (server) or predicted (owner client)
        private uint _lastSeenSeq;  // server — monotonic guard against stale/replayed inputs

        // server input queue — parsed inputs (deserialized at adoption; malformed payloads never reach the ring)
        private readonly uint[] _qSeq = new uint[ServerQueueCapacity];
        private readonly TInput[] _qData = new TInput[ServerQueueCapacity];
        private int _qHead;         // next write index
        private int _qCount;

        // owner client history ring — (seq, input value, position after the step); struct copies, no serialization on replay
        private readonly uint[] _hSeq = new uint[HistoryCapacity];
        private readonly TInput[] _hIn = new TInput[HistoryCapacity];
        private readonly float[] _hX = new float[HistoryCapacity];
        private readonly float[] _hY = new float[HistoryCapacity];
        private readonly float[] _hZ = new float[HistoryCapacity];
        private int _hHead;         // next write index
        private int _hCount;

        /// <summary>Fixed step in seconds (1 / stepsPerSecond).</summary>
        public float StepIntervalSeconds { get; }

        /// <summary>Accepted input payload cap — wire inputs larger than this are rejected before adoption.</summary>
        public int InputCapacityBytes { get; }

        /// <summary>Unacknowledged predicted inputs still in the history ring.</summary>
        public int PendingCount => _hCount;

        /// <summary>Sequence of the last input adopted on the server.</summary>
        public uint LastAdoptedSeq { get; private set; }

        /// <param name="step">Pure step rule — shared by the server and the predicting client (a diverging rule makes prediction error permanent).</param>
        /// <param name="writeInput">Input serializer.</param>
        /// <param name="readInput">Input deserializer.</param>
        /// <param name="writeState">State serializer (echoed to the owner for rollback anchoring).</param>
        /// <param name="readState">State deserializer.</param>
        /// <param name="stepsPerSecond">Fixed simulation rate — 60 renders smoothly with one-step-behind interpolation; lower trades input cadence for bandwidth.</param>
        /// <param name="inputCapacityBytes">Input payload cap (default 64).</param>
        public MoveSimulation(
            StepFn step,
            WriteInputFn writeInput,
            ReadInputFn readInput,
            WriteStateFn writeState,
            ReadStateFn readState,
            float stepsPerSecond,
            int inputCapacityBytes = 64)
        {
            _step = step ?? throw new ArgumentNullException(nameof(step));
            _writeInput = writeInput ?? throw new ArgumentNullException(nameof(writeInput));
            _readInput = readInput ?? throw new ArgumentNullException(nameof(readInput));
            _writeState = writeState ?? throw new ArgumentNullException(nameof(writeState));
            _readState = readState ?? throw new ArgumentNullException(nameof(readState));
            if (stepsPerSecond <= 0f) throw new ArgumentOutOfRangeException(nameof(stepsPerSecond));
            if (inputCapacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(inputCapacityBytes));

            StepIntervalSeconds = 1f / stepsPerSecond;
            InputCapacityBytes = inputCapacityBytes;
        }

        /// <summary>Sets the input used by the next step — call from the game's Update before the driver steps (predicting owner and listen-server host).</summary>
        public void SetInput(in TInput input) => _input = input;

        /// <summary>Replaces the state directly (game-side initialization, custom teleport semantics).</summary>
        public void SetState(in TState state) => _state = state;

        // ── owner client (prediction) ─────────────────────────────

        /// <inheritdoc />
        byte[] INetworkMoveSimulation.PredictStep(uint seq, ref float x, ref float y, ref float z)
        {
            var w = MessageBufferWriter.Create();
            _writeInput(ref w, in _input);
            byte[] payload = w.ToArray();   // fresh exact-size array — safe to hand to the async send path

            _step(ref _state, ref x, ref y, ref z, in _input, StepIntervalSeconds);

            // record AFTER the step — the stored position is what the server should echo for this seq
            _hSeq[_hHead] = seq;
            _hIn[_hHead] = _input;
            _hX[_hHead] = x; _hY[_hHead] = y; _hZ[_hHead] = z;
            _hHead = (_hHead + 1) % HistoryCapacity;
            if (_hCount < HistoryCapacity) _hCount++;
            return payload;
        }

        /// <inheritdoc />
        bool INetworkMoveSimulation.TryGetPredicted(uint seq, out float x, out float y, out float z)
        {
            int tail = (_hHead - 1 + HistoryCapacity) % HistoryCapacity;
            for (int i = 0; i < _hCount; i++)
            {
                int idx = (tail - i + HistoryCapacity * 2) % HistoryCapacity;
                if (_hSeq[idx] == seq) { x = _hX[idx]; y = _hY[idx]; z = _hZ[idx]; return true; }
                if (_hSeq[idx] < seq) break;   // entries are stored in increasing seq order — everything older cannot match
            }
            x = y = z = 0f;
            return false;
        }

        /// <inheritdoc />
        void INetworkMoveSimulation.DiscardThrough(uint ackSeq) => Compact(ackSeq);

        /// <inheritdoc />
        void INetworkMoveSimulation.Rollback(uint ackSeq, byte[] serverState, float serverX, float serverY, float serverZ, ref float x, ref float y, ref float z)
        {
            bool found = ContainsSeq(ackSeq);
            RestoreStateCore(serverState);
            x = serverX; y = serverY; z = serverZ;

            if (found)
            {
                // replay unacknowledged inputs (seq > ackSeq, oldest first) — refreshing their snapshots so the
                // NEXT ack compares against exactly what this replay produced
                int oldest = (_hHead - _hCount + HistoryCapacity) % HistoryCapacity;
                for (int i = 0; i < _hCount; i++)
                {
                    int idx = (oldest + i) % HistoryCapacity;
                    if (_hSeq[idx] <= ackSeq) continue;
                    TInput input = _hIn[idx];
                    _step(ref _state, ref x, ref y, ref z, in input, StepIntervalSeconds);
                    _hX[idx] = x; _hY[idx] = y; _hZ[idx] = z;
                }
            }

            Compact(ackSeq);   // acked inputs (a prefix of the ring — sequences only increase) are dropped; not-found leaves an empty ring (hard snap)
        }

        // ── server (authority) ────────────────────────────────────

        /// <inheritdoc />
        void INetworkMoveSimulation.AdoptInput(uint seq, byte[] payload)
        {
            if (seq <= _lastSeenSeq) return;   // stale or replayed (reliable-ordered delivery keeps order; the guard is defense in depth)
            if (payload == null || payload.Length > InputCapacityBytes) return;   // rejected upstream by Validate — belt and braces
            try
            {
                var r = new MessageBufferReader(payload);
                TInput input = _readInput(ref r);
                if (_qCount == ServerQueueCapacity)
                {
                    // server is starving — drop the oldest input; the client's rollback will converge on the next ack
                    _qHead = (_qHead + 1) % ServerQueueCapacity;
                    _qCount--;
                }
                int slot = (_qHead + _qCount) % ServerQueueCapacity;
                _qSeq[slot] = seq;
                _qData[slot] = input;
                _qCount++;
                _lastSeenSeq = seq;
            }
            catch (System.Exception ex)
            {
                // malformed payload — dropped (the sender's next well-formed input continues the stream), but observable:
                // a client spraying parse failures is a version mismatch or a cheat probe — silent drops would hide it
                UniNetEnvironment.LogFault(ex);
            }
        }

        /// <inheritdoc />
        void INetworkMoveSimulation.TickServer(ref float x, ref float y, ref float z)
        {
            if (_qCount > 0)
            {
                _input = _qData[_qHead];
                LastAdoptedSeq = _qSeq[_qHead];
                _qHead = (_qHead + 1) % ServerQueueCapacity;
                _qCount--;
            }
            _step(ref _state, ref x, ref y, ref z, in _input, StepIntervalSeconds);   // empty queue → re-apply the latest input (extrapolation)
        }

        /// <inheritdoc />
        byte[] INetworkMoveSimulation.WriteState()
        {
            var w = MessageBufferWriter.Create();
            _writeState(ref w, in _state);
            return w.ToArray();
        }

        private void RestoreStateCore(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
            {
                _state = default;
                return;
            }
            try
            {
                var r = new MessageBufferReader(blob);
                _state = _readState(ref r);
            }
            catch (System.Exception ex)
            {
                UniNetEnvironment.LogFault(ex);
                _state = default;   // corrupt echo (never expected from the server) — a defined reset beats a stuck arc
            }
        }

        /// <inheritdoc />
        void INetworkMoveSimulation.OnTeleport()
        {
            _state = default;
            _input = default;
            _qHead = 0;
            _qCount = 0;
            _hHead = 0;
            _hCount = 0;
        }

        // ── internals ─────────────────────────────────────────────

        private bool ContainsSeq(uint seq)
        {
            int oldest = (_hHead - _hCount + HistoryCapacity) % HistoryCapacity;
            for (int i = 0; i < _hCount; i++)
                if (_hSeq[(oldest + i) % HistoryCapacity] == seq) return true;
            return false;
        }

        /// <summary>Compacts the ring by dropping entries with Seq &lt;= cutoff. Sequences only increase, so the dropped set is a prefix — a single in-place pass suffices (the write cursor can never overtake the read cursor).</summary>
        private void Compact(uint cutoff)
        {
            if (_hCount == 0) return;
            int oldest = (_hHead - _hCount + HistoryCapacity) % HistoryCapacity;
            int write = oldest;
            int newCount = 0;
            for (int i = 0; i < _hCount; i++)
            {
                int idx = (oldest + i) % HistoryCapacity;
                if (_hSeq[idx] <= cutoff) continue;
                if (write != idx)
                {
                    _hSeq[write] = _hSeq[idx];
                    _hIn[write] = _hIn[idx];
                    _hX[write] = _hX[idx]; _hY[write] = _hY[idx]; _hZ[write] = _hZ[idx];
                }
                write = (write + 1) % HistoryCapacity;
                newCount++;
            }
            _hHead = write;
            _hCount = newCount;
        }
    }
}
