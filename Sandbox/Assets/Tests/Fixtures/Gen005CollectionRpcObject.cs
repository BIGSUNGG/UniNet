using System.Collections.Generic;
using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// FR-003 — RPC collection boundary fixtures: a non-empty collection followed by a trailing scalar argument
    /// (round-trip + wire contract + unconsumed-bytes probes) and the 65535/65536 length-limit probes for arrays
    /// and Lists. ServerRpc so the same full receive path (ownership gate → decode → QueueOnMain) is exercised.
    /// </summary>
    public sealed partial class Gen005CollectionRpcObject : NetworkBehaviour
    {
        /// <summary>RpcListTail execution count (truncated/trailed payloads must never bump this).</summary>
        public int ListTailCalls;

        /// <summary>Last List received by RpcListTail.</summary>
        public List<int> LastValues = new List<int>();

        /// <summary>Trailing argument received after the List — misaligned decode would corrupt this.</summary>
        public int LastTail;

        [ServerRpc]
        internal partial void RpcListTail(List<int> values, int tail);

        private void RpcListTail_Implementation(List<int> values, int tail)
        {
            ListTailCalls++;
            LastValues = values;
            LastTail = tail;
        }

        [ServerRpc]
        internal partial void RpcArrTail(int[] values, int tail);

        private void RpcArrTail_Implementation(int[] values, int tail)
        {
            // count-only probe — element restoration is covered by the List path (same codec helpers)
        }
    }
}
