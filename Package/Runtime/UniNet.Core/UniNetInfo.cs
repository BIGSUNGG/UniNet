// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.23.0";   // 0.23.0 — FR-01~07 안정화: 승인/씬 준비 전 게임 RPC 즉시 드롭(ServerConnection.CanReceiveGameData), Unreliable 이동 중 주기 기준선(1.0s)·AOI 재진입 기존 인스턴스 기준선 갱신, RPC 컬렉션 코덱 경계(수신 List 복원·65535 상한 초과 명시 거부·페이로드 미소비 검증), 위치 무관 권위 TState 리컨사일(ReconcileState), 연기 큐 (NetId,SubId) 최신 전체 병합+상한 강제 송신·메인 큐 상한, 동봉 analyzer/CI 회귀 게이트(생성기 0.2.18). 와이어 형식 불변 — 핸드셰이크 기본 버전만 상승(ADR-0028)
    }
}
