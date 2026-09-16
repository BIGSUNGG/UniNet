using System;

namespace UniNet.Core
{
    /// <summary>
    /// [Replicated] 필드의 리플리케이션 조건 — 생성자 인자로 지정하며 비트 조합이 가능하다.
    /// OwnerOnly|SkipOwner 동시 지정은 모순이라 컴파일 타임 진단(UNINET010)으로 거부된다.
    /// </summary>
    [Flags]
    public enum ReplicateCondition
    {
        /// <summary>조건 없음 — 모든 수신자에게 항상 전송.</summary>
        None = 0,

        /// <summary>소유 연결에만 전송 (UE COND_OwnerOnly 상당).</summary>
        OwnerOnly = 1,

        /// <summary>소유 연결을 제외한 전체에게 전송 (UE COND_SkipOwner 상당).</summary>
        SkipOwner = 2,

        /// <summary>스폰/첫 동기화 시에만 전송, 이후 델타 추적에서 제외 (UE COND_InitialOnly 상당).</summary>
        InitialOnly = 4,
    }
}
