using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniNet.Core.Hosting;
using UnityEditor;
using UnityEngine;

namespace UniNet.Unity.Editor
{
    /// <summary>
    /// Maintains the spawnable-prefab registry at edit time (ADR-0024):
    /// 1) stamps every NetworkSpawnable marker with the FNV-1a 64 hash of its prefab asset GUID,
    /// 2) validates marked prefabs (root must carry at least one NetworkBehaviour; markers off the root are ignored),
    /// 3) regenerates the baked prefab table (Assets/Resources/UniNet/UniNetPrefabTable.asset) that clients
    ///    use at runtime to resolve spawn-message prefab ids.
    /// Runs on prefab import/save; the table is also rebuilt on domain reload so deleted prefabs leave no stale entries.
    /// </summary>
    public sealed class NetworkSpawnablePostprocessor : AssetPostprocessor
    {
        private const string TablePath = "Assets/Resources/UniNet/UniNetPrefabTable.asset";

        static NetworkSpawnablePostprocessor()
        {
            EditorApplication.delayCall += () => Rebuild(null);   // startup rebuild — catches prefabs deleted since the last import
        }

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var touched = importedAssets.Concat(movedAssets).Concat(deletedAssets).Where(p => p.EndsWith(".prefab")).ToList();
            if (touched.Count > 0)   // deleted .prefabs are already in touched — nothing else can change spawnable roots
                Rebuild(touched);
        }

        /// <summary>Stamps marked prefabs and regenerates the baked table. Pass paths to limit stamping; the table always rebuilds from a fresh scan.
        /// The in-memory table updates synchronously — runtime Lookups in the same editor session see the fresh entries immediately —
        /// while disk persistence is deferred one tick (SaveAssets inside the import pipeline risks re-entrant imports).</summary>
        internal static void Rebuild(List<string> prefabPaths)
        {
            StampPrefabs(prefabPaths);
            BakeTable();
        }

        private static void StampPrefabs(List<string> prefabPaths)
        {
            bool dirty = false;

            if (prefabPaths != null)
            {
                foreach (var path in prefabPaths)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;   // deleted or not a GameObject — table rebuild below drops it
                    foreach (var marker in prefab.GetComponentsInChildren<NetworkSpawnable>(true))
                    {
                        if (marker.transform != prefab.transform)
                        {
                            Debug.LogError($"[UniNet] NetworkSpawnable는 프리팹 루트에만 붙일 수 있다 — 무시됨: {path} ({marker.gameObject.name})", marker.gameObject);
                            continue;
                        }
                        if (prefab.GetComponent<NetworkBehaviour>() == null)
                        {
                            Debug.LogError($"[UniNet] spawnable 프리팹 루트에는 NetworkBehaviour가 최소 1개 필요하다 — NetworkInstantiate 대상이 될 수 없다: {path}", prefab);
                            continue;
                        }
                        ulong id = Fnv1a.Hash64(AssetDatabase.AssetPathToGUID(path));
                        var so = new SerializedObject(marker);
                        var prop = so.FindProperty("_prefabId");
                        if (prop != null && prop.ulongValue != id)
                        {
                            prop.ulongValue = id;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            dirty = true;
                        }
                    }
                }
            }
            if (dirty) AssetDatabase.SaveAssets();
        }

        private static void BakeTable()
        {
            // Bake the table from a fresh scan — idempotent, drops deleted/duplicate prefabs.
            var entries = new List<UniNetPrefabTable.Entry>();
            var ids = new HashSet<ulong>();
            // Scan all prefab paths directly instead of FindAssets("t:NetworkSpawnable") — the type search index
            // updates asynchronously in import workers, so a bake right after creating/importing prefabs sees stale
            // (empty) results. ponytail: full-path scan is O(all assets) — cold path (prefab import/save) only;
            // revisit a cached index if multi-thousand-asset projects make this hurt.
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.EndsWith(".prefab")) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var marker = prefab.GetComponent<NetworkSpawnable>();   // root only — off-root markers are rejected above
                if (marker == null) continue;
                ulong id = Fnv1a.Hash64(AssetDatabase.AssetPathToGUID(path));
                if (!ids.Add(id))
                {
                    Debug.LogError($"[UniNet] 프리팹 아이디 중복 (GUID 해시 충돌 — Unity 자산 이상): {path} id={id}", prefab);
                    continue;
                }
                entries.Add(new UniNetPrefabTable.Entry { Id = id, Prefab = prefab });
            }
            entries.Sort((a, b) => a.Id.CompareTo(b.Id));   // deterministic asset content — churn-free diffs

            var table = AssetDatabase.LoadAssetAtPath<UniNetPrefabTable>(TablePath);
            var isNew = table == null;
            if (isNew)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TablePath)!);
                table = ScriptableObject.CreateInstance<UniNetPrefabTable>();
                AssetDatabase.CreateAsset(table, TablePath);   // registers the asset now — Resources.Load sees it this session
            }
            table.Bake(entries);
            EditorUtility.SetDirty(table);
            UniNetPrefabTable.ResetCache();   // runtime lookup cache — rebuilt from the fresh in-memory table on next use
            EditorApplication.delayCall += () => { EditorUtility.SetDirty(table); AssetDatabase.SaveAssets(); };   // persist off the import pipeline

            Debug.Log($"[UniNet] spawnable 프리팹 테이블 갱신 — {entries.Count}개 등록 ({TablePath}){(isNew ? " 신규" : "")}");
        }
    }
}
