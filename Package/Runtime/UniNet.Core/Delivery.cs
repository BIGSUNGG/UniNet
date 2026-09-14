namespace UniNet.Core
{
    /// <summary>RPC 전달 모드 — 신뢰성·순서 보장 수준. DRPC 전달 모드 매핑은 구현 시 확정.</summary>
    public enum Delivery
    {
        /// <summary>신뢰 + 순서 보장 (기본값). 상태 변경·중요 이벤트용.</summary>
        ReliableOrdered,

        /// <summary>비신뢰. 빈도 높고 유실 허용 데이터(이동, FX)용.</summary>
        Unreliable,
    }
}
