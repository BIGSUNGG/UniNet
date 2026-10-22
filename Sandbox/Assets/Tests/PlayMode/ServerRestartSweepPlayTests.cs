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
    /// LIF-001 — server restart hygiene (PlayMode real loopback): ServerStopAsync must sweep dynamic spawns
    /// (symmetric with the synchronous ServerStop), a failed listen must roll the environment back instead of
    /// leaving a zombie server, and scene objects must re-register with a RESTARTED server (generation stamps
    /// replaced the one-way registration booleans that silently skipped re-registration forever).
    /// Test plan §4 mapping: T1 sweep / T2 same-port respawn / T3 scene re-registration / T4 listen-failure
    /// rollback / T5 host restart loopback. Pattern follows LifecycleStopTests — TestPortAllocator for fresh
    /// ports, TearDown HostStop for cleanup.
    /// </summary>
    public sealed class ServerRestartSweepPlayTests
    {
        [SetUp]
        public void AllowStopNoise()
        {
            // Stop/bind-failure paths log fire-and-forget send noise and bind failures from thread-pool
            // continuations — expected noise here, may surface even after TearDown ends (LifecycleStopTests).
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void StopListeners()
        {
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // idempotent — covers server-only and client-only tests too
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator 서버_비동기_정지시_동적_스폰이_스윕된다()
        {
            int port = TestPortAllocator.Next();
            ulong genBefore = NetworkServer.Generation;   // LIF-001 — restart must mint a fresh generation

            var t = UniNetManager.ServerAsync(port);
            while (!t.IsCompleted) yield return null;
            Assert.IsFalse(t.IsFaulted, "첫 리슨 실패: " + t.Exception);

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab));
            Assert.IsNotNull(go, "동적 스폰 성공");
            var spawned = go.GetComponent<SpawnablePlayer>();
            Assert.IsTrue(spawned.IsDynamicSpawn, "동적 스폰 판정 — 스윕 대상");

            yield return StopTask(UniNetManager.ServerStopAsync());

            // RED until the ServerStopAsync sweep: Destroy defers to end of frame, so wait (bounded) for the sweep to land
            yield return WaitUntil(() => CountDynamicSpawns() == 0, 5);
            Assert.AreEqual(0, CountDynamicSpawns(), "비동기 정지 후 동적 스폰 유령 0 — 동기 ServerStop과 대칭");
            Assert.IsTrue(go == null, "스폰된 오브젝트 파괴 확인");

            Assert.Greater(NetworkServer.Generation, genBefore, "인스턴스 재생성 — 세대 증가 (등록 스탬프 무효화 기반)");
        }

        [UnityTest]
        public IEnumerator 동일_포트_재시작후_재스폰_netId_비충돌과_유령_없음()
        {
            int port = TestPortAllocator.Next();

            var t1 = UniNetManager.ServerAsync(port);
            while (!t1.IsCompleted) yield return null;
            Assert.IsFalse(t1.IsFaulted, "첫 리슨 실패: " + t1.Exception);

            var go1 = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab));
            Assert.IsNotNull(go1, "첫 세션 스폰");
            ulong firstNetId = go1.GetComponent<SpawnablePlayer>().NetId;
            Assert.AreNotEqual(0ul, firstNetId, "동적 netId 할당");

            yield return StopTask(UniNetManager.ServerStopAsync());
            yield return WaitUntil(() => CountDynamicSpawns() == 0, 5);

            // Restart on the SAME port — the new instance's dynamic netId counter restarts at 1, so an unswept
            // ghost from session 1 would alias onto the recycled ids (the exact defect class the sweep prevents)
            var t2 = UniNetManager.ServerAsync(port);
            while (!t2.IsCompleted) yield return null;
            Assert.IsFalse(t2.IsFaulted, "동일 포트 재시작 실패(포트 잔존): " + t2.Exception);

            var go2 = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab));
            Assert.IsNotNull(go2, "재시작 세션 재스폰");
            var respawned = go2.GetComponent<SpawnablePlayer>();

            Assert.AreEqual(1, CountDynamicSpawns(), "유령 0 — 재스폰 1개만 존재");
            int sameNetId = 0;
            foreach (var nb in Object.FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (nb.NetId == respawned.NetId)
                    sameNetId++;
            Assert.AreEqual(1, sameNetId, "netId 비충돌 — 같은 netId를 쓰는 살아있는 객체는 1개");
            Assert.IsNotNull(UniNetEnvironment.Server.Get(respawned.NetId), "새 서버 등록 확인");
        }

        [UnityTest]
        public IEnumerator 정지후_재시작하면_씬_객체가_새_서버에_재등록된다()
        {
            int port = TestPortAllocator.Next();

            var t1 = UniNetManager.ServerAsync(port);
            while (!t1.IsCompleted) yield return null;
            Assert.IsFalse(t1.IsFaulted, "첫 리슨 실패: " + t1.Exception);

            // Runtime-created scene object (unstamped → hierarchy-path netId) — registered in session 1
            var sceneGo = new GameObject("lif001-scene-obj");
            var nb = sceneGo.AddComponent<SpawnablePlayer>();
            try
            {
                int first = NetworkBehaviour.RegisterAllToServer();
                Assert.Greater(first, 0, "1세션 — 씬 객체 등록");
                ulong sceneNetId = nb.NetId;
                Assert.IsNotNull(UniNetEnvironment.Server.Get(sceneNetId), "1세션 등록 확인");
                Assert.IsTrue(nb.IsServer, "1세션 IsServer");

                yield return StopTask(UniNetManager.ServerStopAsync());

                var t2 = UniNetManager.ServerAsync(port);
                while (!t2.IsCompleted) yield return null;
                Assert.IsFalse(t2.IsFaulted, "재시작 실패: " + t2.Exception);

                // RED before the fix: the one-way boolean skipped re-registration, so the new server's table
                // never learned this object. GREEN with generation stamps: ServerAsync's RegisterAllToServer
                // re-admits the object under the new generation — so the count assertion is expressed through
                // Get/IsServer (a manual count here would read 0 by idempotency, not by the old defect).
                Assert.IsNotNull(UniNetEnvironment.Server.Get(sceneNetId), "재시작 후 재등록 — 새 서버 등록 테이블");
                Assert.IsTrue(nb.IsServer, "재시작 후 IsServer — 현재 세대 스탬프");
                Assert.AreEqual(0, NetworkBehaviour.RegisterAllToServer(), "재등록 스킵 유지 — 현재 세대 내에서는 0건 (Idempotent)");
            }
            finally
            {
                Object.Destroy(sceneGo);
            }
        }

        [UnityTest]
        public IEnumerator 리슨_실패시_서버_참조가_롤백된다()
        {
            int port = TestPortAllocator.Next();

            var t1 = UniNetManager.ServerAsync(port);
            while (!t1.IsCompleted) yield return null;
            Assert.IsFalse(t1.IsFaulted, "첫 리슨 실패: " + t1.Exception);
            Assert.IsNotNull(UniNetEnvironment.Server);

            // Same port while still bound — the second listen must fault, and the environment must NOT keep
            // pointing at the failed instance (RED before the fix: a zombie server stayed attached)
            var t2 = UniNetManager.ServerAsync(port);
            yield return WaitCompleted(t2, 5);
            Assert.IsTrue(t2.IsFaulted, "동일 포트 재호출은 실패해야 한다: " + t2.Exception);
            Assert.IsNull(UniNetEnvironment.Server, "리슨 실패 롤백 — 좀비 서버 잔존 없음");

            // The first listener's handle survived the failed second call — stop frees the port for a fresh start
            yield return StopTask(UniNetManager.ServerStopAsync());
            Assert.IsNull(UniNetEnvironment.Server);

            int port2 = TestPortAllocator.Next();
            var t3 = UniNetManager.ServerAsync(port2);
            while (!t3.IsCompleted) yield return null;
            Assert.IsFalse(t3.IsFaulted, "신규 포트 재기동 성공: " + t3.Exception);
            Assert.IsNotNull(UniNetEnvironment.Server, "롤백 후 정상 기동");
        }

        [UnityTest]
        public IEnumerator 호스트_재시작후_스폰이_루프백으로_동작한다()
        {
            int port = TestPortAllocator.Next();

            var h1 = UniNetManager.HostAsync(port);
            while (!h1.IsCompleted) yield return null;
            Assert.IsFalse(h1.IsFaulted, "첫 호스트 기동 실패: " + h1.Exception);
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);

            yield return StopTask(UniNetManager.HostStopAsync());
            Assert.IsNull(UniNetEnvironment.Server, "호스트 정지 — 서버 해제");
            Assert.IsNull(UniNetEnvironment.Client, "호스트 정지 — 클라 해제");

            var h2 = UniNetManager.HostAsync(port);
            while (!h2.IsCompleted) yield return null;
            Assert.IsFalse(h2.IsFaulted, "호스트 재시작 실패: " + h2.Exception);
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);

            // Spawn under the RESTARTED session — server and client both carry fresh generation stamps
            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab));
            Assert.IsNotNull(go, "재시작 세션 스폰");
            var spawned = go.GetComponent<SpawnablePlayer>();
            Assert.IsTrue(spawned.IsServer, "재시작 서버 등록 — 새 세대 스탬프");

            yield return WaitUntil(() => UniNetEnvironment.Client.Get(spawned.NetId) != null, 5);
            Assert.AreSame(spawned, (SpawnablePlayer)UniNetEnvironment.Client.Get(spawned.NetId, spawned.SubId), "루프백 — 호스트 서버 인스턴스 재사용");
            Assert.IsTrue(spawned.IsClient, "재시작 클라 등록 — 새 세대 스탬프 (클라 세대 패리티)");
        }

        private static IEnumerator StopTask(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted)
                Assert.Fail("Stop 실패: " + task.Exception);
        }

        private static IEnumerator WaitCompleted(Task task, float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("리슨 호출이 시간 내에 완료되지 않았다 (바인드 실패가 관측되지 않음)");
                yield return null;
            }
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("조건 대기 시간 초과");
                yield return null;
            }
        }

        private static int CountDynamicSpawns()
        {
            int n = 0;
            foreach (var nb in Object.FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (nb.IsDynamicSpawn)
                    n++;
            return n;
        }
    }
}
