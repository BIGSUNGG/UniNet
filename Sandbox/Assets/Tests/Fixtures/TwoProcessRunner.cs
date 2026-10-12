using System;
using System.Linq;
using System.Threading;
using DRPC;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Two-process verification runner — run via batchmode -executeMethod; dispatches on the custom argument --uninet-role=server|client.
    /// Verifies a real cross-process RUDP round-trip and emits [UNINET-2PROC] markers.
    /// </summary>
    public static class TwoProcessRunner
    {
        const int Port = 7811;

        public static void Run()
        {
            string role = null;
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--uninet-role=")) role = a.Substring("--uninet-role=".Length);

            // RuntimeInitializeOnLoadMethod doesn't run in EditMode, so register explicitly (idempotent)
            global::UniNet.Generated.__UniNetRegistration.Register();

            if (role == "server") RunServer();
            else if (role == "client") RunClient();
            else if (role == "host") RunHost();
            else Debug.LogError("[UNINET-2PROC] --uninet-role=server|client 미지정");
        }

        private static void RunServer()
        {
            var player = CreatePlayer();
            var serTank = new GameObject("SerTank").AddComponent<QuantizedTank>();      // custom-serializer + FastArray verification targets (ADR-0020)
            var faTank = new GameObject("FaTank").AddComponent<FastArrayTank>();

            var serverTask = UniNetManager.ServerAsync(Port);
            Wait(serverTask, 15, "서버 리슨");
            if (serverTask.IsFaulted) { Debug.LogError("[UNINET-2PROC] SERVER-ABORT"); return; }

            // Catch-up verification target — spawned dynamically *before* the client connects (a late joiner must receive the spawn plus full state)
            var a = CreateSpawnable(seed: 1001, score: 11, secret: 21, pos: new Vector3(10f, 20f, 30f));
            Debug.Log("[UNINET-2PROC] server-ready (pre-spawned A netId=" + a.NetId + ")");
            var keys = string.Join(",", global::System.Linq.Enumerable.Select(UniNetDispatch.ServerHandlers().Keys, k => k.ToString()));
            Debug.Log("[UNINET-2PROC] server-table=[" + keys + "] factory=" + (UniNetEnvironment.HubFactory != null));

            // The client may be a fresh project copy whose first import takes minutes — allow generously for attach
            var deadline = DateTime.UtcNow.AddSeconds(240);
            bool loggedAttach = false;
            bool spawnedB = false;
            bool destroyed = false;
            bool destroyedMulti = false;
            bool kicked = false;
            while (DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                UniNetEnvironment.Server.TickReplication();
                if (!loggedAttach && UniNetEnvironment.Server.SnapshotConnections().Count > 0)
                {
                    loggedAttach = true;
                    Debug.Log("[UNINET-2PROC] SERVER-ATTACHED conns=" + UniNetEnvironment.Server.SnapshotConnections().Count);
                }
                if (player.ServerPingCount > 0 && !LoggedPing)
                {
                    LoggedPing = true;
                    Debug.Log("[UNINET-2PROC] SERVER-RCVD-PING count=" + player.ServerPingCount);
                }
                // After the ping: spawn B as a live broadcast + apply conditional value changes (OwnerOnly propagates; InitialOnly and SkipOwner do not)
                if (!spawnedB && player.ServerPingCount > 0)
                {
                    spawnedB = true;

                    // Serialization verification targets (ADR-0020) — quantized position + collection element deltas
                    serTank.Position = new Vector3(12.5f, -7.25f, 99.5f);   // exact-float values → dequantize round-trips exactly
                    serTank.PolarAim = new PolarAim { Radius = 3.2f, Angle = 1.1f };
                    faTank.Scores.AddRange(new[] { 1, 2, 3, 4 });
                    faTank.Scores.Remove(2);       // [1,3,4]
                    faTank.Scores.Insert(1, 9);    // [1,9,3,4]
                    faTank.Scores.Add(5);          // [1,9,3,4,5] — append op
                    Debug.Log("[UNINET-2PROC] SERVER-SER-CHANGED");

                    _b = CreateSpawnable(seed: 2002, score: 22, secret: 32, pos: new Vector3(-5f, 0f, 0f));
                    _b.Score = 66;      // unconditional — propagates
                    _b.SecretHp = 33;   // OwnerOnly — propagates to the owner (the only connection)
                    _b.SpawnSeed = 42;  // InitialOnly — not propagated (stays at spawn value 2002)
                    _b.TeamId = 8;      // SkipOwner — not propagated to the owner (stays at initial value 3)
                    Debug.Log("[UNINET-2PROC] SERVER-SPAWNED-B netId=" + _b.NetId);

                    // Multi-component object — MovementBrain + HealthTank on one GameObject (see ADR-0010)
                    // Spawnable prefab (ADR-0024) — the client instantiates the same prefab from its baked table
                    var multiPrefab = TestPrefabs.Load(TestPrefabs.MultiTankPrefab);
                    _multi = UniNetManager.NetworkInstantiate(multiPrefab, clone =>
                    {
                        var mb0 = clone.GetComponent<MovementBrain>();
                        var ht0 = clone.GetComponent<HealthTank>();
                        mb0.Speed = 10; ht0.Armor = 20;   // Spawn baseline — configure runs on the clone before replication
                    });
                    var mb = _multi.GetComponent<MovementBrain>();
                    var ht = _multi.GetComponent<HealthTank>();
                    mb.Speed = 99;    // unconditional delta
                    ht.Armor = 88;    // OwnerOnly delta
                    Debug.Log("[UNINET-2PROC] SERVER-SPAWNED-MULTI netId=" + mb.NetId);

                    // ADR-0030 — mid-session ownership dance: release now, re-grant shortly after (the client samples the transitions)
                    _originalOwner = UniNetEnvironment.Server.GetOwner(player.NetId);
                    UniNetEnvironment.Server.SetOwner(player.NetId, 0);
                    _releasedAt = DateTime.UtcNow;
                    Debug.Log("[UNINET-2PROC] SERVER-OWNER-RELEASED orig=" + _originalOwner);
                }
                if (_releasedAt != default && _originalOwner >= 0 && !_regranted && DateTime.UtcNow > _releasedAt.AddSeconds(0.5))
                {
                    _regranted = true;
                    UniNetEnvironment.Server.SetOwner(player.NetId, _originalOwner);
                    Debug.Log("[UNINET-2PROC] SERVER-OWNER-REGRANTED conn=" + _originalOwner);
                }
                // Destroy after values had time to propagate — the client observes the deregistration
                if (spawnedB && !destroyed && DateTime.UtcNow > DeadlineAfter(3))
                {
                    destroyed = true;
                    UniNetManager.NetworkDestroy(a.gameObject);
                    if (_b != null) UniNetManager.NetworkDestroy(_b.gameObject);
                    Debug.Log("[UNINET-2PROC] SERVER-DESTROYED-AB");
                }
                if (destroyed && !destroyedMulti && DateTime.UtcNow > DeadlineAfter(9))
                {
                    destroyedMulti = true;
                    if (_multi != null) UniNetManager.NetworkDestroy(_multi);
                    Debug.Log("[UNINET-2PROC] SERVER-DESTROYED-MULTI");
                }
                if (destroyedMulti && !kicked && DateTime.UtcNow > DeadlineAfter(11))
                {
                    kicked = true;
                    var conns = UniNetEnvironment.Server.SnapshotConnections();
                    if (conns.Count > 0)
                    {
                        UniNetEnvironment.Server.DisconnectClient(conns[0].ConnId, UniNet.Core.UniNetDisconnectReason.Kicked);   // ADR-0027 — notice flushes, grace, then the transport close
                        Debug.Log("[UNINET-2PROC] SERVER-KICKED connId=" + conns[0].ConnId);
                    }
                    else
                        Debug.LogWarning("[UNINET-2PROC] SERVER-KICK-SKIP — 연결 없음");
                }
                if (kicked && DateTime.UtcNow > DeadlineAfter(15))
                {
                    Thread.Sleep(500);   // give the client time to finish the mismatch reconnect check
                    UniNetEnvironment.PumpMain();
                    Debug.Log("[UNINET-2PROC] SERVER-DONE score=" + player.Score);
                    return;
                }
                Thread.Sleep(30);
            }
            Debug.LogError("[UNINET-2PROC] SERVER-TIMEOUT ping=" + player.ServerPingCount);
        }

        private static DateTime _phaseMark;
        private static SpawnablePlayer _b;
        private static GameObject _multi;
        private static long _originalOwner = -1;   // ADR-0030 ownership dance — original owner before the release/re-grant
        private static DateTime _releasedAt;
        private static bool _regranted;

        private static DateTime DeadlineAfter(double seconds)
        {
            if (_phaseMark == default) _phaseMark = DateTime.UtcNow;
            return _phaseMark.AddSeconds(seconds);
        }

        /// <summary>Creates a fixture for dynamic-spawn verification — clones, registers, and broadcasts the template (public field initial values ride the serialized copy as the InitialOnly baseline).</summary>
        private static SpawnablePlayer CreateSpawnable(int seed, int score, int secret, Vector3 pos)
        {
            var prefab = TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab);
            var go = UniNetManager.NetworkInstantiate(prefab, pos, Quaternion.identity, clone =>
            {
                var s = clone.GetComponent<SpawnablePlayer>();
                s.Score = score;
                s.SecretHp = secret;
                s.SpawnSeed = seed;
            });
            return go.GetComponent<SpawnablePlayer>();
        }

        private static bool LoggedPing;

        private static void RunClient()
        {
            var player = CreatePlayer();
            new GameObject("SerTank").AddComponent<QuantizedTank>();   // matching scene objects — both processes create the same hierarchy before connecting
            new GameObject("FaTank").AddComponent<FastArrayTank>();
            var clientTask = UniNetManager.ClientAsync("127.0.0.1", Port);
            Wait(clientTask, 15, "클라 접속");
            if (clientTask.IsFaulted) { Debug.LogError("[UNINET-2PROC] CLIENT-ABORT"); return; }
            Debug.Log("[UNINET-2PROC] CLIENT-CONNECTED");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (UniNetEnvironment.Client.LocalConnId == 0 && DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
            if (UniNetEnvironment.Client.LocalConnId == 0)
            {
                Debug.LogError("[UNINET-2PROC] CLIENT-WELCOME-TIMEOUT");
                return;
            }

            // (ADR-0024) No client-side catalog registration — the client resolves spawn prefab ids
            // through its own baked prefab table (same Fixtures prefabs as the server).

            // Wait for ownership, then send a ServerRpc over the network wire — [netId][int amount]
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (UniNetEnvironment.Client.GetOwner(player.NetId) == 0 && DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
            Debug.Log("[UNINET-2PROC] CLIENT-OWNER connId=" + UniNetEnvironment.Client.LocalConnId
                      + " owner=" + UniNetEnvironment.Client.GetOwner(player.NetId));

            int pingId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcPing");
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(player.NetId);
            w.WriteByte(0);   // SubId — slot 0 of a single-component object
            w.WriteInt32(11);
            UniNetEnvironment.ClientSender.UniNetSend(pingId, w.ToArray(), RpcDeliveryMode.ReliableOrdered);

            deadline = DateTime.UtcNow.AddSeconds(15);
            long lastOwner = long.MinValue;   // ADR-0030 — sample the observed owner history (the server runs a release/re-grant dance mid-session)
            _ownerHistory.Clear();
            bool sawRelease = false, sawRegrant = false;
            var danceDeadline = DateTime.UtcNow.AddSeconds(8);   // the dance completes ~1.5 s after the ping — sample past it even when the roundtrip asserts finish early
            while (DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                long owner = UniNetEnvironment.Client.GetOwner(player.NetId);
                if (owner != lastOwner)
                {
                    lastOwner = owner;
                    _ownerHistory.Add(owner);
                }
                if (owner == 0) sawRelease = true;
                else if (sawRelease && owner == UniNetEnvironment.Client.LocalConnId) sawRegrant = true;
                if (player.ScoreNotified && player.ClientFxRan && player.MulticastCount == 1
                    && (sawRegrant || DateTime.UtcNow > danceDeadline)) break;
                Thread.Sleep(20);
            }

            Debug.Log("[UNINET-2PROC] CLIENT-DONE score=" + player.Score
                      + " prev=" + player.LastPrevScore
                      + " fx=" + player.ClientFxRan
                      + " multicast=" + player.MulticastCount
                      + " notified=" + player.ScoreNotified);

            VerifyDynamicSpawn();
            VerifyMultiComponent();
            VerifySerialization();
            VerifyOwnershipTransfer();
            VerifyKick();
            VerifyVersionMismatch();
        }

        private static readonly System.Collections.Generic.List<long> _ownerHistory = new();

        /// <summary>Verifies the ADR-0030 ownership-transfer round-trip — the server released and re-granted the player's
        /// ownership mid-session (SetOwner 0 → back); the client must have observed both transitions over the wire.</summary>
        private static void VerifyOwnershipTransfer()
        {
            Debug.Log("[UNINET-2PROC] OWNER history=[" + string.Join(",", _ownerHistory) + "]");
            bool sawRelease = false;
            bool sawRegrant = false;
            for (int i = 0; i < _ownerHistory.Count; i++)
            {
                if (_ownerHistory[i] == 0) sawRelease = true;
                else if (sawRelease && _ownerHistory[i] == UniNetEnvironment.Client.LocalConnId) sawRegrant = true;
            }
            if (!sawRelease || !sawRegrant)
            {
                Debug.LogError("[UNINET-2PROC] OWNERSHIP-TRANSFER-FAIL — 해제/재부여 관찰 실패");
                return;
            }
            Debug.Log("[UNINET-2PROC] OWNERSHIP-TRANSFER PASS — SetOwner 해제·재부여가 클라에 전파됨 (ADR-0030)");
        }

        /// <summary>Verifies the ADR-0028 handshake on the CLIENT — reconnecting with a deliberately wrong protocol version must be
        /// rejected with the VersionMismatch reason (the server enforces; the kick infrastructure delivers the reason).</summary>
        private static void VerifyVersionMismatch()
        {
            UniNet.Core.UniNetDisconnectReason observed = UniNet.Core.UniNetDisconnectReason.None;
            bool raised = false;
            void OnEnded(UniNet.Core.UniNetDisconnectReason reason)
            {
                raised = true;
                observed = reason;
            }
            UniNetManager.ClientDisconnected += OnEnded;
            try
            {
                var reconnect = UniNetManager.ClientAsync("127.0.0.1", Port,
                    new UniNet.Core.Hosting.UniNetEndpointOptions { ProtocolVersion = "uninet-2proc-wrong" });   // deliberately mismatched (server keeps the package default)
                Wait(reconnect, 15, "버전 불일치 재접속");
                if (reconnect.IsFaulted)
                {
                    Debug.LogError("[UNINET-2PROC] VERSION-RECONNECT-FAIL " + reconnect.Exception);
                    return;
                }
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!raised && DateTime.UtcNow < deadline)
                {
                    UniNetEnvironment.PumpMain();
                    Thread.Sleep(20);
                }
                Debug.Log("[UNINET-2PROC] VERSION raised=" + raised + " reason=" + observed);
                if (!raised || observed != UniNet.Core.UniNetDisconnectReason.VersionMismatch)
                {
                    Debug.LogError("[UNINET-2PROC] VERSION-MISMATCH-FAIL — 거부 미관측 또는 사유 불일치");
                    return;
                }
                Debug.Log("[UNINET-2PROC] VERSION-MISMATCH PASS — 버전 불일치 클라가 사유와 함께 거부됨");
            }
            finally
            {
                UniNetManager.ClientDisconnected -= OnEnded;
            }
        }

        /// <summary>Verifies the ADR-0027 kick round-trip on the CLIENT — the server kicks this connection and the
        /// session-end event must arrive with the Kicked reason (notice dispatched before the transport close).</summary>
        private static void VerifyKick()
        {
            UniNet.Core.UniNetDisconnectReason observed = UniNet.Core.UniNetDisconnectReason.None;
            bool raised = false;
            void OnEnded(UniNet.Core.UniNetDisconnectReason reason)
            {
                raised = true;
                observed = reason;
            }
            UniNetManager.ClientDisconnected += OnEnded;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);   // the server kicks at its +11 s mark — cover attach + propagation generously
                while (!raised && DateTime.UtcNow < deadline)
                {
                    UniNetEnvironment.PumpMain();
                    Thread.Sleep(20);
                }
                if (!raised)
                {
                    Debug.LogError("[UNINET-2PROC] KICK-REASON-FAIL — 해제 이벤트 미발화");
                    return;
                }
                Debug.Log("[UNINET-2PROC] KICK reason=" + observed);
                if (observed != UniNet.Core.UniNetDisconnectReason.Kicked)
                {
                    Debug.LogError("[UNINET-2PROC] KICK-REASON-FAIL — 사유 불일치");
                    return;
                }
                Debug.Log("[UNINET-2PROC] KICK-REASON PASS — 서버 킥 사유가 클라에 전달됨");
            }
            finally
            {
                UniNetManager.ClientDisconnected -= OnEnded;
            }
        }

        /// <summary>Verifies the ADR-0020 serialization features on the CLIENT — quantized position restore (0.01 buckets)
        /// and FastArray element-delta replay over the real cross-process wire.</summary>
        private static void VerifySerialization()
        {
            QuantizedTank qt = null;
            FastArrayTank fa = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && (qt == null || fa == null))
            {
                UniNetEnvironment.PumpMain();
                foreach (var c in UnityEngine.Object.FindObjectsByType<QuantizedTank>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (c.gameObject.name == "SerTank") qt = c;
                foreach (var c in UnityEngine.Object.FindObjectsByType<FastArrayTank>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (c.gameObject.name == "FaTank") fa = c;
                Thread.Sleep(20);
            }
            if (qt == null || fa == null)
            {
                Debug.LogError("[UNINET-2PROC] SER-MISS — 직렬화 검증 오브젝트 미수신");
                return;
            }

            // wait for the final values — quantized Vector3 (exact-float domain) + collection replay
            deadline = DateTime.UtcNow.AddSeconds(15);
            var expectPos = new Vector3(12.5f, -7.25f, 99.5f);
            var expectAim = new PolarAim { Radius = 3.2f, Angle = 1.1f };
            var expectScores = new[] { 1, 9, 3, 4, 5 };
            bool AimOk(PolarAim a) => System.Math.Abs(a.Radius - 3.2f) < 0.051f && System.Math.Abs(a.Angle - 1.1f) < 0.051f;
            while (DateTime.UtcNow < deadline
                   && (qt.Position != expectPos || !AimOk(qt.PolarAim) || fa.Scores.Count != expectScores.Length || !fa.Scores.SequenceEqual(expectScores)))
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }

            Debug.Log("[UNINET-2PROC] SER pos=" + qt.Position + " aim=(" + qt.PolarAim.Radius + "," + qt.PolarAim.Angle + ")"
                      + " scores=[" + string.Join(",", fa.Scores) + "]"
                      + " posNotify=" + qt.NotifyCalls + " scoreNotify=" + fa.ScoreNotifyCalls);
            if (qt.Position != expectPos || !AimOk(qt.PolarAim) || !fa.Scores.SequenceEqual(expectScores))
            {
                Debug.LogError("[UNINET-2PROC] SERIALIZATION-FAIL");
                return;
            }
            Debug.Log("[UNINET-2PROC] SERIALIZATION PASS — 양자화 위치(Vector3+구조체) + 배열 요소 델타(삭제·삽입·append)");
        }

        /// <summary>Verifies the multi-component object — MovementBrain+HealthTank on one object, each slot working independently.</summary>
        private static void VerifyMultiComponent()
        {
            MovementBrain mb = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && mb == null)
            {
                UniNetEnvironment.PumpMain();
                foreach (var c in UnityEngine.Object.FindObjectsByType<MovementBrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (c.GetComponent<HealthTank>() != null && c.gameObject.name != "MultiTemplate")
                        mb = c;   // identifies the multi-component object (excluding the catalog template)
                Thread.Sleep(20);
            }
            if (mb == null)
            {
                Debug.LogError("[UNINET-2PROC] MULTI-MISS — 다중 컴포넌트 스폰 실패");
                return;
            }
            var ht = mb.GetComponent<HealthTank>();

            // Wait for per-slot final values — unconditional (Speed) + OwnerOnly (Armor — the only connection is the owner)
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && (mb.Speed != 99 || ht.Armor != 88))
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }

            Debug.Log("[UNINET-2PROC] MULTI speed=" + mb.Speed + "(99) armor=" + ht.Armor + "(88: OwnerOnly) sub0=" + mb.SubId + " sub1=" + ht.SubId
                      + " sameNetId=" + (mb.NetId == ht.NetId));
            if (mb.Speed != 99 || ht.Armor != 88 || mb.SubId != 0 || ht.SubId != 1 || mb.NetId != ht.NetId)
            {
                Debug.LogError("[UNINET-2PROC] MULTI-FAIL");
                return;
            }

            ulong netId = mb.NetId;
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && UniNetEnvironment.Client.Get(netId) != null)
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
            if (UniNetEnvironment.Client.Get(netId) != null)
            {
                Debug.LogError("[UNINET-2PROC] MULTI-DESTROY-FAIL");
                return;
            }
            Debug.Log("[UNINET-2PROC] MULTI-COMPONENT PASS — 다중 서브 스폰/슬롯/서브별 델타/파괴");
        }

        /// <summary>Verifies dynamic spawn, conditions, and destroy — identifies A (catch-up: spawned before connect) and B (broadcast: spawned after the ping) by value.</summary>
        private static void VerifyDynamicSpawn()
        {
            // Wait for A+B to appear — A arrives via connect catch-up, B is spawned by the server after the ping
            SpawnablePlayer a = null, b = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && (a == null || b == null))
            {
                UniNetEnvironment.PumpMain();
                foreach (var s in UnityEngine.Object.FindObjectsByType<SpawnablePlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (s.SpawnSeed == 1001) a = s;
                    if (s.SpawnSeed == 2002) b = s;
                }
                Thread.Sleep(20);
            }
            if (a == null || b == null)
            {
                Debug.LogError($"[UNINET-2PROC] SPAWN-MISS a={(a != null)} b={(b != null)} — 캐치업/브로드캐스트 실패");
                return;
            }

            // A (catch-up) — position, initial state, owner-only/skip conditions
            var pos = a.transform.position;
            bool posOk = pos.x == 10f && pos.y == 20f && pos.z == 30f;
            Debug.Log("[UNINET-2PROC] SPAWN-A pos-ok=" + posOk
                      + " score=" + a.Score + "(11) secret=" + a.SecretHp + "(21)"
                      + " team=" + a.TeamId + "(3: SkipOwner 미수신) seed=" + a.SpawnSeed + "(1001)");
            if (!posOk || a.Score != 11 || a.SecretHp != 21 || a.TeamId != 3)
            {
                Debug.LogError("[UNINET-2PROC] SPAWN-A-FAIL");
                return;
            }

            // B (broadcast) — wait for final values: unconditional 66, OwnerOnly 33 / InitialOnly stays 2002, SkipOwner stays 3
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && (b.Score != 66 || b.SecretHp != 33))
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
            Debug.Log("[UNINET-2PROC] SPAWN-B score=" + b.Score + "(66) secret=" + b.SecretHp + "(33: OwnerOnly)"
                      + " team=" + b.TeamId + "(3: SkipOwner 미전파) seed=" + b.SpawnSeed + "(2002: InitialOnly 미전파)");
            if (b.Score != 66 || b.SecretHp != 33 || b.TeamId != 3 || b.SpawnSeed != 2002)
            {
                Debug.LogError("[UNINET-2PROC] SPAWN-B-FAIL");
                return;
            }

            // Destroy — deregistration + instance removal
            ulong aNet = a.NetId, bNet = b.NetId;
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline
                   && (UniNetEnvironment.Client.Get(aNet) != null || UniNetEnvironment.Client.Get(bNet) != null))
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
            bool aGone = UniNetEnvironment.Client.Get(aNet) == null;
            bool bGone = UniNetEnvironment.Client.Get(bNet) == null;
            if (!aGone || !bGone)
            {
                Debug.LogError("[UNINET-2PROC] DESTROY-FAIL aGone=" + aGone + " bGone=" + bGone);
                return;
            }
            Debug.Log("[UNINET-2PROC] DYNAMIC-SPAWN-DESTROY PASS — 캐치업/브로드캐스트/조건부(OwnerOnly·SkipOwner·InitialOnly)/파괴");
        }

        private static void RunHost()
        {
            var player = CreatePlayer();
            var hostTask = UniNetManager.HostAsync(Port);
            Wait(hostTask, 15, "호스트");
            if (hostTask.IsFaulted) { Debug.LogError("[UNINET-2PROC] HOST-ABORT"); return; }
            Debug.Log("[UNINET-2PROC] HOST-READY (edit-mode loopback)");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (UniNetEnvironment.Client.LocalConnId == 0 && DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
            int pingId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcPing");
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(player.NetId);
            w.WriteByte(0);   // SubId — slot 0 of a single-component object
            w.WriteInt32(5);
            UniNetEnvironment.ClientSender.UniNetSend(pingId, w.ToArray(), RpcDeliveryMode.ReliableOrdered);
            Debug.Log("[UNINET-2PROC] HOST-PING-SENT");

            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                UniNetEnvironment.Server.TickReplication();
                if (player.ServerPingCount > 0)
                {
                    Thread.Sleep(300);
                    UniNetEnvironment.PumpMain();
                    Debug.Log("[UNINET-2PROC] HOST-DONE ping=" + player.ServerPingCount + " score=" + player.Score + " notified=" + player.ScoreNotified);
                    return;
                }
                Thread.Sleep(20);
            }
            Debug.LogError("[UNINET-2PROC] HOST-TIMEOUT ping=" + player.ServerPingCount);
        }

        /// <summary>Creates the player identically on both ends so their hierarchy paths match — required to verify netIds agree without negotiation.</summary>
        private static VerifyPlayer CreatePlayer()
        {
            var go = new GameObject("VProc");
            return go.AddComponent<VerifyPlayer>();
        }

        private static void Wait(System.Threading.Tasks.Task task, double seconds, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
            if (task.IsFaulted)
                Debug.LogError("[UNINET-2PROC] " + what + " 실패: " + task.Exception?.GetBaseException());
        }
    }
}
