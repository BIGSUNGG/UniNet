using System;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>P3 relevancy — verifies cull distance, relevancy hooks, dynamic-spawn gating, and baseline restore on re-entry (no networking).</summary>
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
            UniNetEnvironment.PumpMain();   // catch-up — no viewer position → always relevant → full state + seed
            ch.Clear();

            server.SetViewerPosition(ch.UniNetConnId, 100f, 0f, 0f);   // outside the cull radius of 10
            player.Score = 42;

            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(0, ch.Replicates.Count, "컬 거리 밖 연결에는 델타를 보내지 않는다");

            server.SetViewerPosition(ch.UniNetConnId, 5f, 0f, 0f);     // inside the cull radius — re-entry
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

            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "rel-late");   // registered after connect
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
            server.AttachConnection(ch1);   // ConnId 1 — rejected by the hook
            server.AttachConnection(ch2);   // ConnId 2 — allowed by the hook
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
            server.SetViewerPosition(ch.UniNetConnId, 100f, 0f, 0f);   // 100 units from the dynamic object at the origin — outside the cull

            var go = new GameObject("rel-dyn");
            var bullet = go.AddComponent<SpawnablePlayer>();
            bullet.NetworkCullDistance = 10f;
            var netId = server.RegisterDynamicObject(new object[] { bullet });
            server.BroadcastSpawn(netId);
            Assert.AreEqual(0, ch.Spawns.Count, "스폰 시점 비관련 — 스폰 메시지 생략");
            Assert.AreEqual(0, ch.Owners.Count, "스폰 시점 비관련 — 소유권 알림도 생략");

            server.SetViewerPosition(ch.UniNetConnId, 5f, 0f, 0f);     // inside the cull — becomes relevant
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
