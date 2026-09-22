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
                switch (role)
                {
                    case ArenaRole.Server:
                        await UniNetManager.ServerAsync(_port);
                        break;
                    case ArenaRole.Client:
                        await UniNetManager.ClientAsync("127.0.0.1", _port);
                        break;
                    case ArenaRole.Host:
                        await UniNetManager.HostAsync(_port);
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
        }

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
            if (Time.unscaledTime < _nextManageUtc) return;
            _nextManageUtc = Time.unscaledTime + 0.5f;
            ManagePlayers();
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

        /// <summary>Server — matches the dynamic player count to the connection count and keeps each connection's viewer position at its owned player's coordinates (P3-①).</summary>
        private void ManagePlayers()
        {
            int connections = 0;
            foreach (var conn in _server.SnapshotConnections())
                if (!conn.Disconnected)
                    connections++;

            var players = FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            // P3-① viewer position — the reference point for cull-distance checks; feeds each connection's owned player coordinates to the server
            foreach (var player in players)
            {
                var ownerConnId = _server.GetEntry(player.NetId)?.OwnerConnId ?? 0;
                if (ownerConnId != 0)
                    _server.SetViewerPosition(ownerConnId, player.transform.position.x, 0f, player.transform.position.z);
            }

            int diff = connections - players.Length;

            for (int i = 0; i < diff; i++)
                SpawnPlayer();

            if (diff < 0)
                DestroySurplus(players, -diff);
        }

        private void SpawnPlayer()
        {
            var (x, z) = ArenaConfig.SpawnPoints[_spawnCounter % ArenaConfig.SpawnPoints.Length];
            var template = new GameObject("ArenaPlayer");
            template.transform.position = new Vector3(x, 0.5f, z);
            template.AddComponent<ArenaPlayer>();

            string displayName = ArenaConfig.DisplayNames[_spawnCounter % ArenaConfig.DisplayNames.Length];
            int colorSeed = _spawnCounter * 173 + 57;   // unique color seed per spawn (InitialOnly — rides on the spawn baseline)
            _spawnCounter++;

            // clone → configure (set non-serialized InitialOnly state) → register and broadcast — one NetworkInstantiate call
            var go = NetworkInstantiate(template,
                clone => clone.GetComponent<ArenaPlayer>().InitServerState(displayName, colorSeed));
            Destroy(template);   // clean up the template — its state was captured into the clone at configure time
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
