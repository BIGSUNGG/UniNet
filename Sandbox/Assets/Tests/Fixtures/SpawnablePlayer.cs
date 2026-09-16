using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// 동적 스폰·조건부 리플리케이션 검증 픽스처 — 4종 조건(무조건·OwnerOnly·SkipOwner·InitialOnly) 필드 전부 사용.
    /// 필드 인덱스(델타 마스크 비트): 0=Score, 1=SecretHp, 2=TeamId, 3=SpawnSeed.
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

        /// <summary>SecretHp RepNotify 호출 여부.</summary>
        public bool SecretNotified;

        /// <summary>SecretHp RepNotify가 받은 이전값.</summary>
        public int LastPrevSecret;

        private void OnSecretChanged(int prev)
        {
            SecretNotified = true;
            LastPrevSecret = prev;
        }
    }
}
