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
    /// <summary>ServerRpc owner auto-enforcement — real loopback RUDP host round-trip (see ADR-0016).</summary>
    public sealed class ServerRpcOwnershipPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 64;   // random port per run — no offset overlap with the other PlayMode classes

        /// <summary>Record-only channel standing in for a second (non-owner) connection — a single process has only one ClientSender, so the server receive path substitutes for it.</summary>
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
        public void StopListeners() => UniNetManager.HostStop();   // clean up leftover listeners (idempotent)

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

                // 1) Owner-sent round-trip — enforcement must not block the legitimate path
                int pingId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcPing");
                UniNetEnvironment.ClientSender.UniNetSend(pingId,
                    VerifyPlayer.__UniNetEncode_RpcPing(player.NetId, player.SubId, 7), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.ServerPingCount == 1, 5);
                Assert.AreEqual(1, player.ServerPingCount, "소유자 발신은 왕복해 실행된다");

                // 2) Non-owner send — attach a real guest connection to the server and request the same object under a non-owner connection identity.
                //    Who owns the object after reassignment depends on object count and netId ordering, so pick a non-owner connection at runtime.
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
