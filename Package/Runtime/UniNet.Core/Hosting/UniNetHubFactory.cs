using Communication.Shared.Channels;
using DRPC.Shared.Network;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 코드 생성기가 내놓은 허브 쌍의 팩토리 계약 — 사용자 어셈블리의 생성 코드가 구현해 등록한다.
    /// 생성 허브는 DRPC 허브 베이스 + HubSessionFactory 조립을 그대로 사용한다.
    /// </summary>
    public abstract class UniNetHubFactory
    {
        /// <summary>연결별 서버 허브를 생성한다 (채널 위에 RUDP 세션 조립 포함).</summary>
        public abstract HubBase CreateServerHub(IMessageChannel channel);

        /// <summary>클라이언트 허브를 생성한다.</summary>
        public abstract HubBase CreateClientHub(IMessageChannel channel);
    }
}
