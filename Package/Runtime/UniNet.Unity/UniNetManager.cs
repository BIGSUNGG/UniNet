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
    /// Connection lifecycle entry point — starts the server, client, or host. Transport and security come
    /// from the DRPC (RUDP + DTLS) stack as-is. Incoming RPCs and replication run on the driver's main-thread pump.
    /// </summary>
    public static class UniNetManager
    {
        private static RpcListenHandle _listenHandle;
        private static volatile bool _clientConnected;   // shared between the network thread (Disconnected handler) and the main thread — volatile for visibility

        /// <summary>
        /// Client session-ended event — raised on the main thread when the session ends remotely (server shutdown, network loss).
        /// A voluntary <see cref="ClientStop"/> only clears <see cref="IsClientConnected"/> immediately and does NOT raise this event.
        /// Handle reconnect prompts, returning to the main menu, etc. here (connection lifecycle gateway).
        /// </summary>
        public static event Action ClientDisconnected;

        /// <summary>True while the client is connected to the server with a live session (true after ClientAsync succeeds, false once the connection ends).</summary>
        public static bool IsClientConnected => _clientConnected;

        /// <summary>Starts a dedicated server and waits for client connections (server authority).</summary>
        public static Task ServerAsync(int port)
            => ServerAsync(port, null);

        /// <summary>Starts a server with explicit connection settings (pre-shared key, DTLS, timeouts, etc.).</summary>
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

        /// <summary>Connects as a client to the server at the given address.</summary>
        public static Task ClientAsync(string address, int port)
            => ClientAsync(address, port, null);

        /// <summary>Connects with explicit connection settings.</summary>
        public static async Task ClientAsync(string address, int port, UniNetEndpointOptions options)
        {
            var factory = RequireFactory();
            UniNetDriver.Ensure();
            UniNetEnvironment.SetClient(new NetworkClient());
            UniNetEnvironment.SetClientSender(null);   // so a stale session hub's late Disconnected fails the hub identity filter
            NetworkBehaviour.RegisterAllToClient();

            // Wrap client hub creation to observe session end (Disconnected) — mirrors the server-side OnConnected wiring
            Task connect = options != null
                ? RpcClient.ConnectWithOptionsAsync<HubBase>(address, port, options.ToRpcEndpointOptions(), CreateWatchedHub)
                : RpcClient.ConnectAsync<HubBase>(address, port, null, CreateWatchedHub);

            try
            {
                await connect;
            }
            catch
            {
                _clientConnected = false;   // no stuck "connected" state even if the failed hub never raised Disconnected
                throw;
            }

            return;

            // Attach a hub-specific end observer the moment the hub is created. The flag is confirmed right after subscribing,
            // so a Disconnected observed on the network thread before the await resumes is not swallowed by the guard.
            // volatile guarantees visibility between the main and network threads; the tiny window between the factory
            // returning and the assignment is covered by the connect-failure catch.
            HubBase CreateWatchedHub(IMessageChannel channel)
            {
                var hub = factory.CreateClientHub(channel);
                hub.Disconnected += () =>
                {
                    if (!ReferenceEquals(hub, UniNetEnvironment.ClientSender)) return;   // ignore late observation from a stale session hub
                    if (!_clientConnected) return;   // absorb duplicate fires and voluntary ClientStop — ClientStop clears the flag first
                    _clientConnected = false;
                    UniNetEnvironment.QueueOnMain(RaiseClientDisconnected);   // may arrive on a network thread — marshal to the main queue
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
                catch (Exception e) { Debug.LogException(e); }   // isolate each subscriber — same pattern as the generated code's main-pump guard
            }
        }

        /// <summary>Starts as a listen server — server and client in one process (for development and testing — loopback RUDP round-trips).</summary>
        public static Task HostAsync(int port)
            => HostAsync(port, null, null);

        /// <summary>Starts a listen server with explicit connection settings (applied to the server and client sides separately).</summary>
        public static async Task HostAsync(int port, UniNetEndpointOptions serverOptions, UniNetEndpointOptions clientOptions)
        {
            await ServerAsync(port, serverOptions);
            await ClientAsync("127.0.0.1", port, clientOptions);
        }

        /// <summary>Stops the client — closes the hub connection (Disconnect + Dispose) and clears environment state. Idempotent: no-op when not connected. Synchronous — safe in pre-shutdown paths.</summary>
        public static void ClientStop()
        {
            var sender = UniNetEnvironment.ClientSender;
            _clientConnected = false;   // explicit stop — state clears immediately regardless of whether the session-end event fires
            UniNetEnvironment.SetClientSender(null);
            UniNetEnvironment.SetClient(null);
            if (sender is global::DRPC.Shared.Network.HubBase hub)
            {
                hub.Disconnect();
                hub.Dispose();
            }
        }

        /// <summary>Stops the dedicated server — stops the listener (unbinding the socket) and clears environment state. Idempotent: no-op when not listening.</summary>
        public static async Task ServerStopAsync()
        {
            var handle = _listenHandle;
            _listenHandle = null;
            UniNetEnvironment.SetServer(null);
            if (handle != null)
                await handle.DisposeAsync();
        }

        /// <summary>Stops the dedicated server synchronously — for paths that cannot await (e.g. right before application quit). Performs the same cleanup as ServerStopAsync via synchronous Dispose.</summary>
        public static void ServerStop()
        {
            var handle = _listenHandle;
            _listenHandle = null;
            UniNetEnvironment.SetServer(null);
            handle?.Dispose();
        }

        /// <summary>Stops the host (server + client) — ClientStop + ServerStopAsync. Use HostStop on synchronous paths.</summary>
        public static async Task HostStopAsync()
        {
            ClientStop();
            await ServerStopAsync();
        }

        /// <summary>Stops the host (server + client) synchronously — for paths right before application quit.</summary>
        public static void HostStop()
        {
            ClientStop();
            ServerStop();
        }

        /// <summary>Registers a prefab for client-side dynamic spawns — dynamic spawns of type T are created from this prefab on clients (if unregistered: an empty GameObject + AddComponent). Call it on both ends with the same code.</summary>
        public static void RegisterPrefab<T>(GameObject prefab) where T : NetworkBehaviour
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            UniNetSpawnRegistry.Override(typeof(T), () => UnityEngine.Object.Instantiate(prefab).GetComponent<T>());
        }

        /// <summary>
        /// Server-side dynamic spawn — clones the original (prefab or template), registers it, and replicates the spawn. Returns the registered instance (the original stays behind — clean it up on the caller side).
        /// The clone is a serialization copy, so only public and [SerializeField] fields carry over — initialize private [Replicated] state in the configure callback (runs right after the clone, before the spawn is replicated).
        /// ALL NetworkBehaviours on the object are registered as slots (multi-component support — slot order = GetComponents order).
        /// Multi-component objects need the same prefab registered on both ends via RegisterPrefab (the default factory creates single-component objects only). Server authority — valid on server/host only.
        /// </summary>
        public static GameObject NetworkInstantiate(GameObject original)
            => NetworkInstantiateCore(original, original.transform.position, original.transform.rotation, null);

        /// <summary>Clones, registers, and replicates with an explicit position and rotation. Returns the registered instance.</summary>
        public static GameObject NetworkInstantiate(GameObject original, Vector3 position, Quaternion rotation)
            => NetworkInstantiateCore(original, position, rotation, null);

        /// <summary>
        /// Clones, registers, and replicates with a configure callback — the callback runs on the clone right after instantiation, before the registered spawn is broadcast.
        /// Private [Replicated] fields (non-serialized values such as InitialOnly initial state) must be set here to land in the spawn baseline. Returns the registered instance.
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
                else UnityEngine.Object.DestroyImmediate(instance);   // edit mode (batch validation) — Destroy is not allowed there
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

        /// <summary>Server-side network destroy — replicates the destroy to all clients and destroys the local object. Use on objects spawned via NetworkInstantiate (whole object, all subs).</summary>
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
    /// Hidden driver — drives the main-thread pump (executes incoming dispatches) and the server replication tick from Update.
    /// </summary>
    internal sealed class UniNetDriver : MonoBehaviour
    {
        private static UniNetDriver _instance;

        /// <summary>Creates the driver if missing (once, at first network start). Hidden with HideAndDontSave so it survives scene reloads and is excluded from saves.</summary>
        public static void Ensure()
        {
            if (_instance != null) return;
            UniNetTime.LocalClock = () => Time.unscaledTimeAsDouble;   // P4 — swap the core clock for Unity's unscaled clock (shared process-wide)
            var go = new GameObject("UniNetDriver") { hideFlags = HideFlags.HideAndDontSave };
            _instance = go.AddComponent<UniNetDriver>();
        }

        private static double _nextTimeSync;   // P4 — periodic TimeSync broadcast (assumes a single driver instance)

        private void Update()
        {
            // An exception thrown by a game event handler (including lifecycle events) must not kill the pump — remaining
            // queued work resumes next frame (same pattern as the generated code's main-pump guard)
            try { UniNetEnvironment.PumpMain(); }
            catch (Exception e) { Debug.LogException(e); }
            var server = UniNetEnvironment.Server;
            if (server != null)
            {
                var objects = server.SnapshotObjects();   // once per frame — shared by the sweep and the tick (avoids a second copy)
                var alive = SweepDestroyed(server, objects);   // drop destroyed entries and tick only the living ones
                RecordRewindHistory(alive, Time.unscaledTimeAsDouble);   // P4 — record positions for rewind-tracked objects
                server.TickReplication(alive, Time.unscaledTimeAsDouble);   // feed the real clock — activates P3 rate and starvation policies
                BroadcastTimeSync(server, Time.unscaledTimeAsDouble);   // P4 — time sync (once per second)
            }
        }

        /// <summary>P4 time sync — periodically replicates the server-authoritative clock to every connection (UE ReplicatedWorldTimeSeconds equivalent).</summary>
        private static void BroadcastTimeSync(NetworkServer server, double serverTime)
        {
            if (serverTime < _nextTimeSync) return;
            _nextTimeSync = serverTime + 1.0;
            foreach (var conn in server.SnapshotConnections())
                if (!conn.Disconnected)
                    conn.Channel.SendTimeSync(serverTime);
        }

        /// <summary>P4 — records positions of rewind-tracked objects (NetworkRewindHistory) in server-domain time.</summary>
        private static void RecordRewindHistory(IReadOnlyList<(ulong NetId, NetworkServer.ServerObjectEntry Entry)> objects, double serverTime)
        {
            foreach (var (_, entry) in objects)
                if (entry.Instance is NetworkBehaviour nb && nb.NetworkRewindHistory)
                    nb.RecordRewindSample(serverTime);
        }

        private static readonly List<(ulong NetId, NetworkServer.ServerObjectEntry Entry)> AliveScratch = new();

        /// <summary>
        /// Detects network objects that vanished via a plain Destroy, replicates their destruction, and returns only the living entries.
        /// Prevents a same-frame tick from touching a destroyed component's transform and blowing up the whole tick with a MissingReferenceException.
        /// ponytail: full O(n) scan every frame — swap for destroyed-event hooks if object counts grow. Scratch list reused (single driver instance assumed).
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
