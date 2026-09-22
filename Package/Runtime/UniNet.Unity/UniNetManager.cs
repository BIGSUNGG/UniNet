using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Communication.Shared.Channels;
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
        private static volatile bool _clientConnected;   // 네트워크 스레드(Disconnected 핸들러)와 메인 스레드가 공유 — 가시성 보장

        /// <summary>
        /// 클라이언트 세션 종료 이벤트 — 원격 세션 종료(서버 종료·네트워크 단절) 시 메인 스레드에서 발화한다.
        /// 자발 <see cref="ClientStop"/>은 <see cref="IsClientConnected"/> 상태만 즉시 해제하고 이 이벤트는 발화하지 않는다.
        /// 게임은 여기서 재접속 안내·메인 메뉴 복귀 등을 처리한다 (연결 수명주기 게이트웨이).
        /// </summary>
        public static event Action ClientDisconnected;

        /// <summary>클라이언트가 서버에 접속해 세션이 살아있는가 (ClientAsync 성공 후 true, 연결 종료 시 false).</summary>
        public static bool IsClientConnected => _clientConnected;

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
            UniNetEnvironment.SetClientSender(null);   // 이전 세션 허브의 지연 Disconnected가 허브 동일성 필터를 통과하지 못하게
            NetworkBehaviour.RegisterAllToClient();

            // 클라 허브 생성을 감싸 세션 종료(Disconnected)를 관측한다 — 서버 쪽 OnConnected wiring과 대칭
            Task connect = options != null
                ? RpcClient.ConnectWithOptionsAsync<HubBase>(address, port, options.ToRpcEndpointOptions(), CreateWatchedHub)
                : RpcClient.ConnectAsync<HubBase>(address, port, null, CreateWatchedHub);

            try
            {
                await connect;
            }
            catch
            {
                _clientConnected = false;   // 실패한 허브가 Disconnected를 발화하지 않아도 거짓 '접속 중' 고착 없음
                throw;
            }

            return;

            // 허브를 만들자마자 이 허브 전용 종료 관측을 단다. 플래그는 구독 직후 확정 — Disconnected가
            // 네트워크 스레드에서 await 재개 전에 관측돼도 가드에 삼켜지지 않는다. volatile이 메인·네트워크
            // 스레드 간 가시성을 보장하며, 팩토리 반환~대입 사이의 미시 창은 connect 실패 catch가 보정한다.
            HubBase CreateWatchedHub(IMessageChannel channel)
            {
                var hub = factory.CreateClientHub(channel);
                hub.Disconnected += () =>
                {
                    if (!ReferenceEquals(hub, UniNetEnvironment.ClientSender)) return;   // 이전 세션 허브의 지연 관측 무시
                    if (!_clientConnected) return;   // 중복 발화·자발 ClientStop 흡수 — ClientStop은 플래그를 먼저 끊는다
                    _clientConnected = false;
                    UniNetEnvironment.QueueOnMain(RaiseClientDisconnected);   // 네트워크 스레드에서 올 수 있다 — 메인 큐로
                };
                _clientConnected = true;
                return hub;
            }
        }

        private static void RaiseClientDisconnected()
        {
            var handlers = ClientDisconnected;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception e) { Debug.LogException(e); }   // 구독자별 격리 — 생성 코드 메인 펌프 보호와 동일 패턴
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

        /// <summary>클라이언트를 정지한다 — 허브 연결 종료(Disconnect·Dispose) + 환경 상태 정리. 미접속이면 아무것도 하지 않는다(멱등). 동기 — 종료 직전 경로에서 안전.</summary>
        public static void ClientStop()
        {
            var sender = UniNetEnvironment.ClientSender;
            _clientConnected = false;   // 명시 종료 — 세션 종료 이벤트 발화 여부와 무관하게 상태는 즉시 해제
            UniNetEnvironment.SetClientSender(null);
            UniNetEnvironment.SetClient(null);
            if (sender is global::DRPC.Shared.Network.HubBase hub)
            {
                hub.Disconnect();
                hub.Dispose();
            }
        }

        /// <summary>전용 서버를 정지한다 — 리스너 정지(소켓 언바인딩) + 환경 상태 정리. 리슨 중이 아니면 아무것도 하지 않는다(멱등).</summary>
        public static async Task ServerStopAsync()
        {
            var handle = _listenHandle;
            _listenHandle = null;
            UniNetEnvironment.SetServer(null);
            if (handle != null)
                await handle.DisposeAsync();
        }

        /// <summary>전용 서버를 정지한다(동기) — 애플리케이션 종료 직전처럼 await가 불가능한 경로용. ServerStopAsync와 동일 정리를 동기 Dispose로 수행.</summary>
        public static void ServerStop()
        {
            var handle = _listenHandle;
            _listenHandle = null;
            UniNetEnvironment.SetServer(null);
            handle?.Dispose();
        }

        /// <summary>호스트(서버+클라)를 정지한다 — ClientStop + ServerStopAsync. 동기 경로는 HostStop 사용.</summary>
        public static async Task HostStopAsync()
        {
            ClientStop();
            await ServerStopAsync();
        }

        /// <summary>호스트(서버+클라)를 정지한다(동기) — 애플리케이션 종료 직전 경로용.</summary>
        public static void HostStop()
        {
            ClientStop();
            ServerStop();
        }

        /// <summary>클라 생성용 프리팹 카탈로그 등록 — T 타입 동적 스폰 시 클라에서 이 프리팹으로 생성한다 (미등록 시 빈 GameObject+AddComponent). 양단 같은 코드로 호출하면 된다.</summary>
        public static void RegisterPrefab<T>(GameObject prefab) where T : NetworkBehaviour
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            UniNetSpawnRegistry.Override(typeof(T), () => UnityEngine.Object.Instantiate(prefab).GetComponent<T>());
        }

        /// <summary>
        /// 서버 동적 스폰 — 원본(프리팹·템플릿)을 복제해 등록·전파한다. 반환값이 등록된 인스턴스다 (원본은 남으니 호출측에서 정리).
        /// 복제는 직렬화 복사라 public·[SerializeField] 필드만 따라온다 — private [Replicated] 초기화는 configure 콜백(복제 직후·전파 직전 실행)에서 하라.
        /// 오브젝트의 NetworkBehaviour **전체**가 슬롯으로 등록된다 (다중 컴포넌트 지원 — 슬롯 = GetComponents 순서).
        /// 다중 컴포넌트 오브젝트는 RegisterPrefab으로 양단 같은 프리팹을 등록해야 한다 (기본 팩토리는 단일 컴포넌트만 생성). 서버 권위 — 서버/호스트에서만 유효.
        /// </summary>
        public static GameObject NetworkInstantiate(GameObject original)
            => NetworkInstantiateCore(original, original.transform.position, original.transform.rotation, null);

        /// <summary>위치·회전을 지정해 복제·등록·전파한다. 반환값이 등록된 인스턴스다.</summary>
        public static GameObject NetworkInstantiate(GameObject original, Vector3 position, Quaternion rotation)
            => NetworkInstantiateCore(original, position, rotation, null);

        /// <summary>
        /// 구성 콜백을 지정해 복제·등록·전파한다 — 콜백은 복제 직후·등록 전파 직전에 클론으로 실행된다.
        /// private [Replicated] 필드(InitialOnly 초기 상태 등 비직렬화 값)는 여기서 세팅해야 스폰 기준선에 실린다. 반환값이 등록된 인스턴스다.
        /// </summary>
        public static GameObject NetworkInstantiate(GameObject original, Action<GameObject> configure)
            => NetworkInstantiateCore(original, original.transform.position, original.transform.rotation, configure);

        private static GameObject NetworkInstantiateCore(GameObject original, Vector3 position, Quaternion rotation, Action<GameObject> configure)
        {
            var server = UniNetEnvironment.Server;
            var comps = original != null ? original.GetComponents<NetworkBehaviour>() : null;
            if (server == null || comps == null || comps.Length == 0)
            {
                Debug.LogWarning("[UniNet] NetworkInstantiate 는 서버에서 NetworkBehaviour 컴포넌트가 있는 원본에만 유효하다 (호출 무시 — null 반환)");
                return null;
            }

            var instance = UnityEngine.Object.Instantiate(original, position, rotation);
            configure?.Invoke(instance);
            var instComps = instance.GetComponents<NetworkBehaviour>();
            if (instComps.Length > byte.MaxValue)
            {
                Debug.LogError($"[UniNet] 오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한) — 스폰 거부: {instance.name}");
                if (Application.isPlaying) UnityEngine.Object.Destroy(instance);
                else UnityEngine.Object.DestroyImmediate(instance);   // 에디트 모드(배치 검증) — Destroy 불가
                return null;
            }

            var netId = server.RegisterDynamicObject(instComps);
            for (byte i = 0; i < instComps.Length; i++)
            {
                instComps[i].AssignNetId(netId);
                instComps[i].AssignSubId(i);
                instComps[i].MarkServerRegistered();
            }
            server.BroadcastSpawn(netId);
            return instance;
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
            UniNetTime.LocalClock = () => Time.unscaledTimeAsDouble;   // P4 — 코어 시계를 Unity unscaled 시계로 교체 (전 프로세스 공유)
            var go = new GameObject("UniNetDriver") { hideFlags = HideFlags.HideAndDontSave };
            _instance = go.AddComponent<UniNetDriver>();
        }

        private static double _nextTimeSync;   // P4 — TimeSync 주기 브로드캐스트 (드라이버 1 인스턴스 전제)

        private void Update()
        {
            // 게임 이벤트 핸들러(수명주기 포함) 예외가 펌프를 죽이지 않게 한다 — 잔여 큐 작업은 다음 프레임에서 재개
            // (생성 코드의 메인 펌프 보호와 동일 패턴)
            try { UniNetEnvironment.PumpMain(); }
            catch (Exception e) { Debug.LogException(e); }
            var server = UniNetEnvironment.Server;
            if (server != null)
            {
                var objects = server.SnapshotObjects();   // 프레임당 1회 — 스윕·틱 공유 (복사 2회 방지)
                var alive = SweepDestroyed(server, objects);   // 파괴분 제거 + 살아있는 엔트리만 틱으로
                RecordRewindHistory(alive, Time.unscaledTimeAsDouble);   // P4 — 리와인드 대상 위치 기록
                server.TickReplication(alive, Time.unscaledTimeAsDouble);   // 실제 시계 제공 — P3 주기·기아 정책 활성
                BroadcastTimeSync(server, Time.unscaledTimeAsDouble);   // P4 — 시간 동기화 (1초 주기)
            }
        }

        /// <summary>P4 시간 동기화 — 서버 권위 시각을 전 연결에 주기 전파 (UE ReplicatedWorldTimeSeconds 상응).</summary>
        private static void BroadcastTimeSync(NetworkServer server, double serverTime)
        {
            if (serverTime < _nextTimeSync) return;
            _nextTimeSync = serverTime + 1.0;
            foreach (var conn in server.SnapshotConnections())
                if (!conn.Disconnected)
                    conn.Channel.SendTimeSync(serverTime);
        }

        /// <summary>P4 — 리와인드 대상(NetworkRewindHistory) 오브젝트의 위치를 서버 도메인 시각으로 기록한다.</summary>
        private static void RecordRewindHistory(IReadOnlyList<(ulong NetId, NetworkServer.ServerObjectEntry Entry)> objects, double serverTime)
        {
            foreach (var (_, entry) in objects)
                if (entry.Instance is NetworkBehaviour nb && nb.NetworkRewindHistory)
                    nb.RecordRewindSample(serverTime);
        }

        private static readonly List<(ulong NetId, NetworkServer.ServerObjectEntry Entry)> AliveScratch = new();

        /// <summary>
        /// 일반 Destroy로 사라진 네트워크 오브젝트를 감지해 파괴를 전파하고, 살아있는 엔트리만 남긴 목록을 반환한다.
        /// 같은 프레임 틱이 파괴된 컴포넌트의 transform 등에 접근해 MissingReferenceException으로 틱 전체가 깨지는 것을 방지한다.
        /// ponytail: 매 프레임 전체 순회(O(n)) — 오브젝트 수가 커지면 파괴 이벤트 후크로 교체. 스크래치는 드라이버 1 인스턴스 전제 재사용.
        /// </summary>
        private static List<(ulong NetId, NetworkServer.ServerObjectEntry Entry)> SweepDestroyed(
            NetworkServer server, IReadOnlyList<(ulong NetId, NetworkServer.ServerObjectEntry Entry)> objects)
        {
            AliveScratch.Clear();
            foreach (var (netId, entry) in objects)
            {
                if (entry.Instance is UnityEngine.Object u && u == null)
                    server.DestroyObject(netId);
                else
                    AliveScratch.Add((netId, entry));
            }
            return AliveScratch;
        }
    }
}
