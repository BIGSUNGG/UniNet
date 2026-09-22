using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>P4 wire/driver-level tests — verifies TimeSync synchronization and rewind position history (PlayMode real loopback).</summary>
    public sealed class P4HookPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 16;   // random port per run — avoids listeners lingering in the editor process after play mode ends

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator TimeSync_와이어로_서버_시각_수신이_제공된다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            // The driver broadcasts TimeSync every second — wire delivery is observed via the receive counter.
            // Note: in host mode the local clock is authoritative so offset application is skipped (see ADR-0012), but reception itself is still observed.
            float deadline = Time.realtimeSinceStartup + 3f;
            while (UniNetTime.ReceiveCount == 0 && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.Greater(UniNetTime.ReceiveCount, 0, "TimeSync 수신 — 와이어(methodId 5) 도달");
            Assert.IsTrue(UniNetTime.IsSynced, "서버 도메인 시각 제공");
            Assert.Greater(UniNetTime.Now, 0.0, "동기화된 서버 시각은 양수다");
        }

        [UnityTest]
        public IEnumerator 리와인드_히스토리가_과거_위치를_반환한다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var template = new GameObject("RewindTarget");
            template.AddComponent<SpawnablePlayer>();
            var go = UniNetManager.NetworkInstantiate(template);
            var player = go.GetComponent<SpawnablePlayer>();
            UnityEngine.Object.Destroy(template);
            player.NetworkRewindHistory = true;   // register as a P4 rewind target — a server-behavior flag (not serialized), so set it on the clone after spawn

            // P4 rewind — record samples synchronously so verification is independent of driver tick timing
            // (recording uses the same RecordRewindSample the driver's RecordRewindHistory calls)
            player.transform.position = new Vector3(0f, 0f, 0f);
            double before = UniNetTime.Now;
            player.RecordRewindSample(before);

            player.transform.position = new Vector3(10f, 0f, 0f);
            double after = UniNetTime.Now + 1.0;   // one second later, to distinguish from the earlier sample time
            player.RecordRewindSample(after);
            yield return null;

            Assert.GreaterOrEqual(player.RewindSampleCount, 2, "히스토리가 실제로 기록됐다 (관찰자)");

            Assert.IsTrue(player.GetHistoryPosition(before, out float startX, out _, out _));
            Assert.AreEqual(0f, startX, 0.01f, "과거 시점(before) 질의 — 이동 전 위치 (리와인드)");
            Assert.IsTrue(player.GetHistoryPosition(after, out float endX, out _, out _));
            Assert.AreEqual(10f, endX, 0.01f, "이동 후 시점 질의 — 이동 후 위치");

            // Hold contract — queries past the newest sample (after) keep returning the last value
            Assert.IsTrue(player.GetHistoryPosition(after + 1.0, out float holdX, out _, out _));
            Assert.AreEqual(10f, holdX, 0.01f, "최신 이후 시점 질의 — 끝 값 홀드");
        }
    }
}
