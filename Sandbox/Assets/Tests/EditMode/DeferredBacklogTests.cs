using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// FR-06 — finite semantic-preserving replication backlog + explicit overflow.
    /// Sustained overload (production &gt; budget) must bound the deferred queue by coalescing per (NetId, SubId)
    /// into the LATEST full state, converge receivers (plain fields and FastArray collections), and an overflow past
    /// the cap must be EXPLICIT (force-send + counters), never a silent drop. The main queue keeps a finite capacity
    /// with an explicit drop fault.
    /// </summary>
    public sealed class DeferredBacklogTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        private static (NetworkServer server, RecordingChannel ch) Setup(NetworkServer server)
        {
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            ch.Clear();
            return (server, ch);
        }

        private static T MakeRemote<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            return go.AddComponent<T>();
        }

        private static void ApplyAll(RecordingChannel ch, ulong netId, UniNetReplicationHandler handler, object remote)
        {
            foreach (var (entryNetId, _, _, payload) in ch.Replicates)
            {
                if (entryNetId != netId || payload.Length == 0) continue;
                var r = new global::MessageProtocol.Serialize.MessageBufferReader(payload);
                handler.ApplyDelta(remote, ref r);
            }
        }

        [Test]
        public void 지속_과부하에서_연기_큐는_키_수에_묶이고_최신_권위_값으로_수렴한다()
        {
            var server = new NetworkServer();
            var a = ReplicationTestSupport.RegisterScene<UnreliableDuo>(server, "fr06-a");
            var b = ReplicationTestSupport.RegisterScene<UnreliableDuo>(server, "fr06-b");
            a.NetworkUpdateFrequencyHz = 0;   // produce a delta EVERY tick — full control of the overload
            b.NetworkUpdateFrequencyHz = 0;
            var (_, ch) = Setup(server);

            var remote = MakeRemote<UnreliableDuo>("fr06-remote-a");
            var handler = UniNetTypeRegistry.Find(typeof(UnreliableDuo));
            handler.InitClientSnapshot(remote);
            try
            {
                server.ReplicationBudgetPerTickBytes = 12;   // one two-int delta fits per tick — production outpaces the budget
                long coalescedBefore = server.DeferredCoalescedCount;
                for (int t = 0; t < 120; t++)   // the research repro: 120 overloaded ticks → DeferredSendCount grew to 120 (unbounded)
                {
                    a.FieldA = t; a.FieldB = t;
                    b.FieldA = t; b.FieldB = t;
                    server.TickReplication(server.SnapshotObjects(), t / 10.0);
                    Assert.LessOrEqual(server.DeferredSendCount, 2,
                        "연기 큐는 (NetId, SubId) 키 수에 묶인다 — 시간에 비례한 무한 축적이 아니다");
                }
                Assert.Greater(server.DeferredCoalescedCount, coalescedBefore,
                    "과부하 중 같은 키의 연기 항목은 최신 전체 상태로 대체 병합되었다");

                server.ReplicationBudgetPerTickBytes = 0;   // open the budget — the backlog must fully drain
                server.TickReplication(server.SnapshotObjects(), 100.0);
                Assert.AreEqual(0, server.DeferredSendCount, "예산 개방 틱에 적체는 완전히 드레인된다");

                ApplyAll(ch, a.NetId, handler, remote);
                Assert.AreEqual(119, remote.FieldA, "수신자는 최신 권위 값으로 수렴한다 (과거 값 아님)");
                Assert.AreEqual(119, remote.FieldB, "두 필드 모두 최신 권위 값");
            }
            finally
            {
                Object.DestroyImmediate(remote.gameObject);
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(b.gameObject);
            }
        }

        [Test]
        public void FastArray_변경도_적체_병합_후_최신_전체로_수렴한다()
        {
            var server = new NetworkServer();
            var tankA = ReplicationTestSupport.RegisterScene<FastArrayTank>(server, "fr06-fa-a");
            var tankB = ReplicationTestSupport.RegisterScene<FastArrayTank>(server, "fr06-fa-b");
            tankA.NetworkUpdateFrequencyHz = 0;
            tankB.NetworkUpdateFrequencyHz = 0;
            var (_, ch) = Setup(server);

            var remote = MakeRemote<FastArrayTank>("fr06-fa-remote");
            var handler = UniNetTypeRegistry.Find(typeof(FastArrayTank));
            handler.InitClientSnapshot(remote);
            try
            {
                // Measure one delta's real payload, then set the budget to exactly that — the first sub-object sends
                // each tick and the second defers (its size equals the budget, so no size>budget force-through).
                tankA.Scores.Add(0);
                tankA.Names = new[] { "n0" };
                server.TickReplication(server.SnapshotObjects(), 0.0);
                int payloadSize = 0;
                foreach (var (netId, _, _, payload) in ch.Replicates)
                    if (netId == tankA.NetId) payloadSize = payload.Length;
                Assume.That(payloadSize, Is.GreaterThan(0), "첫 델타 페이로드 관측 실패 — 픽스처/생성 코드 불일치");
                server.ReplicationBudgetPerTickBytes = payloadSize;
                // The measurement tick's payload is a real send a live client WOULD have received — apply it to the
                // remote BEFORE clearing, or every later Insert(t) op lands on a list missing element 0 (self-inflicted
                // loss, ArgumentOutOfRangeException in List.Insert — not a product defect).
                ApplyAll(ch, tankA.NetId, handler, remote);
                ch.Clear();

                for (int t = 1; t <= 60; t++)   // sustained overload — the queue holds at most one entry per tank
                {
                    tankA.Scores.Add(t);   // element op delta (tag-0) — under coalescing the retained entry becomes the latest FULL (tag-1)
                    tankB.Scores.Add(t);
                    tankA.Names = new[] { "a" + t };
                    tankB.Names = new[] { "b" + t };
                    server.TickReplication(server.SnapshotObjects(), t / 10.0);
                    Assert.LessOrEqual(server.DeferredSendCount, 2, "FastArray 과부하에서도 연기 큐는 키 수에 묶인다");
                }
                Assert.Greater(server.DeferredCoalescedCount, 0, "op 델타가 최신 전체 상태로 대체 병합되었다");

                server.ReplicationBudgetPerTickBytes = 0;
                server.TickReplication(server.SnapshotObjects(), 100.0);
                ApplyAll(ch, tankA.NetId, handler, remote);
                var expected = new List<int>();
                for (int t = 0; t <= 60; t++) expected.Add(t);
                CollectionAssert.AreEqual(expected, remote.Scores, "FastArray는 최신 전체(tag-1) 치환으로 수렴한다 (op 리플레이 의존 아님)");
                Assert.AreEqual("a60", remote.Names[0], "Names 도 최신 권위 값");
            }
            finally
            {
                Object.DestroyImmediate(remote.gameObject);
                Object.DestroyImmediate(tankA.gameObject);
                Object.DestroyImmediate(tankB.gameObject);
            }
        }

        [Test]
        public void 연기_큐_상한_도달_초과분은_강제_송신된다_조용한_누락이_아니다()
        {
            var server = new NetworkServer();
            server.MaxDeferredDeltas = 2;   // internal — cheap boundary for the regression
            try
            {
                var objs = new UnreliableDuo[4];
                for (int i = 0; i < objs.Length; i++)
                {
                    objs[i] = ReplicationTestSupport.RegisterScene<UnreliableDuo>(server, $"fr06-cap-{i}");
                    objs[i].NetworkUpdateFrequencyHz = 0;
                }
                var (_, ch) = Setup(server);

                server.ReplicationBudgetPerTickBytes = 12;   // one delta per tick — four candidates can never all fit
                for (int i = 0; i < objs.Length; i++) { objs[i].FieldA = i + 1; objs[i].FieldB = i + 1; }
                server.TickReplication(server.SnapshotObjects(), 1.0);

                Assert.AreEqual(2, server.DeferredSendCount, "연기 큐는 상한(2)에 묶인다");
                Assert.AreEqual(1, server.DeferredForcedSendCount, "상한 도달 후의 초과 후보는 강제 송신으로 관측된다");

                int sent = 0;
                foreach (var (netId, _, _, _) in ch.Replicates)
                    foreach (var o in objs)
                        if (o.NetId == netId) { sent++; break; }
                Assert.AreEqual(2, sent, "예산분 1건 + 강제 송신 1건이 실제 전송되었다 — 조용한 누락이 아니다");
            }
            finally
            {
                server.MaxDeferredDeltas = NetworkServer.DefaultMaxDeferredDeltas;
            }
        }

        [Test]
        public void 메인_큐_상한_초과는_명시적으로_관측된다()
        {
            int prevCapacity = UniNetEnvironment.MainQueueCapacity;
            var prevFault = UniNetEnvironment.LogFault;
            var faults = new List<System.Exception>();
            UniNetEnvironment.LogFault = faults.Add;
            UniNetEnvironment.MainQueueCapacity = 2;
            try
            {
                long droppedBefore = UniNetEnvironment.DroppedMainActions;
                int ran = 0;
                UniNetEnvironment.QueueOnMain(() => ran += 1);
                UniNetEnvironment.QueueOnMain(() => ran += 10);
                UniNetEnvironment.QueueOnMain(() => ran += 100);   // over the cap — dropped, but explicitly
                UniNetEnvironment.QueueOnMain(() => ran += 1000);   // over the cap — dropped, but explicitly

                Assert.AreEqual(2, UniNetEnvironment.PendingMainActions, "상한까지 적재된다");
                Assert.AreEqual(2, UniNetEnvironment.DroppedMainActions - droppedBefore, "초과 적재는 카운터로 명시 관측된다");
                Assert.AreEqual(2, faults.Count, "드롭은 LogFault로 노출된다 — 조용한 삼킴이 아니다");
                Assert.IsInstanceOf<System.InvalidOperationException>(faults[0]);

                UniNetEnvironment.PumpMain();
                Assert.AreEqual(11, ran, "살아남은 작업은 전량 드레인된다 (기존 drain 계약 불변)");
                Assert.AreEqual(0, UniNetEnvironment.PendingMainActions);
            }
            finally
            {
                UniNetEnvironment.MainQueueCapacity = prevCapacity;
                UniNetEnvironment.LogFault = prevFault;
            }
        }
    }
}
