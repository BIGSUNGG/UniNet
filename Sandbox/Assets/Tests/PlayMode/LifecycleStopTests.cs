using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// 수명주기 종료 API 검증 — 리슨/접속을 정지(Stop)한 뒤 동일 포트에서 재시작이 성공하는지 단언한다.
    /// 배경: ServerAsync가 RpcListenHandle을 저장만 하고 Dispose하지 않아 Play 모드 종료·재진입 시
    /// "RUDP 리스너 바인딩 실패 (0.0.0.0:포트)"가 발생했던 결함의 회귀 방지.
    /// 각 테스트는 종료 API로 자기 리스너를 정리한다 — 테스트 간 잔존 리스너 포트 충돌 소멸.
    /// </summary>
    public sealed class LifecycleStopTests
    {
        [SetUp]
        public void AllowStopNoise()
        {
            // 정지 과정에서 DRPC 세션 정리로 완료되지 못한 fire-and-forget 송신이
            // 스레드풀 continuation에서 "세션이 끊겨 송신할 수 없습니다" 예외 로그를 남긴다 —
            // 정상 종료 경로의 예상 노이즈이며 TearDown 종료 후에도 늦게 나올 수 있어
            // 클래스 전체에서 로그 판정을 무시한다 (기능 단언은 Assert로 수행).
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void StopListeners()
        {
            // TearDown은 별도 로그 스코프(ignore 리셋 상태)로 실행되므로 정지 노이즈 무시를 여기서 재설정한다
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // 중간 실패 시 잔존 리스너 정리 (멱등 — 정상 종료 경로는 각 테스트 내부에서 수행)
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;   // 클래스 종료 후 복구 — 이후 테스트의 로그 판정 유지
        }

        [UnityTest]
        public IEnumerator 서버_정지후_동일_포트_재리슨_성공()
        {
            int port = 7843;

            var t1 = UniNetManager.ServerAsync(port);
            while (!t1.IsCompleted) yield return null;
            Assert.IsFalse(t1.IsFaulted, "첫 리슨 실패: " + t1.Exception);
            Assert.IsNotNull(UniNetEnvironment.Server);

            yield return StopTask(UniNetManager.ServerStopAsync());
            Assert.IsNull(UniNetEnvironment.Server, "정지 후 서버 참조 해제");

            // 핵심 회귀 — 정지 후 동일 포트 재리슨이 바인딩 실패 없이 성공해야 한다
            var t2 = UniNetManager.ServerAsync(port);
            while (!t2.IsCompleted) yield return null;
            Assert.IsFalse(t2.IsFaulted, "재리슨 실패(포트 잔존): " + t2.Exception);

            yield return StopTask(UniNetManager.ServerStopAsync());
        }

        [UnityTest]
        public IEnumerator 클라_정지후_서버연결해제와_재접속_성공()
        {
            int port = 7845;
            var serverTask = UniNetManager.ServerAsync(port);
            while (!serverTask.IsCompleted) yield return null;
            Assert.IsFalse(serverTask.IsFaulted, serverTask.Exception?.ToString());

            var c1 = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!c1.IsCompleted) yield return null;
            Assert.IsFalse(c1.IsFaulted, "첫 접속 실패: " + c1.Exception);

            yield return WaitConns(1);
            Assert.AreEqual(1, LiveConns(), "첫 접속");

            UniNetManager.ClientStop();
            Assert.IsNull(UniNetEnvironment.Client, "정지 후 클라 참조 해제");
            yield return WaitConns(0);
            Assert.AreEqual(0, LiveConns(), "정지 후 서버 연결 해제");

            var c2 = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!c2.IsCompleted) yield return null;
            Assert.IsFalse(c2.IsFaulted, "재접속 실패: " + c2.Exception);
            yield return WaitConns(1);
            Assert.AreEqual(1, LiveConns(), "재접속");

            yield return StopTask(UniNetManager.ServerStopAsync());
        }

        [UnityTest]
        public IEnumerator 호스트_정지후_재시작_성공()
        {
            int port = 7847;
            var h1 = UniNetManager.HostAsync(port);
            while (!h1.IsCompleted) yield return null;
            Assert.IsFalse(h1.IsFaulted, h1.Exception?.ToString());

            yield return StopTask(UniNetManager.HostStopAsync());
            Assert.IsNull(UniNetEnvironment.Server, "정지 후 서버 참조 해제");

            var h2 = UniNetManager.HostAsync(port);
            while (!h2.IsCompleted) yield return null;
            Assert.IsFalse(h2.IsFaulted, "호스트 재시작 실패: " + h2.Exception);

            yield return StopTask(UniNetManager.HostStopAsync());
        }

        private static IEnumerator StopTask(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted)
                Assert.Fail("Stop 실패: " + task.Exception);
        }

        private static IEnumerator WaitConns(int expected, float timeout = 5f)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (LiveConns() != expected)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"연결 수 대기 시간 초과: 기대 {expected}, 실제 {LiveConns()}");
                yield return null;
            }
        }

        private static int LiveConns()
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return 0;
            int n = 0;
            foreach (var conn in server.SnapshotConnections())
                if (!conn.Disconnected)
                    n++;
            return n;
        }
    }
}
