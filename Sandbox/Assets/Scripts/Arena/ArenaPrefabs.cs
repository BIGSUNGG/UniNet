using UnityEngine;

namespace Arena
{
    /// <summary>
    /// Runtime prefab access — ARENA-001: the single Resources-based load path shared by the editor and player
    /// builds (replaces the editor-only AssetDatabase load that returned null in players). The prefab file was
    /// MOVED (not recreated) to Assets/Resources/Arena/ with its .meta, so its GUID — and therefore the baked
    /// UniNetPrefabTable id (FNV-1a of the GUID, NetworkSpawnable) — is unchanged.
    /// </summary>
    public static class ArenaPrefabs
    {
        /// <summary>Resources path (no extension, relative to any Assets/Resources root).</summary>
        public const string PlayerResourcePath = "Arena/ArenaPlayerPrefab";

        /// <summary>
        /// Loads the spawnable player prefab. A null here would break every downstream spawn silently, so a miss
        /// is logged loudly — in a player build this almost always means the prefab (or the baked table it
        /// references) was excluded from the build.
        /// </summary>
        public static GameObject LoadPlayer()
        {
            var prefab = Resources.Load<GameObject>(PlayerResourcePath);
            if (prefab == null)
                Debug.LogError("[Arena] 플레이어 프리팹 미발견: Resources/Arena/ArenaPlayerPrefab — 빌드에 포함됐는지 확인");
            return prefab;
        }
    }
}
