using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Communication.Shared.Channels;
using DRPC.Client.Network;
using DRPC.Server.Network;
using DRPC.Shared.Network;
using UniNet.Core;
using UniNet.Core.Hosting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniNet.Unity
{
    /// <summary>
    /// Connection lifecycle entry point — starts the server, client, or host. Transport and security come
    /// from the DRPC (RUDP + DTLS) stack as-is. Incoming RPCs and replication run on the driver's main-thread pump.
    /// </summary>
    public static class UniNetManager
    {
        static UniNetManager()
        {
            // Engine fault logger — Core stays UnityEngine-free; replication-tick serializer faults (ADR-0020)
            // surface as exceptions in the Console instead of silently vanishing into the no-op default.
            UniNetEnvironment.LogFault = ex => Debug.LogException(ex);
        }

        private static RpcListenHandle _listenHandle;
        private static volatile bool _clientConnected;   // shared between the network thread (Disconnected handler) and the main thread — volatile for visibility

        /// <summary>
        /// Client session-ended event — raised on the main thread when the session ends remotely, with the
        /// application-level reason (ADR-0027): Kicked (server DisconnectClient), ServerShutdown (server stop),
        /// or ConnectionLost (no notice — network loss/timeout, or a best-effort notice lost with the connection).
        /// A voluntary <see cref="ClientStop"/> only clears <see cref="IsClientConnected"/> immediately and does NOT
        /// raise this event. Handle reconnect prompts, returning to the main menu, etc. here (connection lifecycle gateway).
        /// </summary>
        public static event Action<UniNetDisconnectReason> ClientDisconnected;

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
            server.ExpectedProtocolVersion = options?.ProtocolVersion ?? UniNet.Core.UniNetInfo.Version;   // ADR-0028 — handshake expectation
            server.VersionDeclareTimeoutMs = options?.VersionDeclareTimeoutMs ?? 10000;   // CON-001 — undeclared-connection kick timeout (0 = hold indefinitely)
            UniNetEnvironment.SetServer(server);
            NetworkBehaviour.RegisterAllToServer();

            var handle = options != null
                ? await RpcHost.ListenWithOptionsAsync<HubBase>(port, options.ToRpcEndpointOptions(),
                    factory.CreateServerHub, OnConnected(server))
                : await RpcHost.ListenAsync<HubBase>(port, null,
                    factory.CreateServerHub, OnConnected(server));

            _listenHandle = handle;
        }

        /// <summary>
        /// Client-side scene transition handler (ADR-0035) — called by the generated hub (wire 11, main thread).
        /// Loads the Unity scene; ADR-0025 re-registration runs automatically on scene load.
        /// </summary>
        public static void OnSceneTransitionRequested(string sceneName)
        {
            Debug.Log($"[UniNet] 씬 전환 지시 — {sceneName}");
            SceneManager.LoadScene(sceneName);
        }

        /// <summary>Sends the scene-loaded report to the server (ADR-0035, wire 12) — call after the scene and registration settle.</summary>
        public static void ReportSceneLoaded(string sceneName)
        {
            var sender = UniNetEnvironment.ClientSender;
            if (sender == null) return;
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteString(sceneName);
            sender.UniNetSend(12, w.ToArray(), global::DRPC.RpcDeliveryMode.ReliableOrdered);
        }

        /// <summary>
        /// Reconnects to the server after a disconnect (ADR-0034) — preserves the session token so the server reattaches
        /// the grace-held session (objects + ownership). The generated client hub automatically sends the resume request.
        /// Returns whether a reconnect was ATTEMPTED AND the connection established — it is NOT the reattach outcome:
        /// observe <see cref="NetworkClient.ResumeResultReceived"/> (or <c>NetworkClient.LastResumeResult</c>) for the
        /// terminal result (REC-001 — Reattached, or the exact rejection reason; a SessionNotFound detach race retries
        /// automatically at 0.5 s intervals until the server's grace window closes).
        /// Returns false when there was no previous session to resume.
        /// </summary>
        public static async Task<bool> ReconnectAsync(string address, int port, UniNetEndpointOptions options = null)
        {
            var prevToken = UniNetEnvironment.Client?.SessionToken ?? string.Empty;
            if (prevToken.Length == 0) return false;   // no stored session — nothing to resume (inactive: empty token)
            await ClientAsync(address, port, options);
            return true;
        }

        /// <summary>Connects as a client to the server at the given address.</summary>
        public static Task ClientAsync(string address, int port)
            => ClientAsync(address, port, null);

        /// <summary>Connects with explicit connection settings.</summary>
        public static async Task ClientAsync(string address, int port, UniNetEndpointOptions options)
        {
            var factory = RequireFactory();
            UniNetDriver.Ensure();
            var prevToken = UniNetEnvironment.Client?.SessionToken ?? string.Empty;
            UniNetEnvironment.SetClient(new NetworkClient());
            UniNetEnvironment.Client.SessionToken = prevToken;   // ADR-0034 — carry for reattach (empty = fresh session)
            UniNetEnvironment.SetClientSender(null);   // so a stale session hub's late Disconnected fails the hub identity filter
            UniNetEnvironment.LocalProtocolVersion = options?.ProtocolVersion ?? UniNet.Core.UniNetInfo.Version;   // ADR-0028 — declared on connect by the generated hub
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
                    // ADR-0027 — the disconnect notice (if any) arrived before the transport close, so the stored reason is final here;
                    // no notice means an unexplained transport loss
                    var client = UniNetEnvironment.Client;
                    var reason = client != null && client.LastDisconnectReason != UniNetDisconnectReason.None
                        ? client.LastDisconnectReason
                        : UniNetDisconnectReason.ConnectionLost;
                    UniNetEnvironment.QueueOnMain(() => RaiseClientDisconnected(reason));   // may arrive on a network thread — marshal to the main queue
                };
                _clientConnected = true;
                return hub;
            }
        }

        private static void RaiseClientDisconnected(UniNetDisconnectReason reason)
        {
            var handlers = ClientDisconnected;
            if (handlers == null) return;
            foreach (Action<UniNetDisconnectReason> handler in handlers.GetInvocationList())
            {
                try { handler(reason); }
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

        /// <summary>Stops the client — closes the hub connection (Disconnect + Dispose) and clears environment state. Idempotent: no-op when not connected. Synchronous — safe in pre-shutdown paths.
        /// Dynamically spawned mirrors are destroyed here — a stale dynamic object keeps its cached netId and
        /// aliases onto the NEXT session's recycled ids (double joins, wrong-owner reads).</summary>
        public static void ClientStop()
        {
            var sender = UniNetEnvironment.ClientSender;
            _clientConnected = false;   // explicit stop — state clears immediately regardless of whether the session-end event fires
            UniNetEnvironment.SetClientSender(null);
            UniNetEnvironment.SetClient(null);
            SweepDynamicSpawns();
            if (sender is global::DRPC.Shared.Network.HubBase hub)
            {
                hub.Disconnect();
                hub.Dispose();
            }
        }

        /// <summary>Stops the dedicated server — tells every client the shutdown is intentional (ADR-0027 notice, flushed to the wire), stops the listener (unbinding the socket), and clears environment state. Idempotent: no-op when not listening.</summary>
        public static async Task ServerStopAsync()
        {
            var handle = _listenHandle;
            _listenHandle = null;
            var server = UniNetEnvironment.Server;
            if (server != null)
            {
                try { await server.BroadcastDisconnectNoticeAsync(UniNetDisconnectReason.ServerShutdown); }
                catch { /* best effort — stop proceeds regardless */ }
                await Task.Delay(150);   // grace — let clients dispatch the notices before the close datagrams arrive (same rationale as DisconnectClient)
            }
            UniNetEnvironment.SetServer(null);
            if (handle != null)
                await handle.DisposeAsync();
        }

        /// <summary>Stops the dedicated server synchronously — for paths that cannot await (e.g. right before application quit). Performs the same cleanup as ServerStopAsync via synchronous Dispose.</summary>
        public static void ServerStop()
        {
            var handle = _listenHandle;
            _listenHandle = null;
            UniNetEnvironment.Server?.BroadcastDisconnectNotice(UniNetDisconnectReason.ServerShutdown);
            UniNetEnvironment.SetServer(null);
            handle?.Dispose();
            SweepDynamicSpawns();   // 서버 쪽 동적 스폰 원본도 마찬가지 — 낡은 netId 재사용 충돌 방지
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

        /// <summary>
        /// Server-side dynamic spawn — clones a spawnable-marked prefab (NetworkSpawnable on the root, ADR-0024),
        /// registers it, and replicates the spawn. Returns the registered instance (the prefab asset stays put).
        /// Objects without a stamped marker — runtime-created templates, unmarked prefabs — are rejected with a warning
        /// (edit-time marking is the only path; a marker added at runtime has no stamped id either).
        /// The clone is a serialization copy, so only public and [SerializeField] fields carry over — initialize private
        /// [Replicated] state in the configure callback (runs right after the clone, before the spawn is replicated).
        /// ALL NetworkBehaviours on the object are registered as slots (multi-component support — slot order = GetComponents order).
        /// Both ends must ship the same prefab — the spawn message identifies it by the stamped prefab id and each
        /// client instantiates its copy from the baked prefab table. Server authority — valid on server/host only.
        /// </summary>
        public static GameObject NetworkInstantiate(GameObject original)
            => NetworkInstantiateCore(original, original.transform.position, original.transform.rotation, null, 0);

        /// <summary>Clones, registers, and replicates with an explicit position and rotation. Returns the registered instance.</summary>
        public static GameObject NetworkInstantiate(GameObject original, Vector3 position, Quaternion rotation)
            => NetworkInstantiateCore(original, position, rotation, null, 0);

        /// <summary>Clones, registers, and replicates with an explicit position, rotation, and configure callback. Returns the registered instance.</summary>
        public static GameObject NetworkInstantiate(GameObject original, Vector3 position, Quaternion rotation, Action<GameObject> configure)
            => NetworkInstantiateCore(original, position, rotation, configure, 0);

        /// <summary>Destroys every dynamically spawned network object — session teardown. Stale dynamic objects
        /// survive scene loads (DontDestroyOnLoad) and alias onto the next session's recycled netIds.</summary>
        static void SweepDynamicSpawns()
        {
            foreach (var nb in UnityEngine.Object.FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!nb.IsDynamicSpawn) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(nb.gameObject);
                else UnityEngine.Object.DestroyImmediate(nb.gameObject);   // edit mode (batch validation) — Destroy is not allowed there
            }
        }

        /// <summary>
        /// Server-side dynamic spawn with an explicit owning connection — the object belongs to that
        /// connection from the first message (ownership survives other clients joining/leaving/rejoining).
        /// </summary>
        public static GameObject NetworkInstantiate(GameObject original, long ownerConnId)
            => NetworkInstantiateCore(original, original.transform.position, original.transform.rotation, null, ownerConnId);

        public static GameObject NetworkInstantiate(GameObject original, Action<GameObject> configure)
            => NetworkInstantiateCore(original, original.transform.position, original.transform.rotation, configure, 0);

        private static GameObject NetworkInstantiateCore(GameObject original, Vector3 position, Quaternion rotation, Action<GameObject> configure, long ownerConnId)
        {
            var server = UniNetEnvironment.Server;
            var marker = original != null ? original.GetComponent<NetworkSpawnable>() : null;
            if (server == null || marker == null || marker.PrefabId == 0)
            {
                Debug.LogWarning($"[UniNet] NetworkInstantiate 는 NetworkSpawnable 이 각인된 프리팹만 스폰할 수 있다 (ADR-0024) — 런타임 생성 오브젝트·미마킹 프리팹은 거부된다: {(original != null ? original.name : "null")}");
                return null;
            }
            var comps = original.GetComponents<NetworkBehaviour>();
            if (comps.Length == 0)
            {
                Debug.LogWarning("[UniNet] spawnable 프리팹 루트에 NetworkBehaviour 가 없다 — 스폰 거부");
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

            var netId = server.RegisterDynamicObject(instComps, ownerConnId, marker.PrefabId);
            for (byte i = 0; i < instComps.Length; i++)
            {
                instComps[i].AssignNetId(netId);
                instComps[i].AssignSubId(i);
                instComps[i].MarkServerRegistered();
            }
            server.BroadcastSpawn(netId);
            return instance;
        }

        /// <summary>Destroys a dynamic object — replicates the destroy to all clients and destroys the local object. Use on objects spawned via NetworkInstantiate (whole object, all subs).</summary>
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

        /// <summary>
        /// Departure cleanup (ADR-0031, main thread) — destroys every DYNAMIC object owned by the connection INCLUDING
        /// the local GameObjects: NetworkDestroy (deregistration + destroy wire + visibility cleanup) + Object.Destroy.
        /// Precise from server-side entries — no scene scans. Call from the ClientDisconnected handler (it fires before
        /// ownership reassignment, so the leaver's objects are identified exactly). Scene objects are untouched (their
        /// ownership falls to the reassignment policy). Returns the number destroyed.
        /// </summary>
        public static int DestroyObjectsOwnedBy(long connId)
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return 0;
            int destroyed = 0;
            foreach (var (netId, entry) in server.SnapshotObjects())   // copy — NetworkDestroy mutates the registry mid-iteration
            {
                if (!entry.IsDynamic || entry.OwnerConnId != connId) continue;
                if (entry.Instance is NetworkBehaviour nb && nb != null)
                {
                    NetworkDestroy(nb.gameObject);
                    destroyed++;
                }
                else if (server.DestroyObject(netId))   // engine-less instance — network-side destroy only (Core companion behavior)
                    destroyed++;
            }
            return destroyed;
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
            SceneManager.sceneLoaded += OnSceneLoaded;   // ADR-0025 — process-lifetime subscription; the driver survives scene reloads
            var go = new GameObject("UniNetDriver") { hideFlags = HideFlags.HideAndDontSave };
            _instance = go.AddComponent<UniNetDriver>();
        }

        /// <summary>Late-scene registration (ADR-0025) — re-runs scene-object registration on every scene load so
        /// NetworkBehaviours in scenes loaded AFTER connecting (lobby → gameplay flow) become reachable on both ends.
        /// Idempotent: objects already registered under the current session are skipped, so only the newly loaded
        /// scene's objects register. Unloaded scenes leave stale dictionary entries behind — harmless (scene ids are
        /// unique per scene asset and never targeted again; the server sweep drops destroyed instances).</summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            string sceneName = scene.name;
            // Deferred to the next main-thread pump drain: a load initiated INSIDE an RPC dispatch (game-side
            // LoadScene in a multicast implementation) would otherwise re-run registration on the pump's callstack,
            // contending with network threads — listen-server clients deadlocked at the loading gate in production
            // (ck-darkness 3-EXE verification, 2026-10-08). The queue drains from a clean driver Update next frame.
            UniNetEnvironment.QueueOnMain(() =>
            {
                int server = NetworkBehaviour.RegisterAllToServer();
                int client = NetworkBehaviour.RegisterAllToClient();
                if (server > 0 || client > 0)
                    Debug.Log($"[UniNet] 씬 로드 네트 등록 — {sceneName}: 서버 +{server}, 클라 +{client}");
                UniNetManager.ReportSceneLoaded(sceneName);   // ADR-0035 — auto-report after registration (server gate checks all connections ready)
            });
        }

        private static double _nextTimeSync;   // P4 — periodic TimeSync broadcast (assumes a single driver instance)
        private static double _nextPing;   // ADR-0029 — periodic client RTT probe (assumes a single driver instance)

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
                server.TickReplication(alive, Time.unscaledTimeAsDouble);
                server.SweepGraceSessions(Time.unscaledTimeAsDouble);   // ADR-0034 — expire grace sessions   // feed the real clock — activates P3 rate and starvation policies
                server.SweepUnapprovedConnections(Time.unscaledTimeAsDouble);   // CON-001 — kick undeclared connections at the same cadence
                BroadcastTimeSync(server, Time.unscaledTimeAsDouble);   // P4 — time sync (once per second)
            }

            // REC-001 — resume retry cadence: while a SessionNotFound retry window is armed (wire 14), re-send the
            // resume request at 0.5 s intervals until the window closes or a terminal result arrives
            UniNetEnvironment.Client?.TickResumeRetry();

            // ADR-0029 — client RTT probe: ping once per second while connected; the generated server hub echoes it
            // (wire 9) and the client handler EMA-smooths the round trip into NetworkClient.RoundTripTimeMs.
            // The probe piggybacks the current RTT so the server keeps a per-connection view (ServerConnection.RttMs).
            if (UniNetManager.IsClientConnected
                && Time.unscaledTimeAsDouble >= _nextPing
                && UniNetEnvironment.ClientSender != null)
            {
                _nextPing = Time.unscaledTimeAsDouble + 1.0;
                var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
                w.WriteDouble(StopwatchElapsedSeconds());   // probe origin — Stopwatch domain: readable from any thread (the echo handler runs on the dispatch thread, where Unity clocks throw)
                w.WriteDouble(UniNetEnvironment.Client.RoundTripTimeMs);
                UniNetEnvironment.ClientSender.UniNetSend(8, w.ToArray(), global::DRPC.RpcDeliveryMode.ReliableOrdered);
            }
        }

        /// <summary>Monotonic thread-safe clock (ADR-0029 RTT probe domain) — Stopwatch-based, independent of Unity's main-thread-only clocks.</summary>
        private static double StopwatchElapsedSeconds()
            => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

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
