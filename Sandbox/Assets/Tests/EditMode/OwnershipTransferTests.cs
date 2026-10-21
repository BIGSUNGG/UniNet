using System.Collections.Generic;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Explicit ownership transfer (ADR-0030) — SetOwner changes the server-side owner, propagates OwnerUpdate to
    /// every connection (existing wire), and integrates with the orphan-adoption reassignment policy.
    /// </summary>
    public sealed class OwnershipTransferTests
    {
        private readonly List<GameObject> _created = new();   // only GameObjects THIS fixture created — no scene-wide finds/sweeps

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(null);
            foreach (var go in _created)
                if (go != null) Object.DestroyImmediate(go);   // caller owns the created objects (RegisterScene contract)
            _created.Clear();
        }

        private T Track<T>(T comp) where T : Component
        {
            _created.Add(comp.gameObject);
            return comp;
        }

        /// <summary>Attach + version declaration — fake channels never send wire 7, so tests model the real client's automatic declaration (CON-001).</summary>
        private static void AttachApproved(NetworkServer server, RecordingChannel ch)
        {
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");
        }

        [Test]
        public void SetOwner는_소유권을_이전하고_전_연결에_전파한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "setowner");
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // welcome + ownership (single object — first connection owns it)
            chA.Clear();
            chB.Clear();

            bool ok = server.SetOwner(player.NetId, chB.UniNetConnId);

            Assert.IsTrue(ok, "알려진 netId — 이전 성립");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "서버 측 소유자 갱신");
            Assert.AreEqual(1, chA.Owners.Count, "연결 A — OwnerUpdate 수신");
            Assert.AreEqual((player.NetId, chB.UniNetConnId), chA.Owners[0], "갱신 내용 (netId, 새 소유자)");
            Assert.AreEqual(1, chB.Owners.Count, "연결 B — OwnerUpdate 수신 (본인이 새 소유자)");
        }

        [Test]
        public void SetOwner는_미지_오브젝트에_false를_반환한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);

            Assert.IsFalse(server.SetOwner(999_999UL, 1), "미지 netId — 거짓");
        }

        [Test]
        public void SetOwner_0은_방치하고_다음_재배정이_입양한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "setowner-release");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "초기 — 소유자 배정됨");

            Assert.IsTrue(server.SetOwner(player.NetId, 0), "방치(해제) 성립");
            Assert.AreEqual(0, server.GetOwner(player.NetId), "소유자 미배정 상태");

            // A new connection triggers the reassignment pass — the orphan is adopted round-robin
            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "재배정 통과 후 입양 — 다시 소유자 배정");
        }

        // ── OWN-002 — 소유권 이전 시 조건 필드 baseline 재전송 ───────────────────────────────
        // 이전 직전엔 신규 소유자가 OwnerOnly/InitialOnly 값을, 직전 소유자가 SkipOwner 값을 한 번도 받지 못할 수 있다
        // (그룹 마스크가 수신자 역할로 필터링). 이전 후 델타는 "변화가 있을 때만" 나가므로 미변경 값은 영원히 도착하지
        // 않는다 — 서버가 이전 두 당사자에게 state-only baseline을 재전송하는 계약과 그 경계를 고정한다.

        [Test]
        public void SetOwner_후_새_소유자는_미변경_OwnerOnly_값을_baseline으로_받는다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-owneronly"));
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            AttachApproved(server, chA);
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();   // 단일 객체 — chA 소유; chB 캐치업은 Others 뷰 (SecretHp 제외)
            chA.Clear();
            chB.Clear();

            player.SecretHp = 77;   // OwnerOnly — 전송된 적 없는 값 (틱 없음)
            Assert.IsTrue(server.SetOwner(player.NetId, chB.UniNetConnId), "이전 성립");

            Assert.AreEqual(1, chB.Replicates.FindAll(e => e.netId == player.NetId).Count, "신규 소유자 — baseline 1회");
            AssertOwnerBeforeBaseline(chB, player.NetId);
            var receiver = Track(new GameObject("own2-owneronly-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, player.NetId, receiver);
            Assert.AreEqual(77, receiver.SecretHp, "미변경 OwnerOnly — 서버 현재값 도착 (델타에선 영원히 못 받는 값)");
            Assert.IsTrue(receiver.SecretNotified, "RepNotify 발화 — 스폰/캐치업과 동일 계약");
            Assert.AreEqual(50, receiver.LastPrevSecret, "Notify prev — 적용 전 기본값");
        }

        [Test]
        public void SetOwner_후_직전_소유자는_SkipOwner_값을_baseline으로_받는다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-skipowner"));
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            AttachApproved(server, chA);
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();
            chA.Clear();
            chB.Clear();

            player.TeamId = 9;   // SkipOwner — 소유 동안 Owner 페이로드에선 제외되어 받은 적 없다
            Assert.IsTrue(server.SetOwner(player.NetId, chB.UniNetConnId));

            Assert.AreEqual(1, chA.Replicates.FindAll(e => e.netId == player.NetId).Count, "직전 소유자 — baseline 1회");
            var receiver = Track(new GameObject("own2-skipowner-a").AddComponent<SpawnablePlayer>());
            ApplyAll(chA, player.NetId, receiver);
            Assert.AreEqual(9, receiver.TeamId, "SkipOwner 현재값 — 이전 owner도 이전 후엔 Others 뷰 수신 (OWN-002)");
        }

        [Test]
        public void SetOwner_baseline에는_InitialOnly_값도_포함된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-initialonly"));
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            AttachApproved(server, chA);
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();   // 캐치업 — 초기값 7
            chA.Clear();
            chB.Clear();

            player.SpawnSeed = 1234;   // InitialOnly — 델타 경로엔 영구 배제 → baseline 없으면 영구 누락
            Assert.IsTrue(server.SetOwner(player.NetId, chB.UniNetConnId));

            Assert.AreEqual(1, chB.Replicates.FindAll(e => e.netId == player.NetId).Count, "baseline 1회");
            var receiver = Track(new GameObject("own2-initialonly-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, player.NetId, receiver);
            Assert.AreEqual(1234, receiver.SpawnSeed, "InitialOnly 현재값 — 이전 후에도 도착");
        }

        [Test]
        public void SetOwner는_이전_수신자의_연기_델타를_무효화하고_baseline만_남긴다()
        {
            var server = new NetworkServer { ReplicationBudgetPerTickBytes = 8 };   // Score 델타(mask4+int4)=8B — 객체당 틱 1개
            UniNetEnvironment.SetServer(server);
            var high = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-budget-high"));
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-budget"));
            high.NetworkPriority = 100f;   // high가 예산을 먼저 소비 → player 델타 연기
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            AttachApproved(server, chA);
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();
            chA.Clear();
            chB.Clear();

            high.Score = 2;
            player.Score = 10;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(1, server.DeferredSendCount, "준비 — player 델타 예산 연기");
            Assert.IsFalse(chB.Replicates.Exists(e => e.netId == player.NetId), "연기 중 미전송");

            Assert.IsTrue(server.SetOwner(player.NetId, chB.UniNetConnId));
            Assert.AreEqual(0, server.DeferredSendCount, "양쪽 수신자 모두 신선한 baseline 수신 — 연기 항목 전부 무효화");
            Assert.AreEqual(1, chB.Replicates.FindAll(e => e.netId == player.NetId).Count, "baseline만 — 스테일 오버라이드 없음");
            Assert.AreNotEqual(0, FirstMask(chB, player.NetId) & (1u << 3), "baseline 마스크 — InitialOnly 포함 (전체상태, 델타 아님)");

            server.ReplicationBudgetPerTickBytes = 0;
            server.TickReplication(server.SnapshotObjects(), 1.1);   // flush — B에겐 더 이상 갈 것이 없다
            Assert.AreEqual(1, chB.Replicates.FindAll(e => e.netId == player.NetId).Count, "flush 후에도 baseline만 (무효화된 스테일 재생 없음)");

            player.Score = 42;
            server.TickReplication(server.SnapshotObjects(), 1.2);
            var receiver = Track(new GameObject("own2-budget-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, player.NetId, receiver);
            Assert.AreEqual(42, receiver.Score, "baseline 이후 변경 델타 정상 적용 — 최종값 == 서버값");
        }

        [Test]
        public void ResumeSession_재부착_소유자에게_OwnerOnly_baseline이_전달된다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };   // 무씬 — 승인 직후 캐치업 즉시 송신 (SCN-002 홀드 없음)
            UniNetEnvironment.SetServer(server);
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-resume"));
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();   // chA 소유
            string token = server.SnapshotConnections()[0].SessionToken;
            long oldOwner = server.GetOwner(player.NetId);

            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();   // 유예 보존 — 소유자는 죽은 connId 유지
            Assert.AreEqual(oldOwner, server.GetOwner(player.NetId), "준비 — 유예 보존");

            var chB = new RecordingChannel();
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();   // 캐치업 — owner=구 connId(유예), Others 뷰 (결함 시퀀스)
            int preCount = chB.Replicates.FindAll(e => e.netId == player.NetId).Count;
            Assert.GreaterOrEqual(preCount, 1, "준비 — 캐치업 수신");

            player.SecretHp = 88;   // 재부착 전 변경 — 전송된 적 없음
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(token, chB.UniNetConnId), "재부착 성립");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "소유권 이전");
            CollectionAssert.Contains(chB.Owners, (player.NetId, chB.UniNetConnId), "재부착 클라 — OwnerUpdate 수신");
            Assert.AreEqual(preCount + 1, chB.Replicates.FindAll(e => e.netId == player.NetId).Count, "재부착 — baseline 1회 추가");

            var receiver = Track(new GameObject("own2-resume-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, player.NetId, receiver);   // 캐치업 + baseline 와이어 순서 재생
            Assert.AreEqual(88, receiver.SecretHp, "재부착 소유자 — OwnerOnly 현재값 도착 (캐치업만으론 Others 뷰여서 못 받던 값)");
        }

        [Test]
        public void 입양_재배정은_입양자에게_baseline을_보내고_타_연결에_OwnerOnly를_누출하지_않는다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 0 };   // 유예 없음 — 해제 즉시 재배정
            UniNetEnvironment.SetServer(server);
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-adopt"));
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();   // chA 소유
            var chB = new RecordingChannel();
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();   // B 캐치업 (owner=A, Others 뷰)
            int preCount = chB.Replicates.FindAll(e => e.netId == player.NetId).Count;

            player.SecretHp = 66;   // 전송된 적 없는 값
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();   // 고아 입양 — B 승계
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "입양 성립");
            Assert.AreEqual(preCount + 1, chB.Replicates.FindAll(e => e.netId == player.NetId).Count, "입양자 — baseline 1회 추가");

            var receiverB = Track(new GameObject("own2-adopt-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, player.NetId, receiverB);
            Assert.AreEqual(66, receiverB.SecretHp, "입양자 OwnerOnly 현재값 (입양도 이전이다 — OWN-002)");

            // 프라이버시 경계 — 이후 참여자의 캐치업(비소유 뷰)에 소유자의 OwnerOnly가 누출되지 않는다
            // (지연 큐 flush의 전송 시점 그룹 선택과 함께 감사 오탐 메모 종결용 경계 단언)
            var chC = new RecordingChannel();
            AttachApproved(server, chC);
            UniNetEnvironment.PumpMain();
            CollectionAssert.Contains(chC.Owners, (player.NetId, chB.UniNetConnId), "C — 소유자 통보는 받는다");
            var receiverC = Track(new GameObject("own2-adopt-c").AddComponent<SpawnablePlayer>());
            ApplyAll(chC, player.NetId, receiverC);
            Assert.AreEqual(50, receiverC.SecretHp, "비소유 연결 — OwnerOnly 미누출");
        }

        [Test]
        public void 이전_baseline_마스크는_수신자_역할을_따른다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-mask"));
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            AttachApproved(server, chA);
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();
            chA.Clear();
            chB.Clear();

            player.SecretHp = 71;    // OwnerOnly (비트 1<<1)
            player.TeamId = 12;      // SkipOwner (비트 1<<2)
            player.SpawnSeed = 4321; // InitialOnly (비트 1<<3)
            Assert.IsTrue(server.SetOwner(player.NetId, chB.UniNetConnId));

            uint aMask = FirstMask(chA, player.NetId);
            Assert.AreEqual(0, aMask & (1u << 1), "직전 소유자 baseline — OwnerOnly 비트 제외");
            Assert.AreNotEqual(0, aMask & (1u << 2), "직전 소유자 baseline — SkipOwner 비트 포함");
            Assert.AreNotEqual(0, aMask & (1u << 3), "직전 소유자 baseline — InitialOnly 비트 포함 (전체상태)");
            uint bMask = FirstMask(chB, player.NetId);
            Assert.AreEqual(0, bMask & (1u << 2), "신규 소유자 baseline — SkipOwner 비트 제외");
            Assert.AreNotEqual(0, bMask & (1u << 1), "신규 소유자 baseline — OwnerOnly 비트 포함");

            var rA = Track(new GameObject("own2-mask-a").AddComponent<SpawnablePlayer>());
            ApplyAll(chA, player.NetId, rA);
            Assert.AreEqual(50, rA.SecretHp, "직전 소유자 — OwnerOnly 값 미포함 적용");
            Assert.AreEqual(12, rA.TeamId, "직전 소유자 — SkipOwner 값 포함 적용");
            var rB = Track(new GameObject("own2-mask-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, player.NetId, rB);
            Assert.AreEqual(71, rB.SecretHp, "신규 소유자 — OwnerOnly 값 포함 적용");
            Assert.AreEqual(3, rB.TeamId, "신규 소유자 — SkipOwner 값 미포함 적용");
        }

        [Test]
        public void 동적_오브젝트_이전은_스폰을_재전송하지_않고_상태만_보낸다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();

            var bullet = Track(new GameObject("own2-dyn").AddComponent<SpawnablePlayer>());
            var netId = server.RegisterDynamicObject(new object[] { bullet });   // 라운드로빈 → chA
            server.BroadcastSpawn(netId);
            chA.Clear();

            var chB = new RecordingChannel();
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();   // 캐치업 — B는 스폰 메시지로 인스턴스 수신
            Assert.AreEqual(1, chB.Spawns.Count, "준비 — 캐치업 스폰 1회");
            chB.Clear();

            bullet.SecretHp = 55;   // 전송된 적 없는 OwnerOnly 값
            Assert.IsTrue(server.SetOwner(netId, chB.UniNetConnId));
            Assert.AreEqual(0, chB.Spawns.Count, "이전 — 스폰 재전송 없음 (클라 재인스턴스 방지)");
            Assert.AreEqual(1, chB.Replicates.FindAll(e => e.netId == netId).Count, "상태 전송 1회");
            AssertOwnerBeforeBaseline(chB, netId);

            var receiver = Track(new GameObject("own2-dyn-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, netId, receiver);
            Assert.AreEqual(55, receiver.SecretHp, "동적 객체도 state-only baseline으로 OwnerOnly 현재값 도착");
        }

        [Test]
        public void baseline은_서버_스냅샷을_건드리지_않아_이후_델타가_정상_전송된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own2-snapshot"));
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            AttachApproved(server, chA);
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();
            chA.Clear();
            chB.Clear();

            player.Score = 5;
            server.TickReplication(server.SnapshotObjects(), 1.0);   // 스냅샷 → 5
            chA.Clear();
            chB.Clear();

            Assert.IsTrue(server.SetOwner(player.NetId, chB.UniNetConnId));   // baseline — 스냅샷 미갱신이 계약

            player.Score = 6;
            server.TickReplication(server.SnapshotObjects(), 1.1);
            Assert.AreEqual(6, server.GetEntry(player.NetId).Subs[0].Snapshot[0], "틱 회계 — 스냅샷 정상 전진 (baseline이 스냅샷을 건드리지 않음)");
            var rA = Track(new GameObject("own2-snapshot-a").AddComponent<SpawnablePlayer>());
            ApplyAll(chA, player.NetId, rA);
            Assert.AreEqual(6, rA.Score, "직전 소유자 — 이후 변경 델타 수신");
            var rB = Track(new GameObject("own2-snapshot-b").AddComponent<SpawnablePlayer>());
            ApplyAll(chB, player.NetId, rB);
            Assert.AreEqual(6, rB.Score, "신규 소유자 — 이후 변경 델타 수신 (dormancy flush 회계 불변)");
        }

        // ── OWN-002 helpers ─────────────────────────────

        /// <summary>First recorded replicate payload's u32 field mask for a netId (sub 0) — full-state and delta masks differ, which is the discriminator.</summary>
        private static uint FirstMask(RecordingChannel ch, ulong netId)
        {
            foreach (var (id, sub, _, payload) in ch.Replicates)
            {
                if (id != netId || sub != 0) continue;
                var r = new MessageProtocol.Serialize.MessageBufferReader(payload);
                return r.ReadUInt32();
            }
            Assert.Fail("replicate 기록 없음 — netId " + netId);
            return 0;
        }

        /// <summary>Replays every recorded replicate payload for a netId in wire order through the generated ApplyIn (same envelope delta and full state share).</summary>
        private static void ApplyAll(RecordingChannel ch, ulong netId, SpawnablePlayer receiver)
        {
            var handler = UniNetTypeRegistry.Find(receiver.GetType());
            foreach (var (id, _, _, payload) in ch.Replicates)
            {
                if (id != netId) continue;
                var r = new MessageProtocol.Serialize.MessageBufferReader(payload);
                handler.ApplyDelta(receiver, ref r);
            }
        }

        /// <summary>OWN-001 ordering contract — the owner notice must precede the state baseline on the same connection's wire.</summary>
        private static void AssertOwnerBeforeBaseline(RecordingChannel ch, ulong netId)
        {
            int ownerIdx = -1, stateIdx = -1;
            for (int i = 0; i < ch.Order.Count; i++)
            {
                if (ownerIdx < 0 && ch.Order[i].StartsWith("owner:" + netId + ":")) ownerIdx = i;
                if (stateIdx < 0 && ch.Order[i].StartsWith("replicate:" + netId + ":")) stateIdx = i;
            }
            Assert.GreaterOrEqual(ownerIdx, 0, "소유자 통보 존재");
            Assert.GreaterOrEqual(stateIdx, 0, "baseline 상태 존재");
            Assert.Less(ownerIdx, stateIdx, "소유자 통보가 baseline보다 먼저 (OWN-001 순서 계약)");
        }
    }
}
