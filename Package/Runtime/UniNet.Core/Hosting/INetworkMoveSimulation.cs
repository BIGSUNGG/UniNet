namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Untyped contract between NetworkTransform and a game's movement simulation (ADR-0038).
    /// The game supplies its own input/state structs and a pure step rule (state, position, input, fixed dt only —
    /// no engine clocks, no randomness); UniNet drives it at a fixed rate on the server (authority) and the owning
    /// client (prediction), sequences inputs, echoes acknowledged state, and rolls back + re-simulates on mismatch.
    /// Implement this interface only for a fully custom engine — otherwise use <see cref="MoveSimulation{TInput,TState}"/>.
    /// </summary>
    public interface INetworkMoveSimulation
    {
        /// <summary>Fixed simulation step in seconds — the server and the predicting client advance by exactly this dt (deterministic re-simulation).</summary>
        float StepIntervalSeconds { get; }

        /// <summary>Maximum accepted input payload size in bytes — RpcSubmitMove rejects anything larger.</summary>
        int InputCapacityBytes { get; }

        /// <summary>Unacknowledged predicted inputs still in the history ring (owner client).</summary>
        int PendingCount { get; }

        /// <summary>Sequence of the last input adopted on the server (echoed to the owner as the replication ack).</summary>
        uint LastAdoptedSeq { get; }

        // ── owner client (prediction) ─────────────────────────────

        /// <summary>Advances one fixed step with the current input, records (seq, input, predicted position) into the history, and returns the input payload for the wire (a fresh exact-size array).</summary>
        byte[] PredictStep(uint seq, ref float x, ref float y, ref float z);

        /// <summary>The position predicted for the given input sequence, if still in the history ring.</summary>
        bool TryGetPredicted(uint seq, out float x, out float y, out float z);

        /// <summary>Drops history entries at or below the ack sequence — the server confirmed them, no replay needed.</summary>
        void DiscardThrough(uint ackSeq);

        /// <summary>Rolls back to the server's authoritative (position, state) at ackSeq and re-simulates every unacknowledged input. When ackSeq is no longer in the ring (extreme latency / cleared history), performs a hard snap instead.</summary>
        void Rollback(uint ackSeq, byte[] serverState, float serverX, float serverY, float serverZ, ref float x, ref float y, ref float z);

        // ── server (authority) ────────────────────────────────────

        /// <summary>Adopts a received input (order-checked) into the server queue. Malformed payloads are dropped, never thrown.</summary>
        void AdoptInput(uint seq, byte[] payload);

        /// <summary>Advances one fixed step: adopts the next queued input when available (updating <see cref="LastAdoptedSeq"/>), otherwise re-applies the latest input (extrapolation — matches UE server-side movement).</summary>
        void TickServer(ref float x, ref float y, ref float z);

        /// <summary>Serializes the current (authoritative on the server / predicted on the client) state to a fresh exact-size array.</summary>
        byte[] WriteState();

        // ── both ──────────────────────────────────────────────────

        /// <summary>Teleport/respawn reset — clears the input queue, history, and state (a jump arc must not resume after a teleport).</summary>
        void OnTeleport();

        /// <summary>
        /// Ownership tenure boundary reset (MOV-001) — clears the server input queue, the owner-client history, and
        /// the sequence guards (the next owner's stream starts at seq 1), while PRESERVING the game state and the
        /// latest input. Contract difference from <see cref="OnTeleport"/>: a teleport also resets state (a jump
        /// arc must not resume at the destination); an ownership handoff does not — the object keeps its velocity,
        /// cooldowns, and windows, so authority continues seamlessly for the next owner. Server: call on every actual
        /// owner change, paired with the movement-epoch bump. Owner client: call when (re)gaining ownership.
        /// </summary>
        void ResetOwnershipStream();

        /// <summary>
        /// Death-gate cleanup (MOV-002) — drops the unacknowledged prediction history when the game gates the
        /// simulation off (death, cutscene) while predicted inputs are still pending. Contract difference from
        /// <see cref="OnTeleport"/>: OnTeleport is the full reset (queue, history, state, input — a jump arc must
        /// not resume); this hook only guarantees the PENDING history is gone and leaves whether the game state
        /// and the latest input survive the gate to the implementation. The default falls back to the full
        /// <see cref="OnTeleport"/> wipe; <see cref="MoveSimulation{TInput,TState}"/> overrides it to keep both
        /// (the position re-converges via the death-window ack hard snap and the respawn re-anchor).
        /// </summary>
        void DropPendingHistory() => OnTeleport();
    }
}
