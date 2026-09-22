using System;
using MessageProtocol.Serialize;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Per-type replication handler contract — the code generator implements and registers one for every type
    /// with [Replicated] fields. Server: compares snapshots to serialize only the changed part, per receive
    /// group (OwnerOnly/SkipOwner). Client: applies the delta and fires RepNotify (previous value).
    /// Each NetworkBehaviour on an object (sub-object) has its own handler (ADR-0010).
    /// </summary>
    public abstract class UniNetReplicationHandler
    {
        /// <summary>DRPC method ID dedicated to this type's replication (assigned at generation time).</summary>
        public int MethodId { get; protected set; }

        /// <summary>Whether the type has any [Replicated] fields (otherwise it is excluded from ticks).</summary>
        public bool HasFields { get; protected set; }

        /// <summary>Server — initializes the snapshot on first registration.</summary>
        public abstract void InitSnapshot(NetworkServer.SubObjectEntry sub);

        /// <summary>Client — initializes the previous-value (seen) snapshot on registration (for accurate host-mode prev values).</summary>
        public abstract void InitClientSnapshot(object instance);

        /// <summary>
        /// Server — compares against the snapshot and writes a delta per receive group (Owner = for the owning
        /// connection, Others = for the rest). For types without conditions both payloads are identical. Both
        /// are null when nothing changed. The snapshot is updated once inside this call.
        /// </summary>
        public abstract (byte[] Owner, byte[] Others) CompareAndWriteDelta(NetworkServer.SubObjectEntry sub);

        /// <summary>Server — full state payload for spawns and late-join catch-up (includes InitialOnly, filtered per receive-group conditions).</summary>
        public abstract byte[] WriteFull(object instance, bool isOwner);

        /// <summary>Client — applies a delta and invokes the declared RepNotify (previous value) on the main thread.</summary>
        public abstract void ApplyDelta(object instance, ref MessageBufferReader reader);
    }
}
