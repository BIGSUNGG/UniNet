using UnityEditor;
using UnityEngine;
using UniNet.Core.Hosting;

namespace UniNet.Unity.Editor
{
    /// <summary>
    /// UniNet network debugger (ADR-0036) — a live EditorWindow showing connections (RTT, player, ready),
    /// traffic counters, registered objects with owners, and rate-limit drops. Window → UniNet → Network Debugger.
    /// Uses the ADR-0029 statistics API; read-only (no mutations). Opens during Play Mode for live sessions.
    /// </summary>
    public sealed class UniNetDebuggerWindow : EditorWindow
    {
        private bool _autoRefresh = true;
        private Vector2 _scroll;

        [MenuItem("Window/UniNet/Network Debugger")]
        public static void Open() => GetWindow<UniNetDebuggerWindow>("UniNet Debug");

        private void OnEnable() => EditorApplication.playModeStateChanged += OnPlayModeChanged;
        private void OnDisable() => EditorApplication.playModeStateChanged -= OnPlayModeChanged;

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) Repaint();
        }

        private void OnGUI()
        {
            var server = UniNetEnvironment.Server;
            var client = UniNetEnvironment.Client;

            EditorGUILayout.BeginHorizontal();
            _autoRefresh = EditorGUILayout.ToggleLeft("Auto-refresh", _autoRefresh, GUILayout.Width(100));
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) Repaint();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            if (server == null && client == null)
            {
                EditorGUILayout.HelpBox("No network session. Start a server/host/client in Play Mode.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (server != null) DrawServer(server);
            if (client != null) DrawClient(client);

            EditorGUILayout.EndScrollView();

            if (_autoRefresh && Event.current.type == EventType.Layout)
                Repaint();   // next frame refresh
        }

        private void DrawServer(NetworkServer server)
        {
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            // Connections
            var conns = server.SnapshotConnections();
            EditorGUILayout.LabelField($"Connections: {conns.Count}", EditorStyles.miniBoldLabel);
            if (conns.Count > 0)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Conn", GUILayout.Width(50));
                EditorGUILayout.LabelField("RTT", GUILayout.Width(60));
                EditorGUILayout.LabelField("Player", GUILayout.Width(80));
                EditorGUILayout.LabelField("Ready", GUILayout.Width(50));
                EditorGUILayout.LabelField("Rate Drops", GUILayout.Width(80));
                EditorGUILayout.LabelField("Session Token", GUILayout.Width(120));
                EditorGUILayout.EndHorizontal();

                foreach (var conn in conns)
                {
                    if (conn.Disconnected) continue;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(conn.ConnId.ToString(), GUILayout.Width(50));
                    EditorGUILayout.LabelField(conn.RttMs >= 0 ? $"{conn.RttMs:F0}ms" : "…", GUILayout.Width(60));
                    ulong player = server.GetPlayerObject(conn.ConnId);
                    EditorGUILayout.LabelField(player != 0 ? player.ToString() : "—", GUILayout.Width(80));
                    EditorGUILayout.LabelField(server.IsClientSceneReady(conn.ConnId) ? "✓" : "…", GUILayout.Width(50));
                    long drops = server.GetRateLimitedServerRpcDrops(conn.ConnId);
                    EditorGUILayout.LabelField(drops > 0 ? drops.ToString() : "0", GUILayout.Width(80));
                    EditorGUILayout.LabelField($"0x{conn.SessionToken:X}", GUILayout.Width(120));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();
            }

            // Traffic stats
            var stats = server.TrafficStats;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Traffic (UniNet payload)", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Sent:      {stats.BytesSent / 1024.0:F1} KB / {stats.PacketsSent} msgs");
            EditorGUILayout.LabelField($"Received:  {stats.BytesReceived / 1024.0:F1} KB / {stats.PacketsReceived} msgs");
            EditorGUILayout.LabelField($"Deferred queue: {server.DeferredSendCount}");
            EditorGUILayout.LabelField($"Rate drops: {server.RateLimitedServerRpcDrops}");
            EditorGUILayout.EndVertical();

            // Registered objects
            EditorGUILayout.Space(4);
            var objects = server.SnapshotObjects();
            EditorGUILayout.LabelField($"Objects: {objects.Count}", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("netId", GUILayout.Width(80));
            EditorGUILayout.LabelField("Type", GUILayout.Width(160));
            EditorGUILayout.LabelField("Owner", GUILayout.Width(50));
            EditorGUILayout.LabelField("Dynamic", GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            foreach (var (netId, entry) in objects)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(netId.ToString(), GUILayout.Width(80));
                EditorGUILayout.LabelField(entry.Instance?.GetType().Name ?? "<destroyed>", GUILayout.Width(160));
                EditorGUILayout.LabelField(entry.OwnerConnId.ToString(), GUILayout.Width(50));
                EditorGUILayout.LabelField(entry.IsDynamic ? "✓" : "", GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            // Config
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Config", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"Protocol: {server.ExpectedProtocolVersion}");
            EditorGUILayout.LabelField($"RPC rate limit: {server.ServerRpcRateLimitPerSecond}/s (0=off)");
            EditorGUILayout.LabelField($"Grace: {server.SessionGraceSeconds}s (0=off)");
            EditorGUILayout.LabelField($"Pending scene: {server.PendingScene ?? "—"}");

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(8);
        }

        private void DrawClient(NetworkClient client)
        {
            EditorGUILayout.LabelField("Client", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            EditorGUILayout.LabelField($"ConnId: {client.LocalConnId}");
            EditorGUILayout.LabelField($"Session token: 0x{client.SessionToken:X}");
            EditorGUILayout.LabelField($"Server version: {client.ServerProtocolVersion ?? "…"}");
            EditorGUILayout.LabelField($"RTT: {(client.RoundTripTimeMs >= 0 ? $"{client.RoundTripTimeMs:F0}ms" : "…")}");
            EditorGUILayout.LabelField($"Last disconnect: {client.LastDisconnectReason}");
            EditorGUILayout.LabelField($"Traffic: ↓{client.TrafficStats.BytesReceived / 1024.0:F1}KB ↑{client.TrafficStats.BytesSent / 1024.0:F1}KB");

            EditorGUI.indentLevel--;
        }
    }
}
