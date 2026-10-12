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
    /// Protocol version handshake over real loopback RUDP sessions (ADR-0028) — a mismatched client is rejected
    /// with the VersionMismatch reason (kick infrastructure reuse), a matched custom version connects normally.
    /// </summary>
    public sealed class VersionMismatchPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 96;   // random port per run — offset 96 (unused by the other classes)

        private static bool _raised;
        private static UniNetDisconnectReason _raisedReason;

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;   // the mismatch rejection intentionally logs a warning server-side
            _raised = false;
            _raisedReason = UniNetDisconnectReason.None;
            UniNetManager.ClientDisconnected += OnSessionEnded;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetManager.ClientDisconnected -= OnSessionEnded;
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.ClientStop();
            UniNetManager.ServerStop();
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
        public IEnumerator 버전_불일치_클라는_VersionMismatch_사유로_거부된다()
        {
            var serverTask = UniNetManager.ServerAsync(Port, new UniNetEndpointOptions { ProtocolVersion = "arena-9.0" });
            while (!serverTask.IsCompleted) yield return null;
            Assert.IsFalse(serverTask.IsFaulted, serverTask.Exception?.ToString());

            var clientTask = UniNetManager.ClientAsync("127.0.0.1", Port);   // declares the default package version — mismatch by design
            while (!clientTask.IsCompleted) yield return null;
            Assert.IsFalse(clientTask.IsFaulted, clientTask.Exception?.ToString());

            yield return WaitUntil(() => _raised, 8, "버전 불일치 거부 발화");
            Assert.AreEqual(UniNetDisconnectReason.VersionMismatch, _raisedReason, "버전 불일치 사유 (ADR-0028)");
            Assert.IsFalse(UniNetManager.IsClientConnected, "거부 후 해제 상태");
        }

        [UnityTest]
        public IEnumerator 버전_일치_커스텀_버전은_정상_접속된다()
        {
            var serverTask = UniNetManager.ServerAsync(Port + 1, new UniNetEndpointOptions { ProtocolVersion = "arena-9.0" });
            while (!serverTask.IsCompleted) yield return null;
            Assert.IsFalse(serverTask.IsFaulted, serverTask.Exception?.ToString());

            var clientTask = UniNetManager.ClientAsync("127.0.0.1", Port + 1, new UniNetEndpointOptions { ProtocolVersion = "arena-9.0" });
            while (!clientTask.IsCompleted) yield return null;
            Assert.IsFalse(clientTask.IsFaulted, clientTask.Exception?.ToString());

            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "Welcome 수신");
            Assert.AreEqual("arena-9.0", UniNetEnvironment.Client.ServerProtocolVersion, "서버 버전이 Welcome에 실려 온다 (ADR-0028)");
            Assert.IsTrue(UniNetManager.IsClientConnected, "일치 — 접속 유지");

            for (int i = 0; i < 30 && !_raised; i++) yield return null;   // ~0.5 s — a late rejection would surface here
            Assert.IsFalse(_raised, "일치 버전은 거부되지 않는다");
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
