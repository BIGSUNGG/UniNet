using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// 사용자 정의 관련성 훅(IsNetworkRelevant 재정의) 검증 픽스처 — AllowedConnId와 같은 연결에만 관련.
    /// </summary>
    public sealed partial class FilteredRelay : NetworkBehaviour
    {
        [Replicated] public int Value;

        /// <summary>이 연결 ID에만 관련 (기본 -1 = 아무 연결에도 관련 없음).</summary>
        public long AllowedConnId = -1;

        public override bool IsNetworkRelevant(long viewerConnId) => viewerConnId == AllowedConnId;
    }
}
