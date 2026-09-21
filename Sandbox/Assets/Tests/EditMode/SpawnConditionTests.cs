using System;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>동적 스폰/파괴 + 조건부 리플리케이션(OwnerOnly·SkipOwner·InitialOnly) 코어 로직 테스트 (네트워킹 없음).</summary>
    public sealed class SpawnConditionTests
    {
        private const uint ScoreBit = 1u << 0;      // SpawnablePlayer 필드 인덱스 고정
        private const uint SecretBit = 1u << 1;
        private const uint TeamBit = 1u << 2;
        private const uint SeedBit = 1u << 3;

        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        private static SpawnablePlayer CreatePlayer(string name)
        {
            var go = new GameObject(name);
            return go.AddComponent<SpawnablePlayer>();
        }

        private static uint MaskOf(byte[] payload)
            => BitConverter.ToUInt32(payload, 0);

        [Test]
        public void 조건_델타는_수신_그룹별로_필드를_가른다()
        {
            var player = CreatePlayer("cond-delta");
            try
            {
                var handler = UniNetTypeRegistry.Find(player.GetType());
                Assert.IsNotNull(handler, "픽스처 리플리케이션 핸들(생성 코드)");

                var entry = new NetworkServer.SubObjectEntry(player);
                handler.InitSnapshot(entry);

                player.Score = 5;
                player.SecretHp = 40;
                player.TeamId = 9;

                var (toOwner, toOthers) = handler.CompareAndWriteDelta(entry);
                Assert.IsNotNull(toOwner, "소유자 델타 생성");
                Assert.IsNotNull(toOthers, "비소유자 델타 생성");
                Assert.AreNotSame(toOwner, toOthers, "조건 필드가 있으면 그룹별 페이로드");

                uint ownerMask = MaskOf(toOwner);
                uint otherMask = MaskOf(toOthers);
                Assert.AreEqual(ScoreBit, ownerMask & ScoreBit, "무조건 필드 — 소유자 포함");
                Assert.AreEqual(ScoreBit, otherMask & ScoreBit, "무조건 필드 — 비소유자 포함");
                Assert.AreEqual(SecretBit, ownerMask & SecretBit, "OwnerOnly — 소유자 포함");
                Assert.AreEqual(0u, otherMask & SecretBit, "OwnerOnly — 비소유자 제외");
                Assert.AreEqual(TeamBit, otherMask & TeamBit, "SkipOwner — 비소유자 포함");
                Assert.AreEqual(0u, ownerMask & TeamBit, "SkipOwner — 소유자 제외");
                Assert.AreEqual(0u, ownerMask & SeedBit, "InitialOnly — 델타 제외");
                Assert.AreEqual(0u, otherMask & SeedBit, "InitialOnly — 델타 제외");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [Test]
        public void 조건_델타는_각_수신자에게_올바르게_적용된다()
        {
            var player = CreatePlayer("cond-apply-src");
            var ownerRecv = CreatePlayer("cond-apply-owner");
            var otherRecv = CreatePlayer("cond-apply-other");
            try
            {
                var handler = UniNetTypeRegistry.Find(player.GetType());
                var entry = new NetworkServer.SubObjectEntry(player);
                handler.InitSnapshot(entry);
                handler.InitClientSnapshot(ownerRecv);
                handler.InitClientSnapshot(otherRecv);

                player.Score = 5;
                player.SecretHp = 40;
                player.TeamId = 9;
                var (toOwner, toOthers) = handler.CompareAndWriteDelta(entry);

                var ownerReader = new MessageProtocol.Serialize.MessageBufferReader(toOwner);
                handler.ApplyDelta(ownerRecv, ref ownerReader);
                Assert.AreEqual(5, ownerRecv.Score, "소유자 — 무조건 필드 적용");
                Assert.AreEqual(40, ownerRecv.SecretHp, "소유자 — OwnerOnly 필드 적용");
                Assert.AreEqual(3, ownerRecv.TeamId, "소유자 — SkipOwner 필드 미수신(초기값 유지)");
                Assert.IsTrue(ownerRecv.SecretNotified, "OwnerOnly 필드 RepNotify도 그룹 필터링 후 정상 호출");

                var otherReader = new MessageProtocol.Serialize.MessageBufferReader(toOthers);
                handler.ApplyDelta(otherRecv, ref otherReader);
                Assert.AreEqual(5, otherRecv.Score, "비소유자 — 무조건 필드 적용");
                Assert.AreEqual(9, otherRecv.TeamId, "비소유자 — SkipOwner 필드 적용");
                Assert.AreEqual(50, otherRecv.SecretHp, "비소유자 — OwnerOnly 필드 미수신(초기값 유지)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
                UnityEngine.Object.DestroyImmediate(ownerRecv.gameObject);
                UnityEngine.Object.DestroyImmediate(otherRecv.gameObject);
            }
        }

        [Test]
        public void InitialOnly는_초기_전송에만_포함된다()
        {
            var player = CreatePlayer("initial-only");
            try
            {
                var handler = UniNetTypeRegistry.Find(player.GetType());
                var entry = new NetworkServer.SubObjectEntry(player);
                handler.InitSnapshot(entry);

                player.SpawnSeed = 42;   // 스폰 이후 값 변경
                var (toOwner, toOthers) = handler.CompareAndWriteDelta(entry);
                Assert.IsNull(toOwner, "InitialOnly 변경은 델타를 만들지 않는다");
                Assert.IsNull(toOthers, "InitialOnly 변경은 델타를 만들지 않는다");

                byte[] full = handler.WriteFull(player, true);
                Assert.IsNotNull(full, "전체 상태에는 포함");
                Assert.AreEqual(SeedBit, MaskOf(full) & SeedBit, "InitialOnly 필드 — 전체 상태 포함");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [Test]
        public void 전체_상태_페이로드도_수신_그룹별_조건을_반영한다()
        {
            var player = CreatePlayer("full-mask");
            try
            {
                var handler = UniNetTypeRegistry.Find(player.GetType());
                uint ownerMask = MaskOf(handler.WriteFull(player, true));
                uint otherMask = MaskOf(handler.WriteFull(player, false));

                Assert.AreEqual(ScoreBit, ownerMask & ScoreBit, "무조건 — 소유자 전체");
                Assert.AreEqual(ScoreBit, otherMask & ScoreBit, "무조건 — 비소유자 전체");
                Assert.AreEqual(SecretBit, ownerMask & SecretBit, "OwnerOnly — 소유자 전체 포함");
                Assert.AreEqual(0u, otherMask & SecretBit, "OwnerOnly — 비소유자 전체 제외");
                Assert.AreEqual(0u, ownerMask & TeamBit, "SkipOwner — 소유자 전체 제외");
                Assert.AreEqual(TeamBit, otherMask & TeamBit, "SkipOwner — 비소유자 전체 포함");
                Assert.AreEqual(SeedBit, ownerMask & SeedBit, "InitialOnly — 소유자 전체 포함");
                Assert.AreEqual(SeedBit, otherMask & SeedBit, "InitialOnly — 비소유자 전체 포함");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [Test]
        public void 동적_등록은_netId를_할당하고_스폰을_전파한다()
        {
            var player = CreatePlayer("dyn-spawn");
            var server = new NetworkServer();
            var ch1 = new RecordingChannel();
            var ch2 = new RecordingChannel();
            try
            {
                server.AttachConnection(ch1);
                server.AttachConnection(ch2);
                UniNetEnvironment.PumpMain();   // Welcome·재배정 실행 — 연결 2개 확정

                ulong netId = server.RegisterDynamicObject(new object[] { player });
                Assert.AreNotEqual(0ul, netId, "동적 netId 할당");
                var entry = server.GetEntry(netId);
                Assert.IsNotNull(entry, "동적 등록 엔트리");
                Assert.IsTrue(entry.IsDynamic, "동적 플래그");
                Assert.AreEqual(1, entry.Subs.Length, "서브 테이블");
                Assert.AreNotEqual(0ul, entry.Subs[0].TypeKey, "스폰 타입 키");
                Assert.AreNotEqual(0, entry.OwnerConnId, "스폰 즉시 소유자 배정");

                server.BroadcastSpawn(netId);
                Assert.AreEqual(1, ch1.Spawns.Count, "전 연결 스폰 1회");
                Assert.AreEqual(1, ch2.Spawns.Count, "전 연결 스폰 1회");
                Assert.AreEqual(netId, ch1.Spawns[0].NetId, "스폰 netId");
                Assert.AreEqual(entry.Subs[0].TypeKey, ch1.Spawns[0].TypeKeys[0], "스폰 타입 키 일치");
                Assert.Greater(ch1.Spawns[0].States[0].Length, 0, "전체 상태 페이로드 동봉");
                Assert.AreEqual(1, ch1.OwnerUpdates.FindAll(u => u.Item1 == netId).Count, "소유권 알림 1회");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [Test]
        public void 파괴는_등록을_해제하고_전_연결에_전달한다()
        {
            var player = CreatePlayer("dyn-destroy");
            var server = new NetworkServer();
            var ch1 = new RecordingChannel();
            var ch2 = new RecordingChannel();
            try
            {
                server.AttachConnection(ch1);
                server.AttachConnection(ch2);
                UniNetEnvironment.PumpMain();

                ulong netId = server.RegisterDynamicObject(new object[] { player });
                server.BroadcastSpawn(netId);

                Assert.IsTrue(server.DestroyObject(netId), "등록된 오브젝트 파괴 성공");
                Assert.IsNull(server.GetEntry(netId), "등록 해제");
                Assert.AreEqual(1, ch1.Destroys.Count, "전 연결 파괴 전달");
                Assert.AreEqual(1, ch2.Destroys.Count, "전 연결 파괴 전달");
                Assert.IsFalse(server.DestroyObject(netId), "이중 파괴는 false");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [Test]
        public void 후발_접속_캐치업은_기존_상태를_합류시킨다()
        {
            var player = CreatePlayer("late-join-src");
            var server = new NetworkServer();
            var ch1 = new RecordingChannel();
            var late = new RecordingChannel();
            try
            {
                server.AttachConnection(ch1);
                UniNetEnvironment.PumpMain();   // 첫 연결 확정

                player.Score = 77;
                ulong netId = server.RegisterDynamicObject(new object[] { player });
                server.BroadcastSpawn(netId);
                Assert.AreEqual(1, ch1.Spawns.Count);

                // 후발 접속 — 캐치업이 기존 동적 오브젝트를 스폰으로 전달
                server.AttachConnection(late);
                UniNetEnvironment.PumpMain();

                Assert.AreEqual(1, late.Spawns.Count, "후발 접속 — 동적 오브젝트 스폰 1회");
                Assert.AreEqual(netId, late.Spawns[0].NetId, "캐치업 스폰 netId");
                uint mask = BitConverter.ToUInt32(late.Spawns[0].States[0], 0);
                Assert.AreEqual(ScoreBit, mask & ScoreBit, "캐치업 전체 상태에 현재값 포함");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        /// <summary>스폰·파괴·리플리케이션 기록 채널.</summary>
        private sealed class RecordingChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal long WelcomeConnId;
            internal readonly System.Collections.Generic.List<(ulong NetId, byte SubCount, ulong[] TypeKeys, byte[][] States)> Spawns = new();
            internal readonly System.Collections.Generic.List<ulong> Destroys = new();
            internal readonly System.Collections.Generic.List<(ulong, long)> OwnerUpdates = new();
            internal int Replicates;

            public void SendWelcome(long connId) => WelcomeConnId = connId;
            public void SendOwnerUpdate(ulong netId, long ownerConnId) => OwnerUpdates.Add((netId, ownerConnId));
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) => Replicates++;
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, byte subCount, ulong[] typeKeys, byte[][] states)
                => Spawns.Add((netId, subCount, typeKeys, states));
            public void SendDestroy(ulong netId) => Destroys.Add(netId);
            public void SendTimeSync(double serverTime) { }
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
        }
    }
}
