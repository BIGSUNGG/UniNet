// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.24.0";   // 0.24.0 — ADR-0056 RpcSender 주입(비소유 ServerRpc 발신자 컨텍스트·와이어 불변)·ADR-0057 additive 씬 동기화(LoadSceneAdditive/UnloadScene·와이어 11 [mode][scene] 확장 — 구버전 클라는 핸드셰이크 버전 불일치로 거부, 생성기 0.3.0)
    }
}
