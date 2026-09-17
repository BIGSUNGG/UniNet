using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DRPC.Client.Network;
using DRPC.Server.Network;
using DRPC.Shared.Network;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// 연결 수명주기 진입점 — 서버·클라이언트·호스트 시작. 전송·보안은 DRPC(RUDP+DTLS) 스택을 그대로 사용한다.
    /// 받은 RPC·리플리케이션은 드라이버의 메인 스레드 펌프에서 실행된다.
    /// </summary>
    public static class UniNetManager
    {
        private static RpcListenHandle _listenHandle;

        /// <summary>전용 서버를 시작하고 클라이언트 접속을 대기한다 (서버 권위).</summary>
        public static Task ServerAsync(int port)
            => ServerAsync(port, null);

        /// <summary>연결 설정(키·DTLS·타임아웃 등)을 지정해 서버를 시작한다.</summary>
        public static async Task ServerAsync(int port, UniNetEndpointOptions options)
        {
            var factory = RequireFactory();
            UniNetDriver.Ensure();
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            NetworkBehaviour.RegisterAllToServer();

            var handle = options != null
                ? await RpcHost.ListenWithOptionsAsync<HubBase>(port, options.ToRpcEndpointOptions(),
                    factory.CreateServerHub, OnConnected(server))
                : await RpcHost.ListenAsync<HubBase>(port, null,
                    factory.CreateServerHub, OnConnected(server));

            _listenHandle = handle;
        }

        /// <summary>클라이언트로 지정 주소의 서버에 접속한다.</summary>
        public static Task ClientAsync(string address, int port)
            => ClientAsync(address, port, null);

        /// <summary>연결 설정을 지정해 접속한다.</summary>
        public static async Task ClientAsync(string address, int port, UniNetEndpointOptions options)
        {
            var factory = RequireFactory();
            UniNetDriver.Ensure();
            UniNetEnvironment.SetClient(new NetworkClient());
            NetworkBehaviour.RegisterAllToClient();

            if (options != null)
            {
                await RpcClient.ConnectWithOptionsAsync<HubBase>(address, port, options.ToRpcEndpointOptions(),
                    factory.CreateClientHub);
            }
            else
            {
                await RpcClient.ConnectAsync<HubBase>(address, port, null, factory.CreateClientHub);
            }
        }

        /// <summary>서버 + 클라이언트를 한 프로세스에서 시작한다 (개발·테스트용 — 루프백 RUDP 왕복).</summary>
        public static Task HostAsync(int port)
            => HostAsync(port, null, null);

        /// <summary>연결 설정을 지정해 호스트로 시작한다 (서버/클라 각각 적용).</summary>
        public static async Task HostAsync(int port, UniNetEndpointOptions serverOptions, UniNetEndpointOptions clientOptions)
        {
            await ServerAsync(port, serverOptions);
            await ClientAsync("127.0.0.1", port, clientOptions);
        }

        /// <summary>클라 생성용 프리팹 카탈로그 등록 — T 타입 동적 스폰 시 클라에서 이 프리팹으로 생성한다 (미등록 시 빈 GameObject+AddComponent). 양단 같은 코드로 호출하면 된다.</summary>
        public static void RegisterPrefab<T>(GameObject prefab) where T : NetworkBehaviour
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            UniNetSpawnRegistry.Override(typeof(T), () => UnityEngine.Object.Instantiate(prefab).GetComponent<T>());
        }

        /// <summary>
        /// 서버 동적 스폰 — Instantiate 후 호출하면 netId 할당·소유권 배정·전 클라 스폰 전파가 일어난다.
        /// 오브젝트의 NetworkBehaviour **전체**가 슬롯으로 등록된다 (다중 컴포넌트 지원 — 슬롯 = GetComponents 순서).
        /// 다중 컴포넌트 오브젝트는 RegisterPrefab으로 양단 같은 프리팹을 등록해야 한다 (기본 팩토리는 단일 컴포넌트만 생성). 서버 권위 — 서버/호스트에서만 유효.
        /// </summary>
        public static void Spawn(GameObject instance)
        {
            var server = UniNetEnvironment.Server;
            var comps = instance != null ? instance.GetComponents<NetworkBehaviour>() : null;
            if (server == null || comps == null || comps.Length == 0)
            {
                Debug.LogWarning("[UniNet] Spawn 은 서버에서 NetworkBehaviour 컴포넌트가 있는 오브젝트에만 유효하다 (호출 무시 — 인스턴스는 호출측 소유)");
                return;
            }
            if (comps.Length > byte.MaxValue)
            {
                Debug.LogError($"[UniNet] 오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한) — 스폰 거부: {instance.name}");
                return;
            }

            var netId = server.RegisterDynamicObject(comps);
            for (byte i = 0; i < comps.Length; i++)
            {
                comps[i].AssignNetId(netId);
                comps[i].AssignSubId(i);
                comps[i].MarkServerRegistered();
            }
            server.BroadcastSpawn(netId);
        }

        /// <summary>서버 네트워크 파괴 — 전 클라에 파괴를 전파하고 로컬도 파괴한다. Spawn 으로 스폰한 오브젝트에 사용 (오브젝트 전체·전 서브).</summary>
        public static void NetworkDestroy(GameObject instance)
        {
            var server = UniNetEnvironment.Server;
            var nb = instance != null ? instance.GetComponent<NetworkBehaviour>() : null;
            if (server == null || nb == null)
            {
                Debug.LogWarning("[UniNet] NetworkDestroy 는 서버에서 NetworkBehaviour 컴포넌트가 있는 오브젝트에만 유효하다");
                return;
            }

            server.DestroyObject(nb.NetId);
            UnityEngine.Object.Destroy(instance);
        }

        private static UniNetHubFactory RequireFactory()
            => UniNetEnvironment.HubFactory
               ?? throw new InvalidOperationException(
                   "UniNet 생성 허브가 등록되지 않았습니다. NetworkBehaviour 파생 타입이 있는 어셈블리에서만 네트워킹을 시작할 수 있습니다.");

        private static Func<HubBase, Task> OnConnected(NetworkServer server)
        {
            return hub =>
            {
                if (hub is IUniNetSystemChannel channel)
                {
                    server.AttachConnection(channel);
                    hub.Disconnected += () => server.DetachConnection(channel);
                }
                return Task.CompletedTask;
            };
        }
    }

    /// <summary>
    /// 숨은 드라이버 — 메인 스레드 펌프(수신 디스패치 실행)와 서버 리플리케이션 틱을 Update로 구동한다.
    /// </summary>
    internal sealed class UniNetDriver : MonoBehaviour
    {
        private static UniNetDriver _instance;

        /// <summary>드라이버가 없으면 만든다 (첫 네트워크 시작 시 1회). HideAndDontSave 플래그로 재로드·저장에서 제외된다.</summary>
        public static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("UniNetDriver") { hideFlags = HideFlags.HideAndDontSave };
            _instance = go.AddComponent<UniNetDriver>();
        }

        private void Update()
        {
            UniNetEnvironment.PumpMain();
            var server = UniNetEnvironment.Server;
            if (server != null)
            {
                var objects = server.SnapshotObjects();   // 프레임당 1회 — 스윕·틱 공유 (복사 2회 방지)
                SweepDestroyed(objects);
                server.TickReplication(objects);
            }
        }

        /// <summary>
        /// 일반 Destroy로 사라진 네트워크 오브젝트를 감지해 파괴를 전파한다 (고스트 등록 방지 — 리소스 소진 방어).
        /// ponytail: 매 프레임 전체 순회(O(n)), 오브젝트 수가 커지면 파괴 이벤트 후크로 교체.
        /// </summary>
        private static void SweepDestroyed(IReadOnlyList<(ulong NetId, NetworkServer.ServerObjectEntry Entry)> objects)
        {
            foreach (var (netId, entry) in objects)
                if (entry.Instance is UnityEngine.Object u && u == null)
                    UniNetEnvironment.Server.DestroyObject(netId);
        }
    }
}
