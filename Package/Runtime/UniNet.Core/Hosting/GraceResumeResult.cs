namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Terminal outcome of a grace-session resume (REC-001, wire 10 → wire 14) — returned by
    /// <see cref="NetworkServer.ResumeSession"/> and reported to the requesting client. Every failure leaves the
    /// session unconsumed (ADR-0041): a probing or racing client can never burn a victim's grace window.
    /// <see cref="SessionNotFound"/> carries a retryable window on the wire — the detach race (a reconnect arriving
    /// before the server noticed the old connection died) heals by re-sending, see
    /// <see cref="NetworkClient.TickResumeRetry"/>.
    /// </summary>
    public enum GraceResumeResult : byte
    {
        /// <summary>No result yet — the client has not received a wire-14 message (also the initial state).</summary>
        None = 0,

        /// <summary>The token matched a live, unexpired session and ownership was transferred to this connection.</summary>
        Reattached = 1,

        /// <summary>No session with this token exists YET — either the detach race (the grace session is not saved
        /// until the server observes the old connection's death) or a bogus token; indistinguishable server-side.
        /// Retryable while the grace window could still save it (retryableRemainingMs on the wire).</summary>
        SessionNotFound = 2,

        /// <summary>The session existed but its grace window has closed — the sweep owns removal and orphan reassignment. Terminal.</summary>
        Expired = 3,

        /// <summary>The token does not match any session and retrying cannot help: malformed/empty presentation,
        /// an invalid/dead connection id, or the session was already consumed by an earlier resume (double-consume
        /// loser). Terminal.</summary>
        UnknownToken = 4,
    }
}
