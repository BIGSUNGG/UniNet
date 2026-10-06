using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Applies client-side dynamic spawns and destroys — called from the generated client hub when a Spawn/Destroy
    /// message arrives (marshalled through the main queue). The spawn message carries the stamped prefab id
    /// (ADR-0024); the object is instantiated from the baked prefab table and verified against the sub composition.
    /// Public only so generated code (in user assemblies) can call it — never call it directly.
    /// </summary>
    public static class UniNetSpawn
    {
        /// <summary>Applies a spawn message from the server — instantiates the prefab, assigns the netId, registers subs, and applies full state (main thread). Generated code only.</summary>
        public static void Apply(ulong netId,
            float px, float py, float pz, float qx, float qy, float qz, float qw,
            ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states)
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;
            if (client.Get(netId) != null) return;   // already registered — ignore the duplicate spawn

            // Host — reuse the server's component array as the client registration (avoids double instantiation). Values and transform are already the server originals, left as-is.
            var server = UniNetEnvironment.Server;
            var serverEntry = server?.GetEntry(netId);
            if (serverEntry != null && serverEntry.Subs.Length > 0)
            {
                var own = new object[serverEntry.Subs.Length];
                for (int i = 0; i < own.Length; i++) own[i] = serverEntry.Subs[i].Instance;
                client.Register(netId, own);
                for (int i = 0; i < own.Length; i++)
                {
                    var nb = (NetworkBehaviour)own[i];
                    nb.MarkClientRegistered();
                    UniNetTypeRegistry.Find(nb.GetType())?.InitClientSnapshot(nb);   // seed the RepNotify previous-value baseline
                }
                return;
            }

            if (subCount == 0 || typeKeys == null || typeKeys.Length != subCount)
            {
                Debug.LogError($"[UniNet] 스폰 실패 — 서브 구성이 무효 (netId={netId}, subCount={subCount})");
                return;
            }

            // Instantiate the SAME prefab the server spawned — resolved by the stamped id through the baked table (ADR-0024).
            // Unknown id = this build lacks the prefab (not marked here, or version skew) — fail loudly.
            var prefab = UniNetPrefabTable.Lookup(prefabId);
            if (prefab == null)
            {
                Debug.LogError($"[UniNet] 스폰 실패 — 알 수 없는 프리팹 아이디 {prefabId} (netId={netId}) — 이 빌드에 해당 spawnable 프리팹이 없다 (미마킹·버전 어긋남)");
                return;
            }
            var spawned = Object.Instantiate(prefab, new Vector3(px, py, pz), new Quaternion(qx, qy, qz, qw));
            var primary = spawned.GetComponent<NetworkBehaviour>();
            if (primary == null)
            {
                Debug.LogError($"[UniNet] 스폰 실패 — 프리팹 루트에 NetworkBehaviour 가 없다 (netId={netId}, prefab={prefab.name})");
                DestroyObject(spawned);   // never leave an unregistered orphan in the scene
                return;
            }

            var comps = primary.gameObject.GetComponents<NetworkBehaviour>();
            if (!VerifySlots(comps, typeKeys))
            {
                Debug.LogError($"[UniNet] 스폰 슬롯 불일치 (netId={netId}, prefab={prefab.name}) — 이 빌드의 프리팹이 서버 구성과 다르다(초과·누락·순서 어긋남) — 양단 프리팹을 동일하게 맞추세요");
                DestroyObject(primary.gameObject);
                return;
            }

            primary.AssignNetId(netId);
            var boxed = (object[])comps;
            client.Register(netId, boxed);
            for (byte i = 0; i < comps.Length; i++)
            {
                comps[i].AssignSubId(i);
                comps[i].AssignNetId(netId);
                comps[i].MarkClientRegistered();

                var handler = UniNetTypeRegistry.Find(comps[i].GetType());
                handler?.InitClientSnapshot(comps[i]);
                if (states != null && i < states.Length && states[i] != null && states[i].Length > 0 && handler != null)
                {
                    var reader = new MessageProtocol.Serialize.MessageBufferReader(states[i]);
                    handler.ApplyDelta(comps[i], ref reader);   // full state — includes InitialOnly; raises RepNotify (local initial values)
                }
            }
        }

        /// <summary>Applies a destroy message from the server — unregisters and destroys locally (main thread). No-op if the object is already gone. Generated code only.</summary>
        public static void Remove(ulong netId)
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;

            var comps = client.Get(netId);
            client.Unregister(netId);
            if (comps != null && comps.Length > 0 && comps[0] is NetworkBehaviour nb && nb != null)   // host: the server side already destroyed locally — skip the destroy, just unregister
            {
                if (Application.isPlaying) Object.Destroy(nb.gameObject);
                else Object.DestroyImmediate(nb.gameObject);   // edit mode (batch validation) — Destroy is not allowed there
            }
        }

        /// <summary>Checks that the instantiated object's components match the spawn message's sub type list slot by slot.</summary>
        private static bool VerifySlots(NetworkBehaviour[] comps, ulong[] typeKeys)
        {
            if (comps.Length != typeKeys.Length) return false;
            for (int i = 0; i < comps.Length; i++)
                if (UniNetSpawnRegistry.TypeKeyOf(comps[i].GetType()) != typeKeys[i])
                    return false;
            return true;
        }

        private static void DestroyObject(GameObject go)
        {
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }
    }
}
