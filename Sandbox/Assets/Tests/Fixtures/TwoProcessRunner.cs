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
            var serverTask = UniNetManager.ServerAsync(Port);
            Wait(serverTask, 15, "서버 리슨");
            if (serverTask.IsFaulted) { Debug.LogError("[UNINET-2PROC] SERVER-ABORT"); return; }
            Debug.Log("[UNINET-2PROC] server-ready");
            var keys = string.Join(",", global::System.Linq.Enumerable.Select(UniNetDispatch.ServerHandlers().Keys, k => k.ToString()));
            Debug.Log("[UNINET-2PROC] server-table=[" + keys + "] factory=" + (UniNetEnvironment.HubFactory != null));

            var deadline = DateTime.UtcNow.AddSeconds(25);
            bool loggedAttach = false;
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
                if (player.ServerPingCount > 0 && player.Score != 100)
                {
                    Thread.Sleep(500);   // 클라 수신 여유
                    UniNetEnvironment.PumpMain();
                    Debug.Log("[UNINET-2PROC] SERVER-DONE score=" + player.Score);
                    return;
                }
                Thread.Sleep(30);
            }
            Debug.LogError("[UNINET-2PROC] SERVER-TIMEOUT ping=" + player.ServerPingCount);
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
