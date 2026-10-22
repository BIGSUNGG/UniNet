// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.17.0";   // 0.17.0 — GEN-001 containment: UniNetDispatch.Add distinguishes an idempotent re-registration (same delegate method + mode) from a genuine collision — a different handler (or mode) on a taken user ID (>=64) is rejected via LogFault, first-wins, instead of silently overwriting; reserved wires (<64, e.g. 13/14) keep the cross-assembly silent overwrite; wire & protocol version unchanged
    }
}
