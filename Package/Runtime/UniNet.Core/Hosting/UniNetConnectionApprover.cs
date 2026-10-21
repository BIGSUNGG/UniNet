using System.Threading.Tasks;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// CON-002 — game-side connection approval callback (ADR-0047). Receives the declaring connection's identity
    /// and game credential and decides admission: true approves the connection (the held attach work — ownership
    /// reassignment, catch-up, ClientConnected — then runs in the legacy order), false rejects it with an
    /// ApprovalRejected kick. Async-first because credential validation is typically DB/HTTP; a synchronous game
    /// returns via Task.FromResult. Invoked on the main thread (lifecycle-event contract); the time budget is
    /// <see cref="NetworkServer.VersionDeclareTimeoutMs"/> — a hung callback is swept with a VersionMismatch kick
    /// like an undeclared connection, so no connection is held indefinitely.
    /// </summary>
    public delegate Task<bool> UniNetConnectionApprover(UniNetApprovalRequest request);

    /// <summary>
    /// CON-002 — the data handed to <see cref="UniNetConnectionApprover"/>. Deliberately a data carrier
    /// (adding fields later is non-breaking); pass-through judgment logic belongs in the game.
    /// </summary>
    public readonly struct UniNetApprovalRequest
    {
        /// <summary>Server connection ID of the declaring connection (NetworkServer addressing — DisconnectClient etc.).</summary>
        public long ConnId { get; }

        /// <summary>Protocol version the client declared on wire 7 — already validated against <c>ExpectedProtocolVersion</c> when version checking is active; with version checking disabled (empty option) the declared value reaches the callback unvalidated.</summary>
        public string ProtocolVersion { get; }

        /// <summary>Game credential from the wire-7 declaration. Empty when the client sent none (or is a legacy
        /// client predating CON-002) — treating an absent credential as reject or allow is the game's decision.</summary>
        public string Credential { get; }

        public UniNetApprovalRequest(long connId, string protocolVersion, string credential)
        {
            ConnId = connId;
            ProtocolVersion = protocolVersion ?? string.Empty;
            Credential = credential ?? string.Empty;
        }
    }
}
