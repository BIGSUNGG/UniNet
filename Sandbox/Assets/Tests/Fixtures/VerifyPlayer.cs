using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// NetworkBehaviour fixture for round-trip verification — exercises all three RPC kinds + [Replicated]+RepNotify + _Validate.
    /// (also used for delta-logic unit tests without networking)
    /// </summary>
    public sealed partial class VerifyPlayer : NetworkBehaviour
    {
        [Replicated(Notify = nameof(OnScoreChanged))]
        public int Score = 100;

        /// <summary>Previous value received by the RepNotify callback.</summary>
        public int LastPrevScore = -999;

        /// <summary>Whether the RepNotify callback fired.</summary>
        public bool ScoreNotified;

        /// <summary>Server-side RpcPing execution count (validated calls only).</summary>
        public int ServerPingCount;

        /// <summary>Whether validation rejected a call (negative argument).</summary>
        public bool ValidateRejected;

        /// <summary>Whether the ClientRpc ran on the client.</summary>
        public bool ClientFxRan;

        /// <summary>Local Multicast execution count (ideally 1 server-local + 1 pure client — verifies no host double-execution).</summary>
        public int MulticastCount;

        private void OnScoreChanged(int prev)
        {
            LastPrevScore = prev;
            ScoreNotified = true;
        }

        [ServerRpc(Validate = true)]
        internal partial void RpcPing(int amount);

        private Task<bool> RpcPing_Validate(int amount)
            => Task.FromResult(amount > 0);

        private void RpcPing_Implementation(int amount)
        {
            if (amount <= 0) { ValidateRejected = true; return; }
            ServerPingCount++;
            Score -= amount;
            RpcFx();
            RpcDeath();
        }

        [ClientRpc(Delivery.Unreliable)]
        internal partial void RpcFx();

        private void RpcFx_Implementation()
            => ClientFxRan = true;

        [MulticastRpc]
        internal partial void RpcDeath();

        private void RpcDeath_Implementation()
            => MulticastCount++;

        // ── Message type support verification (MP [Message] + polymorphism) ──

        /// <summary>Last message the server received (for polymorphism checks).</summary>
        public PayloadMsg LastMsg;

        /// <summary>Whether the last message was of the derived type.</summary>
        public bool LastMsgWasDerived => LastMsg is DerivedPayloadMsg;

        /// <summary>Message field under replication.</summary>
        [Replicated(Notify = nameof(OnStateMsgChanged))]
        public PayloadMsg StateMsg = new PayloadMsg();

        /// <summary>Whether the StateMsg RepNotify callback fired.</summary>
        public bool StateMsgNotified;

        /// <summary>Previous reference received by the StateMsg RepNotify callback.</summary>
        public PayloadMsg LastStatePrev;

        [ServerRpc(Validate = true)]
        internal partial void RpcDeliver(PayloadMsg msg);

        private System.Threading.Tasks.Task<bool> RpcDeliver_Validate(PayloadMsg msg)
            => System.Threading.Tasks.Task.FromResult(msg != null);

        private void RpcDeliver_Implementation(PayloadMsg msg)
            => LastMsg = msg;

        private void OnStateMsgChanged(PayloadMsg prev)
        {
            StateMsgNotified = true;
            LastStatePrev = prev;
        }
    }
}
