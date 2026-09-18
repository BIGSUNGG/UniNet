using System;
using System.Collections.Generic;
using DRPC;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>리플리케이션 우선순위·예산(ReplicationBudgetPerTickBytes) 코어 로직 테스트 (네트워킹 없음)
    /// — 예산 초과 연기·우선순위 순서·기아 방지·무제한 기본값.</summary>
    public sealed class PriorityTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        private static SpawnablePlayer RegisterScenePlayer(NetworkServer server, string name, int score)
        {
            var go = new GameObject(name);
            var player = go.AddComponent<SpawnablePlayer>();
            player.Score = score;
            server.RegisterSceneObject(player.NetId, new object[] { player });   // 실제 등록 경로와 동일 — 컴포넌트 NetId 키
            return player;
        }

        [Test]
        public void 예산_초과_델타는_대기열로_연기되고_다음_틱에_전송된다()
        {
            var server = new NetworkServer();
            server.ReplicationBudgetPerTickBytes = 8;   // SpawnablePlayer Score 델타 = mask(4)+int(4) = 8바이트 — 1개만 허용
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            var high = RegisterScenePlayer(server, "prio-high", 1);
            var low = RegisterScenePlayer(server, "prio-low", 1);
            high.NetworkPriority = 2f;
            low.NetworkPriority = 1f;
            ch.Clear();

            try
            {
                high.Score = 11;   // 등록 후 변경 — 델타 발생 (스냅샷은 등록 시점 값)
                low.Score = 22;

                server.TickReplication(server.SnapshotObjects(), 1.0);
                Assert.AreEqual(1, ch.Replicates.Count, "예산 8바이트 — 우선순위 1개만 전송");
                Assert.AreEqual(high.NetId, ch.Replicates[0].netId, "우선순위가 높은 오브젝트 먼저");

                server.TickReplication(server.SnapshotObjects(), 1.5);
                Assert.AreEqual(2, ch.Replicates.Count, "다음 틱에 연기된 델타 전송");
                Assert.AreEqual(low.NetId, ch.Replicates[1].netId, "연기분은 낮은 우선순위 오브젝트");

                server.TickReplication(server.SnapshotObjects(), 2.0);
                Assert.AreEqual(2, ch.Replicates.Count, "변경 없는 틱 — 전송분이 대기열에 남아 재전송되지 않는다");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(high.gameObject);
                UnityEngine.Object.DestroyImmediate(low.gameObject);
            }
        }

        [Test]
        public void 우선순위가_높은_오브젝트의_델타가_먼저_나간다()
        {
            var server = new NetworkServer();
            server.ReplicationBudgetPerTickBytes = 8;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            var a = RegisterScenePlayer(server, "prio-a", 1);
            var b = RegisterScenePlayer(server, "prio-b", 2);
            a.NetworkPriority = 1f;
            b.NetworkPriority = 3f;   // b가 더 높음
            a.Score = 10;
            b.Score = 20;
            ch.Clear();

            try
            {
                server.TickReplication(server.SnapshotObjects(), 1.0);
                Assert.AreEqual(1, ch.Replicates.Count);
                Assert.AreEqual(b.NetId, ch.Replicates[0].netId, "우선순위 3인 b가 먼저 전송");

                server.TickReplication(server.SnapshotObjects(), 1.5);
                Assert.AreEqual(a.NetId, ch.Replicates[1].netId, "a는 대기열 경유");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a.gameObject);
                UnityEngine.Object.DestroyImmediate(b.gameObject);
            }
        }

        [Test]
        public void 기아_지수가_쌓이면_낮은_우선순위도_전송된다()
        {
            var server = new NetworkServer();
            server.ReplicationBudgetPerTickBytes = 8;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            var high = RegisterScenePlayer(server, "starve-high", 1);   // 매 틱 변경 — 우선순위 2
            var low = RegisterScenePlayer(server, "starve-low", 1);     // 한 번만 변경 — 우선순위 1
            high.NetworkPriority = 2f;
            low.NetworkPriority = 1f;
            ch.Clear();

            try
            {
                low.Score = 99;                          // low의 유일한 변경 — 연기될 것
                for (int i = 0; i < 7; i++)
                {
                    double now = 1.0 + i * 0.5;
                    high.Score = 10 + i;                 // high는 매 틱 변경 — 예산을 매번 차지
                    server.TickReplication(server.SnapshotObjects(), now);
                }

                // 기아 지수가 쌓인 low의 연기 델타(99)가 결국 전송된다 — 스타베이션 방지
                bool lowConverged = false;
                foreach (var (_, payload) in ch.Replicates)
                    if (BitConverter.ToUInt32(payload, 0) == (1u << 0) && BitConverter.ToInt32(payload, 4) == 99)
                        lowConverged = true;
                Assert.IsTrue(lowConverged, $"기아 지수 성장으로 낮은 우선순위도 7틱 내 전송 (전송 {ch.Replicates.Count}건)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(high.gameObject);
                UnityEngine.Object.DestroyImmediate(low.gameObject);
            }
        }

        [Test]
        public void 예산_0은_무제한으로_기존_동작을_유지한다()
        {
            var server = new NetworkServer();   // 기본 예산 0 = 무제한
            Assert.AreEqual(0, server.ReplicationBudgetPerTickBytes);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            var high = RegisterScenePlayer(server, "prio-unl1", 1);
            var low = RegisterScenePlayer(server, "prio-unl2", 2);
            ch.Clear();

            try
            {
                high.Score = 10;
                low.Score = 20;
                server.TickReplication(server.SnapshotObjects(), 1.0);
                Assert.AreEqual(2, ch.Replicates.Count, "예산 0 — 변경분 전부 즉시 전송");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(high.gameObject);
                UnityEngine.Object.DestroyImmediate(low.gameObject);
            }
        }

        [Test]
        public void 서로소_필드_창의_연기_델타는_모두_보존된다()
        {
            var server = new NetworkServer();
            server.ReplicationBudgetPerTickBytes = 8;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            var x = RegisterScenePlayer(server, "prio-x", 1);   // 우선순위 1 — 계속 연기
            var y = RegisterScenePlayer(server, "prio-y", 1);   // 우선순위 2 — 예산 선점
            y.NetworkPriority = 2f;
            ch.Clear();

            try
            {
                // 틱1 — X는 Score 창이 연기되고
                x.Score = 50;
                y.Score = 10;
                server.TickReplication(server.SnapshotObjects(), 1.0);
                Assert.AreEqual(1, ch.Replicates.Count);

                // 틱2 — X의 다른 필드(TeamId) 창이 또 연기된다
                x.TeamId = 7;
                y.Score = 11;
                server.TickReplication(server.SnapshotObjects(), 1.5);

                // 이후 틱 — 대기열 FIFO 배출로 두 창 모두 전송 (어느 쪽도 유실 없음)
                server.TickReplication(server.SnapshotObjects(), 2.0);
                server.TickReplication(server.SnapshotObjects(), 2.5);

                bool scoreSeen = false, teamSeen = false;
                foreach (var (_, payload) in ch.Replicates)
                {
                    uint mask = BitConverter.ToUInt32(payload, 0);
                    if ((mask & (1u << 0)) != 0) scoreSeen = true;    // Score
                    if ((mask & (1u << 2)) != 0) teamSeen = true;     // TeamId
                }
                Assert.IsTrue(scoreSeen, "Score 창 보존");
                Assert.IsTrue(teamSeen, "TeamId 창 보존 — 서로소 델타 창 유실 없음");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(x.gameObject);
                UnityEngine.Object.DestroyImmediate(y.gameObject);
            }
        }

        [Test]
        public void 예산보다_큰_단일_델타는_최상위에서_강제_전송된다()
        {
            var server = new NetworkServer();
            server.ReplicationBudgetPerTickBytes = 8;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            var x = RegisterScenePlayer(server, "prio-big", 1);
            ch.Clear();

            try
            {
                // Score+TeamId 동시 변경 — others 페이로드 12바이트 > 예산 8
                x.Score = 30;
                x.TeamId = 7;

                server.TickReplication(server.SnapshotObjects(), 1.0);
                Assert.AreEqual(1, ch.Replicates.Count, "단일 델타가 예산을 넘어도 기아하지 않게 강제 전송");
                uint mask = BitConverter.ToUInt32(ch.Replicates[0].payload, 0);
                Assert.AreEqual((1u << 0) | (1u << 2), mask, "두 필드가 한 델타로 온전히 전송");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(x.gameObject);
            }
        }

        private sealed class RecordingChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal readonly List<(ulong netId, byte[] payload)> Replicates = new();

            public void Clear() => Replicates.Clear();

            public void SendWelcome(long connId) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload)
            {
                if (payload != null && payload.Length > 0)
                    Replicates.Add((netId, payload));
            }

            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) { }
        }
    }
}
