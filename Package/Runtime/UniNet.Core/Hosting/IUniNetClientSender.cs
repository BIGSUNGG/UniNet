using DRPC;

namespace UniNet.Core.Hosting
{
    /// <summary>Send contract implemented by the generated client hub and registered into the environment
    /// slot — lets any assembly send to the server without referencing it.</summary>
    public interface IUniNetClientSender
    {
        /// <summary>Sends an RPC payload to the server (the payload already includes the target netId).</summary>
        void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode);
    }
}
