// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.14.0";   // 0.14.0 — opt-in server input expiry (MOV-003): MoveSimulation ctor option inputExpirySteps (default 0 = hold-last forever, today's behavior); after the grace window of inputless server steps the held input swaps to default(TInput) exactly once and then applies idempotently until a fresh adoption; the idle clock zeroes at OnTeleport / ResetOwnershipStream / DropPendingHistory
    }
}
