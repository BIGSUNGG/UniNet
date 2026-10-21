// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.11.0";   // 0.11.0 — ownership-transfer baseline (OWN-002): SetOwner/resume/adoption re-send a state-only baseline to the incoming and outgoing owner so conditional fields (OwnerOnly/SkipOwner/InitialOnly) stay correct
    }
}
