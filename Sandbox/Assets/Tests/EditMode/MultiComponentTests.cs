using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>2층 식별자(netId+SubId) 다중 컴포넌트 테스트 — 등록·subId RPC 라우팅·서브별 델타·다중 서브 스폰·클라 슬롯 대조.</summary>
    public sealed class MultiComponentTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            UniNetEnvironment.SetClient(new NetworkClient());   // 클라 스폰 적용 검증용
        }

        private static NetworkBehaviour[] CreatePair()
        {
            var go = new GameObject("multi");
            return new NetworkBehaviour[] { go.AddComponent<MovementBrain>(), go.AddComponent<HealthTank>() };
        }

        [Test]
        public void 오브젝트_등록은_컴포넌트_전체를_슬롯으로_등록한다()
        {
            var comps = CreatePair();
            try
            {
                var server = new NetworkServer();
                server.RegisterSceneObject(9001, comps);

                var entry = server.GetEntry(9001);
                Assert.IsNotNull(entry, "오브젝트 엔트리");
                Assert.AreEqual(2, entry.Subs.Length, "서브 테이블 2개");
                Assert.IsInstanceOf<MovementBrain>(server.Get(9001, 0), "슬롯 0 = 첫 컴포넌트");
                Assert.IsInstanceOf<HealthTank>(server.Get(9001, 1), "슬롯 1 = 둘째 컴포넌트");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(comps[0].gameObject);
            }
        }

        [Test]
        public void 같은_타입_컴포넌트_중복도_슬롯으로_구분된다()
        {
            var go = new GameObject("dup");
            var h1 = go.AddComponent<HealthTank>();
            var h2 = go.AddComponent<HealthTank>();
            try
            {
                // 실제 흐름 — UniNetManager.Spawn이 netId·SubId를 주입한다
                var server = new NetworkServer();
                UniNetEnvironment.SetServer(server);
                UniNetManager.Spawn(go);

                Assert.AreNotEqual(0ul, h1.NetId, "동적 netId 할당");
                Assert.AreSame(h1, server.Get(h1.NetId, 0), "슬롯 0 = 첫 인스턴스");
                Assert.AreSame(h2, server.Get(h1.NetId, 1), "슬롯 1 = 둘째 인스턴스");
                Assert.AreEqual(0, h1.SubId, "SubId 주입 — 첫째");
                Assert.AreEqual(1, h2.SubId, "SubId 주입 — 둘째 (같은 타입도 슬롯 구분)");
            }
            finally
            {
                UniNetEnvironment.SetServer(null);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void subId별_RPC는_해당_컴포넌트로만_라우팅된다()
        {
            var comps = CreatePair();
            var brain = (MovementBrain)comps[0];
            var tank = (HealthTank)comps[1];
            try
            {
                var server = new NetworkServer();
                UniNetEnvironment.SetServer(server);
                server.RegisterSceneObject(9003, comps);

                // 와이어 [netId][subId][int] — 실제 디스패치 테이블 경유 (생성 __Req 핸들 호출)
                int moveId = Fnv1a.MethodId("UniNet.Tests.MovementBrain.RpcMove");
                int healId = Fnv1a.MethodId("UniNet.Tests.HealthTank.RpcHeal");

                Dispatch(moveId, 9003, 0, 3);   // 슬롯 0 — MovementBrain
                Dispatch(healId, 9003, 1, 4);   // 슬롯 1 — HealthTank
                Dispatch(healId, 9003, 0, 4);   // 슬롯 0에 HealthTank 핸들 — 캐스팅 실패로 무시

                UniNetEnvironment.PumpMain();   // 지연 디스패치 실행

                Assert.AreEqual(1, brain.MoveCalls, "subId 0 → MovementBrain만 실행");
                Assert.AreEqual(1, tank.HealCalls, "subId 1 → HealthTank만 실행");
                Assert.AreEqual(4, brain.Speed, "대상 컴포넌트 값 변경");
            }
            finally
            {
                UniNetEnvironment.SetServer(null);
                UnityEngine.Object.DestroyImmediate(comps[0].gameObject);
            }
        }

        [Test]
        public void 서브별_델타는_독립적으로_계산된다()
        {
            var comps = CreatePair();
            var brain = (MovementBrain)comps[0];
            var tank = (HealthTank)comps[1];
            try
            {
                var server = new NetworkServer();
                server.RegisterSceneObject(9004, comps);
                var entry = server.GetEntry(9004);

                var brainHandler = UniNetTypeRegistry.Find(brain.GetType());
                var tankHandler = UniNetTypeRegistry.Find(tank.GetType());

                brain.Speed = 7;   // MovementBrain만 변경
                var (brainDelta, _) = brainHandler.CompareAndWriteDelta(entry.Subs[0]);
                var (tankDelta, _) = tankHandler.CompareAndWriteDelta(entry.Subs[1]);
                Assert.IsNotNull(brainDelta, "변경된 서브는 델타 생성");
                Assert.IsNull(tankDelta, "변경 없는 서브는 델타 없음");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(comps[0].gameObject);
            }
        }

        [Test]
        public void 다중_서브_스폰은_전체_구성과_상태를_전달한다()
        {
            var comps = CreatePair();
            var brain = (MovementBrain)comps[0];
            var tank = (HealthTank)comps[1];
            var server = new NetworkServer();
            var ch = new RecordingChannel();
            try
            {
                server.AttachConnection(ch);
                UniNetEnvironment.PumpMain();

                ulong netId = server.RegisterDynamicObject(comps);
                server.BroadcastSpawn(netId);

                Assert.AreEqual(1, ch.Spawns.Count, "스폰 1회");
                var spawn = ch.Spawns[0];
                Assert.AreEqual(netId, spawn.NetId, "스폰 netId");
                Assert.AreEqual(2, spawn.SubCount, "서브 2개 전달");
                Assert.AreEqual(UniNetSpawnRegistry.TypeKeyOf(typeof(MovementBrain)), spawn.TypeKeys[0], "슬롯 0 타입");
                Assert.AreEqual(UniNetSpawnRegistry.TypeKeyOf(typeof(HealthTank)), spawn.TypeKeys[1], "슬롯 1 타입");
                Assert.Greater(spawn.States[0].Length, 0, "서브 0 전체 상태");
                Assert.Greater(spawn.States[1].Length, 0, "서브 1 전체 상태 (OwnerOnly Armor 포함 — 유일 연결=소유자)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(comps[0].gameObject);
            }
        }

        [Test]
        public void 클라_스폰_적용은_슬롯을_대조해_전_서브를_구성한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);

            // 서버 측 오브젝트 — 다중 컴포넌트
            var comps = CreatePair();
            var brain = (MovementBrain)comps[0];
            var tank = (HealthTank)comps[1];
            brain.Speed = 42;
            tank.Armor = 77;

            // 클라 생성용 프리팹 템플릿 — 같은 구성(MovementBrain+HealthTank)
            var template = new GameObject("template");
            template.AddComponent<MovementBrain>();
            template.AddComponent<HealthTank>();
            try
            {
                UniNetManager.RegisterPrefab<MovementBrain>(template);

                ulong netId = server.RegisterDynamicObject(comps);
                var entry = server.GetEntry(netId);
                var typeKeys = new[] { entry.Subs[0].TypeKey, entry.Subs[1].TypeKey };
                var states = new[]
                {
                    UniNetTypeRegistry.Find(brain.GetType()).WriteFull(brain, true),
                    UniNetTypeRegistry.Find(tank.GetType()).WriteFull(tank, true),
                };

                // 클라 적용 (호스트 아님 — 순수 클라 경로)
                UniNetEnvironment.SetServer(null);
                UniNetSpawn.Apply(netId, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 2, typeKeys, states);

                var clientComps = UniNetEnvironment.Client.Get(netId);
                Assert.IsNotNull(clientComps, "클라 등록");
                Assert.AreEqual(2, clientComps.Length, "서브 2개");
                var clientBrain = (MovementBrain)clientComps[0];
                var clientTank = (HealthTank)clientComps[1];
                Assert.AreEqual(42, clientBrain.Speed, "서브 0 상태 적용");
                Assert.AreEqual(77, clientTank.Armor, "서브 1 상태 적용 (OwnerOnly)");
                Assert.AreEqual(new Vector3(1f, 2f, 3f), clientBrain.transform.position, "스폰 변환 적용");
                Assert.AreEqual(netId, clientBrain.NetId, "netId 주입");
                Assert.AreEqual(0, clientBrain.SubId, "클라 슬롯 주입");
                Assert.AreEqual(1, clientTank.SubId, "클라 슬롯 주입");

                // 서브 자동 복원 (ADR-0014) — 단일 컴포넌트 템플릿(슬롯 0)이어도 누락 서브는 typeKeys 순서대로 자동 추가된다
                var solo = new GameObject("solo");
                solo.AddComponent<MovementBrain>();
                UniNetManager.RegisterPrefab<MovementBrain>(solo);
                UniNetSpawn.Apply(netId + 1, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 2, typeKeys, states);
                var restored = UniNetEnvironment.Client.Get(netId + 1);
                Assert.IsNotNull(restored, "자동 복원 — 스폰 등록");
                Assert.AreEqual(2, restored.Length, "서버와 동일한 2슬롯 복원");
                Assert.IsInstanceOf<MovementBrain>(restored[0], "슬롯 0 — 템플릿 제공분");
                Assert.IsInstanceOf<HealthTank>(restored[1], "슬롯 1 — 자동 복원분");
                Assert.AreEqual(77, ((HealthTank)restored[1]).Armor, "복원분에도 전체 상태 적용");
                UnityEngine.Object.DestroyImmediate(solo);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(comps[0].gameObject);
                UnityEngine.Object.DestroyImmediate(template);
            }
        }

        [Test]
        public void 컴포넌트_256개_초과는_거부되고_서버_방어가_예외를_던진다()
        {
            // (a) Unity 진입점 — Spawn 가드: 에러 로그 + 서버 미등록
            var go = new GameObject("too-many");
            for (int i = 0; i < 256; i++) go.AddComponent<HealthTank>();
            try
            {
                var server = new NetworkServer();
                UniNetEnvironment.SetServer(server);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("최대 255"));
                UniNetManager.Spawn(go);
                Assert.AreEqual(0, server.SnapshotObjects().Count, "상한 초과 — 서버 미등록 (랩어라운드 루프 도달 불가)");

                // (b) Core 최종 방어 — AttachSubs 예외 (직접 등록 경유)
                var tooMany = new object[256];
                for (int i = 0; i < tooMany.Length; i++) tooMany[i] = new object();
                Assert.Throws<System.ArgumentOutOfRangeException>(() => server.RegisterDynamicObject(tooMany), "SubId byte 상한 — 서버 등록 최종 방어");
            }
            finally
            {
                UniNetEnvironment.SetServer(null);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>[netId][subId][int amount] 페이로드를 실제 디스패치 테이블로 보낸다.</summary>
        private static void Dispatch(int methodId, ulong netId, byte subId, int amount)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteByte(subId);
            w.WriteInt32(amount);
            var handler = UniNetDispatch.ServerHandlers()[methodId].Handler;
            handler(0, w.ToArray()).GetAwaiter().GetResult();
        }

        private sealed class RecordingChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal readonly System.Collections.Generic.List<(ulong NetId, byte SubCount, ulong[] TypeKeys, byte[][] States)> Spawns = new();

            public void SendWelcome(long connId) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, byte subCount, ulong[] typeKeys, byte[][] states)
                => Spawns.Add((netId, subCount, typeKeys, states));
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
        }
    }
}
