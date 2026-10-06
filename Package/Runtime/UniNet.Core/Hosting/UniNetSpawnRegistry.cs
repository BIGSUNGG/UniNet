using System;
using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// NetworkBehaviour type registry — generated code registers every NetworkBehaviour type with its
    /// type identifier (FNV-1a 64 of the full name). Spawn messages carry these keys per sub-slot so the
    /// client can verify prefab composition (ADR-0024); object creation itself is prefab-identity based
    /// (UniNetPrefabTable) — this registry no longer creates anything.
    /// Registration happens on the main thread (assembly load, explicit registration); lookups also run on the
    /// main thread only (spawn processing).
    /// </summary>
    public static class UniNetSpawnRegistry
    {
        private static readonly Dictionary<Type, ulong> TypeKeys = new();

        /// <summary>Registers a type's spawn identifier (generated code only — typeKey is the compile-time full-name hash).</summary>
        public static void Register(Type type, ulong typeKey)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            TypeKeys[type] = typeKey;
        }

        /// <summary>Returns the type identifier of an instance type (0 = unregistered).</summary>
        public static ulong TypeKeyOf(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (TypeKeys.TryGetValue(t, out var key))
                    return key;
            return 0;
        }
    }
}
