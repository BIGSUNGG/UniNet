using System.Collections.Generic;
using System;
using UniNet.Core.Hosting;

namespace UniNet.Core.Hosting
{
    /// <summary>Per-type replication handler registry — generated code registers handlers on assembly load.</summary>
    public static class UniNetTypeRegistry
    {
        private static readonly Dictionary<Type, UniNetReplicationHandler> Handlers = new();

        /// <summary>Registers a type's replication handler.</summary>
        public static void Register(Type type, UniNetReplicationHandler handler)
        {
            Handlers[type] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>Returns a type's handler — walks the base-type chain so [Replicated] members declared on an intermediate base class are found.</summary>
        public static UniNetReplicationHandler Find(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (Handlers.TryGetValue(t, out var h))
                    return h;
            return null;
        }
    }
}
