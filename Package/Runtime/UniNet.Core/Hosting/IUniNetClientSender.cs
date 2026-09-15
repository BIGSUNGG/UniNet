using DRPC;

namespace UniNet.Core.Hosting
{
    /// <summary>클라 허브 송신 계약 — 생성 클라 허브가 구현하고 환경 슬롯에 등록한다 (어셈블리 무관 송신).</summary>
    public interface IUniNetClientSender
    {
        /// <summary>서버로 RPC 페이로드를 전송한다 (페이로드에 netId 포함).</summary>
        void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode);
    }
}
