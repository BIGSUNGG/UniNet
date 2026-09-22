using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>Multi-component test fixture 1 — movement sub-object (attached to the same object as HealthTank).</summary>
    public sealed partial class MovementBrain : NetworkBehaviour
    {
        [Replicated]
        public int Speed = 1;

        /// <summary>Number of times RpcMove ran (for routing verification).</summary>
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
