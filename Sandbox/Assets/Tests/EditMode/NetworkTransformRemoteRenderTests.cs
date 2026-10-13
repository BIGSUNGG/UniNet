using System;
using System.Reflection;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Remote rendering paths (ADR-0038 review follow-up) — teleport discontinuity (the buffer must never lerp
    /// through a jump) and the adaptive interpolation delay (dense streams shrink below the maximum).
    /// Drives the remote branch directly: a registered client environment where another connection owns the object,
    /// with a controllable UniNetTime clock so samples land on distinct timestamps.
    /// </summary>
    public sealed class NetworkTransformRemoteRenderTests
    {
        private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Func<double> OriginalClock = UniNetTime.LocalClock;

        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _clock = 1000.0;   // arbitrary non-zero start — far from the monotonicity guard's initial state
            UniNetTime.Reset();
            UniNetTime.LocalClock = () => _clock;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetTime.LocalClock = OriginalClock;
            UniNetTime.Reset();
            UniNetEnvironment.SetClient(null);
            foreach (var nt in UnityEngine.Object.FindObjectsByType<NetworkTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(nt.gameObject);
        }

        private NetworkTransform NewRemote()
        {
            var go = new GameObject("nt-remote");
            var nt = go.AddComponent<NetworkTransform>();
            var client = new NetworkClient();
            client.SetLocalConnId(1);
            client.ApplyOwner(nt.NetId, 2);   // owner = conn 2 → this instance is a remote viewer
            UniNetEnvironment.SetClient(client);
            typeof(NetworkBehaviour).GetMethod("MarkClientRegistered", Flags).Invoke(nt, null);   // IsClient gate
            return nt;
        }

        /// <summary>Applies one received position (as the generated apply path would) and advances the clock.</summary>
        private void Sample(NetworkTransform nt, float x, float y, float z, double gapSeconds)
        {
            Set(nt, "_px", x);
            Set(nt, "_py", y);
            Set(nt, "_pz", z);
            Notify(nt, "OnNetXChanged");
            Notify(nt, "OnNetYChanged");
            Notify(nt, "OnNetZChanged");
            _clock += gapSeconds;
        }

        private static void Notify(NetworkTransform nt, string method)
            => typeof(NetworkTransform).GetMethod(method, Flags).Invoke(nt, new object[] { 0f });

        private static void Set(NetworkTransform nt, string field, float value)
            => typeof(NetworkTransform).GetField(field, Flags).SetValue(nt, value);

        private static void InvokeUpdate(NetworkTransform nt)
            => typeof(NetworkTransform).GetMethod("Update", Flags).Invoke(nt, null);

        [Test]
        public void 텔레포트_불연속은_보간없이_스냅한다()
        {
            var nt = NewRemote();
            nt.SnapThreshold = 0.5f;
            nt.InterpolationDelay = 0.12f;

            // dense movement stream near the origin — the renderer interpolates behind the stream
            for (int i = 0; i < 10; i++)
                Sample(nt, i * 0.1f, 0.5f, 0f, 1.0 / 60.0);
            InvokeUpdate(nt);
            Assert.Less(nt.transform.position.x, 0.9f, "정상 이동 — 렌더는 스트림 뒤를 따라간다 (지연 보간)");

            // teleport far — the jump must render as a snap, not a slide through the gap
            Sample(nt, 20f, 0.5f, 20f, 1.0 / 60.0);
            InvokeUpdate(nt);
            Assert.AreEqual(20f, nt.transform.position.x, 0.01f, "텔레포트 스냅 (x) — 이전 위치로 되돌아가 보간하지 않는다");
            Assert.AreEqual(20f, nt.transform.position.z, 0.01f, "텔레포트 스냅 (z)");
            Assert.AreEqual(0.5f, nt.transform.position.y, 0.01f, "연속 축(y)은 영향 없음");

            // the stream continues normally after the snap
            for (int i = 1; i <= 5; i++)
                Sample(nt, 20f + i * 0.1f, 0.5f, 20f, 1.0 / 60.0);
            InvokeUpdate(nt);
            Assert.Greater(nt.transform.position.x, 20f, "스냅 이후 스트림 재개 — 새 위치 기준으로 보간");
        }

        [Test]
        public void 적응_지연은_밀집_스트림에서_최대치보다_수축한다()
        {
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;

            for (int i = 0; i < 60; i++)   // 60 Hz for a full second — the gap EMA converges to ~16.7 ms
                Sample(nt, i * 0.1f, 0f, 0f, 1.0 / 60.0);

            double effective = (double)typeof(NetworkTransform)
                .GetProperty("EffectiveInterpolationDelay", Flags).GetValue(nt);
            Assert.Less(effective, 0.12, "밀집 스트림 — 최대 지연(0.12s)보다 작게 수축");
            Assert.GreaterOrEqual(effective, 0.03, "하한 — 너무 줄어들면 지터에 굶주린다");
        }

        [Test]
        public void 적응_지연은_희소_스트림에서_최대치를_유지한다()
        {
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;

            for (int i = 0; i < 20; i++)   // 5 Hz — sparse stream needs the full delay cushion
                Sample(nt, i * 0.1f, 0f, 0f, 0.2);

            double effective = (double)typeof(NetworkTransform)
                .GetProperty("EffectiveInterpolationDelay", Flags).GetValue(nt);
            Assert.AreEqual(0.12, effective, 1e-6, "희소 스트림 — 최대 지연 유지 (굶주림 방지)");
        }

        [Test]
        public void 시뮬레이션_미주입_리모트도_인터폴레이션으로_렌더한다()
        {
            var nt = NewRemote();
            Assert.IsNull(nt.Simulation, "복제전용 리모트");

            // a moving stream then a jump — interpolation (not the raw authoritative value) must drive the transform
            for (int i = 0; i < 10; i++)
                Sample(nt, i * 0.1f, 0f, 0f, 1.0 / 60.0);
            InvokeUpdate(nt);
            Assert.Less(nt.transform.position.x, 0.9f, "복제전용 리모트 — 인터폴레이션 경로 (원시 좌표 미렌더)");
        }

        [Test]
        public void 병합된_고속_샘플은_텔레포트로_오판하지_않는다()
        {
            var nt = NewRemote();
            nt.SnapThreshold = 0.5f;
            nt.InterpolationDelay = 0.12f;

            // dash-speed stream — per-sample displacement 0.33u, the motion-scaled tolerance converges to ~1.0u
            for (int i = 0; i < 30; i++)
                Sample(nt, i * 0.33f, 0f, 0f, 1.0 / 60.0);

            // two sim ticks merged into one delta (server frame carry) — 0.66u, must still interpolate (not snap)
            float latestX = 29 * 0.33f;
            Sample(nt, latestX + 0.66f, 0f, 0f, 1.0 / 30.0);   // double-gap arrival carries the merged displacement
            InvokeUpdate(nt);
            float afterMerged = nt.transform.position.x;
            Assert.Less(afterMerged, latestX + 0.66f - 0.1f, "병합 샘플 — 보간 유지 (스냅·홀드로 끊기지 않는다)");

            // a real teleport still snaps
            float tp = latestX + 20f;
            Sample(nt, tp, 0f, 0f, 1.0 / 60.0);
            InvokeUpdate(nt);
            Assert.AreEqual(tp, nt.transform.position.x, 0.01f, "실제 텔레포트는 스냅");
        }

        [Test]
        public void 수직_전용_이동도_적응_지연이_수축한다()
        {
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;

            // in-place jump arc — X never changes, only Y notifies fire (a full-field delta is NOT assumed)
            for (int i = 0; i < 60; i++)
            {
                Set(nt, "_py", 0.5f + Mathf.Sin(i * 0.3f) * 0.5f);
                Notify(nt, "OnNetYChanged");
                Notify(nt, "OnNetZChanged");
                _clock += 1.0 / 60.0;
            }

            double effective = (double)typeof(NetworkTransform)
                .GetProperty("EffectiveInterpolationDelay", Flags).GetValue(nt);
            Assert.Less(effective, 0.12, "수직 전용 스트림 — Y축 노티파이가 박자를 운반해 지연 수축");
        }
    }
}
