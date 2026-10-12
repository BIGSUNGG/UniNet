using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Network statistics (ADR-0029) — counter accumulation, budget-deferral queue depth, and main-queue depth.
    /// Real-session byte counts and RTT measurement run in PlayMode (NetworkStatsPlayTests).
    /// </summary>
    public sealed class NetworkStatsTests
    {
        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.PumpMain();   // drain — keep the queue-depth test from leaking into other tests
            UniNetEnvironment.SetServer(null);
        }

        [Test]
        public void 카운터는_송수신_기록을_누적한다()
        {
            var counters = new UniNetTrafficCounters();
            counters.OnSent(10);
            counters.OnSent(15);
            counters.OnReceived(5);
            counters.OnReceived(7);
            counters.OnReceived(3);
            counters.OnSent(0);   // zero-length payload still counts as a packet

            Assert.AreEqual(25, counters.BytesSent, "송신 바이트 누적");
            Assert.AreEqual(3, counters.PacketsSent, "송신 패킷 수");
            Assert.AreEqual(15, counters.BytesReceived, "수신 바이트 누적");
            Assert.AreEqual(3, counters.PacketsReceived, "수신 패킷 수");
        }

        [Test]
        public void 예산_초과_지연은_DeferredSendCount로_관측된다()
        {
            var server = new NetworkServer { ReplicationBudgetPerTickBytes = 8 };   // fits one delta (~6 B) but not two — the second defers (anti-starvation still force-sends single deltas larger than the whole budget)
            UniNetEnvironment.SetServer(server);
            var a = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "stats-budget-a");
            var b = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "stats-budget-b");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // catch-up (outside the budget policy)
            ch.Clear();

            a.Score = 42;   // two deltas in one tick — the second exceeds the remaining budget
            b.Score = 43;
            server.TickReplication(server.SnapshotObjects(), 1.0);

            Assert.GreaterOrEqual(server.DeferredSendCount, 1, "예산 초과 델타가 지연 큐에 보인다 (ADR-0029)");
            Assert.AreEqual(1, CountSent(ch), "첫 델타만 이번 틱에 전송");
        }

        private static int CountSent(RecordingChannel ch)
        {
            int count = 0;
            for (int i = 0; i < ch.Replicates.Count; i++)
                if (ch.Replicates[i].payload != null && ch.Replicates[i].payload.Length > 0)
                    count++;
            return count;
        }

        [Test]
        public void PendingMainActions는_메인_큐_깊이를_보고한다()
        {
            int before = UniNetEnvironment.PendingMainActions;
            bool ran = false;
            UniNetEnvironment.QueueOnMain(() => ran = true);

            Assert.AreEqual(before + 1, UniNetEnvironment.PendingMainActions, "큐 적립 직후 깊이 +1");
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(ran, "드레인 실행");
            Assert.AreEqual(before, UniNetEnvironment.PendingMainActions, "드레인 후 깊이 복원");
        }
    }
}
