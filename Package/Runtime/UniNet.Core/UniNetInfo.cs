// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.21.0";   // 0.21.0 — TRS-001: NetworkTransform opt-in SyncRotation/SyncScale — 7 replicated float fields (rotation quaternion + scale) widen the NetworkTransform spawn/delta wire and full-state masks, so mixed-version ends cannot interoperate; the full-state spawn carries the Awake-seeded pose so late joiners start at the current rotation. Handshake version string follows the package bump as usual (ADR-0028)
    }
}
