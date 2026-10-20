using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// CON-001 over a real loopback RUDP session. Real UniNet client hubs declare their protocol version
    /// automatically on connect — asserted here (the host's own client reaches the approved state over the real
    /// wire). The undeclared side is exercised with a channel-less fake connection on the REAL server (the same
    /// attach path a raw/undeclared transport client takes): it stays held (no ClientConnected, unapproved) and the
    /// real driver cadence's timeout sweep kicks it with VersionMismatch. Honest scope: the transport-level
    /// non-declaring socket is not spawned (a bare DRPC hub needs a session factory the test cannot construct);
    /// the held state, approval, and kick paths all run on the real server + driver.
    /// </summary>
    public sealed class ConnectionApprovalPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 17;   // random port per run — offset +17 (no overlap with the other PlayMode classes)

        /// <summary>Record-only channel standing in for an undeclared connection (never sends wire 7).</summary>
        private sealed class SilentChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            public System.Collections.Generic.List<UniNet.Core.UniNetDisconnectReason> Notices = new();
            public void SendWelcome(long connId, string serverVersion, string sessionToken) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, DRPC.RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason)
            {
                Notices.Add(reason);
                return System.Threading.Tasks.Task.CompletedTask;
            }
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
            public void SendLoadScene(string sceneName) { }
        }

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            foreach (var boot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                boot.enabled = false;   // tests run on the Arena scene — the bootstrap must not adopt the test server
        }

        [TearDown]
        public void TearDown() => UniNetManager.HostStop();

        [OneTimeTearDown]
        public void RestoreLogChecks() => LogAssert.ignoreFailingMessages = false;

        [UnityTest]
        public IEnumerator 실제_클라는_자동_선언으로_승인되고_미선언_연결은_유보_후_타임아웃_킥된다()
        {
            // real RUDP server with a short undeclared timeout (500 ms) — the sweep rides the driver tick
            var hostTask = UniNetManager.HostAsync(Port, new UniNetEndpointOptions { VersionDeclareTimeoutMs = 500 }, null);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var server = UniNetEnvironment.Server;
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "호스트 클라 Welcome");
            yield return WaitUntil(() => server.IsVersionApproved(UniNetEnvironment.Client.LocalConnId), 5, "실제 클라 — 자동 선언 승인 (실RUDP 왕복)");

            int connected = 0;
            server.ClientConnected += _ => connected++;
            int baseline = connected;

            // an undeclared connection on the real server — the same attach path a raw socket client takes
            var silent = new SilentChannel();
            server.AttachConnection(silent);
            yield return new WaitForSecondsRealtime(0.2f);
            UniNetEnvironment.PumpMain();

            Assert.IsFalse(server.IsVersionApproved(silent.UniNetConnId), "미선언 연결 — 미승인 유지");
            Assert.AreEqual(baseline, connected, "ClientConnected 유보");

            // the driver cadence's sweep (UniNetManager wires it) kicks it with VersionMismatch
            yield return WaitUntil(() => silent.Notices.Count >= 1, 8, "타임아웃 킥 — VersionMismatch 공지");
            Assert.AreEqual(UniNet.Core.UniNetDisconnectReason.VersionMismatch, silent.Notices[0], "킥 사유");
            UnityEngine.Debug.Log("[UNINET-VERIFY] ConnectionApproval PASS — real client auto-approved over RUDP; undeclared held then timed out");
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, float timeout, string label)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed.TotalSeconds > timeout)
                    Assert.Fail($"대기 시간 초과: {label}");
                yield return null;
            }
        }
    }
}
