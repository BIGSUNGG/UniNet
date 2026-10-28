using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace UniNet.Tests
{
    /// <summary>
    /// AUDIT-20261027-F06/F08 regressions.
    /// F06 — the REAL public client-session boundary (ClientAsync/ReconnectAsync; never a bare SetClient for the swap
    /// under test) must purge the previous client generation's dynamic mirrors, while host ClientStop preserves the
    /// live server's authoritative originals/entries (identity match against live entries — never netId: a recycled id
    /// may name a different component) and ServerStop removes them. Multi-component objects are destroyed once.
    /// EditMode strategy: ClientAsync against a closed port (bounded ConnectTimeoutMs) exercises the synchronous
    /// prefix — the sweep and generation swap run before the await; mirrors are applied through the real
    /// UniNetSpawn.Apply catch-up path with the in-memory prefab-table test injection (no user prefab touched).
    /// Runtime driver destruction is verified separately by DriverLifetimePlayTests; EditMode fixtures explicitly
    /// clean their owned runtime-only driver's callbacks because Unity does not dispatch its OnDestroy here.
    /// </summary>
    public sealed class ClientSessionPurgeTests
    {
        private UniNetHubFactory _factoryBefore;
        private Func<double> _clockBefore;
        private MonoBehaviour _driverBefore;

        private static Type DriverType => typeof(NetworkBehaviour).Assembly.GetType("UniNet.Unity.UniNetDriver");
        private static FieldInfo DriverInstance => DriverType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp]
        public void CaptureEnvironment()
        {
            _factoryBefore = UniNetEnvironment.HubFactory;
            _clockBefore = UniNetTime.LocalClock;
            _driverBefore = DriverInstance.GetValue(null) as MonoBehaviour;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetManager.ClientStop();
            UniNetManager.ServerStop();
            UniNetEnvironment.SetClient(null);
            UniNetEnvironment.SetServer(null);
            typeof(UniNetEnvironment).GetProperty(nameof(UniNetEnvironment.HubFactory)).SetValue(null, _factoryBefore);
            if (_driverBefore == null && DriverInstance.GetValue(null) is MonoBehaviour created && created != null)
            {
                // Fixture cleanup only, NOT the F08 lifecycle assertion (that lives in PlayMode).
                DriverType.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(created, null);
                Object.DestroyImmediate(created.gameObject);
            }
            UniNetTime.LocalClock = _clockBefore;
        }

        private static IEnumerator FailConnect(int port)
        {
            var task = UniNetManager.ClientAsync("127.0.0.1", port, new UniNetEndpointOptions { ConnectTimeoutMs = 300 });
            float deadline = Time.realtimeSinceStartup + 20f;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("ClientAsync 실패 대기 초과 (닫힌 포트)");
                yield return null;
            }
            Assert.IsTrue(task.IsFaulted, "닫힌 포트 연결은 예외로 종료 — 동기 prefix(스왑+스윕)는 이미 실행됐다");
        }

        private static GameObject MakeTemplate(out ulong prefabId, out ulong[] typeKeys)
        {
            var go = new GameObject("F06TankTemplate");
            go.AddComponent<HealthTank>();
            var marker = go.AddComponent<NetworkSpawnable>();
            prefabId = 0xC0F06000 + (ulong)go.GetInstanceID();
            marker.Stamp(prefabId);
            UniNetPrefabTable.RegisterForTest(prefabId, go);   // in-memory table injection — no user prefab touched
            typeKeys = new[] { UniNetSpawnRegistry.TypeKeyOf(typeof(HealthTank)) };
            return go;
        }

        private static int LiveClones(GameObject template)
            => Object.FindObjectsByType<HealthTank>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(t => t != null && t.gameObject != template && t.name.Contains("F06TankTemplate"));

        private static void ApplySpawn(ulong netId, ulong prefabId, ulong[] typeKeys)
            => UniNetSpawn.Apply(netId, 0f, 0f, 0f, 0f, 0f, 0f, 1f, prefabId, 1, typeKeys, null);

        [UnityTest]
        public IEnumerator ClientAsync_세대교체가_이전_클라이언트_동적_미러를_제거한다()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            var template = MakeTemplate(out var prefabId, out var typeKeys);
            try
            {
                const ulong netId = 4242;

                yield return FailConnect(57891);   // generation 1 — real boundary (synchronous prefix ran)
                ApplySpawn(netId, prefabId, typeKeys);   // real catch-up path lands a mirror under generation 1
                Assert.AreEqual(1, LiveClones(template), "1세대 미러 1개");

                yield return FailConnect(57893);   // generation 2 — the swap must have purged the old mirror
                Assert.AreEqual(0, LiveClones(template), "세대 교체 후 이전 미러 제거 (F06)");

                ApplySpawn(netId, prefabId, typeKeys);   // recycled id on the fresh registry — exactly one live instance
                Assert.AreEqual(1, LiveClones(template), "재활용 netId 재스폰 후 라이브 인스턴스 1개 (유령 없음)");
            }
            finally
            {
                Object.DestroyImmediate(template);
                foreach (var t in Object.FindObjectsByType<HealthTank>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (t != null && t.name.Contains("F06TankTemplate")) Object.DestroyImmediate(t.gameObject);
                UniNetPrefabTable.ResetCache();
            }
        }

        [UnityTest]
        public IEnumerator ReconnectAsync_세대교체도_동일하게_이전_미러를_제거한다()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            var template = MakeTemplate(out var prefabId, out var typeKeys);
            try
            {
                yield return FailConnect(57895);
                UniNetEnvironment.Client.SessionToken = "f06-resume-token";   // ReconnectAsync only resumes with a stored token
                ApplySpawn(777, prefabId, typeKeys);
                Assert.AreEqual(1, LiveClones(template));

                var task = UniNetManager.ReconnectAsync("127.0.0.1", 57897, new UniNetEndpointOptions { ConnectTimeoutMs = 300 });
                float deadline = Time.realtimeSinceStartup + 20f;
                while (!task.IsCompleted)
                {
                    if (Time.realtimeSinceStartup > deadline) Assert.Fail("ReconnectAsync 실패 대기 초과");
                    yield return null;
                }
                Assert.IsTrue(task.IsFaulted, "실제 재접속 경계 — 닫힌 포트는 예외로 종료");
                Assert.AreEqual(0, LiveClones(template), "ReconnectAsync 세대 교체 후 이전 미러 제거 (F06)");
            }
            finally
            {
                Object.DestroyImmediate(template);
                foreach (var t in Object.FindObjectsByType<HealthTank>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (t != null && t.name.Contains("F06TankTemplate")) Object.DestroyImmediate(t.gameObject);
                UniNetPrefabTable.ResetCache();
            }
        }

        [Test]
        public void 호스트_ClientStop은_서버_원본과_등록을_보존하고_ServerStop은_제거한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);

            // authoritative original (host) — a LIVE server entry names this exact component/GameObject
            var original = new GameObject("F06Original");
            var tank = original.AddComponent<HealthTank>();
            var marker = original.AddComponent<NetworkSpawnable>();
            ulong prefabId = 0xC0F06006;
            marker.Stamp(prefabId);
            ulong netId = server.RegisterDynamicObject(new object[] { tank }, 0, prefabId);
            tank.AssignNetId(netId);
            tank.AssignSubId(0);
            tank.MarkServerRegistered();

            // RECYCLED-ID control — a stale two-component mirror carrying the SAME netId but a DIFFERENT GameObject:
            // identity (not netId) must decide. Multi-component: the GameObject must be destroyed exactly once.
            var mirror = new GameObject("F06RecycledMirror");
            var m1 = mirror.AddComponent<HealthTank>();
            var m2 = mirror.AddComponent<MovementBrain>();
            m1.AssignNetId(netId);
            m2.AssignNetId(netId);

            UniNetEnvironment.SetClient(new NetworkClient());   // host: server + client both set
            UniNetManager.ClientStop();   // real boundary

            Assert.IsFalse(original == null, "호스트 서버 원본 보존 (F06) — Unity null 의미론으로 파괴 여부 검사");
            Assert.IsNotNull(server.GetEntry(netId), "서버 등록 엔트리 보존");
            Assert.IsTrue(mirror == null, "재활용 netId 미러(다중 컴포넌트)는 정체성 대조로 제거");
            Assert.IsTrue(m1 == null && m2 == null, "다중 컴포넌트 일괄 파괴 — 중복 DestroyImmediate 없음");

            UniNetManager.ServerStop();   // server teardown removes the server dynamics
            Assert.IsTrue(original == null, "ServerStop 이후 서버 동적 원본 제거");
            Assert.IsNull(UniNetEnvironment.Server, "ServerStop 이후 서버 런타임 탈착 — 폐기된 이전 registry의 외부 참조는 재사용하지 않는다");
            UniNetEnvironment.SetServer(null);
        }

    }
}
