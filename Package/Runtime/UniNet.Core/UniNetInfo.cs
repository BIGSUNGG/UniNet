// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.15.0";   // 0.15.0 — targeted ClientRpc (RPC-001): generator 0.2.14 emits {Name}To(long connId, args) / {Name}ToOwner(args) companions per [ClientRpc] — same methodId/payload/delivery as the broadcast (encode reuse), wire & protocol version unchanged
    }
}
