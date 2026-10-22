using System;
using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>P4 core primitives — UniNetTime (time sync), SnapshotBuffer (interpolation), and PositionHistory (rewind).</summary>
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

            UniNetTime.ApplyServerTime(105.0);   // first sync — adopt the +5 offset immediately
            Assert.IsTrue(UniNetTime.IsSynced);
            Assert.AreEqual(105.0, UniNetTime.Now, 1e-9);

            UniNetTime.ApplyServerTime(107.0);   // new offset +7 — EMA α=0.25 → 5 + (7-5)×0.25 = 5.5
            Assert.AreEqual(100.0 + 5.5, UniNetTime.Now, 1e-9);
        }

        // ── TIM-001 — 시간 동기화 계약 테스트 (계약 문서화만, 로직 무변경) ──

        [Test]
        public void UniNetTime_오프셋_감소는_단조를_유지하고_정지는_EMA_스텝으로_유계다_T1()
        {
            // 계약 ②+④ — 감소 조정 −0.4s → 단일 EMA 스텝 −0.1s: Now는 비감소, 0.1s 정지 후 진행률 1 복원.
            double local = 100.0;
            UniNetTime.LocalClock = () => local;
            UniNetTime.ApplyServerTime(105.0);            // 오프셋 +5 즉시 채택
            double prevNow = UniNetTime.Now;              // 105.0 — 클램프 기준

            UniNetTime.ApplyServerTime(104.6);            // 목표 오프셋 +4.6 — 스텝: 5 + (4.6−5)×0.25 = 4.9
            double after = UniNetTime.Now;
            Assert.GreaterOrEqual(after, prevNow, "감소 조정 직후에도 Now 비감소 (단조)");
            Assert.AreEqual(105.0, after, 1e-9, "스텝 −0.1s는 클램프로 흡수 — 정지");

            local += 0.05;                                 // 정지 구간 — 0.1s 미만 경과
            Assert.AreEqual(105.0, UniNetTime.Now, 1e-9, "0.25×|Δoffset| = 0.1s 도달 전 — 여전히 정지");
            local += 0.04;                                 // 누적 0.09s < 0.1s
            Assert.AreEqual(105.0, UniNetTime.Now, 1e-9, "정지 한계 직전 — 여전히 정지");

            local += 0.02;                                 // 누적 0.11s > 0.1s — 클램프 해제
            double released = UniNetTime.Now;             // 100.11 + 4.9 = 105.01
            Assert.AreEqual(105.01, released, 1e-9, "정지 해제 — 정지는 한계 0.1s를 초과하지 않는다");

            double before = UniNetTime.Now;
            local += 0.2;
            Assert.AreEqual(before + 0.2, UniNetTime.Now, 1e-9, "정지 해제 후 — 진행률 1 복원");
        }

        [Test]
        public void UniNetTime_반복_감소의_누적_손실은_EMA_스텝의_합으로_유계다_T2()
        {
            // 계약 ② — 매 샘플 −0.2s (지속 과대추정 패턴): 스텝 −0.05s씩, 10회 누적 손실 ≤ Σ(0.05s) = 0.5s.
            // 영구 정지 불가 (조각상수 오프셋) — 마지막에 진행률 1 복원.
            double local = 100.0;
            UniNetTime.LocalClock = () => local;
            UniNetTime.ApplyServerTime(110.0);            // 오프셋 +10
            double smoothed = 10.0;                        // 테스트 추적 값 — 기대 EMA 오프셋 (계약 수학과 동일 재현)
            double prevNow = UniNetTime.Now;              // 110.0

            for (int i = 0; i < 10; i++)
            {
                double serverTime = local + smoothed - 0.2;   // 현재 추정치보다 0.2s 낮은 서버 시각
                UniNetTime.ApplyServerTime(serverTime);
                smoothed += (serverTime - local - smoothed) * 0.25;   // EMA α=0.25 — 스텝 = −0.05
                double now = UniNetTime.Now;
                Assert.GreaterOrEqual(now, prevNow, $"{i + 1}회차 반복 감소 — Now 비감소 (단조)");
                prevNow = now;
            }

            double ideal = local + smoothed;               // 무클램프 이상 시각 = 100 + 9.5 = 109.5
            double stall = prevNow - ideal;                // 실제 정지량 (누적 손실)
            Assert.AreEqual(0.5, stall, 1e-9, "누적 손실 = Σ(0.25×0.2) = 0.5s — 유계 확인");

            local += stall;                                 // 정지 해제 지점까지 경과
            double before = UniNetTime.Now;
            local += 0.3;
            Assert.AreEqual(before + 0.3, UniNetTime.Now, 1e-9, "정지 해제 후 최종 진행률 1");
        }

        [Test]
        public void UniNetTime_EMA는_고정_목표에_0_75_n으로_수렴한다_T3()
        {
            // 계약 ② 보조 — 초기 오차 2 (+5 → +7): n스텝 후 오차 ≤ 0.75ⁿ × 2 (결정론적 수렴 상한).
            double local = 100.0;
            UniNetTime.LocalClock = () => local;
            UniNetTime.ApplyServerTime(105.0);            // +5 즉시 채택 — 초기 오차 |5−7| = 2

            for (int n = 1; n <= 60; n++)
            {
                UniNetTime.ApplyServerTime(107.0);         // 고정 목표 +7
                double error = Math.Abs(UniNetTime.Now - 107.0);
                Assert.LessOrEqual(error, 2.0 * Math.Pow(0.75, n) + 1e-12, $"EMA {n}스텝 — 오차 ≤ 0.75ⁿ×초기오차");
            }
            Assert.AreEqual(107.0, UniNetTime.Now, 1e-6, "충분한 스텝 후 목표 수렴");
        }

        [Test]
        public void UniNetTime_서버는_ApplyServerTime을_무시하고_수신_카운트는_증가한다_T4()
        {
            // 계약 (ApplyServerTime 서버 분기) — 서버/호스트는 오프셋 0 유지 (로컬 클럭이 권위), 와이어 관측자는 증가.
            var prevServer = UniNetEnvironment.Server;
            try
            {
                UniNetEnvironment.SetServer(new NetworkServer());
                double local = 100.0;
                UniNetTime.LocalClock = () => local;
                Assert.IsTrue(UniNetTime.IsSynced, "서버/호스트 — 항상 동기화 상태 (로컬 클럭이 권위)");

                UniNetTime.ApplyServerTime(500.0);         // 터무니없는 서버 시각 — 무시되어야 함
                Assert.AreEqual(1, UniNetTime.ReceiveCount, "서버가 무시해도 수신 카운트 증가 (와이어 관측자)");
                Assert.AreEqual(100.0, UniNetTime.Now, 1e-9, "오프셋 0 유지 — 기록 클럭과 조회 클럭 일치");

                UniNetTime.ApplyServerTime(501.0);
                Assert.AreEqual(2, UniNetTime.ReceiveCount, "반복 수신 — 카운트 계속 증가");
                Assert.AreEqual(100.0, UniNetTime.Now, 1e-9, "반복 적용에도 오프셋 0 유지");
            }
            finally
            {
                UniNetEnvironment.SetServer(prevServer);   // 복원 — 다른 픽스처 오염 방지
            }
        }

        [Test]
        public void UniNetTime_히트_시각_전달은_편도지연_바이어스_이내로_리와인드된다_T5측정()
        {
            // 계약 ③ — 클라 Now = 서버 + d (편도지연 상시 바이어스) 시나리오에서 히트 스탬프의 리와인드 동작 측정.
            // 측정 보고용: 수치 로그를 리더가 Phase B(편도 보정) 게이트 판단에 사용 — 실패 조건은 "오차 > d"만.
            const double d = 0.05;                         // s2c 편도지연 50ms
            const double speed = 10.0;                     // 대상 이동 속도 10 m/s
            var history = new PositionHistory(128);        // 최소 간격 0 — 부동소수점 반올림과 무관하게 전 기록 수용 (결정론)

            for (int k = 0; k <= 120; k++)                 // 서버 틱 60Hz 간격 — 서버 시간축에 직진 기록
            {
                double t = 100.0 + k * (1.0 / 60.0);
                history.Record(t, (float)(speed * t), 0f, 0f);
            }
            double serverT = 100.0 + 120 * (1.0 / 60.0);   // 102.0 — 최신 기록 시각
            Assert.IsTrue(history.TryGetLatestTime(out var latestT));
            Assert.AreEqual(serverT, latestT, 1e-9);

            // 클라: TimeSync 오프셋이 편도지연 d를 포함해 채택 → Now = 서버 + d
            UniNetTime.LocalClock = () => serverT;
            UniNetTime.Reset();
            UniNetTime.ApplyServerTime(serverT + d);       // 첫 샘플 즉시 채택 — 오프셋 = +d
            double hitStamp = UniNetTime.Now;              // 히트 시각 = 클라 Now = 서버 + d (미래 스탬프)
            Assert.AreEqual(serverT + d, hitStamp, 1e-9, "클라 Now = 서버 + d 바이어스 시나리오");

            // 서버 리와인드 쿼리 — 히트 스탬프로 위치 이력 조회 (미래 시각 → 최신 샘플 홀드 클램프)
            Assert.IsTrue(history.Sample(hitStamp, out var hitX, out _, out _), "히트 스탬프 리와인드 조회 성공");
            double timeError = hitStamp - latestT;                        // 시간 도메인 오차
            double posError = Math.Abs(hitX - speed * (serverT + d));     // 진 히트 순간 위치 대비 오차

            Assert.LessOrEqual(timeError, d + 1e-9, "리와인드 조회 시간 오차 ≤ 편도지연 d");
            UnityEngine.Debug.Log($"[TIM-001] bias={d * 1000.0:0.0}ms — 클라 Now=서버+{d * 1000.0:0.0}ms, 리와인드 시간 오차={timeError * 1000.0:0.0}ms (≤ 편도지연), 위치 오차={posError:0.000}m (10m/s 홀드 클램프) — Phase B 게이트 자료");
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
            buffer.Add(0.5, 99f);   // late-arriving past snapshot — discarded

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
            history.Record(1.0, 5f, 0f, 0f);   // duplicate record for the same time — discarded (keeps the time axis monotonic, same contract as SnapshotBuffer)

            Assert.IsTrue(history.Sample(1.0, out var x, out _, out _));
            Assert.AreEqual(0f, x, 1e-4);
        }
    }
}
