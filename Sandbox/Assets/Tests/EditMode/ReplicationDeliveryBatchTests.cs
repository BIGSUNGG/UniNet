using System;
using System.Linq;
using NUnit.Framework;
using DRPC;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

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

        // ── FR-02 — in-motion loss convergence ──
        // A delta dropped from the unreliable (Sequenced) stream never re-appears in later deltas (the snapshot advanced),
        // so while OTHER fields keep changing the periodic full-state baseline on the reliable batch is what converges.

        private static (NetworkServer server, RecordingChannel ch, T obj) SetupUnreliable<T>(string name) where T : NetworkBehaviour
        {
            var server = new NetworkServer();
            var obj = ReplicationTestSupport.RegisterScene<T>(server, name);
            obj.NetworkReplicationDelivery = Delivery.Unreliable;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            ch.Clear();
            return (server, ch, obj);
        }

        /// <summary>Applies every batch recorded since the cursor to a remote client instance — dropSequenced leaves the Sequenced batch unapplied (loss simulation).</summary>
        private static void Drain(RecordingChannel ch, UniNetReplicationHandler handler, object remote, ref int consumed, bool dropSequenced)
        {
            for (; consumed < ch.Batches.Count; consumed++)
            {
                var (mode, entries) = ch.Batches[consumed];
                if (dropSequenced && mode == RpcDeliveryMode.Sequenced) continue;   // loss simulation — this batch never arrives
                foreach (var (_, _, _, payload) in entries)
                {
                    if (payload.Length == 0) continue;
                    var r = new MessageProtocol.Serialize.MessageBufferReader(payload);
                    handler.ApplyDelta(remote, ref r);
                }
            }
        }

        [Test]
        public void 유실된_델타는_다른_필드가_계속_변해도_주기_기준선으로_수렴한다()
        {
            var (server, ch, duo) = SetupUnreliable<UnreliableDuo>("fr02-motion");
            var clientGo = new GameObject("fr02-remote");
            var remote = clientGo.AddComponent<UnreliableDuo>();
            var handler = UniNetTypeRegistry.Find(typeof(UnreliableDuo));
            handler.InitClientSnapshot(remote);
            try
            {
                int consumed = 0;
                duo.FieldA = 111;   // A changes ONCE — this delta will be lost
                server.TickReplication(server.SnapshotObjects(), 0.0);
                Drain(ch, handler, remote, ref consumed, dropSequenced: true);   // the Sequenced batch carrying A's delta is dropped

                for (int i = 1; i <= 30; i++)   // B keeps changing at 60 Hz — half a baseline interval
                {
                    duo.FieldB = i;
                    server.TickReplication(server.SnapshotObjects(), i / 60.0);
                    Drain(ch, handler, remote, ref consumed, dropSequenced: false);
                }
                Assert.AreEqual(0, remote.FieldA, "수렴 전 — 유실된 A는 이후 델타에 다시 나타나지 않는다 (스냅샷 전진)");
                Assert.AreEqual(30, remote.FieldB, "B는 계속 수신된다");

                for (int i = 31; i <= 90; i++)   // past the baseline interval — a reliable full-state baseline must have arrived
                {
                    duo.FieldB = i;
                    server.TickReplication(server.SnapshotObjects(), i / 60.0);
                    Drain(ch, handler, remote, ref consumed, dropSequenced: false);
                }
                Assert.AreEqual(111, remote.FieldA, "FR-02 — 유실된 A가 주기 기준선(신뢰 전체 상태)으로 수렴한다");
                Assert.AreEqual(90, remote.FieldB, "B는 최신 권위 값");
                Assert.GreaterOrEqual(ch.Batches.Count(b => b.mode == RpcDeliveryMode.ReliableOrdered), 1,
                    "수렴을 이끈 것은 신뢰 배치의 전체 상태 기준선이다");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clientGo);
                UnityEngine.Object.DestroyImmediate(duo.gameObject);
            }
        }

        [Test]
        public void 조용한_비신뢰_서브는_기준선_후_주기_재전송을_하지_않는다()
        {
            var (server, ch, duo) = SetupUnreliable<UnreliableDuo>("fr02-idle");
            var clientGo = new GameObject("fr02-idle-remote");
            var remote = clientGo.AddComponent<UnreliableDuo>();
            var handler = UniNetTypeRegistry.Find(typeof(UnreliableDuo));
            handler.InitClientSnapshot(remote);
            try
            {
                int consumed = 0;
                duo.FieldA = 5;   // one change — its Sequenced delta (applied) and the quiescence full-state resend (reliable) are all this sub should ever send
                server.TickReplication(server.SnapshotObjects(), 0.0);
                Drain(ch, handler, remote, ref consumed, dropSequenced: false);

                for (double t = 0.3; t <= 3.5; t += 0.1)   // quiet past the 0.25s quiescence delay and several 1.0s baseline intervals
                    server.TickReplication(server.SnapshotObjects(), t);
                Drain(ch, handler, remote, ref consumed, dropSequenced: false);

                Assert.AreEqual(5, remote.FieldA, "정지 재전송으로 수렴 (양성 유지)");
                Assert.AreEqual(1, ch.Batches.Count(b => b.mode == RpcDeliveryMode.ReliableOrdered),
                    "FR-02 게이트 — 조용한 서브는 퀴에센스 재전송 1회뿐, 주기 기준선은 무한 재전송하지 않는다");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clientGo);
                UnityEngine.Object.DestroyImmediate(duo.gameObject);
            }
        }

        [Test]
        public void FastArray_오파_델타_유실은_최신_전체_치환으로_수렴한다()
        {
            var (server, ch, tank) = SetupUnreliable<FastArrayTank>("fr02-fa");
            var clientGo = new GameObject("fr02-fa-remote");
            var remote = clientGo.AddComponent<FastArrayTank>();
            var handler = UniNetTypeRegistry.Find(typeof(FastArrayTank));
            handler.InitClientSnapshot(remote);
            try
            {
                int consumed = 0;
                tank.Scores.AddRange(new[] { 1, 2, 3 });   // element op delta (tag-0) — will be lost
                server.TickReplication(server.SnapshotObjects(), 0.0);
                Drain(ch, handler, remote, ref consumed, dropSequenced: true);

                for (int i = 1; i <= 30; i++)
                {
                    tank.Names = new[] { "n" + i };   // another field keeps changing (60 Hz)
                    server.TickReplication(server.SnapshotObjects(), i / 60.0);
                    Drain(ch, handler, remote, ref consumed, dropSequenced: false);
                }
                Assert.IsEmpty(remote.Scores, "수렴 전 — 유실된 op 델타는 자체 수렴이 불가능하다 (인덱스 기반)");
                Assert.AreEqual("n30", remote.Names[0], "Names 는 계속 수신");

                for (int i = 31; i <= 90; i++)
                {
                    tank.Names = new[] { "n" + i };
                    server.TickReplication(server.SnapshotObjects(), i / 60.0);
                    Drain(ch, handler, remote, ref consumed, dropSequenced: false);
                }
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, remote.Scores, "FR-02 — 유실 후 최신 전체(tag-1) 치환으로 수렴한다 (op 리플레이 아님)");
                Assert.AreEqual("n90", remote.Names[0], "Names 최신 값");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clientGo);
                UnityEngine.Object.DestroyImmediate(tank.gameObject);
            }
        }
    }
}
