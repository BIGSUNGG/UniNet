using System;
using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>P3 dormancy — verifies delta suppression while dormant and delivery of accumulated changes on wake (no networking).</summary>
    public sealed class DormancyTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [Test]
        public void 휴면_중_변경은_전송되지_않는다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "dorm-1");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            player.NetworkDormant = true;
            player.Score = 99;

            server.TickReplication(server.SnapshotObjects(), 1.0);
            server.TickReplication(server.SnapshotObjects(), 1.5);
            Assert.AreEqual(0, ch.Replicates.Count, "휴면 중 — 변경돼도 델타 비교·전송이 중단된다");
        }

        [Test]
        public void 깨우면_휴면_중_누적된_변경분이_전송된다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "dorm-2");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            player.NetworkDormant = true;
            player.Score = 99;
            server.TickReplication(server.SnapshotObjects(), 1.0);

            player.FlushNetworkDormancy();
            server.TickReplication(server.SnapshotObjects(), 1.5);
            Assert.AreEqual(1, ch.Replicates.Count, "깨어난 틱 — 휴면 중 누적 변경분이 한 번에 전송된다");
            Assert.AreEqual(99, BitConverter.ToInt32(ch.Replicates[0].payload, 4), "스냅샷이 휴면 중 갱신되지 않아 변경분이 보존된다");
        }

        [Test]
        public void 깨어난_후_변경_없으면_재전송하지_않는다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "dorm-3");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            player.NetworkDormant = true;
            player.Score = 99;
            player.FlushNetworkDormancy();
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(1, ch.Replicates.Count);

            server.TickReplication(server.SnapshotObjects(), 1.5);
            Assert.AreEqual(1, ch.Replicates.Count, "이후 틱 — 대기열 잔여물이 재전송되지 않는다");
        }
    }
}
