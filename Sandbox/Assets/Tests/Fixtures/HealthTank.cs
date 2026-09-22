using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>Multi-component test fixture 2 — health sub-object. Its OwnerOnly field also verifies per-slot replicate conditions.</summary>
    public sealed partial class HealthTank : NetworkBehaviour
    {
        [Replicated(ReplicateCondition.OwnerOnly)]
        public int Armor = 5;

        /// <summary>Number of times RpcHeal ran (for routing verification).</summary>
        public int HealCalls;

        /// <summary>Opt-out example (see ADR-0016) — RequireOwnership=false for an RPC any client may report healing through.</summary>
        [ServerRpc(RequireOwnership = false)]
        internal partial void RpcHeal(int amount);

        private void RpcHeal_Implementation(int amount)
        {
            HealCalls++;
            Armor += amount;
        }
    }
}
