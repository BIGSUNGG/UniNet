// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.12.0";   // 0.12.0 — destroy-time owner-cache cleanup (OWN-003): Unregister clears the netId's cached owner so GetOwner returns 0 ("unassigned") after a network destroy
    }
}
