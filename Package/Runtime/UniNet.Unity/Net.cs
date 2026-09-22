using System;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Call-site convenience entry point — put <c>using static UniNet.Unity.Net;</c> at the top of your file
    /// to call <see cref="NetworkInstantiate(GameObject)"/> and <see cref="NetworkDestroy(GameObject)"/>
    /// without a qualifier, just like the regular Instantiate/Destroy. The implementation lives in <see cref="UniNetManager"/> (pure forwarding).
    /// </summary>
    public static class Net
    {
        /// <summary>Clones, registers, and replicates the original (prefab or template) — returns the registered instance (forwards to UniNetManager).</summary>
        public static GameObject NetworkInstantiate(GameObject original)
            => UniNetManager.NetworkInstantiate(original);

        /// <summary>Clones, registers, and replicates with an explicit position and rotation (forwards to UniNetManager).</summary>
        public static GameObject NetworkInstantiate(GameObject original, Vector3 position, Quaternion rotation)
            => UniNetManager.NetworkInstantiate(original, position, rotation);

        /// <summary>Clones, registers, and replicates, running a configure callback to set non-serialized initial state (e.g. private [Replicated] fields) — the callback runs right after the clone, before the spawn is replicated (forwards to UniNetManager).</summary>
        public static GameObject NetworkInstantiate(GameObject original, Action<GameObject> configure)
            => UniNetManager.NetworkInstantiate(original, configure);

        /// <summary>Server-side network destroy — replicates the destroy to all clients and destroys the local object (forwards to UniNetManager).</summary>
        public static void NetworkDestroy(GameObject instance)
            => UniNetManager.NetworkDestroy(instance);
    }
}
