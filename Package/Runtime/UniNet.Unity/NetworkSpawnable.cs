using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Edit-time spawnable marker — attach it to a prefab root to make the prefab network-spawnable (ADR-0024).
    /// The editor stamps <c>_prefabId</c> with the FNV-1a 64 hash of the prefab asset GUID at save/import time;
    /// the spawn message carries that id and each client instantiates the same prefab from its baked prefab table.
    /// A marker without a stamped id (added at runtime, or a prefab the editor never imported) is rejected by
    /// NetworkInstantiate — marking is only possible at edit time, by design.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UniNet/Network Spawnable")]
    public sealed class NetworkSpawnable : MonoBehaviour
    {
        // Stamped by NetworkSpawnablePostprocessor at edit time — FNV-1a 64 of the prefab asset GUID.
        // 0 = unstamped (runtime-added marker or never-imported prefab) — NetworkInstantiate rejects it.
        [SerializeField] private ulong _prefabId;

        /// <summary>Network prefab identity — the stamped GUID hash (0 = unstamped, not spawnable).</summary>
        public ulong PrefabId => _prefabId;

        /// <summary>Editor/test injection point — stamps the prefab id (never call from game code).</summary>
        internal void Stamp(ulong prefabId) => _prefabId = prefabId;
    }
}
