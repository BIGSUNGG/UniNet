#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Loads the committed spawnable test prefabs (ADR-0024) — Sandbox/Assets/Tests/Fixtures/Prefabs/.
    /// Editor-only: tests and 2-process runners run inside the Unity editor; a player build never includes
    /// test assemblies. Baked once by TestPrefabBaker (the postprocessor stamps ids and bakes the table).
    /// </summary>
    public static class TestPrefabs
    {
        public const string SpawnablePlayerPrefab = "Assets/Tests/Fixtures/Prefabs/SpawnablePlayerPrefab.prefab";
        public const string MultiTankPrefab = "Assets/Tests/Fixtures/Prefabs/MultiTankPrefab.prefab";
        public const string AltSpawnablePlayerPrefab = "Assets/Tests/Fixtures/Prefabs/AltSpawnablePlayerPrefab.prefab";   // same component as SpawnablePlayerPrefab — proves the 1:1 type-key limit is gone (ADR-0024)
        public const string NetworkTransformPrefab = "Assets/Tests/Fixtures/Prefabs/NetworkTransformPrefab.prefab";
        public const string ArenaPlayerPrefab = "Assets/Resources/Arena/ArenaPlayerPrefab.prefab";   // demo prefab lives in the demo's Resources folder (ARENA-001 — Resources.Load path shared by editor and player builds)

        /// <summary>Loads a spawnable test prefab (null + error log if missing — run TestPrefabBaker.Bake first).</summary>
        public static GameObject Load(string assetPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                Debug.LogError($"[UniNet-Test] 테스트 프리팹 없음: {assetPath} — TestPrefabBaker.Bake 를 먼저 실행하라");
            return prefab;
        }
    }
}
#endif
