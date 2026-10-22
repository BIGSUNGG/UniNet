// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.16.0";   // 0.16.0 — RPC-002 containment: generator 0.2.15 wraps the OwnerUpdate (wire 2) and single Replicate receivers in fault guards — malformed payloads die at the handler boundary and are dropped observably (NetworkClient.FaultedPayloadCount) instead of escaping into DRPC dispatch; wire & protocol version unchanged
    }
}
