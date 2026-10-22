// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.22.0";   // 0.22.0 — GEN-002/GEN-004: 중첩 NetworkBehaviour 지원(생성 partial이 중첩 선언에 병합), 제네릭 NB UNINET016 오류 진단, RPC 인자 Vector2/3/4·Quaternion 비압축 float 분해 + 1차원 T[]/List<T>(null 플래그 + UInt16 길이 접두) 지원 — 생성기 0.2.17. 런타임 코드 무변경이라 와이어 호환성 영향 없음(신규 인자 타입은 신규 페이로드). Handshake version string follows the package bump as usual (ADR-0028)
    }
}
