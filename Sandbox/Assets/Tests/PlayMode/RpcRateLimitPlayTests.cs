using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// ServerRpc rate limiting over a real loopback session (ADR-0032) — a same-frame flood of ServerRpcs
    /// exceeds the per-connection limit: the handler runs at most limit times and the drops are counted.
    /// </summary>
    public sealed class RpcRateLimitPlayTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;   // the drop path intentionally logs a throttled warning
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
        public IEnumerator 동시_프레임_플러드는_한도만_실행하고_드롭을_집계한다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var go = new GameObject("ratelimit-play");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                var hostTask = UniNetManager.HostAsync(port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "Welcome 수신");
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) == UniNetEnvironment.Client.LocalConnId, 5, "소유권");

                var server = UniNetEnvironment.Server;
                server.ServerRpcRateLimitPerSecond = 5;
                int pingId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcPing");

                // Same-frame burst — 40 ServerRpcs in one window with a limit of 5
                for (int i = 0; i < 40; i++)
                {
                    var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
                    w.WriteUInt64(player.NetId);
                    w.WriteByte(0);
                    w.WriteInt32(1);
                    UniNetEnvironment.ClientSender.UniNetSend(pingId, w.ToArray(), DRPC.RpcDeliveryMode.ReliableOrdered);
                }

                yield return WaitUntil(() => server.RateLimitedServerRpcDrops >= 30, 8, "드롭 집계 관찰");
                // let any straggler executes settle, then bound the executed count by the window limit
                yield return new WaitForSeconds(0.5f);
                Assert.LessOrEqual(player.ServerPingCount, 8, "실행 횟수는 한도(+창 경계 여유) 이하");
                Assert.GreaterOrEqual(server.RateLimitedServerRpcDrops, 30, "드롭 30+ 집계 (ADR-0032)");
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
