using System;
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
                    _b = CreateSpawnable(seed: 2002, score: 22, secret: 32, pos: new Vector3(-5f, 0f, 0f));
                    _b.Score = 66;      // unconditional — propagates
                    _b.SecretHp = 33;   // OwnerOnly — propagates to the owner (the only connection)
                    _b.SpawnSeed = 42;  // InitialOnly — not propagated (stays at spawn value 2002)
                    _b.TeamId = 8;      // SkipOwner — not propagated to the owner (stays at initial value 3)
                    Debug.Log("[UNINET-2PROC] SERVER-SPAWNED-B netId=" + _b.NetId);

                    // Multi-component object — MovementBrain + HealthTank on one GameObject (see ADR-0010)
                    var template = new GameObject("MultiTemplate");
                    var tmb = template.AddComponent<MovementBrain>();
                    var tht = template.AddComponent<HealthTank>();
                    UniNetManager.RegisterPrefab<MovementBrain>(template);   // Catalog — the client rebuilds the same configuration from it
                    tmb.Speed = 10; tht.Armor = 20;   // Spawn baseline — template values are copied through serialization
                    _multi = UniNetManager.NetworkInstantiate(template);   // Clone-spawns the same configuration (the template is the catalog original — never destroy it)
                    var mb = _multi.GetComponent<MovementBrain>();
                    var ht = _multi.GetComponent<HealthTank>();
                    mb.Speed = 99;    // unconditional delta
                    ht.Armor = 88;    // OwnerOnly delta
                    Debug.Log("[UNINET-2PROC] SERVER-SPAWNED-MULTI netId=" + mb.NetId);
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
                if (destroyedMulti && DateTime.UtcNow > DeadlineAfter(11))
                {
                    Thread.Sleep(500);   // give the client time to observe the destroy
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

        private static DateTime DeadlineAfter(double seconds)
        {
            if (_phaseMark == default) _phaseMark = DateTime.UtcNow;
            return _phaseMark.AddSeconds(seconds);
        }

        /// <summary>Creates a fixture for dynamic-spawn verification — clones, registers, and broadcasts the template (public field initial values ride the serialized copy as the InitialOnly baseline).</summary>
        private static SpawnablePlayer CreateSpawnable(int seed, int score, int secret, Vector3 pos)
        {
            var template = new GameObject("Dyn" + seed);
            var s = template.AddComponent<SpawnablePlayer>();
            template.transform.position = pos;
            s.Score = score;
            s.SecretHp = secret;
            s.SpawnSeed = seed;
            var go = UniNetManager.NetworkInstantiate(template);
            UnityEngine.Object.Destroy(template);
            return go.GetComponent<SpawnablePlayer>();
        }

        private static bool LoggedPing;

        private static void RunClient()
        {
            var player = CreatePlayer();
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

            // Catalog for the client-side multi-component object — create only after the connection is confirmed, or RegisterAllToClient would register the template as a scene object
            var template = new GameObject("MultiTemplate");
            template.AddComponent<MovementBrain>();
            template.AddComponent<HealthTank>();
            UniNetManager.RegisterPrefab<MovementBrain>(template);

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
            while (DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                if (player.ScoreNotified && player.ClientFxRan && player.MulticastCount == 1) break;
                Thread.Sleep(20);
            }

            Debug.Log("[UNINET-2PROC] CLIENT-DONE score=" + player.Score
                      + " prev=" + player.LastPrevScore
                      + " fx=" + player.ClientFxRan
                      + " multicast=" + player.MulticastCount
                      + " notified=" + player.ScoreNotified);

            VerifyDynamicSpawn();
            VerifyMultiComponent();
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
