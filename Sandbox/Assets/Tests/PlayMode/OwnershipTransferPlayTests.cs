using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// Explicit ownership transfer over a real loopback RUDP session (ADR-0030) — the server moves an object to
    /// another connection and the real client observes the OwnerUpdate (IsOwner flips) over the wire.
    /// </summary>
    public sealed class OwnershipTransferPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 112;   // random port per run — offset 112 (unused by the other classes)

        /// <summary>Record-only channel standing in for a second connection (same pattern as ServerRpcOwnershipPlayTests).</summary>
        private sealed class GuestChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            public void SendWelcome(long connId, string serverVersion, ulong sessionToken) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason) => System.Threading.Tasks.Task.CompletedTask;
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
        }

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator 소유권_이전이_실세션에서_클라에_전파된다()
        {
            var go = new GameObject("setowner-play");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                var hostTask = UniNetManager.HostAsync(Port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "Welcome 수신");
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) == UniNetEnvironment.Client.LocalConnId, 5, "초기 소유");
                Assert.IsTrue(player.IsOwner, "이전 전 — 호스트 클라 소유");

                // Second (fake) connection on the server — the transfer target
                var guest = new GuestChannel();
                UniNetEnvironment.Server.AttachConnection(guest);
                UniNetEnvironment.PumpMain();

                bool ok = UniNetEnvironment.Server.SetOwner(player.NetId, guest.UniNetConnId);
                Assert.IsTrue(ok, "이전 성립");

                yield return WaitUntil(() => !player.IsOwner, 5, "클라 IsOwner 해제 (OwnerUpdate 왕복)");
                Assert.AreEqual(guest.UniNetConnId, UniNetEnvironment.Client.GetOwner(player.NetId), "클라 측 소유자 관찰 일치");

                // Release → the client re-owns after the next reassignment pass (a new attach triggers it)
                UniNetEnvironment.Server.SetOwner(player.NetId, 0);
                var ch3 = new GuestChannel();
                UniNetEnvironment.Server.AttachConnection(ch3);
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) != 0, 5, "재배정 입양 관찰");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, float timeout, string label)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"대기 시간 초과: {label}");
                yield return null;
            }
        }
    }
}
