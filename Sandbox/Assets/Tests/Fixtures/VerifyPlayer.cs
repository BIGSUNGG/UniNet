using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// 왕복 검증용 NetworkBehaviour 픽스처 — RPC 3종 + [Replicated]+RepNotify + _Validate 전부 사용.
    /// (네트워킹 없이 델타 로직 단위 테스트에도 쓰인다)
    /// </summary>
    public sealed partial class VerifyPlayer : NetworkBehaviour
    {
        [Replicated(Notify = nameof(OnScoreChanged))]
        public int Score = 100;

        /// <summary>RepNotify가 받은 이전값.</summary>
        public int LastPrevScore = -999;

        /// <summary>RepNotify 호출 여부.</summary>
        public bool ScoreNotified;

        /// <summary>서버 측 RpcPing 실행 횟수 (검증 통과분만).</summary>
        public int ServerPingCount;

        /// <summary>검증 거부가 일어났는가 (음수 인자).</summary>
        public bool ValidateRejected;

        /// <summary>클라 측 ClientRpc 실행 여부.</summary>
        public bool ClientFxRan;

        /// <summary>Multicast 로컬 실행 횟수 (서버 로컬 1 + 순수 클라 1이 이상적 — 호스트 중복 방지 확인).</summary>
        public int MulticastCount;

        private void OnScoreChanged(int prev)
        {
            LastPrevScore = prev;
            ScoreNotified = true;
        }

        [ServerRpc]
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

        // ── 메시지 타입 지원 검증 (MP [Message] + 다형성) ──

        /// <summary>서버가 마지막으로 받은 메시지 (다형성 확인용).</summary>
        public PayloadMsg LastMsg;

        /// <summary>마지막 메시지가 자식 타입이었는가.</summary>
        public bool LastMsgWasDerived => LastMsg is DerivedPayloadMsg;

        /// <summary>리플리케이션 대상 메시지 필드.</summary>
        [Replicated(Notify = nameof(OnStateMsgChanged))]
        public PayloadMsg StateMsg = new PayloadMsg();

        /// <summary>StateMsg RepNotify 호출 여부.</summary>
        public bool StateMsgNotified;

        /// <summary>StateMsg RepNotify가 받은 이전 참조.</summary>
        public PayloadMsg LastStatePrev;

        [ServerRpc]
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
