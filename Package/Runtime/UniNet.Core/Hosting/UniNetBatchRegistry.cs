using System;
using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Wire methodId → delta applier for batched replication (wire 13). Every generated assembly contributes its
    /// types at registration time; the assembly whose receiver wins the dispatch slot resolves appliers globally,
    /// so cross-assembly types keep working regardless of which copy received the batch.
    /// </summary>
    public static class UniNetBatchRegistry
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<int, UniNetDeltaApplier> Appliers = new();

        /// <summary>Registers (or overwrites) the applier for one replication methodId — idempotent.</summary>
        public static void Register(int methodId, UniNetDeltaApplier applier)
        {
            lock (Gate) Appliers[methodId] = applier;
        }

        /// <summary>Resolves the applier for one replication methodId.</summary>
        public static bool TryGet(int methodId, out UniNetDeltaApplier applier)
        {
            lock (Gate) return Appliers.TryGetValue(methodId, out applier);
        }
    }
}
