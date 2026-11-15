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

        /// <summary>버퍼의 시간축에서 이웃 표본 사이 최소 간격 — 고른 박자의 증거(관측용, 디버그 전용 경로).</summary>
        private static double MinSegmentWidth(SnapshotBuffer<float> buffer)
        {
            var type = typeof(SnapshotBuffer<float>);
            var ring = (Array)type.GetField("_ring", Flags).GetValue(buffer);
            int head = (int)type.GetField("_head", Flags).GetValue(buffer);
            int count = (int)type.GetField("_count", Flags).GetValue(buffer);
            if (count < 2) return double.MaxValue;

            double min = double.MaxValue;
            double prev = 0;
            for (int i = 0; i < count; i++)
            {
                int index = ((head - count + i) % ring.Length + ring.Length) % ring.Length;
                object entry = ring.GetValue(index);
                double t = (double)entry.GetType().GetField("Item1").GetValue(entry);
                if (i > 0) min = Math.Min(min, t - prev);
                prev = t;
            }
            return min;
        }

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

        /// <summary>
        /// 두 델타가 한 프레임에 몰려 도착해도 표본 간격은 «도착시각» 이 아니라 추정 박자를 따릅니다.
        /// 도착시각으로 적으면 그 구간이 ~0ms 로 압축되어(또는 같은 시각이라 버려져) 렌더가 한 프레임에
        /// 한 표본 분량을 건너뜁니다 — 원격 대시가 «끊겨 보이는» 그 모양입니다.
        /// </summary>
        [Test]
        public void 한_프레임에_몰린_표본도_한_프레임에_건너뛰지_않는다()
        {
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;
            nt.SnapThreshold = 0.5f;

            float perSample = 1f / 30f;         // 30Hz 스냅샷, 1 u/s 이동
            float x = 0f;

            for (int i = 0; i < 12; i++)        // 예열 — 박자 추정이 첫 실제 간격으로 시드된다 (버퍼 용량 32 이하)
            {
                x += perSample;
                Sample(nt, x, 0f, 0f, 1.0 / 30.0);
                InvokeUpdate(nt);
            }

            SnapshotBuffer<float> bufX = (SnapshotBuffer<float>)typeof(NetworkTransform)
                .GetField("_bufX", Flags).GetValue(nt);
            int before = bufX.SampleCount;

            // 한 프레임에 델타 두 개 (프레임 히치 잔량) — 두 번째는 1ms 뒤에 도착한 것으로 준다.
            // 도착시각으로 적으면 두 번째는 «같은 시각» 이라 버퍼의 단조 가드에 거부되어 위치 값이 통째로 사라진다.
            x += perSample;
            Sample(nt, x, 0f, 0f, 0.001);
            x += perSample;
            Sample(nt, x, 0f, 0f, 0.0);

            Assert.AreEqual(before + 2, bufX.SampleCount,
                "한 프레임에 몰린 두 델타 모두 버퍼에 남는다 — 버려진 표본은 그 거리만큼 한 프레임에 건너뛴다");

            // 그리고 표본 간격이 고르다 — 도착시각으로 적으면 이 구간만 1ms 짜리 조각이 되어,
            // 그리는 시각이 그 조각을 한 프레임에 건너뛰며 몸이 순간이동한다.
            double minWidth = MinSegmentWidth(bufX);
            Assert.Greater(minWidth, 1.0 / 30.0 * 0.4,
                $"표본 간 최소 간격({minWidth * 1000.0:0.0}ms)이 박자의 40% 이상 — 몰아둔 델타도 제 간격을 받는다");
        }

        /// <summary>
        /// 거리만으로 순간이동을 판정하면 «이미 빠른 몸 + 식은 이동 EMA + 몰아온 두 틱» 이 순간이동으로 오인되어
        /// 버퍼가 지워지고 몸이 한 프레임에 튑니다(실측 1.24 유닛). 속도가 시간에 맞으면 이동으로 보아야 합니다.
        /// </summary>
        [Test]
        public void 식은_이동EMA에서도_빠른_정상_이동은_순간이동으로_오인하지_않는다()
        {
            var nt = NewRemote();
            nt.SnapThreshold = 0.5f;
            nt.InterpolationDelay = 0.12f;

            // 아직 EMA 가 식어 있는 상태 — 첫 두 표본만 작다
            Sample(nt, 0f, 0f, 0f, 1.0 / 30.0);
            Sample(nt, 0.1f, 0f, 0f, 1.0 / 30.0);

            // 두 서버 틱이 하나의 델타로 합쳐졌다: 0.9 유닛 / 33ms = 27 u/s — 빠르지만 «이동» 이다
            Sample(nt, 1.0f, 0f, 0f, 1.0 / 30.0);
            InvokeUpdate(nt);

            SnapshotBuffer<float> bufX = (SnapshotBuffer<float>)typeof(NetworkTransform)
                .GetField("_bufX", Flags).GetValue(nt);
            Assert.AreEqual(3, bufX.SampleCount, "빠른 정상 이동 — 버퍼를 지우지 않는다 (스냅 금지)");

            // 진짜 순간이동(수백 u/s)은 여전히 스냅한다
            Sample(nt, 30f, 0f, 0f, 1.0 / 30.0);
            InvokeUpdate(nt);
            Assert.AreEqual(30f, nt.transform.position.x, 0.5f, "실제 순간이동은 최신 값으로 스냅");
        }

        /// <summary>보간 지연은 표본 하나 사이에 급변하지 않는다 — 지연이 튀면 그리는 시각이 튀고 몸이 순간이동한다.
        /// 게임이 대시 진입에 지연을 바꾸던 동작(0.12→0.18)이 정확히 이 급변이었다.</summary>
        [Test]
        public void 게임이_지연을_바꿔도_한_표본_사이에_급변하지_않는다()
        {
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;

            for (int i = 0; i < 20; i++)   // 5 Hz — 지연은 설정값(상한)에 붙어 있다: 상한을 바꾸면 지연이 따라 움직인다
                Sample(nt, i * 0.1f, 0f, 0f, 0.2);

            double before = (double)typeof(NetworkTransform)
                .GetProperty("EffectiveInterpolationDelay", Flags).GetValue(nt);

            nt.InterpolationDelay = 0.06f;   // 대시 진입에 게임이 지연을 좁히던 그 순간
            Sample(nt, 20 * 0.1f, 0f, 0f, 0.2);
            double after = (double)typeof(NetworkTransform)
                .GetProperty("EffectiveInterpolationDelay", Flags).GetValue(nt);

            Assert.Less(Math.Abs(after - before), 0.02,
                $"한 표본 사이 지연 변화({Math.Abs(after - before):0.0000}s)가 작다 — 그리는 시각이 튀지 않는다");
            Assert.Greater(after, 0.06, "설정값으로 수렴 중이다 (한 번에 뛰어가지 않는다)");
        }
    }
}
