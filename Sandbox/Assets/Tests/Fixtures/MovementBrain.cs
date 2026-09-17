using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>다중 컴포넌트 검증 픽스처 1 — 이동 담당 서브오브젝트 (HealthTank와 같은 오브젝트에 붙는다).</summary>
    public sealed partial class MovementBrain : NetworkBehaviour
    {
        [Replicated]
        public int Speed = 1;

        /// <summary>RpcMove 실행 횟수 (라우팅 검증용).</summary>
        public int MoveCalls;

        [ServerRpc]
        internal partial void RpcMove(int amount);

        private void RpcMove_Implementation(int amount)
        {
            MoveCalls++;
            Speed += amount;
        }
    }
}
