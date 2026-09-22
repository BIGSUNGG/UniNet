using NUnit.Framework;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>P4 NetworkTransform component — verifies the ReconcileAxis prediction-correction math (see ADR-0013).</summary>
    public sealed class NetworkTransformComponentTests
    {
        [Test]
        public void ReconcileAxis_오차가_작으면_소프트_흡수한다()
        {
            // predicted 9.0 vs server 9.3 — error 0.3 < snap threshold 0.5, so absorb error × softRate
            var reconciled = NetworkTransform.ReconcileAxis(9f, 9.3f, 0.5f, 0.15f);
            Assert.AreEqual(9.045f, reconciled, 1e-4, "잔여 오차 0.3 × 0.15 = 0.045 흡수");
        }

        [Test]
        public void ReconcileAxis_오차가_크면_서버_값으로_스냅한다()
        {
            var reconciled = NetworkTransform.ReconcileAxis(9f, 12f, 0.5f, 0.15f);
            Assert.AreEqual(12f, reconciled, 1e-4, "오차 3 > 스냅 임계 — 하드 조정");
        }

        [Test]
        public void ReconcileAxis_오차가_없으면_변경하지_않는다()
        {
            Assert.AreEqual(7f, NetworkTransform.ReconcileAxis(7f, 7f, 0.5f, 0.15f), 1e-6);
        }
    }
}
