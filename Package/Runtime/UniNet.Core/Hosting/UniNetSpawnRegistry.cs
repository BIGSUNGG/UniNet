using System;
using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Dynamic spawn type registry — generated code registers every NetworkBehaviour type with a default
    /// factory (empty GameObject + AddComponent) and a type identifier (FNV-1a 64 of the full name). Use
    /// RegisterPrefab to swap a type's factory for prefab instantiation.
    /// Registration happens on the main thread (assembly load, explicit registration); lookups also run on the
    /// main thread only (spawn processing).
    /// </summary>
    public static class UniNetSpawnRegistry
    {
        private static readonly Dictionary<ulong, Func<object>> Factories = new();
        private static readonly Dictionary<ulong, Type> FactoryTypes = new();
        private static readonly Dictionary<Type, ulong> TypeKeys = new();

        /// <summary>Registers a type's spawn factory (generated code only — typeKey is the compile-time full-name hash).</summary>
        public static void Register(Type type, ulong typeKey, Func<object> factory)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            Factories[typeKey] = factory;
            FactoryTypes[typeKey] = type;
            TypeKeys[type] = typeKey;
        }

        /// <summary>Replaces a registered type's factory — makes client-side creation instantiate a prefab (RegisterPrefab).</summary>
        public static void Override(Type type, Func<object> factory)
        {
            if (TypeKeys.TryGetValue(type, out var key)) Factories[key] = factory ?? throw new ArgumentNullException(nameof(factory));
            else throw new InvalidOperationException($"타입 {type.FullName} 은(는) UniNet 스폰 등록이 없습니다 — NetworkBehaviour 파생 partial 타입인지 확인하세요.");
        }

        /// <summary>Returns the spawn identifier of an instance type (0 = unregistered).</summary>
        public static ulong TypeKeyOf(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (TypeKeys.TryGetValue(t, out var key))
                    return key;
            return 0;
        }

        /// <summary>Creates an instance from an identifier (null = unregistered).</summary>
        public static object Create(ulong typeKey)
            => Factories.TryGetValue(typeKey, out var factory) ? factory() : null;

        /// <summary>Returns the registered type for an identifier (null = unregistered) — used to restore missing subs (AddComponent) on client spawn.</summary>
        public static Type TypeOf(ulong typeKey)
            => FactoryTypes.TryGetValue(typeKey, out var type) ? type : null;
    }
}
