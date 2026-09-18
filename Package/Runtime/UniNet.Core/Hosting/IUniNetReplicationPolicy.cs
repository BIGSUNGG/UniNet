namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 오브젝트 리플리케이션 정책 계약 — P3 (가시성·우선순위·휴면·전송 주기).
    /// 서버 틱이 오브젝트별 전송 여부·순서·주기를 결정할 때 읽는다. NetworkBehaviour가 구현하며
    /// 사용자는 파생 클래스에서 가상 멤버를 재정의해 정책을 바꾼다. 미구현 오브젝트는 기본 정책(매 틱·무제한)을 따른다.
    /// </summary>
    public interface IUniNetReplicationPolicy
    {
        /// <summary>리플리케이션 우선순위 (UE NetPriority 상응, 기본 1). 예산 부족 시 높은 값부터 전송되고, 대기 시간이 길어질수록 기아 보정으로 상승한다.</summary>
        float NetworkPriority { get; }

        /// <summary>오브젝트별 전송 주기(Hz, UE NetUpdateFrequency 상응). 0 = 매 틱 무제한 (기본). 마지막 전송 후 1/Hz 경과 전에는 변경 비교를 건너뛴다.</summary>
        float NetworkUpdateFrequencyHz { get; }

        /// <summary>가시성 컬 거리(월드 단위 반경, UE NetCullDistance 상응). 0 = 거리 컬 미적용 (기본). 뷰어 위치는 서버 SetViewerPosition 으로 제공된다.</summary>
        float NetworkCullDistance { get; }

        /// <summary>휴면 여부 (UE NetDormancy 단순화 — Awake/DormantAll 2상태). true면 델타 비교·전송을 중단한다. FlushNetworkDormancy로 깨우면 누적 변경분이 전송된다.</summary>
        bool IsNetworkDormant { get; }

        /// <summary>연결별 사용자 정의 관련성 판정 (UE IsNetRelevantFor 상응). false면 해당 연결에 델타·캐치업·스폰을 보내지 않는다.</summary>
        bool IsNetworkRelevant(long viewerConnId);
    }
}
