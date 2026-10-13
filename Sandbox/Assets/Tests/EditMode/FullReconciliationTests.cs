using NUnit.Framework;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// Full CSP&R reconciliation (ADR-0037) — ReconcileAxis behavior (snap vs soft) and rollback trigger conditions.
    /// The e2e rollback + re-simulation path runs via Arena PlayMode tests (FullReconciliation = true).
    /// </summary>
    public sealed class FullReconciliationTests
    {
        [Test]
        public void 리컨사일_축은_스냅_또는_소프트_중_하나를_반환한다()
        {
            // Hard snap — error beyond threshold
            float snapped = NetworkTransform.ReconcileAxis(10f, 20f, 0.5f, 0.15f);
            Assert.AreEqual(20f, snapped, "오차 > 스냅 임계 — 서버 값으로 스냅");

            // Soft correction — error within threshold
            float softened = NetworkTransform.ReconcileAxis(10f, 10.3f, 0.5f, 0.15f);
            Assert.Greater(softened, 10f, "소프트 정정 — 오차 방향으로 이동");
            Assert.Less(softened, 10.3f, "소프트 정정 — 전체 오차의 일부만 흡수");

            // Zero error — no movement
            float noError = NetworkTransform.ReconcileAxis(10f, 10f, 0.5f, 0.15f);
            Assert.AreEqual(10f, noError, 0.0001f, "오차 없음 — 무변화");
        }

        [Test]
        public void 롤백_임계값은_제곱_거리로_판정한다()
        {
            // Threshold 0.5 → thresholdSq = 0.25
            // Error 0.4 → errorSq = 0.16 < 0.25 → soft
            float soft = NetworkTransform.ReconcileAxis(0f, 0.4f, 0.5f, 0.1f);
            Assert.Greater(soft, 0f, "0.4 오차는 스냅 미달 — 소프트 정정");

            // Error 0.6 → errorSq = 0.36 > 0.25 → snap
            float snap = NetworkTransform.ReconcileAxis(0f, 0.6f, 0.5f, 0.1f);
            Assert.AreEqual(0.6f, snap, "0.6 오차는 스냅 초과 — 하드 스냅");
        }
    }
}
