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
                // CON-002 — demo approval gate (ADR-0047): the server admits only clients presenting a credential,
                // and the client presents one. One options object serves both ends — each side reads only its own
                // field (HostAsync passes it to ServerAsync and ClientAsync). Games swap the demo predicate for
                // real token/DB validation; the callback runs on the main thread.
                options.ConnectionApprover = request =>
                {
                    bool admit = !string.IsNullOrEmpty(request.Credential);
                    Debug.Log($"[UNINET] 접속 승인 콜백 — connId={request.ConnId} credentialProvided={request.Credential.Length > 0} → {(admit ? "승인" : "거부 (ApprovalRejected)")}");
                    return System.Threading.Tasks.Task.FromResult(admit);
                };
                options.ConnectionCredential = "arena-demo";
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
                _server.ServerRpcRateLimitPerSecond = ArenaConfig.ServerRpcRateLimitPerSecond;   // ADR-0032 — generous gameplay ceiling, floods dropped
                _server.SessionGraceSeconds = 10;   // ADR-0034 — reconnect grace (mobile network switches)

                // ADR-0035 — scene transition gate: all clients loaded → start the round (demo wiring)
                _server.AllClientsSceneLoaded += scene => ArenaHud.NoteLifecycle($"씬 전환 완료: {scene} (전원 준비)");

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

            // ADR-0031 — departure cleanup via the mapping API: precise from server-side entries (no scene scans),
            // local GameObjects included. Fires before ownership reassignment (lifecycle contract).
            int destroyed = UniNetManager.DestroyObjectsOwnedBy(connId);
            if (destroyed > 0)
                Debug.Log($"[Arena] 퇴장 플레이어 아바타 파괴 connId={connId} count={destroyed}");
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

        // ARENA-001 — prefab loading lives in ArenaPrefabs (Resources.Load): one path for the editor AND player
        // builds, replacing the editor-only AssetDatabase load that returned null in players and broke every spawn.

        private void SpawnPlayer()
        {
            var (x, z) = ArenaConfig.SpawnPoints[_spawnCounter % ArenaConfig.SpawnPoints.Length];

            string displayName = ArenaConfig.DisplayNames[_spawnCounter % ArenaConfig.DisplayNames.Length];
            int colorSeed = _spawnCounter * 173 + 57;   // unique color seed per spawn (InitialOnly — rides on the spawn baseline)
            _spawnCounter++;

            // prefab → configure (set non-serialized InitialOnly state) → register and broadcast — one NetworkInstantiate call (ADR-0024)
            var go = NetworkInstantiate(ArenaPrefabs.LoadPlayer(),
                new Vector3(x, 0.5f, z), Quaternion.identity,
                clone => clone.GetComponent<ArenaPlayer>().InitServerState(displayName, colorSeed));
            var player = go.GetComponent<ArenaPlayer>();

            // ADR-0031 — register the connection↔avatar mapping (connId ↔ netId) — the framework-owned replacement
            // for game-side dictionaries and the "find my avatar" scan pattern
            var ownerConnId = _server.GetEntry(player.NetId)?.OwnerConnId ?? 0;
            if (ownerConnId != 0)
                _server.SetPlayerObject(ownerConnId, player.NetId);

            Debug.Log($"[Arena] 플레이어 스폰 name={player.DisplayName} netId={player.NetId} owner={ownerConnId}");
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

#if UNITY_EDITOR
        // ── SEC-001 (ADR-0041) in-editor diagnostic — real server/client/RUDP APIs on a side port ─────────────
        // Proves: a forged/guessed session token cannot resume a victim's grace session (ownership unchanged),
        // while the genuine token reattaches it over the real wire. The ownership subject is a NON-DYNAMIC marker
        // registered in the real server registry — ClientStop's dynamic-spawn sweep would destroy a dynamic avatar
        // (existing LIF/REC gap) and break the grace assertion, so this verifies MARKER-based ownership + resume +
        // player mapping, NOT full avatar recovery (that remains REC-001). Token values are never logged.
        // Precondition/behavior: an active demo session (any role) is EXPLICITLY and gracefully stopped first —
        // never silently overwritten (an orphaned listener would keep accepting connections until play ends).
        private const int DiagPort = ArenaConfig.Port + 7;
        private static bool _sec001Running;   // re-entry guard — ContextMenu double-click protection

        [ContextMenu("Diag/SEC-001 위조 토큰 재개 거부 (기존 세션 정지 후)")]
        private async void RunSec001TokenDiagnostic()
        {
            const string tag = "[UNINET-VERIFY] SEC-001";
            if (!Application.isPlaying)   // ContextMenu also shows in Edit Mode — the documented Play-Mode precondition, enforced: nothing is started or stopped otherwise
            { Debug.LogError($"{tag} SKIP — Play Mode 전용 (씬 진입 후 플레이 상태에서 실행)"); return; }
            if (_sec001Running) { Debug.LogError($"{tag} SKIP — 진단 이미 실행 중 (재진입 차단)"); return; }
            if (_own001Running) { Debug.LogError($"{tag} SKIP — OWN-001 진단 실행 중 (상호 충돌 방지)"); return; }
            _sec001Running = true;
            try
            {
                // 0) stop any ACTIVE demo session gracefully (the same stop path as OnApplicationQuit, awaited) so the
                // diagnostic never orphans the demo listener — announced, never a silent overwrite
                if (UniNetEnvironment.Server != null || UniNetEnvironment.Client != null)
                {
                    Debug.Log($"{tag} 기존 데모 런타임 정상 정지 (role={_resolvedRole}) 후 진단 시작");
                    if (_started)
                    {
                        switch (_resolvedRole)
                        {
                            case ArenaRole.Server: await UniNetManager.ServerStopAsync(); break;
                            case ArenaRole.Host: await UniNetManager.HostStopAsync(); break;
                            default: UniNetManager.ClientStop(); break;
                        }
                    }
                    else
                    {
                        UniNetManager.ClientStop();
                        await UniNetManager.ServerStopAsync();
                    }
                }
                var options = new UniNet.Core.Hosting.UniNetEndpointOptions { ProtocolVersion = ArenaConfig.ProtocolVersion };

                // 1) dedicated diagnostic runtime on a side port (grace 10 s) + a NON-DYNAMIC scene-style marker in the
                // real server registry — it survives ClientStop's dynamic-spawn sweep (a dynamic avatar would not)
                await UniNetManager.ServerAsync(DiagPort, options);
                var server = UniNetEnvironment.Server;
                server.SessionGraceSeconds = 10;
                ulong netId = Fnv1a.Hash64("arena-sec001-ownership-marker");   // deterministic side id — no scene/prefab dependency
                server.RegisterSceneObject(netId, new object[] { new object() });

                // 2) victim connects — round-robin assigns the marker's ownership (only connection)
                await UniNetManager.ClientAsync("127.0.0.1", DiagPort, options);
                if (!await WaitAsync(() => UniNetEnvironment.Client != null && UniNetEnvironment.Client.LocalConnId != 0, 5, "피해자 Welcome")) return;
                long victimConn = UniNetEnvironment.Client.LocalConnId;
                string realToken = UniNetEnvironment.Client.SessionToken;   // secret — used in-process only, never logged
                server.SetPlayerObject(victimConn, netId);
                if (server.GetOwner(netId) != victimConn) { Debug.LogError($"{tag} 준비 실패 — 마커 소유자≠피해자"); return; }

                // 3) victim drops — grace must hold the ownership (no other connection to reassign to)
                UniNetManager.ClientStop();
                if (!await WaitGoneAsync(victimConn, 5, "피해자 해제 관측")) return;
                if (server.GetOwner(netId) != victimConn) { Debug.LogError($"{tag} 유예 보존 실패"); return; }

                // 4) attacker joins twice with forged tokens — ownership must NOT move (SEC-001)
                char[] mutated = realToken.ToCharArray();
                mutated[0] = mutated[0] == 'A' ? 'B' : 'A';
                string[] forgeries = { new string('A', 43) + "=", new string(mutated) };
                for (int i = 0; i < forgeries.Length; i++)
                {
                    UniNetEnvironment.SetClient(new NetworkClient { SessionToken = forgeries[i] });
                    await UniNetManager.ClientAsync("127.0.0.1", DiagPort, options);
                    if (!await WaitAsync(() => UniNetEnvironment.Client != null && UniNetEnvironment.Client.LocalConnId != 0, 5, $"공격자{i} Welcome")) return;
                    long attackerConn = UniNetEnvironment.Client.LocalConnId;
                    await System.Threading.Tasks.Task.Delay(600);   // let the (forged) resume reach the server
                    long owner = server.GetOwner(netId);
                    if (owner == attackerConn) { Debug.LogError($"{tag} FAIL — 위조 토큰{i}로 소유권 탈취됨 (SEC-001 잔존)"); return; }
                    if (owner != victimConn) { Debug.LogError($"{tag} FAIL — 예상 밖 재배정 owner={owner}"); return; }
                    UniNetManager.ClientStop();
                    if (!await WaitGoneAsync(attackerConn, 5, $"공격자{i} 해제 관측")) return;
                }
                Debug.Log($"{tag} 위조 토큰 2종(전제 추측·근접 변조) 재개 거부 — 피해자 마커 소유권 유지 PASS");

                // 5) genuine token reattach via the documented ReconnectAsync path (marker ownership + player mapping)
                UniNetEnvironment.SetClient(new NetworkClient { SessionToken = realToken });
                bool reconnected = await UniNetManager.ReconnectAsync("127.0.0.1", DiagPort, options);
                if (!reconnected || !await WaitAsync(() => server.GetOwner(netId) == UniNetEnvironment.Client?.LocalConnId, 5, "진짜 토큰 재부착"))
                { Debug.LogError($"{tag} FAIL — 진짜 토큰 재부착 실패 (토큰이 공격 중 소진됨?)"); return; }
                long resumedConn = UniNetEnvironment.Client.LocalConnId;
                if (server.GetPlayerObject(resumedConn) != netId) { Debug.LogError($"{tag} FAIL — 플레이어 매핑 미복원"); return; }

                Debug.Log($"{tag} 진짜 토큰 재부착 — 마커 소유권·플레이어 매핑 복원 PASS (위조 시도 후 세션 미소진)");
                Debug.Log($"[UNINET-VERIFY] SEC-001 DIAG PASS — 위조 토큰 거부·피해자 소유권 유지·진짜 토큰 재부착 (마커 기반·아바타 복구 아님·토큰 원문 미출력)");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError($"{tag} 예외로 중단");
            }
            finally
            {
                UniNetManager.ClientStop();
                UniNetManager.ServerStop();
                _sec001Running = false;
                Debug.Log($"{tag} 진단 종료 — 런타임 정리 (데모 부트스트랩은 재플레이로 복귀)");
            }
        }

        private static async System.Threading.Tasks.Task<bool> WaitAsync(System.Func<bool> until, float seconds, string what, string tag = "SEC-001")
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (until()) return true;
                await System.Threading.Tasks.Task.Delay(50);
            }
            Debug.LogError($"[UNINET-VERIFY] {tag} 대기 실패 — {what}");
            return false;
        }

        private static System.Threading.Tasks.Task<bool> WaitGoneAsync(long connId, float seconds, string what, string tag = "SEC-001")
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return WaitAsync(() => false, seconds, what, tag);
            return WaitAsync(() =>
            {
                foreach (var conn in server.SnapshotConnections())
                    if (conn.ConnId == connId) return false;
                return true;
            }, seconds, what, tag);
        }
#endif

#if UNITY_EDITOR
        // ── OWN-001 ownership baseline — in-editor diagnostic, real server/client/RUDP APIs on a side port ────
        // Proves over the real wire: a late-joining client receives the EXISTING owner of a scene object as its
        // baseline (owner notice before state — grace-held in this single-process reproduction), and the scene
        // relevancy re-entry path runs the same shared baseline over the real stack. Scope honesty: one real client
        // per process (singleton) — the KEEP path is reproduced as first-client graceful drop → grace-held ownership
        // → second fresh join; the live-owner variant is covered by the EditMode regressions and the final
        // multi-process check is a separate step (this is NOT the two-client avatar verification).
        private const int OwnDiagPort = ArenaConfig.Port + 8;
        private static bool _own001Running;   // re-entry guard — ContextMenu double-click protection

        /// <summary>Plain marker with a switchable relevancy hook — a real scene-registry object, no scene/prefab asset touched.</summary>
        private sealed class Own001Marker : IUniNetReplicationPolicy
        {
            internal bool Blocked;
            public float NetworkPriority => 1f;
            public float NetworkUpdateFrequencyHz => 0f;
            public UniNet.Core.Delivery NetworkReplicationDelivery => UniNet.Core.Delivery.ReliableOrdered;
            public float NetworkCullDistance => 0f;
            public bool IsNetworkDormant => false;
            public bool IsNetworkRelevant(long viewerConnId) => !Blocked;
        }

        [ContextMenu("Diag/OWN-001 후발 소유권 기준선 (기존 세션 정지 후)")]
        private async void RunOwn001BaselineDiagnostic()
        {
            const string tag = "[UNINET-VERIFY] OWN-001";
            if (!Application.isPlaying)   // ContextMenu also shows in Edit Mode — the documented Play-Mode precondition, enforced: nothing is started or stopped otherwise
            { Debug.LogError($"{tag} SKIP — Play Mode 전용 (씬 진입 후 플레이 상태에서 실행)"); return; }
            if (_own001Running) { Debug.LogError($"{tag} SKIP — 진단 이미 실행 중 (재진입 차단)"); return; }
            if (_sec001Running) { Debug.LogError($"{tag} SKIP — SEC-001 진단 실행 중 (상호 충돌 방지)"); return; }
            _own001Running = true;
            try
            {
                // 0) stop any ACTIVE demo session gracefully (the same stop path as OnApplicationQuit, awaited) so the
                // diagnostic never orphans the demo listener — announced, never a silent overwrite
                if (UniNetEnvironment.Server != null || UniNetEnvironment.Client != null)
                {
                    Debug.Log($"{tag} 기존 데모 런타임 정상 정지 (role={_resolvedRole}) 후 진단 시작");
                    if (_started)
                    {
                        switch (_resolvedRole)
                        {
                            case ArenaRole.Server: await UniNetManager.ServerStopAsync(); break;
                            case ArenaRole.Host: await UniNetManager.HostStopAsync(); break;
                            default: UniNetManager.ClientStop(); break;
                        }
                    }
                    else
                    {
                        UniNetManager.ClientStop();
                        await UniNetManager.ServerStopAsync();
                    }
                }
                var options = new UniNetEndpointOptions { ProtocolVersion = ArenaConfig.ProtocolVersion };

                // 1) dedicated diagnostic runtime on a side port (grace 10 s) + a scene-style marker with a real relevancy hook
                await UniNetManager.ServerAsync(OwnDiagPort, options);
                var server = UniNetEnvironment.Server;
                server.SessionGraceSeconds = 10;
                var marker = new Own001Marker();
                ulong netId = Fnv1a.Hash64("arena-own001-baseline-marker");   // deterministic side id — no scene/prefab dependency
                server.RegisterSceneObject(netId, new object[] { marker });

                // 2) first client joins — round-robin assigns the marker; the real client cache must reflect it (baseline on the real wire)
                await UniNetManager.ClientAsync("127.0.0.1", OwnDiagPort, options);
                if (!await WaitAsync(() => UniNetEnvironment.Client != null && UniNetEnvironment.Client.LocalConnId != 0, 5, "첫 클라 Welcome", tag)) return;
                long victimConn = UniNetEnvironment.Client.LocalConnId;
                if (server.GetOwner(netId) != victimConn) { Debug.LogError($"{tag} 준비 실패 — 마커 소유자≠첫 클라"); return; }
                if (!await WaitAsync(() => UniNetEnvironment.Client.GetOwner(netId) == victimConn, 5, "첫 클라 소유권 기준선", tag)) return;
                Debug.Log($"{tag} 첫 클라 — 실제 RUDP 캐치업에서 소유권 기준선 수신 PASS");

                // 3) first client drops — grace must hold the marker's ownership (no other connection to reassign to)
                UniNetManager.ClientStop();
                if (!await WaitGoneAsync(victimConn, 5, "첫 클라 해제 관측", tag)) return;
                if (server.GetOwner(netId) != victimConn) { Debug.LogError($"{tag} 유예 보존 실패"); return; }

                // 4) second fresh client joins (ClientAsync resets the cache instance — the assertion below sees a fresh state):
                //    catch-up must deliver the EXISTING (grace-held) owner as the per-object baseline — THE OWN-001 fix on the real wire
                await UniNetManager.ClientAsync("127.0.0.1", OwnDiagPort, options);
                if (!await WaitAsync(() => UniNetEnvironment.Client != null && UniNetEnvironment.Client.LocalConnId != 0, 5, "후발 클라 Welcome", tag)) return;
                long lateConn = UniNetEnvironment.Client.LocalConnId;
                if (lateConn == victimConn) { Debug.LogError($"{tag} 준비 실패 — 커넥션 아이디 충돌"); return; }
                if (server.GetOwner(netId) != victimConn) { Debug.LogError($"{tag} FAIL — 기존 소유자 KEEP 위반"); return; }
                if (!await WaitAsync(() => UniNetEnvironment.Client.GetOwner(netId) == victimConn, 5, "후발 소유권 기준선", tag)) return;
                Debug.Log($"{tag} 후발 클라 — 실제 RUDP 캐치업에서 기존(유예) 소유자 기준선 수신 PASS");

                // 5) scene relevancy cycle over TIMED ticks — the P3 policy/relevancy path the driver runs (the
                //    parameterless immediate overload never evaluates it). DIAGNOSTIC FAULT INJECTION — not a normal
                //    game usage pattern: after the blocked timed tick the LOCAL cache is deliberately corrupted to
                //    owner 0, so the unblocked timed tick must re-deliver the baseline over the real RUDP wire for the
                //    grace-held owner to come back (no wire send ⇒ the cache stays 0 ⇒ FAIL — a cache shortcut can
                //    never pass; the expected owner value itself is never written by the diagnostic).
                marker.Blocked = true;
                server.TickReplication(server.SnapshotObjects(), Time.unscaledTimeAsDouble);
                UniNetEnvironment.Client.ApplyOwner(netId, 0);   // injected stale cache — diagnostic-only disturbance
                if (UniNetEnvironment.Client.GetOwner(netId) != 0) { Debug.LogError($"{tag} 준비 실패 — 스테일 주입 미반영"); return; }
                marker.Blocked = false;
                server.TickReplication(server.SnapshotObjects(), Time.unscaledTimeAsDouble);
                if (!await WaitAsync(() => UniNetEnvironment.Client.GetOwner(netId) == victimConn, 5, "재진입 소유권 기준선(와이어 복원)", tag)) return;
                Debug.Log($"[UNINET-VERIFY] OWN-001 DIAG PASS — 후발 소유권 기준선(유예 기존 owner)·씬 재진입 타임드 티에서 스테일 캐시 와이어 복원 (실제 RUDP·진단용 fault injection·단일 프로세스 마커 기반·다중 프로세스는 별도 단계)");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError($"{tag} 예외로 중단");
            }
            finally
            {
                UniNetManager.ClientStop();
                UniNetManager.ServerStop();
                _own001Running = false;
                Debug.Log($"{tag} 진단 종료 — 런타임 정리 (데모 부트스트랩은 재플레이로 복귀)");
            }
        }
#endif

#if UNITY_EDITOR
        // ── REC-002 voluntary-leave cleanup — in-editor diagnostic, real server/client/RUDP APIs on a side port ──
        // Proves over the real wire: ClientStop's wire-15 notice reaches the server (IntentionalLeaves counter advances),
        // the notified departure skips the grace save, and a fresh client joining INSIDE the grace window adopts
        // the orphaned ownership immediately (the REC-002 orphan-blocking window is gone). Best-effort honesty:
        // a notice lost in the close race keeps the legacy grace behavior — a systematically lost notice fails the
        // wait below and must trigger the flush-lease design review (plan BLOCKER 2).
        private const int Rec002DiagPort = ArenaConfig.Port + 9;
        private static bool _rec002Running;   // re-entry guard — ContextMenu double-click protection

        [ContextMenu("Diag/REC-002 자발 퇴장 즉시 정리 (기존 세션 정지 후)")]
        private async void RunRec002VoluntaryLeaveDiagnostic()
        {
            const string tag = "[UNINET-VERIFY] REC-002";
            if (!Application.isPlaying)   // ContextMenu also shows in Edit Mode — the documented Play-Mode precondition, enforced
            { Debug.LogError($"{tag} SKIP — Play Mode 전용 (씬 진입 후 플레이 상태에서 실행)"); return; }
            if (_rec002Running) { Debug.LogError($"{tag} SKIP — 진단 이미 실행 중 (재진입 차단)"); return; }
            if (_sec001Running || _own001Running) { Debug.LogError($"{tag} SKIP — SEC-001/OWN-001 진단 실행 중 (상호 충돌 방지)"); return; }
            _rec002Running = true;
            try
            {
                // 0) stop any ACTIVE demo session gracefully (the same stop path as OnApplicationQuit, awaited) so the
                // diagnostic never orphans the demo listener — announced, never a silent overwrite
                if (UniNetEnvironment.Server != null || UniNetEnvironment.Client != null)
                {
                    Debug.Log($"{tag} 기존 데모 런타임 정상 정지 (role={_resolvedRole}) 후 진단 시작");
                    if (_started)
                    {
                        switch (_resolvedRole)
                        {
                            case ArenaRole.Server: await UniNetManager.ServerStopAsync(); break;
                            case ArenaRole.Host: await UniNetManager.HostStopAsync(); break;
                            default: UniNetManager.ClientStop(); break;
                        }
                    }
                    else
                    {
                        UniNetManager.ClientStop();
                        await UniNetManager.ServerStopAsync();
                    }
                }
                var options = new UniNet.Core.Hosting.UniNetEndpointOptions { ProtocolVersion = ArenaConfig.ProtocolVersion };

                // 1) dedicated diagnostic runtime on a side port (grace 10 s) + a NON-DYNAMIC marker in the real
                // server registry — it survives ClientStop's dynamic-spawn sweep (a dynamic avatar would not)
                await UniNetManager.ServerAsync(Rec002DiagPort, options);
                var server = UniNetEnvironment.Server;
                server.SessionGraceSeconds = 10;
                ulong netId = Fnv1a.Hash64("arena-rec002-voluntary-marker");   // deterministic side id — no scene/prefab dependency
                server.RegisterSceneObject(netId, new object[] { new object() });

                // 2) leaver joins — round-robin assigns the marker (only connection)
                await UniNetManager.ClientAsync("127.0.0.1", Rec002DiagPort, options);
                if (!await WaitAsync(() => UniNetEnvironment.Client != null && UniNetEnvironment.Client.LocalConnId != 0, 5, "퇴장자 Welcome", tag)) return;
                long leaverConn = UniNetEnvironment.Client.LocalConnId;
                if (!await WaitAsync(() => server.GetOwner(netId) == leaverConn, 5, "마커 소유권 배정", tag)) return;

                // 3) voluntary ClientStop — the wire-15 notice must reach the server BEFORE the close (reliable channel);
                //    the counter advancing is the real-RUDP delivery observation (plan BLOCKER 2)
                UniNetManager.ClientStop();
                if (!await WaitAsync(() => server.IntentionalLeaves >= 1, 5, "와이어 15 도달", tag)) return;
                if (!await WaitGoneAsync(leaverConn, 5, "퇴장자 해제 관측", tag)) return;
                Debug.Log($"{tag} 자발 ClientStop — 와이어 15 도달 확인 (유예 저장 생략 경로 진입)");

                // 4) a fresh client joins INSIDE the grace window (10 s) — the orphan must be adopted immediately,
                //    proving no grace session was saved (IsInGraceSession would have blocked the adoption)
                await UniNetManager.ClientAsync("127.0.0.1", Rec002DiagPort, options);
                if (!await WaitAsync(() => UniNetEnvironment.Client != null && UniNetEnvironment.Client.LocalConnId != 0, 5, "후발 클라 Welcome", tag)) return;
                long lateConn = UniNetEnvironment.Client.LocalConnId;
                if (lateConn == leaverConn) { Debug.LogError($"{tag} 준비 실패 — 커넥션 아이디 충돌"); return; }
                if (!await WaitAsync(() => server.GetOwner(netId) == lateConn, 5, "유예 창 내 고아 즉시 입양", tag)) return;

                Debug.Log($"[UNINET-VERIFY] REC-002 DIAG PASS — 자발 ClientStop이 유예 저장을 건너뛰고 후발 클라가 유예 창 내 고아를 즉시 입양 (실제 RUDP·와이어 15 도달·마커 기반)");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError($"{tag} 예외로 중단");
            }
            finally
            {
                UniNetManager.ClientStop();
                UniNetManager.ServerStop();
                _rec002Running = false;
                Debug.Log($"{tag} 진단 종료 — 런타임 정리 (데모 부트스트랩은 재플레이로 복귀)");
            }
        }
#endif
    }
}
