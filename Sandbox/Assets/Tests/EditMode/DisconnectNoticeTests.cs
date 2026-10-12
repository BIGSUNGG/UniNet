using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// Disconnect notices (ADR-0027) — server-side kick API and shutdown broadcast wire behavior.
    /// Uses RecordingChannel (not a real hub), so only the notice path is asserted here;
    /// the transport-close and client-reason paths run in PlayMode (DisconnectReasonPlayTests, LifecycleEventPlayTests).
    /// </summary>
    public sealed class DisconnectNoticeTests
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
        public void DisconnectClient는_사유_공지를_해당_연결에만_보낸다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            chA.Clear();
            chB.Clear();

            bool kicked = server.DisconnectClient(chA.UniNetConnId, UniNetDisconnectReason.Kicked);

            Assert.IsTrue(kicked, "알려진 연결 — 킥 성립");
            Assert.AreEqual(1, chA.DisconnectNotices.Count, "대상 연결 — 공지 1회");
            Assert.AreEqual(UniNetDisconnectReason.Kicked, chA.DisconnectNotices[0], "킥 사유 전달");
            Assert.AreEqual(0, chB.DisconnectNotices.Count, "다른 연결 — 공지 없음");
        }

        [Test]
        public void DisconnectClient는_알려지지_않은_연결에_false를_반환한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            Assert.IsFalse(server.DisconnectClient(ch.UniNetConnId + 100), "미지 연결 id — 거짓");
        }

        [Test]
        public void 서버_종료_공지는_전_연결에_전달된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();

            server.BroadcastDisconnectNotice(UniNetDisconnectReason.ServerShutdown);

            Assert.AreEqual(1, chA.DisconnectNotices.Count, "연결 A — 공지 1회");
            Assert.AreEqual(UniNetDisconnectReason.ServerShutdown, chA.DisconnectNotices[0], "서버 종료 사유");
            Assert.AreEqual(1, chB.DisconnectNotices.Count, "연결 B — 공지 1회");
            Assert.AreEqual(UniNetDisconnectReason.ServerShutdown, chB.DisconnectNotices[0], "서버 종료 사유");
        }
    }
}
