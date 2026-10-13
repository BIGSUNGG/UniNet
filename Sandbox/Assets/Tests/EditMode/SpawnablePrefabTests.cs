using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Spawnable-prefab gate and pipeline tests (ADR-0024) — NetworkInstantiate only accepts stamped
    /// spawnable prefabs; the same first component type can ship as two different prefabs (the old
    /// type-key 1:1 catalog limit is gone); the baked table resolves prefab ids at runtime.
    /// </summary>
    public sealed class SpawnablePrefabTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(null);
        }

        [Test]
        public void 미마킹_오브젝트는_거부되고_아무것도_전파되지_않는다()
        {
            var runtime = new GameObject("runtime-template");
            runtime.AddComponent<SpawnablePlayer>();
            try
            {
                var server = new NetworkServer();
                UniNetEnvironment.SetServer(server);
                var ch = new RecordingChannel();
                server.AttachConnection(ch);
                UniNetEnvironment.PumpMain();

                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("NetworkSpawnable 이 각인된"));
                Assert.IsNull(UniNetManager.NetworkInstantiate(runtime), "런타임 생성 오브젝트 — null 반환");
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("NetworkSpawnable 이 각인된"));
                var unmarked = new GameObject("unmarked-prefab-standin");
                unmarked.AddComponent<SpawnablePlayer>();
                try { Assert.IsNull(UniNetManager.NetworkInstantiate(unmarked), "마커 없는 오브젝트 — null 반환"); }
                finally { Object.DestroyImmediate(unmarked); }

                Assert.AreEqual(0, ch.Spawns.Count, "스폰 메시지 0건");
                Assert.AreEqual(0, server.SnapshotObjects().Count, "서버 등록 0건");
            }
            finally
            {
                Object.DestroyImmediate(runtime);
            }
        }

        [Test]
        public void 각인되지_않은_마커는_거부된다()
        {
            var go = new GameObject("unstamped-marker");
            go.AddComponent<SpawnablePlayer>();
            go.AddComponent<NetworkSpawnable>();   // runtime-added marker — id stays 0
            try
            {
                UniNetEnvironment.SetServer(new NetworkServer());
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("NetworkSpawnable 이 각인된"));
                Assert.IsNull(UniNetManager.NetworkInstantiate(go), "id 0 마커 — 거부 (에디트 타임 각인만이 유일 경로)");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void 같은_타입의_서로_다른_프리팹_두_종이_동시에_스폰된다()
        {
            var a = TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab);
            var b = TestPrefabs.Load(TestPrefabs.AltSpawnablePlayerPrefab);
            Assert.IsNotNull(a);
            Assert.IsNotNull(b);
            Assert.AreNotEqual(a.GetComponent<NetworkSpawnable>().PrefabId, b.GetComponent<NetworkSpawnable>().PrefabId,
                "두 프리팹은 다른 프리팹 아이디를 갖는다");

            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var ia = UniNetManager.NetworkInstantiate(a, clone => clone.GetComponent<SpawnablePlayer>().Score = 11);
            var ib = UniNetManager.NetworkInstantiate(b, clone => clone.GetComponent<SpawnablePlayer>().Score = 22);
            try
            {
                Assert.IsNotNull(ia);
                Assert.IsNotNull(ib);
                Assert.AreNotEqual(ia.GetComponent<SpawnablePlayer>().NetId, ib.GetComponent<SpawnablePlayer>().NetId, "서로 다른 netId");
                Assert.AreEqual(11, ia.GetComponent<SpawnablePlayer>().Score, "A 초기값");
                Assert.AreEqual(22, ib.GetComponent<SpawnablePlayer>().Score, "B 초기값");
                Assert.AreEqual(2, server.SnapshotObjects().Count, "두 오브젝트 등록 — 타입 키 1:1 제약 해소");
            }
            finally
            {
                if (ia != null) Object.DestroyImmediate(ia);
                if (ib != null) Object.DestroyImmediate(ib);
            }
        }

        [Test]
        public void 베이크_테이블이_프리팹_아이디를_해석한다()
        {
            var prefab = TestPrefabs.Load(TestPrefabs.MultiTankPrefab);
            Assert.IsNotNull(prefab);
            var id = prefab.GetComponent<NetworkSpawnable>().PrefabId;
            Assert.AreNotEqual(0ul, id, "포스트프로세서가 프리팹 GUID 해시를 각인했다");

            var resolved = UniNetPrefabTable.Lookup(id);
            Assert.AreSame(prefab, resolved, "Resources 베이크 테이블이 아이디를 프리팹으로 해석");
        }

        private sealed class RecordingChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal readonly System.Collections.Generic.List<ulong> Spawns = new();
            public void SendWelcome(long connId, string serverVersion, ulong sessionToken) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, DRPC.RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states)
                => Spawns.Add(netId);
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason) => System.Threading.Tasks.Task.CompletedTask;
            public void SendLoadScene(string sceneName) { }
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
        }
    }
}
