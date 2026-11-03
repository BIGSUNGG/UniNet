using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// ADR-0056 fixture — trailing RpcSender injection on a non-ownership ServerRpc (chat/world-interaction shape).
    /// Callers keep the original signature; the generated dispatch injects the transport-assigned sender into
    /// _Implementation/_Validate.
    /// </summary>
    public sealed partial class SenderContextPlayer : NetworkBehaviour
    {
        /// <summary>Last chat message the implementation received.</summary>
        public string LastChat = "";

        /// <summary>ConnId the implementation saw (transport-assigned — the assertion target).</summary>
        public long ImplSenderConnId = -999;

        /// <summary>Whether the implementation saw a local-authority call.</summary>
        public bool ImplSenderIsLocal = true;

        /// <summary>PlayerNetId the implementation saw.</summary>
        public ulong ImplSenderPlayerNetId;

        /// <summary>ConnId the validate hook saw (-999 = never invoked).</summary>
        public long ValidateSenderConnId = -999;

        /// <summary>PlayerNetId the validate hook saw.</summary>
        public ulong ValidateSenderPlayerNetId;

        /// <summary>Gate for the validate hook (tests the skip path).</summary>
        public bool AllowChat = true;

        [ServerRpc(RequireOwnership = false, Validate = true)]
        internal partial void RpcChat(string message);

        private Task<bool> RpcChat_Validate(string message, RpcSender sender)
        {
            ValidateSenderConnId = sender.ConnId;
            ValidateSenderPlayerNetId = sender.PlayerNetId;
            return Task.FromResult(AllowChat && message != null);
        }

        private void RpcChat_Implementation(string message, RpcSender sender)
        {
            LastChat = message;
            ImplSenderConnId = sender.ConnId;
            ImplSenderIsLocal = sender.IsLocal;
            ImplSenderPlayerNetId = sender.PlayerNetId;
        }

        /// <summary>Ownership-enforced variant with a sender — the two contracts are orthogonal (ADR-0056).</summary>
        public long OwnedSenderConnId = -999;

        [ServerRpc]
        internal partial void RpcOwnedPing(int amount);

        private void RpcOwnedPing_Implementation(int amount, RpcSender sender)
            => OwnedSenderConnId = sender.ConnId;

        /// <summary>Implementation WITHOUT the trailing sender — old codegen output must stay identical (compat contract).</summary>
        public int PlainCount;

        [ServerRpc(RequireOwnership = false)]
        internal partial void RpcPlain(int amount);

        private void RpcPlain_Implementation(int amount)
            => PlainCount++;
    }
}
