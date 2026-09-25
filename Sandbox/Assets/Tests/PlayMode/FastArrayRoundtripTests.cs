using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// Host round-trip verification for FastArray element deltas (ADR-0020) — Scores mutations cross a real loopback
    /// RUDP connection; the host keeps the authoritative instance, so correctness is observed through RepNotify
    /// (call count + previous-state shallow copy) and the unchanged-content suppression path.
    /// On success it logs a [UNINET-VERIFY] marker (grep target for batch runs).
    /// </summary>
    public sealed class FastArrayRoundtripTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 80;   // random port per run (offset 80 — no overlap with the other PlayMode classes)

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator 호스트_왕복_컬렉션_요소_델타()
        {
            var go = new GameObject("fastarray-host");
            var tank = go.AddComponent<FastArrayTank>();
            try
            {
                var hostTask = UniNetManager.HostAsync(Port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);
                yield return WaitUntil(() => tank.IsOwner, 5);

                // baseline — scene registration delivers the full state once (tag-1 path), firing one RepNotify
                yield return WaitUntil(() => tank.ScoreNotifyCalls >= 1, 5);
                int baseline = tank.ScoreNotifyCalls;

                // 1) append + middle change — deltas cross loopback, RepNotify fires once per batch
                tank.Scores.AddRange(new[] { 1, 2, 3, 4 });
                tank.Scores.Remove(2);
                tank.Scores.Insert(1, 9);   // [1,9,3,4]
                yield return WaitUntil(() => tank.ScoreNotifyCalls == baseline + 1, 5);
                CollectionAssert.AreEqual(new[] { 1, 9, 3, 4 }, tank.Scores, "호스트 원본 — 서버 권위 값 보존");
                CollectionAssert.AreEqual(new int[0], tank.LastPrevScores ?? new List<int>(), "이전값 — 등록 기준선은 빈 상태");

                // 2) unchanged content — no new delta/notify over the wire
                tank.Scores = new List<int> { 1, 9, 3, 4 };
                yield return new WaitForSecondsRealtime(0.6f);   // observation window — at least one full send period
                Assert.AreEqual(baseline + 1, tank.ScoreNotifyCalls, "동일 내용 재할당 → 재전송 없음");

                // 3) real change — prev is the last wire-visible state
                tank.Scores.Add(5);
                yield return WaitUntil(() => tank.ScoreNotifyCalls == baseline + 2, 5);
                CollectionAssert.AreEqual(new[] { 1, 9, 3, 4 }, tank.LastPrevScores, "이전값은 마지막 동기화 상태의 얕은 복사");

                UnityEngine.Debug.Log("[UNINET-VERIFY] FastArrayRoundtrip PASS — ElementDelta/RepNotifyPrev/UnchangedSuppression");
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
