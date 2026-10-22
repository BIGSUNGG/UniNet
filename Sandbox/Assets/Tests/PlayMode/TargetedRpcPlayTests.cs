using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DRPC;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// RPC-001 T7 — targeted ClientRpc companions on a real loopback: with two connections (host client + guest),
    /// an owner-targeted send reaches the owner connection (loops back to the host client's implementation) and
    /// never lands on the guest channel. GuestChannel pattern reused from ServerRpcOwnershipPlayTests, plus UniNetSend
    /// recording so a missed send is assertable. Requires generator 0.2.14 deployed to Package analyzers.
    /// </summary>
    public sealed class TargetedRpcPlayTests
    {
        /// <summary>Record-only channel standing in for a second (non-owner) connection — a single process has only one ClientSender, so the server send path substitutes for it.</summary>
        private sealed class GuestChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal readonly List<(int methodId, byte[] payload, RpcDeliveryMode mode)> RpcSends = new();

            public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) => RpcSends.Add((methodId, payload, mode));
            public void SendWelcome(long connId, string serverVersion, string sessionToken) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason) => System.Threading.Tasks.Task.CompletedTask;
            public void SendLoadScene(string sceneName) { }
        }

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();   // clean up leftover listeners (idempotent)

        [UnityTest]
        public IEnumerator 소유자와_연결_타깃_ClientRpc는_게스트에게_수신되지_않는다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var go = new GameObject("targeted-play");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                var hostTask = UniNetManager.HostAsync(port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) != 0, 5);
                Assert.IsTrue(player.IsOwner, "유일 연결(호스트 클라)이 소유");

                var guest = new GuestChannel();
                UniNetEnvironment.Server.AttachConnection(guest);
                UniNetEnvironment.PumpMain();

                int awardId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcAward");
                long ownerConn = UniNetEnvironment.Server.GetOwner(player.NetId);
                Assert.AreEqual(UniNetEnvironment.Client.LocalConnId, ownerConn, "사전 조건 — 호스트 클라가 소유");

                // 1) owner-targeted send on the server → rides the real loopback to the owner connection only
                player.RpcAwardToOwner(7, "mvp");
                yield return WaitUntil(() => player.AwardRan, 5);
                Assert.IsTrue(player.AwardRan, "owner 연결로 루프백 — 클라에서 구현 실행");
                Assert.AreEqual(7, player.AwardCoins, "owner 타깃 인자 전달");
                Assert.AreEqual("mvp", player.AwardReason, "owner 타깃 문자열 인자 전달");
                Assert.AreEqual(0, guest.RpcSends.Count(s => s.methodId == awardId), "게스트(비소유) 채널 미수신");

                // 2) connId targeting — same single-recipient contract on the real wire
                player.RpcAwardTo(ownerConn, 8, "direct");
                yield return WaitUntil(() => player.AwardCoins == 8, 5);
                Assert.AreEqual("direct", player.AwardReason, "connId 타깃 인자 전달");
                Assert.AreEqual(0, guest.RpcSends.Count(s => s.methodId == awardId), "게스트 채널 여전히 미수신");

                UnityEngine.Debug.Log("[UNINET-VERIFY] TargetedRpc PASS — 소유자·연결 타깃 ClientRpc 게스트 미수신");
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
