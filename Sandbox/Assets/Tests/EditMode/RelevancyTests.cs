using System;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>P3-① 가시성(Relevancy) — 컬 거리·관련성 훅·동적 스폰 게이팅·재진입 기준선 복구 검증 (네트워킹 없음).</summary>
    public sealed class RelevancyTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [Test]
        public void 컬거리_밖_연결에는_델타를_보내지_않고_복귀하면_기준선을_복구한다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "rel-dist");
            player.NetworkCullDistance = 10f;

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // 캐치업 — 뷰어 위치 없음 → 항상 관련 → 전체 상태 전송 + 시드
            ch.Clear();

            server.SetViewerPosition(ch.UniNetConnId, 100f, 0f, 0f);   // 컬 반경 10 밖
            player.Score = 42;

            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(0, ch.Replicates.Count, "컬 거리 밖 연결에는 델타를 보내지 않는다");

            server.SetViewerPosition(ch.UniNetConnId, 5f, 0f, 0f);     // 컬 안 — 재진입
            server.TickReplication(server.SnapshotObjects(), 1.5);
            Assert.AreEqual(2, ch.Replicates.Count, "재진입 — 기준선(전체 상태) + 미전송 변경분 델타");
            AssertScoreReceived(ch, 42, "복구분에 변경된 Score 값이 포함된다");

            server.TickReplication(server.SnapshotObjects(), 2.0);
            Assert.AreEqual(2, ch.Replicates.Count, "이후 틱 — 잔여물 없음 (churn 없음)");
        }

        [Test]
        public void 씬_오브젝트의_첫_평가는_전체_상태를_추가로_보내지_않는다()
        {
            var server = new NetworkServer();
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "rel-late");   // 접속 후 등록
            player.Score = 7;
            ch.Clear();

            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(1, ch.Replicates.Count, "첫 평가 — P2 계약대로 변경분 델타만 전송");
            AssertScoreReceived(ch, 7, "델타 값 확인");
        }

        [Test]
        public void 관련성_훅이_거부한_연결은_캐치업과_델타를_모두_받지_않는다()
        {
            var server = new NetworkServer();
            var relay = ReplicationTestSupport.RegisterScene<FilteredRelay>(server, "rel-hook");
            relay.AllowedConnId = 2;

            var ch1 = new RecordingChannel();
            var ch2 = new RecordingChannel();
            server.AttachConnection(ch1);   // ConnId 1 — 훅 거부
            server.AttachConnection(ch2);   // ConnId 2 — 훅 허용
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(0, ch1.Replicates.Count, "거부 연결 — 캐치업 제외");
            Assert.AreEqual(1, ch2.Replicates.Count, "허용 연결 — 캐치업 전체 상태 수신");

            relay.Value = 7;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(0, ch1.Replicates.Count, "거부 연결 — 델타 제외");
            Assert.AreEqual(2, ch2.Replicates.Count, "허용 연결 — 델타 수신");
        }

        [Test]
        public void 스폰_시점에_비관련인_연결은_관련_전환_틱에_스폰을_받는다()
        {
            var server = new NetworkServer();
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            server.SetViewerPosition(ch.UniNetConnId, 100f, 0f, 0f);   // 원점의 동적 오브젝트에서 100 — 컬 밖

            var go = new GameObject("rel-dyn");
            var bullet = go.AddComponent<SpawnablePlayer>();
            bullet.NetworkCullDistance = 10f;
            var netId = server.RegisterDynamicObject(new object[] { bullet });
            server.BroadcastSpawn(netId);
            Assert.AreEqual(0, ch.Spawns.Count, "스폰 시점 비관련 — 스폰 메시지 생략");
            Assert.AreEqual(0, ch.Owners.Count, "스폰 시점 비관련 — 소유권 알림도 생략");

            server.SetViewerPosition(ch.UniNetConnId, 5f, 0f, 0f);     // 컬 안 — 관련 전환
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(1, ch.Spawns.Count, "동적 오브젝트 관련 전환 — 스폰(생성) 전송");
            Assert.AreEqual(1, ch.Owners.Count, "관련 전환 — 소유권 알림 동반");
        }

        private static void AssertScoreReceived(RecordingChannel ch, int expected, string message)
        {
            foreach (var (_, _, _, payload) in ch.Replicates)
            {
                if (payload.Length >= 8
                    && (BitConverter.ToUInt32(payload, 0) & (1u << 0)) != 0
                    && BitConverter.ToInt32(payload, 4) == expected)
                    return;
            }
            Assert.Fail($"{message} — Score={expected} 페이로드 없음 (전송 {ch.Replicates.Count}건)");
        }
    }
}
