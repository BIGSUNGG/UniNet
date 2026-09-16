using System;
using MessageProtocol.Serialize;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 타입별 리플리케이션 핸들 계약 — [Replicated] 필드가 있는 타입마다 코드 생성기가 구현·등록한다.
    /// 서버: 스냅샷 비교로 변경분만 직렬화(수신 그룹별 — OwnerOnly/SkipOwner). 클라: 델타 적용 + RepNotify(이전값) 호출.
    /// </summary>
    public abstract class UniNetReplicationHandler
    {
        /// <summary>이 타입 리플리케이션 전용 DRPC 메서드 ID (생성 시 할당).</summary>
        public int MethodId { get; protected set; }

        /// <summary>[Replicated] 필드가 있는가 (없으면 틭에서 제외된다).</summary>
        public bool HasFields { get; protected set; }

        /// <summary>서버 — 첫 등록 시 스냅샷을 초기화한다.</summary>
        public abstract void InitSnapshot(NetworkServer.ServerObjectEntry entry);

        /// <summary>클라 — 등록 시 이전값(Seen) 스냅샷을 초기화한다 (호스트 모드 prev 정확성).</summary>
        public abstract void InitClientSnapshot(object instance);

        /// <summary>
        /// 서버 — 스냅샷과 비교해 수신 그룹별 델타를 쓴다 (Owner = 소유 연결용, Others = 나머지용).
        /// 조건 없는 타입은 둘 다 같은 페이로드다. 변경 없으면 둘 다 null. 스냅샷은 이 호출에서 1회 갱신된다.
        /// </summary>
        public abstract (byte[] Owner, byte[] Others) CompareAndWriteDelta(NetworkServer.ServerObjectEntry entry);

        /// <summary>서버 — 스폰·후발 접속 캐치업용 전체 상태 페이로드 (InitialOnly 포함, 수신 그룹별 조건 필터링).</summary>
        public abstract byte[] WriteFull(object instance, bool isOwner);

        /// <summary>클라 — 델타를 적용하고 선언된 RepNotify(이전값)를 호출한다 (메인 스레드).</summary>
        public abstract void ApplyDelta(object instance, ref MessageBufferReader reader);
    }
}
