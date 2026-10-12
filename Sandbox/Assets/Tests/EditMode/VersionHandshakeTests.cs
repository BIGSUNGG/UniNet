using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// Protocol version handshake (ADR-0028) — server-side accept/reject of client version declarations.
    /// The end-to-end mismatch kick over a real session runs in PlayMode (VersionMismatchPlayTests).
    /// </summary>
    public sealed class VersionHandshakeTests
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
        public void 불일치_버전은_VersionMismatch_킥으로_거부된다()
        {
            var server = new NetworkServer { ExpectedProtocolVersion = "game-2.0" };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            bool accepted = server.OnClientVersionDeclared(ch.UniNetConnId, "game-1.0");

            Assert.IsFalse(accepted, "불일치 — 거부");
            Assert.AreEqual(1, ch.DisconnectNotices.Count, "거부 공지 1회");
            Assert.AreEqual(UniNetDisconnectReason.VersionMismatch, ch.DisconnectNotices[0], "버전 불일치 사유 (ADR-0028)");
        }

        [Test]
        public void 일치_버전은_거부되지_않는다()
        {
            var server = new NetworkServer { ExpectedProtocolVersion = "game-2.0" };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            bool accepted = server.OnClientVersionDeclared(ch.UniNetConnId, "game-2.0");

            Assert.IsTrue(accepted, "일치 — 수용");
            Assert.AreEqual(0, ch.DisconnectNotices.Count, "거부 공지 없음");
        }

        [Test]
        public void 기대_버전이_비면_검사를_건너뛴다()
        {
            var server = new NetworkServer { ExpectedProtocolVersion = "" };   // disabled — same-version enforcement is opt-in per deployment
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            bool accepted = server.OnClientVersionDeclared(ch.UniNetConnId, "anything");

            Assert.IsTrue(accepted, "검사 비활성 — 어떤 버전이든 수용");
            Assert.AreEqual(0, ch.DisconnectNotices.Count, "거부 공지 없음");
        }
    }
}
