using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// Fixture for the custom relevancy hook (IsNetworkRelevant override) — relevant only to the connection matching AllowedConnId.
    /// </summary>
    public sealed partial class FilteredRelay : NetworkBehaviour
    {
        [Replicated] public int Value;

        /// <summary>Only this connection ID is relevant (default -1 = relevant to no connection).</summary>
        public long AllowedConnId = -1;

        public override bool IsNetworkRelevant(long viewerConnId) => viewerConnId == AllowedConnId;
    }
}
