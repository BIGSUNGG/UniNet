using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// Per-connection ServerRpc rate limiting (ADR-0032) — fixed-window gate, drop accounting, window refresh,
    /// and the unlimited default. The generated-dispatch drop path runs in PlayMode (RpcRateLimitPlayTests).
    /// </summary>
    public sealed class RpcRateLimitTests
    {
        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(null);
        }

        [Test]
        public void 기본값_무제한은_항상_허용한다()
        {
            var server = new NetworkServer();   // ServerRpcRateLimitPerSecond = 0 (default)
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            for (int i = 0; i < 100; i++)
                Assert.IsTrue(server.AllowsServerRpc(ch.UniNetConnId), "무제한 — 100연속 허용");
            Assert.AreEqual(0, server.RateLimitedServerRpcDrops, "드롭 없음");
        }

        [Test]
        public void 플러드는_한도_초과분을_드롭하고_집계한다()
        {
            var server = new NetworkServer { ServerRpcRateLimitPerSecond = 3 };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            int allowed = 0;
            for (int i = 0; i < 13; i++)
                if (server.AllowsServerRpc(ch.UniNetConnId))
                    allowed++;

            Assert.AreEqual(3, allowed, "1초 창 내 한도만 허용");
            Assert.AreEqual(10, server.RateLimitedServerRpcDrops, "초과 10건 드롭 집계(전체)");
            Assert.AreEqual(10, server.GetRateLimitedServerRpcDrops(ch.UniNetConnId), "연결별 드롭 집계");
        }

        [Test]
        public void 창이_갱신되면_다시_허용한다()
        {
            var server = new NetworkServer { ServerRpcRateLimitPerSecond = 2 };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            Assert.IsTrue(server.AllowsServerRpc(ch.UniNetConnId), "1번째");
            Assert.IsTrue(server.AllowsServerRpc(ch.UniNetConnId), "2번째");
            Assert.IsFalse(server.AllowsServerRpc(ch.UniNetConnId), "한도 초과");

            System.Threading.Thread.Sleep(1100);   // fixed 1-second window rolls over
            Assert.IsTrue(server.AllowsServerRpc(ch.UniNetConnId), "새 창 — 다시 허용");
        }
    }
}
