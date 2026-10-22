// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.20.0";   // 0.20.0 — TIM-001: time-sync contract documented in UniNetTime XML docs (progress rate 1, bounded stall ≤ 0.25·|Δoffset| per EMA step, one-way-delay bias ≤ s2c, monotonic clamp, main-thread-only application) — documentation only, zero logic change; wire bytes unchanged. Handshake version string follows the package bump as usual (ADR-0028)
    }
}
