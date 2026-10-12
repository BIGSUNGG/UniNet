using System.Collections;
using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// Client-side disconnect reasons over a real loopback RUDP session (ADR-0027) — kick round-trip and
    /// the voluntary-stop contract. The server-shutdown reason is covered by LifecycleEventPlayTests.
    /// </summary>
    public sealed class DisconnectReasonPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 88;   // random port per run — offset 88 (80 collides with FastArrayRoundtripTests)

        private static bool _raised;
        private static UniNetDisconnectReason _raisedReason;

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;   // fire-and-forget send noise during shutdown — same handling as the other lifecycle tests
            _raised = false;
            _raisedReason = UniNetDisconnectReason.None;
            UniNetManager.ClientDisconnected += OnSessionEnded;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetManager.ClientDisconnected -= OnSessionEnded;   // static event — prevents leaking across tests
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // idempotent cleanup of leftovers
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        private static void OnSessionEnded(UniNetDisconnectReason reason)
        {
            _raised = true;
            _raisedReason = reason;
        }

        [UnityTest]
        public IEnumerator 서버_킥은_클라에_킥_사유로_전달된다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "Welcome 수신");
            Assert.IsTrue(UniNetManager.IsClientConnected, "킥 전 접속 상태");

            // Server kicks the (single) host-client connection — notice rides the wire before the transport close
            bool kicked = UniNetEnvironment.Server.DisconnectClient(UniNetEnvironment.Client.LocalConnId, UniNetDisconnectReason.Kicked);
            Assert.IsTrue(kicked, "킥 요청 성립");

            yield return WaitUntil(() => _raised, 5, "클라 해제 콜백 발화");
            Assert.AreEqual(UniNetDisconnectReason.Kicked, _raisedReason, "킥 사유 수신 (ADR-0027)");
            Assert.IsFalse(UniNetManager.IsClientConnected, "킥 후 해제 상태");
        }

        [UnityTest]
        public IEnumerator 자발적_ClientStop은_해제_콜백을_발화하지_않는다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "Welcome 수신");

            UniNetManager.ClientStop();   // voluntary — the documented contract: flag clears, event NOT raised
            Assert.IsFalse(UniNetManager.IsClientConnected, "즉시 해제 상태");

            for (int i = 0; i < 30; i++)   // ~0.5 s at 60 fps — a late raise would surface here
                yield return null;
            Assert.IsFalse(_raised, "자발적 종료 — 이벤트 미발화 (기존 계약 유지)");
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
