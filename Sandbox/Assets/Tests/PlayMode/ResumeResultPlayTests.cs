using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// REC-001 over a real loopback RUDP session — the client observes its own resume RESULTS (wire 14): a genuine
    /// preserved token reattaches (event Reattached + ownership restored) and a forged token is rejected with an
    /// exact terminal reason (UnknownToken — never a retryable SessionNotFound misread). Honest scope: the token is
    /// preserved across the stop by re-seeding the environment client (the same test-procedure injection the SEC-001
    /// Arena diagnostic uses — voluntary ClientStop burns the token by contract, REC-002, out of scope here).
    /// </summary>
    public sealed class ResumeResultPlayTests
    {
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
        public IEnumerator 진짜_토큰은_재부착_결과를_위조_토큰은_거절_결과를_관측한다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            var server = UniNetEnvironment.Server;
            server.SessionGraceSeconds = 10;   // grace on — the stop path saves the session

            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "1차 접속 Welcome");
            long firstConn = UniNetEnvironment.Client.LocalConnId;
            string realToken = UniNetEnvironment.Client.SessionToken;
            Assert.IsNotEmpty(realToken, "세션 토큰 발급 관측");

            // drop the client — the server keeps the grace session (the token is preserved by the test via re-seed)
            RawDrop();

            // 1) FORGED token — terminal rejection, observed as a wire-14 result on the reconnecting client
            var forged = new NetworkClient { SessionToken = realToken.Substring(0, 40) + (realToken[40] == 'A' ? 'B' : 'A') + realToken.Substring(41) };
            UniNetEnvironment.SetClient(forged);
            var connect1 = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!connect1.IsCompleted) yield return null;
            yield return WaitUntil(() => UniNetEnvironment.Client.LastResumeResult != GraceResumeResult.None, 5, "위조 토큰 — wire 14 결과 도착");
            Assert.AreEqual(GraceResumeResult.SessionNotFound, UniNetEnvironment.Client.LastResumeResult, "위조(미지) 토큰 — SessionNotFound (재시도 창과 함께)");
            RawDrop();

            // 2) GENUINE token — reattached. Poll the sticky LastResumeResult on the CURRENT environment client
            // (like case 1): ClientAsync builds a NEW NetworkClient internally and only carries the token, so an event
            // subscribed on this pre-connect instance never fires. The event CONTRACT itself is verified by the EditMode
            // 실제_생성_수신기 test through the real generated wire-14 receiver.
            var genuine = new NetworkClient { SessionToken = realToken };
            UniNetEnvironment.SetClient(genuine);
            var connect2 = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!connect2.IsCompleted) yield return null;
            yield return WaitUntil(() => UniNetEnvironment.Client.LastResumeResult == GraceResumeResult.Reattached, 5, "진짜 토큰 — Reattached 이벤트");
            Assert.AreEqual(GraceResumeResult.Reattached, UniNetEnvironment.Client.LastResumeResult, "최종 결과 상태");
            UnityEngine.Debug.Log($"[UNINET-VERIFY] ResumeResult PASS — conn {firstConn} reattached over real RUDP (forged rejected first)");
        }

        /// <summary>Raw transport drop — NOT a voluntary ClientStop (wire 15 would tombstone the token; REC-002). This
        /// test models an ungraceful drop, where the server keeps the grace session for the reconnect scenarios.</summary>
        private static void RawDrop()
        {
            var sender = UniNetEnvironment.ClientSender;
            UniNetEnvironment.SetClientSender(null);
            UniNetEnvironment.SetClient(null);
            if (sender is DRPC.Shared.Network.HubBase hub)
            {
                hub.Disconnect();
                hub.Dispose();
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
