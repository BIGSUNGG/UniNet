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
        /// <summary>Record-only channel standing in for a second connection (same pattern as ServerRpcOwnershipPlayTests).</summary>
        private sealed class GuestChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            public void SendWelcome(long connId, string serverVersion, string sessionToken) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, DRPC.RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason) => System.Threading.Tasks.Task.CompletedTask;
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
            public void SendLoadScene(string sceneName) { }
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
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var go = new GameObject("setowner-play");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                var hostTask = UniNetManager.HostAsync(port);
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

        [UnityTest]
        public IEnumerator 이전_재입양_직후_baseline으로_OwnerOnly_현재값이_도착한다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var go = new GameObject("setowner-play-baseline");
            var player = go.AddComponent<SpawnablePlayer>();
            try
            {
                var hostTask = UniNetManager.HostAsync(port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "Welcome 수신");
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) == UniNetEnvironment.Client.LocalConnId, 5, "초기 소유");
                yield return WaitUntil(() => player.SecretNotifyCount >= 1, 5, "초기 baseline 적용 (Owner 뷰 캐치업)");
                int notifiesAfterInitial = player.SecretNotifyCount;   // 초기 적용분 — 관측 기준선

                player.SecretHp = 99;   // 서버(호스트) 권위 변경 — 아직 전송된 적 없는 OwnerOnly 값

                // 입양 대상이 비결정적(round-robin이 게스트를 고를 수 있음) — 호스트 클라이언트로 결정적 재입양(직접 SetOwner)으로 baseline 왕복을 검증한다 (OWN-002)
                Assert.IsTrue(UniNetEnvironment.Server.SetOwner(player.NetId, 0), "해제 성립");
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) == 0, 5, "해제 관찰 (OwnerUpdate 왕복)");
                yield return new WaitForSecondsRealtime(0.3f);   // straggler deltas (e.g. the 99 owner delta) land before the baseline re-baseline
                notifiesAfterInitial = player.SecretNotifyCount;   // re-baseline — only the re-adoption baseline counts from here

                Assert.IsTrue(UniNetEnvironment.Server.SetOwner(player.NetId, UniNetEnvironment.Client.LocalConnId), "재입양 성립");
                yield return WaitUntil(() => player.SecretNotifyCount == notifiesAfterInitial + 1, 5, "재입양 baseline 왕복 (OwnerOnly RepNotify)");
                // prev = the RECEIVER’s prior view: the receiver was the owner and had already received the 99 delta, so the
                // re-adoption baseline re-delivers 99 over 99 (the 50 spawn value only holds if that delta never landed).
                Assert.IsTrue(player.LastPrevSecret == 99 || player.LastPrevSecret == 50, "baseline prev — 수신자 직전 뷰 (99 수신 후 또는 50 미수신)");
                Assert.AreEqual(99, player.SecretHp, "호스트 권위 — IsServer 가드로 서버 값 불변");
                UnityEngine.Debug.Log("[UNINET-VERIFY] OwnershipTransferBaseline PASS — re-adoption baseline re-delivered OwnerOnly over real loopback");
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
