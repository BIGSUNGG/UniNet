using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Connection↔player-object mapping (ADR-0031) — register/lookup/reverse-lookup, auto-clear on detach, and the
    /// departure cleanup helper (DestroyObjectsOwnedBy) from server-side entries.
    /// </summary>
    public sealed class PlayerMappingTests
    {
        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(null);
        }

        [Test]
        public void 등록_조회_역조회가_왕복한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "pmap");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            server.SetPlayerObject(ch.UniNetConnId, player.NetId);

            Assert.AreEqual(player.NetId, server.GetPlayerObject(ch.UniNetConnId), "connId → netId 조회");
            Assert.AreEqual(ch.UniNetConnId, server.GetPlayerConnection(player.NetId), "netId → connId 역조회");
            Assert.AreEqual(0UL, server.GetPlayerObject(ch.UniNetConnId + 100), "미등록 연결 — 0");
        }

        [Test]
        public void 연결_해제시_매핑이_자동_해제된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "pmap-detach");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            server.SetPlayerObject(ch.UniNetConnId, player.NetId);
            Assert.AreNotEqual(0UL, server.GetPlayerObject(ch.UniNetConnId), "등록 확인");

            server.DetachConnection(ch);
            UniNetEnvironment.PumpMain();   // the cleanup runs on the main queue

            Assert.AreEqual(0UL, server.GetPlayerObject(ch.UniNetConnId), "해제 후 자동 클리어 (ADR-0031)");
        }

        [Test]
        public void ClearPlayerObject는_매핑을_해제한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            server.SetPlayerObject(ch.UniNetConnId, 77UL);
            server.SetPlayerObject(ch.UniNetConnId, 0);   // 0 = remove

            Assert.AreEqual(0UL, server.GetPlayerObject(ch.UniNetConnId), "netId 0 등록 = 해제");
            server.SetPlayerObject(ch.UniNetConnId, 77UL);
            server.ClearPlayerObject(ch.UniNetConnId);
            Assert.AreEqual(0UL, server.GetPlayerObject(ch.UniNetConnId), "명시적 해제");
        }

        [Test]
        public void DestroyObjectsOwnedBy는_소유_동적_오브젝트만_파괴한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            chA.Clear();
            chB.Clear();

            var goA1 = new GameObject("pmap-a1");
            var a1 = goA1.AddComponent<SpawnablePlayer>();
            var goA2 = new GameObject("pmap-a2");
            var a2 = goA2.AddComponent<SpawnablePlayer>();
            var goB1 = new GameObject("pmap-b1");
            var b1 = goB1.AddComponent<SpawnablePlayer>();
            ulong idA1 = server.RegisterDynamicObject(new NetworkBehaviour[] { a1 }, chA.UniNetConnId);
            ulong idA2 = server.RegisterDynamicObject(new NetworkBehaviour[] { a2 }, chA.UniNetConnId);
            ulong idB1 = server.RegisterDynamicObject(new NetworkBehaviour[] { b1 }, chB.UniNetConnId);

            var sceneFurniture = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "pmap-scene");   // scene object — must SURVIVE the helper (level furniture contract)
            server.SetOwner(sceneFurniture.NetId, chA.UniNetConnId);

            int destroyed = server.DestroyObjectsOwnedBy(chA.UniNetConnId);

            Assert.AreEqual(2, destroyed, "A 소유 동적 2개 파괴");
            Assert.IsNull(server.GetEntry(idA1), "A1 등록 해제");
            Assert.IsNull(server.GetEntry(idA2), "A2 등록 해제");
            Assert.IsNotNull(server.GetEntry(idB1), "B 소유 — 생존");
            Assert.IsNotNull(server.GetEntry(sceneFurniture.NetId), "씬 오브젝트는 파괴 대상 아님(계약 고정)");
            Assert.AreEqual(2, chA.Destroys.Count, "A 연결에 파괴 전파");
            Assert.AreEqual(2, chB.Destroys.Count, "B 연결에 파괴 전파");
        }
    }
}
