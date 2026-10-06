using System.Collections.Generic;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Baked prefab table — the edit-time registry of every NetworkSpawnable-marked prefab (ADR-0024).
    /// NetworkSpawnablePostprocessor regenerates this asset (Assets/Resources/UniNet/UniNetPrefabTable.asset)
    /// whenever marked prefabs change; at runtime clients resolve spawn-message prefab ids through it.
    /// Both ends must ship the same prefab assets — the table is per-project baked, the ids are prefab GUID hashes.
    /// </summary>
    public sealed class UniNetPrefabTable : ScriptableObject
    {
        /// <summary>One entry per spawnable prefab — id = prefab GUID hash (stamped on its NetworkSpawnable).</summary>
        [System.Serializable]
        public struct Entry
        {
            public ulong Id;
            public GameObject Prefab;
        }

        [SerializeField] private List<Entry> _entries = new();

        private static Dictionary<ulong, GameObject> _cache;

        /// <summary>Populates the lookup cache from the baked table asset (once). Must run before any override —
        /// a test/batch registration creating an empty cache first would otherwise shadow the baked entries forever.</summary>
        private static void EnsureCache()
        {
            if (_cache != null) return;
            _cache = new Dictionary<ulong, GameObject>();
            var table = Resources.Load<UniNetPrefabTable>("UniNet/UniNetPrefabTable");
            if (table != null)
                foreach (var e in table._entries)
                    if (e.Prefab != null && e.Id != 0)
                        _cache[e.Id] = e.Prefab;
        }

        /// <summary>Resolves a spawn prefab id to its prefab asset (null = unknown id — not marked on this end).</summary>
        public static GameObject Lookup(ulong prefabId)
        {
            if (prefabId == 0) return null;
            EnsureCache();
            return _cache.TryGetValue(prefabId, out var prefab) ? prefab : null;
        }

        /// <summary>Test/batch-editor injection — makes a runtime-created prefab resolvable without a baked asset.
        /// Real games must rely on the baked table; this exists so editor tests and 2-process runners can assemble
        /// prefabs programmatically (ADR-0024 verification paths).</summary>
        internal static void RegisterForTest(ulong prefabId, GameObject prefab)
        {
            if (prefabId == 0 || prefab == null) return;
            EnsureCache();   // load the baked table first — an empty override cache would shadow real entries
            _cache[prefabId] = prefab;
        }

        /// <summary>Clears the lookup cache — the baked table is re-read on the next Lookup (test isolation).</summary>
        internal static void ResetCache()
        {
            _cache = null;
        }

        /// <summary>Bakes the entry list (editor postprocessor only — keeps the serialized asset in sync with marked prefabs).</summary>
        internal void Bake(List<Entry> entries)
        {
            _entries = entries;
        }
    }
}
