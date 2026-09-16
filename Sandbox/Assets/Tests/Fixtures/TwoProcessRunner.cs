using System;
using System.Threading;
using DRPC;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// 2-프로세스 검증 러너 — batchmode -executeMethod로 실행. 커스텀 인자 --uninet-role=server|client 로 분기.
    /// 실제 프로세스 간 RUDP 왕복을 검증하고 [UNINET-2PROC] 마커를 남긴다.
    /// </summary>
    public static class TwoProcessRunner
    {
        const int Port = 7811;

        public static void Run()
        {
            string role = null;
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--uninet-role=")) role = a.Substring("--uninet-role=".Length);

            // EditMode에서는 RuntimeInitializeOnLoadMethod가 안 돌므로 명시 등록 (멱등)
            global::UniNet.Generated.__UniNetRegistration.Register();

            if (role == "server") RunServer();
            else if (role == "client") RunClient();
            else if (role == "host") RunHost();
            else Debug.LogError("[UNINET-2PROC] --uninet-role=server|client 미지정");
        }

        private static void RunServer()
        {
            var player = CreatePlayer();

            // 캐치업 검증 대상 — 클라 접속 *전* 동적 스폰 (후발 접속이 스폰+전체 상태로 합류해야 한다)
            var a = CreateSpawnable(seed: 1001, score: 11, secret: 21, pos: new Vector3(10f, 20f, 30f));
            var serverTask = UniNetManager.ServerAsync(Port);
            Wait(serverTask, 15, "서버 리슨");
            if (serverTask.IsFaulted) { Debug.LogError("[UNINET-2PROC] SERVER-ABORT"); return; }
            UniNetManager.Spawn(a.gameObject);   // 접속 전 스폰 — 브로드캐스트 대상 없음, 캐치업 대기
            Debug.Log("[UNINET-2PROC] server-ready (pre-spawned A netId=" + a.NetId + ")");
            var keys = string.Join(",", global::System.Linq.Enumerable.Select(UniNetDispatch.ServerHandlers().Keys, k => k.ToString()));
            Debug.Log("[UNINET-2PROC] server-table=[" + keys + "] factory=" + (UniNetEnvironment.HubFactory != null));

            // 클라이언트는 신규 프로젝트 사본일 수 있어 첫 import에 수 분 — attach 대기는 넉넉히
            var deadline = DateTime.UtcNow.AddSeconds(240);
            bool loggedAttach = false;
            bool spawnedB = false;
            bool destroyed = false;
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
                // 핑 수신 후: 라이브 브로드캐스트 스폰 B + 조건부 값 변경 (OwnerOnly 전파 / InitialOnly·SkipOwner 비전파)
                if (!spawnedB && player.ServerPingCount > 0)
                {
                    spawnedB = true;
                    _b = CreateSpawnable(seed: 2002, score: 22, secret: 32, pos: new Vector3(-5f, 0f, 0f));
                    UniNetManager.Spawn(_b.gameObject);
                    _b.Score = 66;      // 무조건 — 전파
                    _b.SecretHp = 33;   // OwnerOnly — 소유자(유일 연결) 전파
                    _b.SpawnSeed = 42;  // InitialOnly — 미전파 (스폰값 2002 유지)
                    _b.TeamId = 8;      // SkipOwner — 미전파 (초기값 3 유지)
                    Debug.Log("[UNINET-2PROC] SERVER-SPAWNED-B netId=" + _b.NetId);
                }
                // 값 전파 여유 후 파괴 — 클라가 등록 해제 관찰
                if (spawnedB && !destroyed && DateTime.UtcNow > DeadlineAfter(3))
                {
                    destroyed = true;
                    UniNetManager.NetworkDestroy(a.gameObject);
                    if (_b != null) UniNetManager.NetworkDestroy(_b.gameObject);
                    Debug.Log("[UNINET-2PROC] SERVER-DESTROYED-AB");
                }
                if (destroyed && DateTime.UtcNow > DeadlineAfter(5))
                {
                    Thread.Sleep(500);   // 클라 파괴 관찰 여유
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

        private static DateTime DeadlineAfter(double seconds)
        {
            if (_phaseMark == default) _phaseMark = DateTime.UtcNow;
            return _phaseMark.AddSeconds(seconds);
        }

        /// <summary>동적 스폰 검증용 픽스처 생성 — 스폰 전에 초기 상태를 세팅한다 (InitialOnly 값이 스폰에 실린다).</summary>
        private static SpawnablePlayer CreateSpawnable(int seed, int score, int secret, Vector3 pos)
        {
            var go = new GameObject("Dyn" + seed);
            var s = go.AddComponent<SpawnablePlayer>();
            go.transform.position = pos;
            s.Score = score;
            s.SecretHp = secret;
            s.SpawnSeed = seed;
            return s;
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

            // 소유권 대기 후 네트워크 경로 ServerRpc 송신 — [netId][int amount]
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
        }

        /// <summary>동적 스폰·조건부·파괴 검증 — A(캐치업: 접속 전 스폰)와 B(브로드캐스트: 핑 후 스폰)를 값으로 식별한다.</summary>
        private static void VerifyDynamicSpawn()
        {
            // A+B 발견 대기 — A는 접속 캐치업, B는 서버가 핑 후 스폰
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

            // A (캐치업) — 위치·초기 상태·소유자 전용/스킵 조건
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

            // B (브로드캐스트) — 최종값 대기: 무조건 66·OwnerOnly 33 / InitialOnly 2002 유지·SkipOwner 3 유지
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

            // 파괴 — 등록 해제 + 인스턴스 제거
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

        /// <summary>양단이 같은 계층 경로를 갖도록 동일하게 생성 — netId 무합의 일치 검증에 필수.</summary>
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
