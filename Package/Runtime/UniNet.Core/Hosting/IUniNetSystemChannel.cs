namespace UniNet.Core.Hosting
{
/// <summary>시스템 메시지(Welcome·소유권·리플리케이션·스폰·파괴) 전송 계약 — 생성 서버 허브가 구현한다.</summary>
public interface IUniNetSystemChannel
{
    /// <summary>서버가 부여한 이 연결의 ID (AttachConnection 시 설정) — ServerRpc 발신자 식별용.</summary>
    long UniNetConnId { get; set; }

    /// <summary>클라이언트에 자신의 연결 ID를 알린다.</summary>
    void SendWelcome(long connId);

    /// <summary>오브젝트 소유자 변경을 알린다 (오브젝트 단위).</summary>
    void SendOwnerUpdate(ulong netId, long ownerConnId);

    /// <summary>서브오브젝트 리플리케이션 델타를 전송한다 (methodId는 타입별 생성 값).</summary>
    void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload);

    /// <summary>
    /// 동적 오브젝트 스폰을 전송한다 — 변환 7값으로 초기 배치를, 서브 수+서브별 typeKey/전체 상태로
    /// 오브젝트의 전 NetworkBehaviour를 클라가 생성·검증하도록 한다 (서브슬롯은 배열 순서 암시).
    /// </summary>
    void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
        byte subCount, ulong[] typeKeys, byte[][] states);

    /// <summary>동적 오브젝트 파괴를 전송한다 (오브젝트 전체).</summary>
    void SendDestroy(ulong netId);

    /// <summary>P4 시간 동기화 — 서버 권위 시각(UniNetTime 도메인, 초)을 클라에 전송한다 (주기적 브로드캐스트).</summary>
    void SendTimeSync(double serverTime);

    /// <summary>일반 RPC 페이로드 전송 (어셈블리 무관 송신 경로).</summary>
    void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode);
}

}
