using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>P3-⑤ 채널 우선순위 큐 — 유형별 대역폭 예산 검증 (네트워킹 없음).</summary>
    public sealed class ChannelBudgetTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [Test]
        public void 유형_예산을_넘은_유형만_연기되고_다른_유형은_계속_흐른다()
        {
            var server = new NetworkServer();   // 전역 예산 0 = 무제한
            var beaconA = ReplicationTestSupport.RegisterScene<PulseBeacon>(server, "ch-beacon-a");
            var beaconB = ReplicationTestSupport.RegisterScene<PulseBeacon>(server, "ch-beacon-b");
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "ch-player");
            server.SetReplicationChannelBudget(typeof(PulseBeacon), 8);   // 비콘 유형은 틱당 8바이트 (델타 1건)

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            beaconA.Charge = 1;
            beaconB.Charge = 2;
            player.Score = 30;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(2, ch.Replicates.Count, "비콘 1건(유형 예산) + 플레이어 1건(무예산)만 전송");
            Assert.AreEqual(1, CountNetId(ch, beaconA.NetId), "예산 안의 첫 비콘은 전송");
            Assert.AreEqual(0, CountNetId(ch, beaconB.NetId), "유형 예산 초과 비콘은 연기");
            Assert.AreEqual(1, CountNetId(ch, player.NetId), "다른 유형은 예산과 무관하게 흐른다");

            server.TickReplication(server.SnapshotObjects(), 1.5);
            Assert.AreEqual(1, CountNetId(ch, beaconB.NetId), "다음 틱 — 연기된 비콘이 기아 보정으로 전송된다");
        }

        [Test]
        public void 예산_해제는_유형_제한을_없앤다()
        {
            var server = new NetworkServer();
            var beaconA = ReplicationTestSupport.RegisterScene<PulseBeacon>(server, "ch-free-a");
            var beaconB = ReplicationTestSupport.RegisterScene<PulseBeacon>(server, "ch-free-b");

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            server.SetReplicationChannelBudget(typeof(PulseBeacon), 8);
            server.SetReplicationChannelBudget(typeof(PulseBeacon), 0);   // 해제

            beaconA.Charge = 1;
            beaconB.Charge = 2;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(2, ch.Replicates.Count, "예산 해제 — 비콘 둘 다 즉시 전송");
        }

        private static int CountNetId(RecordingChannel ch, ulong targetNetId)
        {
            int count = 0;
            foreach (var (netId, _, _, _) in ch.Replicates)
                if (netId == targetNetId) count++;
            return count;
        }
    }
}
