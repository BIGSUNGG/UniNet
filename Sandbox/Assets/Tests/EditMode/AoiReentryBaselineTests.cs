using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// FR-04 — AOI re-entry baseline for an EXISTING client instance: the server's re-entry spawn message must refresh
    /// the already-spawned instance's transform and per-slot full state instead of being ignored — without re-instantiating,
    /// with SubId slots kept, the host reuse branch untouched (server originals stay authoritative), and slot mismatch rejected.
    /// </summary>
    public sealed class AoiReentryBaselineTests
    {
        private readonly List<GameObject> _created = new();
        private NetworkServer _prevServer;
        private NetworkClient _prevClient;

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _prevServer = UniNetEnvironment.Server;   // preserve the ambient globals — no cross-fixture pollution
            _prevClient = UniNetEnvironment.Client;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(_prevServer);
            UniNetEnvironment.SetClient(_prevClient);
            foreach (var go in _created)
                if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        /// <summary>Records the full spawn message arguments (the re-entry baseline must be replayed verbatim client-side).</summary>
        // EditMode, no networking — recorded system messages are replayed through the real client apply path.
        private sealed class SpawnRecorder : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal readonly List<(ulong NetId, float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw, ulong PrefabId, byte SubCount, ulong[] TypeKeys, byte[][] States)> Spawns = new();
            internal readonly List<ulong> Destroys = new();

            public void SendWelcome(long connId, string serverVersion, string sessionToken) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, DRPC.RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
                ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states)
                => Spawns.Add((netId, px, py, pz, qx, qy, qz, qw, prefabId, subCount, typeKeys, states));
            public void SendDestroy(ulong netId) => Destroys.Add(netId);
            public void SendTimeSync(double serverTime) { }
            public Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason) => Task.CompletedTask;
            public void SendLoadScene(string sceneName) { }
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
        }

        private static void ApplySpawn(SpawnRecorder ch, int index)
        {
            var s = ch.Spawns[index];
            UniNetSpawn.Apply(s.NetId, s.Px, s.Py, s.Pz, s.Qx, s.Qy, s.Qz, s.Qw, s.PrefabId, s.SubCount, s.TypeKeys, s.States);
        }

        [Test]
        public void 재진입_스폰은_기존_인스턴스를_최신_기준선으로_갱신한다()
        {
            var server = new NetworkServer();
            var ch = new SpawnRecorder();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            server.SetViewerPosition(ch.UniNetConnId, 100f, 0f, 0f);   // start OUTSIDE the cull — the first spawn arrives on the relevance transition

            var prefab = TestPrefabs.Load(TestPrefabs.MultiTankPrefab);
            ulong prefabId = prefab.GetComponent<NetworkSpawnable>().PrefabId;
            var go = new GameObject("fr04-server");
            _created.Add(go);
            var brain = go.AddComponent<MovementBrain>();
            var tank = go.AddComponent<HealthTank>();
            brain.NetworkCullDistance = 10f;
            ulong netId = server.RegisterDynamicObject(new object[] { brain, tank }, 0, prefabId);

            server.TickReplication(server.SnapshotObjects(), 1.0);   // still outside — nothing sent
            Assert.AreEqual(0, ch.Spawns.Count, "컬 거리 밖 — 스폰 없음");

            server.SetViewerPosition(ch.UniNetConnId, 5f, 0f, 0f);   // enter AOI
            server.TickReplication(server.SnapshotObjects(), 1.1);
            Assert.AreEqual(1, ch.Spawns.Count, "관련 전환 — 스폰 1회");

            var client = new NetworkClient();   // pure client — the server env must stay null for the client-side apply path
            UniNetEnvironment.SetServer(null);
            UniNetEnvironment.SetClient(client);
            ApplySpawn(ch, 0);
            var before = client.Get(netId);
            Assert.IsNotNull(before, "첫 스폰 — 클라 인스턴스 생성");
            Assert.AreEqual(2, before.Length, "서브 2개 슬롯");
            var beforeBrain = (MovementBrain)before[0];

            server.SetViewerPosition(ch.UniNetConnId, 100f, 0f, 0f);   // exit AOI — the client KEEPS its instance
            server.TickReplication(server.SnapshotObjects(), 1.2);
            Assert.AreEqual(0, ch.Destroys.Count, "이탈 — 클라 객체 유지 정책 (파괴 메시지 없음)");

            brain.Speed = 99;   // values change while the client cannot see the object
            tank.Armor = 88;
            go.transform.position = new Vector3(1f, 2f, 3f);   // the object also moved server-side

            server.SetViewerPosition(ch.UniNetConnId, 5f, 0f, 0f);   // re-enter
            server.TickReplication(server.SnapshotObjects(), 1.3);
            Assert.AreEqual(2, ch.Spawns.Count, "재진입 — 스폰 형식 기준선 재전송");

            ApplySpawn(ch, 1);   // the message that used to be silently ignored

            var after = client.Get(netId);
            Assert.AreSame(before, after, "신규 인스턴스 재생성 없음 — 같은 등록 슬롯 배열");
            var clientBrain = (MovementBrain)after[0];
            Assert.AreSame(beforeBrain, clientBrain, "기존 인스턴스 재사용 (재인스턴스 아님)");
            var clientTank = (HealthTank)after[1];
            Assert.AreEqual(99, clientBrain.Speed, "재진입 기준선 — 최신 값 갱신 (MovementBrain)");
            Assert.AreEqual(88, clientTank.Armor, "재진입 기준선 — 최신 값 갱신 (OwnerOnly Armor 포함)");
            Assert.AreEqual(0, clientBrain.SubId, "SubId 슬롯 유지 — 0");
            Assert.AreEqual(1, clientTank.SubId, "SubId 슬롯 유지 — 1");
            Assert.AreEqual(new Vector3(1f, 2f, 3f), clientBrain.transform.position, "재진입 기준선 — 트랜스폼 갱신 (이탈 중 이동 반영)");
        }

        [Test]
        public void 호스트_중복_스폰은_서버_원본을_덮지_않는다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);

            var prefab = TestPrefabs.Load(TestPrefabs.MultiTankPrefab);
            ulong prefabId = prefab.GetComponent<NetworkSpawnable>().PrefabId;
            var go = new GameObject("fr04-host");
            _created.Add(go);
            var brain = go.AddComponent<MovementBrain>();
            var tank = go.AddComponent<HealthTank>();
            brain.Speed = 5;
            ulong netId = server.RegisterDynamicObject(new object[] { brain, tank }, 0, prefabId);

            var entry = server.GetEntry(netId);
            var typeKeys = new[] { entry.Subs[0].TypeKey, entry.Subs[1].TypeKey };
            var staleStates = new[]   // captured BEFORE the server value moves on — a stale re-entry baseline
            {
                UniNetTypeRegistry.Find(typeof(MovementBrain)).WriteFull(brain, true),
                UniNetTypeRegistry.Find(typeof(HealthTank)).WriteFull(tank, true),
            };

            UniNetSpawn.Apply(netId, 0f, 0f, 0f, 0f, 0f, 0f, 1f, prefabId, 2, typeKeys, staleStates);   // first host spawn — registers the server originals
            Assert.AreSame(brain, client.Get(netId)[0], "호스트 — 서버 원본이 클라 등록으로 재사용된다");

            brain.Speed = 42;
            UniNetSpawn.Apply(netId, 0f, 0f, 0f, 0f, 0f, 0f, 1f, prefabId, 2, typeKeys, staleStates);   // duplicate (re-entry) — must be ignored

            Assert.AreSame(brain, client.Get(netId)[0], "호스트 중복 스폰 — 등록 불변");
            Assert.AreEqual(42, brain.Speed, "호스트 원본 분기 무간섭 — 스테일 기준선이 서버 권위 값을 덮지 않는다");
        }

        [Test]
        public void 재진입_스폰의_슬롯_불일치는_거부되고_기존_값을_지킨다()
        {
            var server = new NetworkServer();
            var ch = new SpawnRecorder();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            var prefab = TestPrefabs.Load(TestPrefabs.MultiTankPrefab);
            ulong prefabId = prefab.GetComponent<NetworkSpawnable>().PrefabId;
            var go = new GameObject("fr04-mismatch");
            _created.Add(go);
            var brain = go.AddComponent<MovementBrain>();
            var tank = go.AddComponent<HealthTank>();
            brain.Speed = 10;
            tank.Armor = 20;
            ulong netId = server.RegisterDynamicObject(new object[] { brain, tank }, 0, prefabId);
            server.BroadcastSpawn(netId);
            Assert.AreEqual(1, ch.Spawns.Count, "초기 스폰 1회");

            var client = new NetworkClient();   // pure client — real first spawn builds the existing instance
            UniNetEnvironment.SetServer(null);
            UniNetEnvironment.SetClient(client);
            ApplySpawn(ch, 0);
            var remote = client.Get(netId);
            Assert.IsNotNull(remote, "첫 스폰 — 클라 인스턴스 생성");
            var remoteBrain = (MovementBrain)remote[0];
            var remoteTank = (HealthTank)remote[1];
            Assert.AreEqual(10, remoteBrain.Speed, "첫 스폰 — 초기 기준선 적용");

            // a corrupt/mismatched re-entry message — slot typeKeys swapped
            var swapped = new[] { ch.Spawns[0].TypeKeys[1], ch.Spawns[0].TypeKeys[0] };
            LogAssert.Expect(LogType.Error, new Regex("재진입 스폰 슬롯 불일치"));
            UniNetSpawn.Apply(netId, 9f, 9f, 9f, 0f, 0f, 0f, 1f, prefabId, 2, swapped, ch.Spawns[0].States);

            Assert.AreSame(remoteBrain, client.Get(netId)[0], "불일치 거부 — 등록 불변");
            Assert.AreEqual(10, remoteBrain.Speed, "불일치 거부 — 기존 값 보존");
            Assert.AreEqual(0, remoteBrain.SubId, "슬롯 불일치 — SubId 불변 (0)");
            Assert.AreEqual(1, remoteTank.SubId, "슬롯 불일치 — SubId 불변 (1)");
            Assert.AreNotEqual(new Vector3(9f, 9f, 9f), remoteBrain.transform.position, "불일치 거부 — 트랜스폰도 갱신되지 않는다");
        }
    }
}
