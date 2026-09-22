using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Applies client-side dynamic spawns and destroys — called from the generated client hub when a Spawn/Destroy
    /// message arrives (marshalled through the main queue). Objects are created from the primary sub type's catalog
    /// (RegisterPrefab) or the default factory, then verified against the sub composition in the spawn message.
    /// Public only so generated code (in user assemblies) can call it — never call it directly.
    /// </summary>
    public static class UniNetSpawn
    {
        /// <summary>Applies a spawn message from the server — creates the object, assigns the netId and transform, registers subs, and applies full state (main thread). Generated code only.</summary>
        public static void Apply(ulong netId,
            float px, float py, float pz, float qx, float qy, float qz, float qw,
            byte subCount, ulong[] typeKeys, byte[][] states)
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

            // Create the object — prefer the primary sub type's catalog (prefab), else the default factory (single component)
            var primary = UniNetSpawnRegistry.Create(typeKeys[0]) as NetworkBehaviour;
            if (primary == null)
            {
                Debug.LogError($"[UniNet] 스폰 실패 — 등록되지 않은 타입 키 {typeKeys[0]} (netId={netId})");
                return;
            }

            // Sub restoration — add any missing NetworkBehaviours in the spawn message's type-key order.
            // The default factory only creates the primary sub, so multi-component objects (e.g. ArenaPlayer + NetworkTransform)
            // are restored to the server's exact slot composition even without RegisterPrefab (See ADR-0014).
            for (int i = 1; i < typeKeys.Length; i++)
            {
                if (HasSub(primary.gameObject, typeKeys[i])) continue;
                var subType = UniNetSpawnRegistry.TypeOf(typeKeys[i]);
                if (subType == null)
                {
                    Debug.LogError($"[UniNet] 스폰 실패 — 등록되지 않은 타입 키 {typeKeys[i]} (netId={netId}, 서브 {i})");
                    DestroyObject(primary.gameObject);
                    return;
                }
                primary.gameObject.AddComponent(subType);
            }

            var comps = primary.gameObject.GetComponents<NetworkBehaviour>();
            if (!VerifySlots(comps, typeKeys))
            {
                Debug.LogError($"[UniNet] 스폰 슬롯 불일치 (netId={netId}) — 자동 복원 후에도 서버 {subCount}개/클라 {comps.Length}개. 프리팹이 스폰 메시지와 다른 NetworkBehaviour 서브를 갖거나(초과·누락) 컴포넌트 순서가 어긋난 경우다 — 프리팹 구성을 서버와 동일하게 맞추세요");
                DestroyObject(primary.gameObject);
                return;
            }

            primary.AssignNetId(netId);
            primary.transform.SetPositionAndRotation(new Vector3(px, py, pz), new Quaternion(qx, qy, qz, qw));
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

        /// <summary>Checks whether the object already has a sub with the given type key.</summary>
        private static bool HasSub(GameObject go, ulong typeKey)
        {
            foreach (var nb in go.GetComponents<NetworkBehaviour>())
                if (UniNetSpawnRegistry.TypeKeyOf(nb.GetType()) == typeKey)
                    return true;
            return false;
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
