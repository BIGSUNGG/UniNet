namespace UniNet.Core.Hosting
{
/// <summary>시스템 메시지(Welcome·소유권·리플리케이션) 전송 계약 — 생성 서버 허브가 구현한다.</summary>
public interface IUniNetSystemChannel
{
    /// <summary>서버가 부여한 이 연결의 ID (AttachConnection 시 설정) — ServerRpc 발신자 식별용.</summary>
    long UniNetConnId { get; set; }

    /// <summary>클라이언트에 자신의 연결 ID를 알린다.</summary>
    void SendWelcome(long connId);

    /// <summary>오브젝트 소유자 변경을 알린다.</summary>
    void SendOwnerUpdate(ulong netId, long ownerConnId);

    /// <summary>리플리케이션 델타를 전송한다 (methodId는 타입별 생성 값).</summary>
    void SendReplicate(ulong netId, int methodId, byte[] payload);

    /// <summary>동적 오브젝트 스폰을 전송한다 — typeKey로 클라가 생성할 타입을, 변환 7값과 state(전체 상태 페이로드)로 초기 배치를 정한다.</summary>
    void SendSpawn(ulong netId, ulong typeKey, float px, float py, float pz, float qx, float qy, float qz, float qw, byte[] state);

    /// <summary>동적 오브젝트 파괴를 전송한다.</summary>
    void SendDestroy(ulong netId);

    /// <summary>일반 RPC 페이로드 전송 (어셈블리 무관 송신 경로).</summary>
    void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode);
}

}
