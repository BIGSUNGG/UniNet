// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.18.0";   // 0.18.0 — GEN-003: enum RPC/Replicated parameters ride their underlying integer codec (explicit casts emitted by the generator); the wire BYTES are identical to the underlying primitive, but the handshake version string follows the package bump as usual (ADR-0028)
    }
}
