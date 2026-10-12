using System;
using System.Collections.Generic;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using static UniNet.Unity.Net;   // import NetworkInstantiate/NetworkDestroy for qualifier-free calls — like regular Instantiate/Destroy

namespace Arena
{
    /// <summary>
    /// Arena bootstrap — one per scene; starts UniNet according to the resolved role.
    /// The server (authority) keeps the player count equal to the connection count:
    /// spawns on join, destroys surplus avatars on leave.
    /// The library's round-robin policy assigns ownership, so the game never assumes
    /// "who owns what" — clients find their own avatar via IsOwner (name and color are avatar-unique values).
    /// </summary>
    public sealed class ArenaBootstrap : MonoBehaviour
    {
        [SerializeField] private ArenaRole _role = ArenaRole.Auto;
        [SerializeField] private int _port = ArenaConfig.Port;

        /// <summary>Server instance (null in client role) — used for player count management.</summary>
        private NetworkServer _server;
        private float _nextManageUtc;
        private float _nextStatsAt = 3f;   // ADR-0029 — periodic stats line (server log/HUD + client RTT)
        private int _spawnCounter;
        private ArenaRole _resolvedRole;
        private bool _started;   // whether listen/connect actually started (Stop only cleans up what was started)

        private async void Start()
        {
            var role = ArenaRoleResolver.Resolve(_role);
            _resolvedRole = role;
            ArenaHud.SetRole(role);
            Debug.Log($"[Arena] 부트스트랩 시작 role={role} port={_port}");

            try
            {
                // ADR-0028 — the demo declares its build version on both ends (mismatched builds reject with a VersionMismatch kick)
                var options = new UniNet.Core.Hosting.UniNetEndpointOptions { ProtocolVersion = ArenaConfig.ProtocolVersion };
                switch (role)
                {
                    case ArenaRole.Server:
                        await UniNetManager.ServerAsync(_port, options);
                        break;
                    case ArenaRole.Client:
                        await UniNetManager.ClientAsync("127.0.0.1", _port, options);
                        break;
                    case ArenaRole.Host:
                        await UniNetManager.HostAsync(_port, options, options);
                        break;
                }
                _started = true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                enabled = false;
                return;
            }

            _server = UniNetEnvironment.Server;
            if (_server != null)
            {
                // P3-⑤ channel budget — per-type bandwidth management example limiting per-tick bytes for the player type
                // (retargeted from bullets to players after the P4 switch removed bullets)
                _server.SetReplicationChannelBudget(typeof(ArenaPlayer), ArenaConfig.PlayerChannelBudgetPerTickBytes);

                // Connection lifecycle gateway — spawn avatars and update the HUD on join, precisely destroy the departed connection's avatar on leave
                _server.ClientConnected += OnClientConnected;
                _server.ClientDisconnected += OnClientDisconnected;
            }

            // ADR-0027 — surface the session-end reason to the player (kick vs. server shutdown vs. connection loss UX example).
            // Subscribed for the component lifetime; static event — unsubscribed in OnDestroy to avoid leaking across scene reloads.
            UniNetManager.ClientDisconnected += OnClientSessionEnded;
        }

        private void OnDestroy()
        {
            UniNetManager.ClientDisconnected -= OnClientSessionEnded;
        }

        /// <summary>Client session ended — shows WHY on the HUD (the reason rides the disconnect notice, ADR-0027).</summary>
        private static void OnClientSessionEnded(UniNet.Core.UniNetDisconnectReason reason)
            => ArenaHud.NoteLifecycle($"연결 종료: {ReasonText(reason)}");

        private static string ReasonText(UniNet.Core.UniNetDisconnectReason reason) => reason switch
        {
            UniNet.Core.UniNetDisconnectReason.Kicked => "서버에 의해 퇴장됨",
            UniNet.Core.UniNetDisconnectReason.ServerShutdown => "서버 종료",
            UniNet.Core.UniNetDisconnectReason.ConnectionLost => "연결 상실",
            _ => reason.ToString(),
        };

        /// <summary>Play mode exit — stops listeners and connections so the socket unbinds (restarting on the same port fails to bind otherwise).</summary>
        private void OnApplicationQuit()
        {
            if (!_started) return;            switch (_resolvedRole)
            {
                case ArenaRole.Server:
                    UniNetManager.ServerStop();
                    break;
                case ArenaRole.Client:
                    UniNetManager.ClientStop();
                    break;
                case ArenaRole.Host:
                    UniNetManager.HostStop();
                    break;
            }
            Debug.Log($"[Arena] 부트스트랩 정지 role={_resolvedRole}");
        }

        private void Update()
        {
            if (_server == null) return;
            if (UniNetEnvironment.Server != _server) return;   // step aside if another runtime (e.g. tests) has taken over the environment

            // ADR-0030 — ownership-transfer demo (pickup-style): K rotates the first avatar's owner to the next connection
            if (Input.GetKeyDown(KeyCode.K))
            {
                var players = FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (players.Length > 0)
                {
                    var entry = _server.GetEntry(players[0].NetId);
                    var conns = _server.SnapshotConnections();
                    if (entry != null && conns.Count > 0)
                    {
                        long prev = entry.OwnerConnId;
                        int idx = 0;
                        for (int i = 0; i < conns.Count; i++)
                            if (conns[i].ConnId == prev) idx = i;
                        long next = conns[(idx + 1) % conns.Count].ConnId;
                        _server.SetOwner(players[0].NetId, next);
                        ArenaHud.NoteLifecycle($"소유권 이전 (K): {prev} → {next}");
                    }
                }
            }

            if (Time.unscaledTime < _nextManageUtc) return;
            _nextManageUtc = Time.unscaledTime + 0.5f;
            ManagePlayers();
            PublishNetStats();
        }

        /// <summary>
        /// ADR-0029 usage example — surfaces the statistics API every few seconds: server traffic counters, budget-deferral
        /// queue depth, and per-connection RTT (client-reported probe). Logged for headless servers and shown on the HUD.
        /// </summary>
        private void PublishNetStats()
        {
            if (Time.unscaledTime < _nextStatsAt) return;
            _nextStatsAt = Time.unscaledTime + 5f;

            var st = _server.TrafficStats;
            double rtt = -1;
            foreach (var conn in _server.SnapshotConnections())
                if (!conn.Disconnected && conn.RttMs >= 0)
                    rtt = rtt < 0 ? conn.RttMs : System.Math.Max(rtt, conn.RttMs);   // worst-case view — a demo choice

            string line = $"[Arena] 네트워크 — \u2191{st.BytesSent / 1024.0:F1}KB/{st.PacketsSent}pkt \u2193{st.BytesReceived / 1024.0:F1}KB/{st.PacketsReceived}pkt "
                          + $"지연큐 {_server.DeferredSendCount} RTT {(rtt < 0 ? "측정중" : rtt.ToString("F0") + "ms")}"
                          + (UniNetEnvironment.Client != null && UniNetManager.IsClientConnected
                              ? $" | 클라 RTT {UniNetEnvironment.Client.RoundTripTimeMs:F0}ms"
                              : "");
            ArenaHud.LastNetStats = line;
            Debug.Log(line);
        }

        /// <summary>Client connected — update the HUD and run the management cycle immediately so the avatar spawns without waiting for the next poll.</summary>
        private void OnClientConnected(long connId)
        {
            ArenaHud.NoteLifecycle($"+ 플레이어 접속 (연결 {connId})");
            Debug.Log($"[Arena] 클라 접속 connId={connId}");
            _nextManageUtc = 0f;
        }

        /// <summary>Client disconnected — immediately destroys avatars owned by the departed connection (the event fires before
        /// ownership is reassigned, so the owner lookup identifies them exactly). Unhandled, the avatar would be
        /// reassigned to another connection and linger as a zombie.</summary>
        private void OnClientDisconnected(long connId)
        {
            ArenaHud.NoteLifecycle($"- 플레이어 퇴장 (연결 {connId})");
            Debug.Log($"[Arena] 클라 퇴장 connId={connId}");

            foreach (var player in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var entry = _server.GetEntry(player.NetId);
                if (entry == null || !entry.IsDynamic || entry.OwnerConnId != connId) continue;
                Debug.Log($"[Arena] 퇴장 플레이어 아바타 파괴 connId={connId} name={player.DisplayName}");
                UniNetManager.NetworkDestroy(player.gameObject);
            }
        }

        /// <summary>Server — matches the dynamic player count to the connection count. (Viewer positions were removed with the
        /// grid demo: the small arena sets no cull distances, so per-connection viewers were dead weight — ADR-0026.)</summary>
        private void ManagePlayers()
        {
            int connections = 0;
            foreach (var conn in _server.SnapshotConnections())
                if (!conn.Disconnected)
                    connections++;

            var players = FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            int diff = connections - players.Length;

            for (int i = 0; i < diff; i++)
                SpawnPlayer();

            if (diff < 0)
                DestroySurplus(players, -diff);
        }

        private const string PlayerPrefabPath = "Assets/Scripts/Arena/ArenaPlayerPrefab.prefab";

#if UNITY_EDITOR
        private static GameObject LoadPlayerPrefab()
            => UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
#else
        private static GameObject LoadPlayerPrefab() => null;   // the Arena demo is an editor sandbox — never built for players
#endif

        private void SpawnPlayer()
        {
            var (x, z) = ArenaConfig.SpawnPoints[_spawnCounter % ArenaConfig.SpawnPoints.Length];

            string displayName = ArenaConfig.DisplayNames[_spawnCounter % ArenaConfig.DisplayNames.Length];
            int colorSeed = _spawnCounter * 173 + 57;   // unique color seed per spawn (InitialOnly — rides on the spawn baseline)
            _spawnCounter++;

            // prefab → configure (set non-serialized InitialOnly state) → register and broadcast — one NetworkInstantiate call (ADR-0024)
            var go = NetworkInstantiate(LoadPlayerPrefab(),
                new Vector3(x, 0.5f, z), Quaternion.identity,
                clone => clone.GetComponent<ArenaPlayer>().InitServerState(displayName, colorSeed));
            var player = go.GetComponent<ArenaPlayer>();
            Debug.Log($"[Arena] 플레이어 스폰 name={player.DisplayName} netId={player.NetId}");
        }

        private static void DestroySurplus(ArenaPlayer[] players, int count)
        {
            // remove surplus starting with the most recently spawned (highest netId) — remaining players are handed to reassigned owners
            System.Array.Sort(players, (a, b) => b.NetId.CompareTo(a.NetId));
            var destroyed = new HashSet<ulong>();
            for (int i = 0; i < count && i < players.Length; i++)
            {
                if (!destroyed.Add(players[i].NetId)) continue;
                Debug.Log($"[Arena] 잉여 플레이어 파괴 name={players[i].DisplayName} netId={players[i].NetId}");
                NetworkDestroy(players[i].gameObject);
            }
        }
    }
}
