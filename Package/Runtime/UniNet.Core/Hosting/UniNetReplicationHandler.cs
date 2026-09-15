using System;
using MessageProtocol.Serialize;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 타입별 리플리케이션 핸들 계약 — [Replicated] 필드가 있는 타입마다 코드 생성기가 구현·등록한다.
    /// 서버: 스냅샷 비교로 변경분만 직렬화. 클라: 델타 적용 + RepNotify(이전값) 호출.
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

        /// <summary>서버 — 스냅샷과 비교해 변경분만 쓴다. 변경 없으면 빈 배열.</summary>
        public abstract byte[] CompareAndWriteDelta(NetworkServer.ServerObjectEntry entry);

        /// <summary>클라 — 델타를 적용하고 선언된 RepNotify(이전값)를 호출한다 (메인 스레드).</summary>
        public abstract void ApplyDelta(object instance, ref MessageBufferReader reader);
    }
}
