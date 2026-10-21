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
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs

            // real RUDP server with a short undeclared timeout (500 ms) — the sweep rides the driver tick
            var hostTask = UniNetManager.HostAsync(port, new UniNetEndpointOptions { VersionDeclareTimeoutMs = 500 }, null);
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

        // ── CON-002 — 실RUDP 승인 콜백 (wire 7 자격 증명 왕복, ADR-0047) ──
        // 전제: 생성기 0.2.13 DLL이 Analyzers에 재배포되어 있어야 클라 허브가 wire 7에 자격 증명을 실어 보낸다.

        [UnityTest]
        public IEnumerator 실제_클라의_자격_증명이_서버_승인_콜백에_도달해_승인된다()
        {
            int port = TestPortAllocator.Next();
            string seenCredential = null;
            int connected = 0;

            var serverOptions = new UniNetEndpointOptions
            {
                ConnectionApprover = r =>
                {
                    seenCredential = r.Credential;
                    return System.Threading.Tasks.Task.FromResult(!string.IsNullOrEmpty(r.Credential));
                },
            };
            var clientOptions = new UniNetEndpointOptions { ConnectionCredential = "play-cred" };

            var hostTask = UniNetManager.HostAsync(port, serverOptions, clientOptions);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var server = UniNetEnvironment.Server;
            server.ClientConnected += _ => connected++;

            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "호스트 클라 Welcome");
            yield return WaitUntil(() => server.IsVersionApproved(UniNetEnvironment.Client.LocalConnId), 5, "자격 증명 승인 (실RUDP wire 7 왕복)");

            Assert.AreEqual("play-cred", seenCredential, "클라 자격 증명 — 서버 콜백 도달 (조건부 읽기 경로)");
            Assert.AreEqual(1, connected, "승인 — ClientConnected 발화");
            Assert.IsTrue(UniNetManager.IsClientConnected, "양단 세션 성립");
            yield return new WaitForSecondsRealtime(0.3f);   // 플레이 지속 — 승인 직후 세션이 끊기지 않음
            Assert.IsTrue(UniNetManager.IsClientConnected, "승인 후 세션 지속");
            UnityEngine.Debug.Log("[UNINET-VERIFY] ConnectionApproval CON-002 PASS — credential approved over real RUDP");
        }

        [UnityTest]
        public IEnumerator 거부_서버는_클라에_ApprovalRejected를_통보한다()
        {
            int port = TestPortAllocator.Next();
            UniNet.Core.UniNetDisconnectReason? seenReason = null;
            UniNetManager.ClientDisconnected += r => seenReason = r;

            var serverOptions = new UniNetEndpointOptions
            {
                ConnectionApprover = r => System.Threading.Tasks.Task.FromResult(false),   // 무조건 거부
            };

            var hostTask = UniNetManager.HostAsync(port, serverOptions, null);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            yield return WaitUntil(() => seenReason != null, 8, "클라 세션 종료 통보");
            Assert.AreEqual(UniNet.Core.UniNetDisconnectReason.ApprovalRejected, seenReason.Value, "거부 사유 — ApprovalRejected (와이어 6 신규 값)");
            UnityEngine.Debug.Log("[UNINET-VERIFY] ConnectionApproval CON-002 PASS — ApprovalRejected delivered over real RUDP");
        }

        [UnityTest]
        public IEnumerator 와이어7_초과_페이로드는_조용히_폐기되고_연결은_영향을_받지_않는다()
        {
            int port = TestPortAllocator.Next();
            int approvals = 0;

            var serverOptions = new UniNetEndpointOptions
            {
                VersionDeclareTimeoutMs = 500,   // the discarded declaration never counts as a declaration — the sweep must kick, proving quiet discard
                ConnectionApprover = r => { System.Threading.Interlocked.Increment(ref approvals); return System.Threading.Tasks.Task.FromResult(true); },
            };

            var hostTask = UniNetManager.HostAsync(port, serverOptions, null);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            var server = UniNetEnvironment.Server;

            yield return WaitUntil(() => server.IsVersionApproved(UniNetEnvironment.Client.LocalConnId), 5, "실제 클라 자동 선언 승인");
            long approvedConn = UniNetEnvironment.Client.LocalConnId;
            int baseline = System.Threading.Interlocked.CompareExchange(ref approvals, 0, 0);

            // oversized wire-7 declaration (5000 > MaxVersionDeclarationPayloadBytes 4096) — discarded BEFORE parsing:
            // the approver must not be consulted again and the healthy connection must not be kicked
            UniNetEnvironment.ClientSender.UniNetSend(7, new byte[5000], DRPC.RpcDeliveryMode.ReliableOrdered);
            yield return new WaitForSecondsRealtime(0.5f);
            UniNetEnvironment.PumpMain();

            int seen = System.Threading.Interlocked.CompareExchange(ref approvals, 0, 0);
            Assert.AreEqual(baseline, seen, "초과 페이로드 — 승인 콜백 미호출 (파싱 전 조용 폐기)");
            Assert.IsTrue(server.IsVersionApproved(approvedConn), "기존 승인 상태 불변");
            Assert.IsTrue(UniNetManager.IsClientConnected, "조용 폐기 — 연결 유지 (킥 없음)");
            UnityEngine.Debug.Log("[UNINET-VERIFY] ConnectionApproval CON-002 PASS — oversized wire-7 payload quietly discarded, connection unaffected");
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
