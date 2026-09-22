using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DRPC;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>ServerRpc 소유자 자동 강제 — 실제 루프백 RUDP 호스트 왕복 (ADR-0016).</summary>
    public sealed class ServerRpcOwnershipPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 64;   // 실행별 랜덤 포트 — 기존 PlayMode 클래스와 오프셋 겹침 없음

        /// <summary>두 번째(비소유) 연결 흉내용 기록 채널 — 단일 프로세스에서 ClientSender는 1개뿐이라 서버 수신 경로로 대체한다.</summary>
        private sealed class GuestChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            public void SendWelcome(long connId) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) { }
        }

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();   // 잔존 리스너 정리 (멱등)

        [UnityTest]
        public IEnumerator 소유자_왕복은_실행되고_비소유_발신은_거부된다()
        {
            var go = new GameObject("ownership-play");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                var hostTask = UniNetManager.HostAsync(Port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) != 0, 5);
                Assert.IsTrue(player.IsOwner, "유일 연결(호스트 클라)이 소유");

                // 1) 소유자 발신 실제 왕복 — 강제가 정상 경로를 막지 않는다
                int pingId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcPing");
                UniNetEnvironment.ClientSender.UniNetSend(pingId,
                    VerifyPlayer.__UniNetEncode_RpcPing(player.NetId, player.SubId, 7), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.ServerPingCount == 1, 5);
                Assert.AreEqual(1, player.ServerPingCount, "소유자 발신은 왕복해 실행된다");

                // 2) 비소유 발신 — 실제 서버에 게스트 연결을 붙이고, 소유자가 아닌 연결 명의로 같은 오브젝트 대상 요청.
                //    재배정 후 누가 소유자인지는 오브젝트 수·netId 정렬에 따라 달라지므로 런타임에 비소유 연결을 고른다.
                var guest = new GuestChannel();
                UniNetEnvironment.Server.AttachConnection(guest);
                UniNetEnvironment.PumpMain();
                long ownerConn = UniNetEnvironment.Server.GetOwner(player.NetId);
                long hostConn = UniNetEnvironment.Client.LocalConnId;
                long nonOwnerConn = ownerConn == hostConn ? guest.UniNetConnId : hostConn;
                LogAssert.Expect(LogType.Warning, new Regex("ServerRpc 거부"));
                UniNetDispatch.ServerHandlers()[pingId].Handler(
                    nonOwnerConn, VerifyPlayer.__UniNetEncode_RpcPing(player.NetId, player.SubId, 9)).GetAwaiter().GetResult();
                UniNetEnvironment.PumpMain();
                Assert.AreEqual(1, player.ServerPingCount, "비소유 발신은 거부 — 구현 미실행");

                UnityEngine.Debug.Log("[UNINET-VERIFY] ServerRpcOwnership PASS — 소유자 왕복 실행 + 비소유 발신 거부");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, double timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.Elapsed.TotalSeconds < timeoutSeconds)
                yield return null;
        }
    }
}
