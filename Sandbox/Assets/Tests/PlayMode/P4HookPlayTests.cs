using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>P4 와이어·드라이버 레벨 테스트 — TimeSync 동기화와 리와인드 위치 히스토리 검증 (PlayMode 실제 루프백).</summary>
    public sealed class P4HookPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 16;   // 실행별 랜덤 포트 — 플레이 모드 종료 후 리스너 소켓이 에디터 프로세스에 잔존하는 환경 문제 회피

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator TimeSync_와이어로_서버_시각_수신이_제공된다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            // 드라이버가 1초 주기로 TimeSync를 브로드캐스트한다 — 와이어 도달은 수신 카운터로 관찰한다.
            // 주의: 호스트 모드는 자기 시계가 권위라 오프셋 적용은 건너뛰지만(ADR-0012), 수신 자체는 관찰된다.
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

            var go = new GameObject("RewindTarget");
            var player = go.AddComponent<SpawnablePlayer>();
            player.NetworkRewindHistory = true;   // P4-② 리와인드 대상 등록
            UniNetManager.Spawn(go);

            // P4-② 리와인드 — 샘플을 동기 기록해 드라이버 틱 타이밍과 무관하게 검증한다
            // (기록 경로 자체는 드라이버 RecordRewindHistory가 호출하는 것과 동일한 RecordRewindSample)
            player.transform.position = new Vector3(0f, 0f, 0f);
            double before = UniNetTime.Now;
            player.RecordRewindSample(before);

            player.transform.position = new Vector3(10f, 0f, 0f);
            double after = UniNetTime.Now + 1.0;   // 스냅 시각과 구분되도록 1초 뒤로
            player.RecordRewindSample(after);
            yield return null;

            Assert.GreaterOrEqual(player.RewindSampleCount, 2, "히스토리가 실제로 기록됐다 (관찰자)");

            Assert.IsTrue(player.GetHistoryPosition(before, out float startX, out _, out _));
            Assert.AreEqual(0f, startX, 0.01f, "과거 시점(before) 질의 — 이동 전 위치 (리와인드)");
            Assert.IsTrue(player.GetHistoryPosition(after, out float endX, out _, out _));
            Assert.AreEqual(10f, endX, 0.01f, "이동 후 시점 질의 — 이동 후 위치");

            // 홀드 계약 — 최신 샘플(after) 이후 시점 질의도 마지막 값을 유지한다
            Assert.IsTrue(player.GetHistoryPosition(after + 1.0, out float holdX, out _, out _));
            Assert.AreEqual(10f, holdX, 0.01f, "최신 이후 시점 질의 — 끝 값 홀드");
        }
    }
}
