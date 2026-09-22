using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// Fixture for dynamic spawn and conditional replication — uses all four conditions (unconditional, OwnerOnly, SkipOwner, InitialOnly).
    /// Field indexes (delta mask bits): 0=Score, 1=SecretHp, 2=TeamId, 3=SpawnSeed.
    /// </summary>
    public sealed partial class SpawnablePlayer : NetworkBehaviour
    {
        [Replicated]
        public int Score = 1;

        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnSecretChanged))]
        public int SecretHp = 50;

        [Replicated(ReplicateCondition.SkipOwner)]
        public int TeamId = 3;

        [Replicated(ReplicateCondition.InitialOnly)]
        public int SpawnSeed = 7;

        /// <summary>Whether the SecretHp RepNotify callback fired.</summary>
        public bool SecretNotified;

        /// <summary>Previous value received by the SecretHp RepNotify callback.</summary>
        public int LastPrevSecret;

        private void OnSecretChanged(int prev)
        {
            SecretNotified = true;
            LastPrevSecret = prev;
        }
    }
}
