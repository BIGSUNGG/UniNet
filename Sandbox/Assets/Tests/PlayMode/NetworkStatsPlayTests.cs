using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// Network statistics over a real loopback RUDP session (ADR-0029) — byte/packet counters move on both ends
    /// and the ping probe (wire 8/9) measures a non-negative loopback RTT within a few seconds.
    /// </summary>
    public sealed class NetworkStatsPlayTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // idempotent cleanup
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator 실세션에서_양단_카운터와_RTT가_측정된다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "Welcome 수신");

            var server = UniNetEnvironment.Server;
            var client = UniNetEnvironment.Client;
            long sentBefore = server.TrafficStats.BytesSent;

            // let the ping probe (1 s period) and replication fly — the first round trip sets the client RTT,
            // and the NEXT probe piggybacks it to the server (ServerConnection.RttMs)
            yield return WaitUntil(() => client.RoundTripTimeMs >= 0, 6, "첫 RTT 왕복");
            yield return WaitUntil(() => System.Linq.Enumerable.Any(server.SnapshotConnections(), c => c.RttMs >= 0), 6, "서버 커넥션 RTT 보고(핑 피기백)");

            Assert.Greater(server.TrafficStats.BytesSent, sentBefore, "서버 송신 바이트 증가 (리플리케이션·TimeSync)");
            Assert.Greater(server.TrafficStats.BytesReceived, 0, "서버 수신 바이트 증가 (버전 선언·핑 프로브)");
            Assert.Greater(client.TrafficStats.BytesReceived, 0, "클라 수신 바이트 증가 (Welcome·리플리케이션·에코)");
            Assert.Greater(client.TrafficStats.BytesSent, 0, "클라 송신 바이트 증가 (버전 선언·핑 프로브)");
            Assert.GreaterOrEqual(client.RoundTripTimeMs, 0, "RTT 측정됨 (ADR-0029)");
            Assert.Less(client.RoundTripTimeMs, 500, "루프백 RTT 상식 범위");

            var conn = server.SnapshotConnections();
            Assert.GreaterOrEqual(conn.Count > 0 ? conn[0].RttMs : -1, 0, "서버 측 커넥션 RTT(클라 보고값) 측정됨");
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
