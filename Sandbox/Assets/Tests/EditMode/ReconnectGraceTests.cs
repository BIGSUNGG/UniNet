using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Grace-session reconnect (ADR-0034) — grace preservation, reattach via token, and grace expiry.
    /// </summary>
    public sealed class ReconnectGraceTests
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
        public void 유예기간_중_소유권이_보존된다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-keep");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            long oldOwner = server.GetOwner(player.NetId);
            Assert.AreNotEqual(0, oldOwner, "초기 소유자 배정");

            server.DetachConnection(ch);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(oldOwner, server.GetOwner(player.NetId), "유예기간 중 소유권 보존 (ADR-0034)");
        }

        [Test]
        public void 재부착이_소유권을_새_연결로_이전한다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-reattach");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            UniNetEnvironment.PumpMain();
            long oldOwner = server.GetOwner(player.NetId);

            ulong token = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "재접속 후 소유자 존재");

            bool resumed = server.ResumeSession(token, chB.UniNetConnId);
            Assert.IsTrue(resumed, "토큰 매치 — 재부착 성립");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "소유권이 새 연결로 이전");
        }

        [Test]
        public void 유예_만료_후_재배정이_고아를_입양한다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 0.01 };   // 10ms grace — expires immediately in test time
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-expire");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            UniNetEnvironment.PumpMain();

            ulong token2 = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            // Grace expires — sweep + reassign adopts the orphan
            server.SweepGraceSessions(double.MaxValue);
            UniNetEnvironment.PumpMain();

            bool resumed = server.ResumeSession(token2, 999);
            Assert.IsFalse(resumed, "만료된 세션 — 재부착 거부");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "만료 후 재배정 — 새 연결이 입양");
        }

        [Test]
        public void 유예_비활성_시_즉시_재배정()
        {
            var server = new NetworkServer { SessionGraceSeconds = 0 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-off");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            UniNetEnvironment.PumpMain();
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "유예 없음 — 즉시 재배정");
        }
    }
}
