namespace UniNet.Core
{
    /// <summary>
    /// Why a client session ended (application-level reason, delivered on the wire by disconnect notices —
    /// ADR-0027). Complements the transport-level Communication DisconnectReason: this enum explains the
    /// session from UniNet's point of view, so games can pick the right UX (reconnect prompt vs. back-to-menu).
    /// </summary>
    public enum UniNetDisconnectReason : byte
    {
        /// <summary>No notice was received — the reason is unknown (or no session ended yet).</summary>
        None = 0,

        /// <summary>The local player called ClientStop voluntarily — the lifecycle event is NOT raised (existing contract).</summary>
        ClientStopped = 1,

        /// <summary>The server is shutting down (ServerStop/HostStop broadcasts this to every connection, best effort).</summary>
        ServerShutdown = 2,

        /// <summary>The server disconnected this client explicitly (DisconnectClient — cheat detection, moderation, version rejection).</summary>
        Kicked = 3,

        /// <summary>The transport dropped without a notice (network loss, timeout, or a lost best-effort notice).</summary>
        ConnectionLost = 4,

        /// <summary>The server rejected the connection because the client's declared protocol version does not match (ADR-0028 version handshake).</summary>
        VersionMismatch = 5,
    }
}
