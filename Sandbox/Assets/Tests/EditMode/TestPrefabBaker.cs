using UnityEditor;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Batch entry point that bakes the spawnable test prefabs (ADR-0024) — run once via
    /// `-executeMethod UniNet.Tests.TestPrefabBaker.Bake`. Creates the four prefabs the tests and
    /// 2-process runners spawn; saving them triggers NetworkSpawnablePostprocessor (GUID-hash stamping
    /// and the baked prefab table). Re-runnable — existing prefabs are left untouched.
    /// </summary>
    public static class TestPrefabBaker
    {
        public static void Bake()
        {
            BakePrefab(TestPrefabs.SpawnablePlayerPrefab, go => go.AddComponent<SpawnablePlayer>());
            BakePrefab(TestPrefabs.AltSpawnablePlayerPrefab, go => go.AddComponent<SpawnablePlayer>());   // same type, different prefab — ADR-0024 twin
            BakePrefab(TestPrefabs.MultiTankPrefab, go =>
            {
                go.AddComponent<MovementBrain>();
                go.AddComponent<HealthTank>();
            });
            BakePrefab(TestPrefabs.NetworkTransformPrefab, go => go.AddComponent<NetworkTransform>());   // ArenaPlayerPrefab is baked by the Arena editor script (Assembly-CSharp)
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();   // import → NetworkSpawnablePostprocessor stamps ids and bakes the table
            Debug.Log("[UniNet-Test] 테스트 프리팹 베이크 완료");
        }

        private static void BakePrefab(string path, System.Action<GameObject> build)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
            build(go);
            go.AddComponent<NetworkSpawnable>();
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
        }
    }
}
