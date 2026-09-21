using System;
using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>P4 코어 프리미티브 — UniNetTime(시간 동기화)·SnapshotBuffer(인터폴레이션)·PositionHistory(리와인드) 테스트.</summary>
    public sealed class P4CoreTests
    {
        private Func<double> _originalClock;

        [SetUp]
        public void CaptureClock() => _originalClock = UniNetTime.LocalClock;

        [TearDown]
        public void RestoreClock()
        {
            UniNetTime.LocalClock = _originalClock;
            UniNetTime.Reset();
        }

        [Test]
        public void UniNetTime_최초_동기화는_즉시_채택하고_이후는_평활한다()
        {
            double local = 100.0;
            UniNetTime.LocalClock = () => local;
            Assert.IsFalse(UniNetTime.IsSynced, "미동기화 상태에서 시작");

            UniNetTime.ApplyServerTime(105.0);   // 첫 동기화 — 오프셋 +5 즉시 채택
            Assert.IsTrue(UniNetTime.IsSynced);
            Assert.AreEqual(105.0, UniNetTime.Now, 1e-9);

            UniNetTime.ApplyServerTime(107.0);   // 새 오프셋 +7 — EMA α=0.25 → 5 + (7-5)×0.25 = 5.5
            Assert.AreEqual(100.0 + 5.5, UniNetTime.Now, 1e-9);
        }

        [Test]
        public void SnapshotBuffer_두_샘플_사이를_보간하고_범위_밖은_홀드한다()
        {
            var buffer = new SnapshotBuffer<float>(4, (a, b, t) => a + (b - a) * (float)t);
            Assert.IsFalse(buffer.TrySample(0.0, out _), "빈 버퍼 — 질의 실패");

            buffer.Add(1.0, 10f);
            buffer.Add(2.0, 20f);

            Assert.IsTrue(buffer.TrySample(1.5, out var mid));
            Assert.AreEqual(15f, mid, 1e-5, "두 스냅샷 사이 — 선형 보간");
            Assert.IsTrue(buffer.TrySample(0.5, out var beforeHold));
            Assert.AreEqual(10f, beforeHold, 1e-5, "앞 경계 밖 — 가장 오래된 값 홀드");
            Assert.IsTrue(buffer.TrySample(9.0, out var afterHold));
            Assert.AreEqual(20f, afterHold, 1e-5, "최신 이후 — 최신 값 홀드");
        }

        [Test]
        public void SnapshotBuffer_시간_역전_스냅샷은_폐기된다()
        {
            var buffer = new SnapshotBuffer<float>(4, (a, b, t) => b);
            buffer.Add(1.0, 1f);
            buffer.Add(2.0, 2f);
            buffer.Add(0.5, 99f);   // 늦게 도착한 과거 스냅샷 — 폐기

            Assert.IsTrue(buffer.TrySample(2.5, out var latest));
            Assert.AreEqual(2f, latest, 1e-5, "역전분이 최신 값을 오염시키지 않는다");
        }

        [Test]
        public void PositionHistory_과거_시점을_보간해_되돌려준다()
        {
            var history = new PositionHistory(8);
            history.Record(1.0, 0f, 0f, 0f);
            history.Record(2.0, 10f, 0f, 0f);
            history.Record(3.0, 20f, 0f, 0f);

            Assert.IsTrue(history.Sample(1.5, out var x, out _, out _));
            Assert.AreEqual(5f, x, 1e-4, "1~2초 사이 — 선형 보간 (리와인드)");
            Assert.IsTrue(history.Sample(0.0, out x, out _, out _));
            Assert.AreEqual(0f, x, 1e-4, "히스토리 이전 — 가장 오래된 값 홀드");
            Assert.IsTrue(history.Sample(99.0, out x, out _, out _));
            Assert.AreEqual(20f, x, 1e-4, "최신 이후 — 최신 값 홀드");
        }

        [Test]
        public void PositionHistory_같은_시각_재기록은_폐기된다()
        {
            var history = new PositionHistory(8);
            history.Record(1.0, 0f, 0f, 0f);
            history.Record(1.0, 5f, 0f, 0f);   // 같은 틱 중복 기록 — 폐기 (단조 시간축 유지, SnapshotBuffer와 동일 계약)

            Assert.IsTrue(history.Sample(1.0, out var x, out _, out _));
            Assert.AreEqual(0f, x, 1e-4);
        }
    }
}
