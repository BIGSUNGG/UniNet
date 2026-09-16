using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
/// <summary>
/// 클라 측 동적 스폰·파괴 적용 — 생성 클라 허브의 Spawn/Destroy 수신이 메인 큐로 넘겨 호출한다.
/// 프리팹 카탈로그(RegisterPrefab)가 있으면 프리팹 인스턴스, 없으면 기본 팩토리(빈 GameObject+AddComponent)로 생성한다.
/// 공개 이유는 생성 코드(사용자 어셈블리) 접근 — 직접 호출하지 않는다.
/// </summary>
public static class UniNetSpawn
{
        /// <summary>서버의 스폰 메시지를 적용한다 — 오브젝트 생성·netId·변환·등록·전체 상태 반영 (메인 스레드). 생성 코드 전용.</summary>
        public static void Apply(ulong netId, ulong typeKey,
            float px, float py, float pz, float qx, float qy, float qz, float qw, byte[] state)
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;
            if (client.Get(netId) != null) return;   // 이미 등록됨 — 중복 스폰 무시

            // 호스트 — 서버 인스턴스를 클라 등록으로 재사용 (이중 생성 방지). 값·변환은 이미 서버 원본이라 그대로 둔다.
            var server = UniNetEnvironment.Server;
            if (server != null && server.Get(netId) is NetworkBehaviour own && own != null)
            {
                client.Register(netId, own);
                own.MarkClientRegistered();
                UniNetTypeRegistry.Find(own.GetType())?.InitClientSnapshot(own);   // RepNotify 이전값 기준선
                return;
            }

            var nb = UniNetSpawnRegistry.Create(typeKey) as NetworkBehaviour;
            if (nb == null)
            {
                Debug.LogError($"[UniNet] 스폰 실패 — 등록되지 않은 타입 키 {typeKey} (netId={netId})");
                return;
            }

            nb.AssignNetId(netId);
            nb.transform.SetPositionAndRotation(new Vector3(px, py, pz), new Quaternion(qx, qy, qz, qw));
            client.Register(netId, nb);
            nb.MarkClientRegistered();

            var handler = UniNetTypeRegistry.Find(nb.GetType());
            handler?.InitClientSnapshot(nb);
            if (state != null && state.Length > 0 && handler != null)
            {
                var reader = new MessageProtocol.Serialize.MessageBufferReader(state);
                handler.ApplyDelta(nb, ref reader);   // 전체 상태 — InitialOnly 포함, RepNotify(로컬 초기값) 발생
            }
        }

        /// <summary>서버의 파괴 메시지를 적용한다 — 등록 해제 + 로컬 파괴 (메인 스레드). 이미 없으면 아무것도 안 한다. 생성 코드 전용.</summary>
        public static void Remove(ulong netId)
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;

            var o = client.Get(netId);
            client.Unregister(netId);
            if (o is NetworkBehaviour nb && nb != null)   // 호스트: 서버가 이미 로컬 파괴 — 파괴 스킵, 등록만 해제
            {
                if (Application.isPlaying) Object.Destroy(nb.gameObject);
                else Object.DestroyImmediate(nb.gameObject);   // 에디트 모드(배치 검증) — Destroy 불가
            }
        }
    }
}
