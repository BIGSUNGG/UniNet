using System;
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
            UniNetEnvironment.Server?.TickReplication();
        }
    }
}
