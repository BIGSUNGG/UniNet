// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.24.2";   // 0.24.2 — 원격 표본 시각을 도착시각이 아니라 박자로(같은 프레임 델타 쌍이 1ms 조각이 되던 문제)·적응 보간 지연의 연속화·하한·관측 노출·거리만으로 순간이동을 판정하지 않음(빠른 정상 이동이 버퍼를 지우던 문제). 생성기·와이어 형식 불변
    }
}
