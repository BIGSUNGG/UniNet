using System;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// P4 spawn sub-slot auto-restore — verifies that UniNetSpawn.Apply restores missing NetworkBehaviour
    /// subs on the client object in the spawn message's typeKeys order (root fix for multi-component
    /// slot mismatch, see ADR-0014).
    /// </summary>
    public sealed class UniNetSpawnApplyTests
    {
        private static ulong KeyPlayer;   // SpawnablePlayer
        private static ulong KeyNT;       // NetworkTransform

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            KeyPlayer = Fnv1a.Hash64(typeof(SpawnablePlayer).FullName);
            KeyNT = Fnv1a.Hash64(typeof(NetworkTransform).FullName);
            UniNetEnvironment.SetClient(new NetworkClient());
            // registers NetworkTransform (UniNet.Unity source) handlers/spawn — EditMode has no bootstrap to fire this, so register explicitly (idempotent)
            UniNet.Generated.Hosting.__UniNetRegistration_Host.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetClient(null);
        }

        [Test]
        public void Apply_2서브_스폰을_순서대로_복원하고_위치_상태도_적용한다()
        {
            UniNetSpawnRegistry.Register(typeof(NetworkTransform), KeyNT,
                () => new GameObject("nt").AddComponent<NetworkTransform>());

            var client = UniNetEnvironment.Client;
            var netId = Fnv1a.Hash64("apply-2sub");

            // capture server-side state — full payload of a NetworkTransform object that moved to (5,2,3)
            var serverGo = new GameObject("server-nt");
            serverGo.transform.position = new Vector3(5f, 2f, 3f);
            var serverNt = serverGo.AddComponent<NetworkTransform>();
            var ntHandler = UniNetTypeRegistry.Find(typeof(NetworkTransform));
            Assert.IsNotNull(ntHandler, "NetworkTransform 리플리케이션 핸들 등록됨");
            var ntState = ntHandler.WriteFull(serverNt, true);

            // spawn message — two server subs: [SpawnablePlayer, NetworkTransform] (the default factory only creates the first sub)
            UniNetSpawn.Apply(netId, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 2,
                new[] { KeyPlayer, KeyNT }, new byte[][] { null, ntState });

            var comps = client.Get(netId);
            Assert.IsNotNull(comps, "스폰 등록됨");
            Assert.AreEqual(2, comps.Length, "서버 서브 2개 — 클라도 2개로 복원 (슬롯 불일치 해소)");
            Assert.IsInstanceOf<SpawnablePlayer>(comps[0], "서브 0 — 서버 순서 유지");
            Assert.IsInstanceOf<NetworkTransform>(comps[1], "서브 1 — 자동 복원");

            Assert.AreEqual((byte)0, ((NetworkBehaviour)comps[0]).SubId, "슬롯 0 SubId");
            Assert.AreEqual((byte)1, ((NetworkBehaviour)comps[1]).SubId, "슬롯 1 SubId");
            Assert.AreEqual(netId, ((NetworkBehaviour)comps[0]).NetId, "netId 일치");

            // remote client receiving position state → interpolation buffer loaded (render-ready) — key assertion against movement-sync regressions
            var clientNt = (NetworkTransform)comps[1];
            Assert.GreaterOrEqual(clientNt.BufferedSampleCount, 1, "리모트 클라 — NT 위치 상태가 인터폴레이션 버퍼에 적재되어 렌더 준비 완료 (Notify 연결 회귀 방지)");
        }

        [Test]
        public void Apply_역방향_순서도_보존한다()
        {
            UniNetSpawnRegistry.Register(typeof(NetworkTransform), KeyNT,
                () => new GameObject("nt2").AddComponent<NetworkTransform>());

            var client = UniNetEnvironment.Client;
            var netId = Fnv1a.Hash64("apply-rev");

            UniNetSpawn.Apply(netId, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 2,
                new[] { KeyNT, KeyPlayer }, new byte[][] { null, null });

            var comps = client.Get(netId);
            Assert.AreEqual(2, comps.Length);
            Assert.IsInstanceOf<NetworkTransform>(comps[0], "서버 서브 순서 [NetworkTransform, SpawnablePlayer] 유지");
            Assert.IsInstanceOf<SpawnablePlayer>(comps[1]);
        }

        [Test]
        public void Apply_미등록_타입_키는_스폰을_거부한다()
        {
            var client = UniNetEnvironment.Client;
            var netId = Fnv1a.Hash64("apply-unknown");
            var unknownKey = Fnv1a.Hash64("UniNet.Tests.존재하지않는타입");

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("등록되지 않은 타입 키"));
            UniNetSpawn.Apply(netId, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 2,
                new[] { KeyPlayer, unknownKey }, new byte[][] { null, null });

            Assert.IsNull(client.Get(netId), "복원 불가 스폰 — 등록되지 않는다 (부분 생성 폐기)");
        }

        [Test]
        public void Apply_단일_서브는_기존_동작을_유지한다()
        {
            var client = UniNetEnvironment.Client;
            var netId = Fnv1a.Hash64("apply-single");

            UniNetSpawn.Apply(netId, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1,
                new[] { KeyPlayer }, new byte[][] { null });

            var comps = client.Get(netId);
            Assert.AreEqual(1, comps.Length, "단일 서브 — 기존 동작 회귀 없음");
            Assert.IsInstanceOf<SpawnablePlayer>(comps[0]);
        }
    }
}
