using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// 클라 측 동적 스폰·파괴 적용 — 생성 클라 허브의 Spawn/Destroy 수신이 메인 큐로 넘겨 호출한다.
    /// 오브젝트는 첫 서브 타입의 카탈로그(RegisterPrefab) 또는 기본 팩토리로 생성하고, 스폰 메시지의 서브 구성과 실제 컴포넌트를 대조 검증한다.
    /// 공개 이유는 생성 코드(사용자 어셈블리) 접근 — 직접 호출하지 않는다.
    /// </summary>
    public static class UniNetSpawn
    {
        /// <summary>서버의 스폰 메시지를 적용한다 — 오브젝트 생성·netId·변환·서브 등록·전체 상태 반영 (메인 스레드). 생성 코드 전용.</summary>
        public static void Apply(ulong netId,
            float px, float py, float pz, float qx, float qy, float qz, float qw,
            byte subCount, ulong[] typeKeys, byte[][] states)
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;
            if (client.Get(netId) != null) return;   // 이미 등록됨 — 중복 스폰 무시

            // 호스트 — 서버의 컴포넌트 배열을 클라 등록으로 재사용 (이중 생성 방지). 값·변환은 이미 서버 원본이라 그대로 둔다.
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
                    UniNetTypeRegistry.Find(nb.GetType())?.InitClientSnapshot(nb);   // RepNotify 이전값 기준선
                }
                return;
            }

            if (subCount == 0 || typeKeys == null || typeKeys.Length != subCount)
            {
                Debug.LogError($"[UniNet] 스폰 실패 — 서브 구성이 무효 (netId={netId}, subCount={subCount})");
                return;
            }

            // 오브젝트 생성 — 첫 서브 타입의 카탈로그(프리팹) 우선, 없으면 기본 팩토리(단일 컴포넌트)
            var primary = UniNetSpawnRegistry.Create(typeKeys[0]) as NetworkBehaviour;
            if (primary == null)
            {
                Debug.LogError($"[UniNet] 스폰 실패 — 등록되지 않은 타입 키 {typeKeys[0]} (netId={netId})");
                return;
            }

            // 서브 복원 — 스폰 메시지의 타입 키 순서대로 누락된 NetworkBehaviour를 추가한다.
            // 기본 팩토리는 첫 서브만 생성하므로, 멀티 컴포넌트 오브젝트(예: ArenaPlayer + NetworkTransform)도
            // RegisterPrefab 없이 서버와 동일한 슬롯 구성으로 복원된다 (ADR-0014).
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
                    handler.ApplyDelta(comps[i], ref reader);   // 전체 상태 — InitialOnly 포함, RepNotify(로컬 초기값) 발생
                }
            }
        }

        /// <summary>오브젝트에 해당 타입 키의 서브가 이미 존재하는가.</summary>
        private static bool HasSub(GameObject go, ulong typeKey)
        {
            foreach (var nb in go.GetComponents<NetworkBehaviour>())
                if (UniNetSpawnRegistry.TypeKeyOf(nb.GetType()) == typeKey)
                    return true;
            return false;
        }

        /// <summary>서버의 파괴 메시지를 적용한다 — 등록 해제 + 로컬 파괴 (메인 스레드). 이미 없으면 아무것도 안 한다. 생성 코드 전용.</summary>
        public static void Remove(ulong netId)
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;

            var comps = client.Get(netId);
            client.Unregister(netId);
            if (comps != null && comps.Length > 0 && comps[0] is NetworkBehaviour nb && nb != null)   // 호스트: 서버가 이미 로컬 파괴 — 파괴 스킵, 등록만 해제
            {
                if (Application.isPlaying) Object.Destroy(nb.gameObject);
                else Object.DestroyImmediate(nb.gameObject);   // 에디트 모드(배치 검증) — Destroy 불가
            }
        }

        /// <summary>생성된 오브젝트의 컴포넌트 구성이 스폰 메시지의 서브 타입 목록과 슬롯별로 일치하는가.</summary>
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
