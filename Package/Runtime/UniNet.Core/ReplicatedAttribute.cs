using System;

namespace UniNet.Core
{
    /// <summary>Marks a field as server-authoritative replicated state. Changes are synchronized to connected clients.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ReplicatedAttribute : Attribute
    {
        /// <summary>Name of a RepNotify callback method (use nameof). Invoked on clients when the value is changed over the network, receiving the previous value as its single argument.</summary>
        public string Notify { get; set; }

        /// <summary>
        /// Custom static serializer type — bypasses the default codec for this field. Must expose
        /// static void Write(ref MessageBufferWriter, in T) and static T Read(ref MessageBufferReader)
        /// matching the field type T (checked at compile time). An optional static bool Equals(in T, in T)
        /// replaces the default value comparison used for delta detection; null arguments never reach it
        /// (the framework handles null on either side). Also unlocks field types that are
        /// neither primitives nor [Message] (e.g. UnityEngine.Vector3).
        /// </summary>
        public Type Serializer { get; set; }

        /// <summary>Replication condition — set via the constructor (OwnerOnly, SkipOwner, InitialOnly combination).</summary>
        public ReplicateCondition Condition { get; }

        /// <summary>Replicates unconditionally.</summary>
        public ReplicatedAttribute() { }

        /// <summary>Replicates under the given condition — e.g. [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnX))].</summary>
        public ReplicatedAttribute(ReplicateCondition condition) => Condition = condition;
    }
}
