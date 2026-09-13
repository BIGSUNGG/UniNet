// 배선 확인용 플레이스홀더 — 순수 C# 코어(no engine references)가 Unity 파이프라인에서
// 컴파일되는지 검증한다. P1 착수 시 실제 코어 타입으로 대체한다.
namespace UniNet.Core
{
    /// <summary>UniNet 코어 어셈블리 식별 정보.</summary>
    public static class UniNetInfo
    {
        /// <summary>패키지 버전 (package.json과 동일하게 유지).</summary>
        public const string Version = "0.1.0";
    }
}
