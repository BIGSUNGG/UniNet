using System;

namespace UniNet.Core
{
    /// <summary>
    /// Replication conditions for [Replicated] fields — set via the attribute constructor, combinable as a bit mask.
    /// Combining OwnerOnly|SkipOwner is contradictory and rejected at compile time with diagnostic UNINET010.
    /// </summary>
    [Flags]
    public enum ReplicateCondition
    {
        /// <summary>No condition — always sent to every receiver.</summary>
        None = 0,

        /// <summary>Send only to the owning connection (UE COND_OwnerOnly equivalent).</summary>
        OwnerOnly = 1,

        /// <summary>Send to everyone except the owning connection (UE COND_SkipOwner equivalent).</summary>
        SkipOwner = 2,

        /// <summary>Sent only on spawn/initial sync, then excluded from delta tracking (UE COND_InitialOnly equivalent).</summary>
        InitialOnly = 4,
    }
}
