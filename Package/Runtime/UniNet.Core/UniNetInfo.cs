// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.19.0";   // 0.19.0 — LIF-001: server-restart hygiene — ServerStopAsync sweeps dynamic spawns (sync-stop parity), a failed listen rolls the environment back (no zombie server), and registration tracking uses per-instance generation stamps so scene objects re-register after a stop → start restart. Wire bytes are unchanged; the handshake version string follows the package bump as usual (ADR-0028)
    }
}
