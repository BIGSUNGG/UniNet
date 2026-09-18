using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>P3-④ NetUpdateFrequency — 오브젝트별 전송 주기 스케줄링 검증 (네트워킹 없음).</summary>
    public sealed class UpdateFrequencyTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [Test]
        public void 주기가_지나기_전에는_변경을_비교하지_않는다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "freq-1");
            player.NetworkUpdateFrequencyHz = 2f;   // 0.5초마다 1회

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            player.Score = 10;
            server.TickReplication(server.SnapshotObjects(), 0.0);
            Assert.AreEqual(1, ch.Replicates.Count, "첫 비교는 즉시 도달 — 전송");

            player.Score = 11;
            server.TickReplication(server.SnapshotObjects(), 0.25);
            Assert.AreEqual(1, ch.Replicates.Count, "주기 미도달 — 변경분은 다음 도달 틱까지 유지된다");

            player.Score = 12;
            server.TickReplication(server.SnapshotObjects(), 0.5);
            Assert.AreEqual(2, ch.Replicates.Count, "도달 틱 — 최신 변경분 전송");
            CheckLatestScore(ch, 12);
        }

        [Test]
        public void 시간이_없는_즉시_모드는_주기를_무시한다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "freq-2");
            player.NetworkUpdateFrequencyHz = 2f;

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            player.Score = 5;
            server.TickReplication(server.SnapshotObjects());   // 1-arg — P2 호환 경로
            Assert.AreEqual(1, ch.Replicates.Count, "즉시 모드 — 주기 정책 미적용");
        }

        [Test]
        public void 주기_0은_매_틱_전송을_유지한다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "freq-3");   // hz 기본 0

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            ch.Clear();

            player.Score = 5;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            player.Score = 6;
            server.TickReplication(server.SnapshotObjects(), 1.1);
            Assert.AreEqual(2, ch.Replicates.Count, "주기 0 (기본) — 매 틱 비교·전송");
        }

        private static void CheckLatestScore(RecordingChannel ch, int expected)
        {
            foreach (var (_, _, _, payload) in ch.Replicates)
            {
                if (payload.Length >= 8
                    && (System.BitConverter.ToUInt32(payload, 0) & (1u << 0)) != 0
                    && System.BitConverter.ToInt32(payload, 4) == expected)
                    return;
            }
            Assert.Fail($"Score={expected} 페이로드 없음 (전송 {ch.Replicates.Count}건)");
        }
    }
}
