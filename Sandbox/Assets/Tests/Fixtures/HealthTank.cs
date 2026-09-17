using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>다중 컴포넌트 검증 픽스처 2 — 체력 담당 서브오브젝트. OwnerOnly 조건 필드로 서브별 조건도 함께 검증한다.</summary>
    public sealed partial class HealthTank : NetworkBehaviour
    {
        [Replicated(ReplicateCondition.OwnerOnly)]
        public int Armor = 5;

        /// <summary>RpcHeal 실행 횟수 (라우팅 검증용).</summary>
        public int HealCalls;

        [ServerRpc]
        internal partial void RpcHeal(int amount);

        private void RpcHeal_Implementation(int amount)
        {
            HealCalls++;
            Armor += amount;
        }
    }
}
