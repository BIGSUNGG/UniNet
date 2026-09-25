using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// Host round-trip verification for custom field serializers (ADR-0020) — quantized Vector3 replication over
    /// a real loopback RUDP connection, including the quantization-suppression path (same bucket → no resend).
    /// On success it logs a [UNINET-VERIFY] marker (grep target for batch runs).
    /// </summary>
    public sealed class NetSerializeRoundtripTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 72;   // random port per run (offset 72 — no overlap with the other PlayMode classes)

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator 호스트_왕복_커스텀_직렬화_양자화_리플리케이션()
        {
            var go = new GameObject("quantized-host");
            var tank = go.AddComponent<QuantizedTank>();
            try
            {
                var hostTask = UniNetManager.HostAsync(Port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);
                yield return WaitUntil(() => tank.IsOwner, 5);

                // baseline — scene registration delivers the full state once (WriteFull path), which fires one RepNotify
                yield return WaitUntil(() => tank.NotifyCalls >= 1, 5);
                int baseline = tank.NotifyCalls;

                // 1) Quantized delta over the real wire — server changes the position (exact float values), the delta
                //    crosses loopback, and RepNotify fires with the pre-quantization previous value
                tank.Position = new Vector3(12f, -5.5f, 89f);
                yield return WaitUntil(() => tank.NotifyCalls == baseline + 1, 5);
                Assert.AreEqual(new Vector3(12f, -5.5f, 89f), tank.Position, "호스트 원본 — 서버 권위 값 보존");
                Assert.AreEqual(Vector3.zero, tank.LastPrev, "RepNotify 이전값 — 등록 기준선은 초기 상태(원본 도메인)");

                // 2) Quantization suppression over the wire — a same-bucket change produces no new delta/notify
                //    (12.009→1200 / -5.509→-550 / 89.009→8900 — all truncation-identical to step 1)
                tank.Position = new Vector3(12.009f, -5.509f, 89.009f);
                yield return new WaitForSecondsRealtime(0.6f);   // observation window — at least one full send period
                Assert.AreEqual(baseline + 1, tank.NotifyCalls, "양자화 동일 버킷 — 재전송 없음 (선택 Equals 계약)");

                // 3) Bucket-crossing change — a real change produces a new delta; prev is the last wire-visible original
                tank.Position = new Vector3(12.02f, -5.5f, 89f);
                yield return WaitUntil(() => tank.NotifyCalls == baseline + 2, 5);
                Assert.AreEqual(new Vector3(12f, -5.5f, 89f), tank.LastPrev, "이전값은 마지막 와이어 가시 원본");

                UnityEngine.Debug.Log("[UNINET-VERIFY] NetSerializeRoundtrip PASS — QuantizedDelta/RepNotifyPrev/BucketSuppression");
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, double timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.Elapsed.TotalSeconds < timeoutSeconds)
                yield return null;
        }
    }
}
