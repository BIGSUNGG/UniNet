using System;
using NUnit.Framework;
using DRPC;
using UniNet.Core;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// Batched replication (wire 13) + per-object delivery policy + quiescence resend (no networking).
    /// Unreliable subs ride the Sequenced batch; a lost final delta is recovered by a one-shot reliable
    /// full-state resend once the sub has been quiet past the resend delay.
    /// </summary>
    public sealed class ReplicationDeliveryBatchTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        private static (NetworkServer server, RecordingChannel ch, SpawnablePlayer player) Setup(string name)
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, name);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            ch.Clear();
            return (server, ch, player);
        }

        [Test]
        public void 기본_정책_델타는_신뢰_배치로_전송된다()
        {
            var (server, ch, player) = Setup("deliv-1");
            player.Score = 42;
            server.TickReplication(server.SnapshotObjects(), 1.0);

            Assert.AreEqual(1, ch.Batches.Count, "한 틱의 델타들은 커넥션당 배치 1개로 묶인다");
            Assert.AreEqual(RpcDeliveryMode.ReliableOrdered, ch.Batches[0].mode);
            Assert.AreEqual(1, ch.Batches[0].entries.Count);
            Assert.AreEqual(42, BitConverter.ToInt32(ch.Batches[0].entries[0].payload, 4), "배치 엔트리 페이로드는 기존 델타 형식과 동일하다");
        }

        [Test]
        public void 비신뢰_정책_오브젝트는_시퀀스_배치로_전송된다()
        {
            var (server, ch, player) = Setup("deliv-2");
            player.NetworkReplicationDelivery = Delivery.Unreliable;
            player.Score = 7;
            server.TickReplication(server.SnapshotObjects(), 1.0);

            Assert.AreEqual(1, ch.Batches.Count);
            Assert.AreEqual(RpcDeliveryMode.Sequenced, ch.Batches[0].mode, "Unreliable 정책은 유실 허용·순서 보장(Sequenced) 배치로 나간다");
        }

        [Test]
        public void 배치_상한을_넘는_틱은_배치를_분할한다()
        {
            var server = new NetworkServer();
            var players = new SpawnablePlayer[45];
            for (int i = 0; i < players.Length; i++)
                players[i] = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, $"split-{i}");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            ch.Clear();

            for (int i = 0; i < players.Length; i++) players[i].Score = i + 2;   // 초기값(1)과 다르게 — entry = 8B payload + 17B header = 25B → 40개가 정확히 1000B
            server.TickReplication(server.SnapshotObjects(), 1.0);

            Assert.AreEqual(2, ch.Batches.Count, "배치 바이트 상한(1000B)을 넘으면 같은 틱 안에서 배치가 분할된다");
            Assert.AreEqual(40, ch.Batches[0].entries.Count, "첫 배치는 상한까지 채운다 (25B × 40 = 1000B)");
            Assert.AreEqual(5, ch.Batches[1].entries.Count);
        }

        [Test]
        public void 같은_틱의_여러_델타는_배치_1개로_묶인다()
        {
            var (server, ch, p1) = Setup("batch-1");
            var p2 = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "batch-2");
            UniNetEnvironment.PumpMain();
            ch.Clear();

            p1.Score = 10;
            p2.Score = 20;
            server.TickReplication(server.SnapshotObjects(), 1.0);

            Assert.AreEqual(1, ch.Batches.Count, "같은 틱·같은 신뢰성의 델타들은 배치 1개로 묶인다 (메시지 수 절감)");
            Assert.AreEqual(2, ch.Batches[0].entries.Count);
        }

        [Test]
        public void 비신뢰_오브젝트가_조용해지면_최종_상태를_신뢰로_재전송한다()
        {
            var (server, ch, player) = Setup("quiet-1");
            player.NetworkReplicationDelivery = Delivery.Unreliable;

            player.Score = 5;
            server.TickReplication(server.SnapshotObjects(), 1.0);   // unreliable delta out
            server.TickReplication(server.SnapshotObjects(), 1.2);   // quiet 0.2s < 0.25s — no resend yet
            Assert.AreEqual(1, ch.Batches.Count);

            server.TickReplication(server.SnapshotObjects(), 1.3);   // quiet 0.3s ≥ 0.25s — one-shot reliable resend
            Assert.AreEqual(2, ch.Batches.Count);
            Assert.AreEqual(RpcDeliveryMode.ReliableOrdered, ch.Batches[1].mode, "정지 재전송은 유실되지 않는 신뢰 배치로 나간다");
            Assert.AreEqual(5, BitConverter.ToInt32(ch.Batches[1].entries[0].payload, 4), "재전송은 최종 상태(풀 상태)다");

            server.TickReplication(server.SnapshotObjects(), 2.0);
            Assert.AreEqual(2, ch.Batches.Count, "재전송은 조용한 구간당 1회다");
        }

        [Test]
        public void 신뢰_오브젝트는_정지_재전송이_없다()
        {
            var (server, ch, player) = Setup("quiet-2");   // default ReliableOrdered
            player.Score = 5;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            server.TickReplication(server.SnapshotObjects(), 2.0);
            Assert.AreEqual(1, ch.Batches.Count, "신뢰 스트림은 유실이 없어 정지 재전송이 필요 없다");
        }

        [Test]
        public void 휴면_플러시_델타는_신뢰_배치로_강제된다()
        {
            var (server, ch, player) = Setup("flush-1");
            player.NetworkReplicationDelivery = Delivery.Unreliable;
            UniNetEnvironment.SetServer(server);   // FlushNetworkDormancy marks the wake reliable via the environment
            try
            {
                player.NetworkDormant = true;
                player.Score = 99;
                server.TickReplication(server.SnapshotObjects(), 1.0);
                Assert.AreEqual(0, ch.Batches.Count, "휴면 중 — 전송 없음");

                player.FlushNetworkDormancy();
                server.TickReplication(server.SnapshotObjects(), 1.5);
                Assert.AreEqual(1, ch.Batches.Count);
                Assert.AreEqual(RpcDeliveryMode.ReliableOrdered, ch.Batches[0].mode, "플러시 델타는 유실 시 오브젝트가 잠긴 상태로 남으므로 신뢰로 강제된다");
                Assert.AreEqual(99, BitConverter.ToInt32(ch.Batches[0].entries[0].payload, 4));
            }
            finally
            {
                UniNetEnvironment.SetServer(null);
            }
        }
    }
}
