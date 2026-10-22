#if UNITY_EDITOR
// Guard, not an Editor-folder platform split: this file sits inside the all-platforms Arena assembly (asmdefs
// ignore the special Editor folder rule), and a player build could not resolve UnityEditor here. ARENA-001 —
// the Arena demo is now built for players, so the bake step must stay editor-only at compile time too.
using Arena;
using UnityEditor;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Arena.Editor
{
    /// <summary>Bakes the Arena demo's spawnable player prefab (ADR-0024) — run via
    /// `-executeMethod UniNet.Arena.Editor.ArenaPrefabBaker.Bake`. Lives inside the Arena assembly so it can see
    /// ArenaPlayer; the fixture prefabs are baked by UniNet.Tests.TestPrefabBaker instead.</summary>
    public static class ArenaPrefabBaker
    {
        public static void Bake()
        {
            const string path = "Assets/Resources/Arena/ArenaPlayerPrefab.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                var go = new GameObject("ArenaPlayerPrefab");
                go.AddComponent<ArenaPlayer>();
                go.AddComponent<NetworkSpawnable>();
                PrefabUtility.SaveAsPrefabAsset(go, path);
                Object.DestroyImmediate(go);
                AssetDatabase.SaveAssets();
            }
            AssetDatabase.Refresh();   // import → NetworkSpawnablePostprocessor stamps the id and bakes the table
            Debug.Log("[Arena] spawnable 프리팹 베이크 완료");
        }
    }
}
#endif
