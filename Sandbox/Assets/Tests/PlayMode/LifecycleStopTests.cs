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
    /// Lifecycle stop API verification — asserts that stopping (Stop) a listener/connection succeeds and that
    /// restarting on the same port then works. Background: regression guard for a defect where ServerAsync only
    /// stored the RpcListenHandle without disposing it, causing "RUDP listener bind failure (0.0.0.0:port)"
    /// when leaving and re-entering Play mode. Each test cleans up its own listener via the stop API —
    /// eliminating leftover-listener port collisions between tests.
    /// </summary>
    public sealed class LifecycleStopTests
    {
        private static readonly int PortBase = 30000 + (System.Environment.TickCount % 2000) * 12 + 40;   // random port per run (this class uses 3 ports)

        [SetUp]
        public void AllowStopNoise()
        {
            // During shutdown, fire-and-forget sends that could not complete while DRPC tore down sessions
            // log a "session disconnected, cannot send" exception from thread-pool continuations.
            // This is expected noise of the normal stop path and may surface even after TearDown ends,
            // so log verdicts are ignored for the whole class (functional assertions use Assert).
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void StopListeners()
        {
            // TearDown runs in its own log scope (ignore resets), so re-apply the stop-noise suppression here
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // clean up listeners after mid-test failures (idempotent — the normal stop path is exercised inside each test)
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;   // restore after the class ends — keeps log verdicts meaningful for later tests
        }

        [UnityTest]
        public IEnumerator 서버_정지후_동일_포트_재리슨_성공()
        {
            int port = PortBase;      // random per run — works around listeners lingering after play mode ends (environment issue)

            var t1 = UniNetManager.ServerAsync(port);
            while (!t1.IsCompleted) yield return null;
            Assert.IsFalse(t1.IsFaulted, "첫 리슨 실패: " + t1.Exception);
            Assert.IsNotNull(UniNetEnvironment.Server);

            yield return StopTask(UniNetManager.ServerStopAsync());
            Assert.IsNull(UniNetEnvironment.Server, "정지 후 서버 참조 해제");

            // Core regression — relistening on the same port after stopping must succeed without a bind failure
            var t2 = UniNetManager.ServerAsync(port);
            while (!t2.IsCompleted) yield return null;
            Assert.IsFalse(t2.IsFaulted, "재리슨 실패(포트 잔존): " + t2.Exception);

            yield return StopTask(UniNetManager.ServerStopAsync());
        }

        [UnityTest]
        public IEnumerator 클라_정지후_서버연결해제와_재접속_성공()
        {
            int port = PortBase + 2;  // random per run
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
            int port = PortBase + 4;  // random per run
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
