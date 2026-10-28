using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// AUDIT-20261027-F11/F12 — Core invariant regressions driven by the audit probe
    /// (.pi-glla/audit-loop/core-probe, evidence/core-invariants-before-validated.log).
    /// F11: a faulted serializer view must neither grow the deferred queue past the cap (keep-BOTH bypass) nor
    /// discard the fresh candidate's payload for the non-faulted view (partial-success wipe) — receivers of every
    /// receive group converge after recovery without a new value change.
    /// F12: a saturated main queue must not orphan the one-shot detach cleanup (dead owner, player mapping,
    /// exactly-once lifecycle callback), and connection admission at the same saturation point must be an explicit
    /// refusal with no residue (this is what bounds the reserved cleanup FIFO).
    /// Engine-free fixtures mirror the probe exactly: plain objects + a hand-written handler whose WriteFull
    /// faults under control, driven through the real TickReplication/DetachConnection paths.
    /// </summary>
    public sealed class CoreInvariantRegressionTests
    {
        private int _prevCapacity;
        private Action<Exception> _prevFault;
        private List<Exception> _faults;

        [SetUp]
        public void CaptureEnvironment()
        {
            _prevCapacity = UniNetEnvironment.MainQueueCapacity;
            _prevFault = UniNetEnvironment.LogFault;
            _faults = new List<Exception>();
            UniNetEnvironment.LogFault = _faults.Add;   // injected faults are expected — collected so tests can prove the fault path ran
            UniNetTypeRegistry.Register(typeof(FaultyThing), new FaultyHandler());
        }

        [TearDown]
        public void RestoreEnvironment()
        {
            UniNetEnvironment.MainQueueCapacity = _prevCapacity;
            UniNetEnvironment.LogFault = _prevFault;
            UniNetEnvironment.PumpMain();   // drain anything a failing assert left behind — no cross-test leakage
        }

        private static (NetworkServer Server, FaultyThing A, FaultyThing B, ProbeChannel Owner, ProbeChannel Other) Setup()
        {
            UniNetEnvironment.PumpMain();
            var server = new NetworkServer();
            var a = new FaultyThing { Priority = 10000 };
            var b = new FaultyThing { Priority = 1 };
            server.RegisterSceneObject(91100, new object[] { a });
            server.RegisterSceneObject(91200, new object[] { b });
            var owner = new ProbeChannel();
            var other = new ProbeChannel();
            Assert.AreNotEqual(0, server.AttachConnection(owner), "정상 큐에서 입장은 수락된다");
            server.OnClientVersionDeclared(owner.UniNetConnId, server.ExpectedProtocolVersion);
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.AttachConnection(other), "정상 큐에서 입장은 수락된다");
            server.OnClientVersionDeclared(other.UniNetConnId, server.ExpectedProtocolVersion);
            UniNetEnvironment.PumpMain();
            server.SetOwner(91100, owner.UniNetConnId);
            server.SetOwner(91200, owner.UniNetConnId);
            owner.Values.Clear();
            other.Values.Clear();
            return (server, a, b, owner, other);
        }

        private static int LastValue(ProbeChannel ch, ulong netId)
            => ch.Values.LastOrDefault(x => x.Id == netId).Value;

        private static ProbeChannel AddPeer(NetworkServer server)
        {
            var channel = new ProbeChannel();
            Assert.AreNotEqual(0, server.AttachConnection(channel));
            server.OnClientVersionDeclared(channel.UniNetConnId, server.ExpectedProtocolVersion);
            UniNetEnvironment.PumpMain();
            channel.Values.Clear();
            channel.Entries.Clear();
            return channel;
        }

        private void RunFaultedOverload(int failureMode, string label, int connectionCount, Delivery delivery)
        {
            var (server, a, b, owner, other) = Setup();
            var peers = new List<ProbeChannel> { owner, other };
            while (peers.Count < connectionCount) peers.Add(AddPeer(server));
            foreach (var peer in peers) { peer.Values.Clear(); peer.Entries.Clear(); }
            b.ReplicationDelivery = delivery;
            server.MaxDeferredDeltas = 2;
            ProbeChannel removed = null, excluded = null;
            try
            {
                server.ReplicationBudgetPerTickBytes = 4;   // A spends the budget; B stays deferred
                b.FailureMode = failureMode;
                int largest = 0;
                for (int tick = 1; tick <= 12; tick++)
                {
                    a.Value = tick;
                    b.Value = tick;
                    server.TickReplication(server.SnapshotObjects(), tick * 0.1);
                    largest = Math.Max(largest, server.DeferredSendCount);
                    if (tick == 1 && connectionCount > 64)
                    {
                        // Shift indices under an existing recipient snapshot, exclude an overflow-word peer, then
                        // append a new ID. Forced originals must not confuse IDs, reuse pooled bits, or leak relevance.
                        removed = other;
                        server.DetachConnection(removed);
                        UniNetEnvironment.PumpMain();
                        excluded = peers[65];
                        b.Excluded.Add(excluded.UniNetConnId);
                        peers.Add(AddPeer(server));
                    }
                }
                Assert.LessOrEqual(largest, 2, $"{label}: 장애 경로에서도 상한 2를 지킨다");
                Assert.Greater(_faults.Count, 0, $"{label}: injected serializer fault actually ran");
                server.ReplicationBudgetPerTickBytes = 0;
                b.FailureMode = 0;   // recover without another value mutation
                server.TickReplication(server.SnapshotObjects(), 5.0);
                foreach (var peer in peers)
                {
                    if (peer == removed || peer == excluded)
                    {
                        Assert.IsFalse(peer.Values.Any(x => x.Id == 91200), "해제·현재 비관련 수신자에게 낡은 델타를 보내지 않는다");
                        continue;
                    }
                    Assert.AreEqual(b.Value, LastValue(peer, 91200), $"{label}: conn {peer.UniNetConnId} converges without another mutation");
                    Assert.IsTrue(peer.Entries.Where(x => x.Id == 91200).All(x => x.Mode == DRPC.RpcDeliveryMode.ReliableOrdered),
                        "강제 배출 원본과 보존된 후속 원본은 같은 reliable 스트림을 탄다");
                }
            }
            finally { server.MaxDeferredDeltas = NetworkServer.DefaultMaxDeferredDeltas; }
        }

        [TestCase(2, Delivery.ReliableOrdered)]
        [TestCase(66, Delivery.ReliableOrdered)]
        [TestCase(66, Delivery.Unreliable)]
        public void 양측_전체상태_장애시에도_연기_큐는_상한을_지키고_회복후_수렴한다(int peers, Delivery delivery)
            => RunFaultedOverload(1, "both-full-fault", peers, delivery);

        [TestCase(2, Delivery.ReliableOrdered)]
        [TestCase(66, Delivery.ReliableOrdered)]
        [TestCase(66, Delivery.Unreliable)]
        public void 소유자뷰만_장애인_부분실패는_새_owner_델타를_버리지_않는다(int peers, Delivery delivery)
            => RunFaultedOverload(2, "owner-only-full-fault", peers, delivery);

        [TestCase(20000f)]
        [TestCase(0.01f)]
        public void 우선순위_변경과_다른키_혼합도_동일키_이전델타를_추월하지_않는다(float priority)
        {
            var (server, a, b, owner, other) = Setup();
            var c = new FaultyThing { Priority = 5000 };
            server.RegisterSceneObject(91300, new object[] { c });
            server.ReplicationBudgetPerTickBytes = 4;
            a.Value = b.Value = 1;
            server.TickReplication(server.SnapshotObjects(), 0.1);
            Assert.AreEqual(1, server.DeferredSendCount, "B의 이전 delta가 실제로 큐에 남아 있다");
            a.Value = b.Value = c.Value = 2;
            b.Priority = priority;
            server.ReplicationBudgetPerTickBytes = 0;
            server.TickReplication(server.SnapshotObjects(), 0.2);
            Assert.AreEqual(2, LastValue(owner, 91200));
            Assert.AreEqual(2, LastValue(other, 91200));
            CollectionAssert.AreEqual(new[] { 1, 2 }, owner.Values.Where(x => x.Id == 91200).Select(x => x.Value),
                "새 delta는 이전 queued delta 이후에 전송되어 권위값을 과거값으로 되돌리지 않는다");
        }

        [TestCase(Delivery.ReliableOrdered)]
        [TestCase(Delivery.Unreliable)]
        public void 실제_FastArray_원본은_전체직렬화_장애중에도_순서대로_재생된다(Delivery delivery)
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            var real = UniNetTypeRegistry.Find(typeof(FastArrayTank));
            var wrapper = new FaultingFullHandler(real);
            UniNetTypeRegistry.Register(typeof(FastArrayTank), wrapper);
            FastArrayTank a = null, b = null, remote = null;
            try
            {
                var server = new NetworkServer();
                a = ReplicationTestSupport.RegisterScene<FastArrayTank>(server, "f11-fast-a");
                b = ReplicationTestSupport.RegisterScene<FastArrayTank>(server, "f11-fast-b");
                remote = new UnityEngine.GameObject("f11-fast-remote").AddComponent<FastArrayTank>();
                real.InitClientSnapshot(remote);
                a.NetworkPriority = 10000;
                b.NetworkPriority = 1;
                a.NetworkUpdateFrequencyHz = b.NetworkUpdateFrequencyHz = 0;
                a.NetworkReplicationDelivery = b.NetworkReplicationDelivery = delivery;
                var channel = AddPeer(server);
                void ApplyB()
                {
                    foreach (var entry in channel.Entries.Where(e => e.Id == b.NetId))
                    {
                        var reader = new global::MessageProtocol.Serialize.MessageBufferReader(entry.Payload);
                        real.ApplyDelta(remote, ref reader);   // real generated FastArray applier, not a copied op model
                        Assert.AreEqual(0, reader.Remaining);
                    }
                }
                a.Scores.Add(0);
                b.Scores.Add(0);
                server.TickReplication(server.SnapshotObjects(), 0);
                int payloadSize = channel.Entries.First(e => e.Id == a.NetId).Payload.Length;
                Assert.Greater(payloadSize, 0);
                ApplyB();   // the measured initial op is part of the real receiver history
                channel.Entries.Clear();
                server.ReplicationBudgetPerTickBytes = payloadSize;
                wrapper.FailingInstance = b;
                for (int tick = 1; tick <= 12; tick++)
                {
                    a.Scores.Add(tick);
                    b.Scores.Add(tick);
                    server.TickReplication(server.SnapshotObjects(), tick * 0.1);
                    Assert.LessOrEqual(server.DeferredSendCount, 2);
                }
                Assert.Greater(wrapper.Faults, 0);
                Assert.IsTrue(channel.Entries.Any(e => e.Id == b.NetId), "예산 초과 원본이 실제로 배출되었다");
                Assert.IsTrue(channel.Entries.Where(e => e.Id == b.NetId).All(e => e.Mode == DRPC.RpcDeliveryMode.ReliableOrdered));
                Assert.DoesNotThrow(ApplyB, "유효 index-op은 전체 직렬화 장애 중에도 앞선 op 뒤에만 적용된다");
                channel.Entries.Clear();
                wrapper.FailingInstance = null;
                server.ReplicationBudgetPerTickBytes = 0;
                server.TickReplication(server.SnapshotObjects(), 5.0);
                ApplyB();
                CollectionAssert.AreEqual(Enumerable.Range(0, 13), remote.Scores,
                    "수신자는 값 재변경 없이 최신 FastArray 전체와 동일한 상태로 수렴한다");
            }
            finally
            {
                UniNetTypeRegistry.Register(typeof(FastArrayTank), real);
                if (remote != null) UnityEngine.Object.DestroyImmediate(remote.gameObject);
                if (a != null) UnityEngine.Object.DestroyImmediate(a.gameObject);
                if (b != null) UnityEngine.Object.DestroyImmediate(b.gameObject);
            }
        }

        private sealed class FaultingFullHandler : UniNetReplicationHandler
        {
            private readonly UniNetReplicationHandler _inner;
            public object FailingInstance;
            public int Faults;
            public FaultingFullHandler(UniNetReplicationHandler inner)
            {
                _inner = inner;
                HasFields = inner.HasFields;
                MethodId = inner.MethodId;
            }
            public override void InitSnapshot(NetworkServer.SubObjectEntry sub) => _inner.InitSnapshot(sub);
            public override void InitClientSnapshot(object instance) => _inner.InitClientSnapshot(instance);
            public override (byte[] Owner, byte[] Others) CompareAndWriteDelta(NetworkServer.SubObjectEntry sub) => _inner.CompareAndWriteDelta(sub);
            public override byte[] WriteFull(object instance, bool isOwner)
            {
                if (ReferenceEquals(instance, FailingInstance))
                {
                    Faults++;
                    throw new InvalidOperationException("Injected real FastArray full-write fault");
                }
                return _inner.WriteFull(instance, isOwner);
            }
            public override void ApplyDelta(object instance, ref global::MessageProtocol.Serialize.MessageBufferReader reader)
                => _inner.ApplyDelta(instance, ref reader);
        }

        [Test]
        public void 병합불가_배출_도중_전송예외도_이탈항목의_풀배열을_남기지_않는다()
        {
            var (server, a, b, owner, _) = Setup();
            while (server.SnapshotConnections().Count < 66) AddPeer(server);
            a.PayloadPadding = b.PayloadPadding = 600;
            b.FailureMode = 1;
            server.ReplicationBudgetPerTickBytes = 604;
            a.Value = b.Value = 1;
            server.TickReplication(server.SnapshotObjects(), 0.1);
            var queueField = typeof(NetworkServer).GetField("_queue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object Held() => ((System.Collections.IEnumerable)queueField.GetValue(server)).Cast<object>().Single();
            object Overflow(object entry) => entry.GetType().GetField("RelevantOverflow").GetValue(entry);
            object old = Held();
            Assert.IsNotNull(Overflow(old), "66 peers exercise owned pooled overflow words");
            owner.ThrowOnNextBatch = true;
            a.Value = b.Value = 2;
            Assert.Throws<InvalidOperationException>(() => server.TickReplication(server.SnapshotObjects(), 0.2),
                "A와 오래된 B가 MTU를 넘겨 병합실패 배출 내부에서 실제 중간 flush가 실패한다");
            Assert.IsNull(Overflow(old), "큐를 떠난 항목은 채널 예외에도 풀 소유권을 반납한다");
            object retained = Held();
            Assert.AreNotSame(old, retained);
            Assert.IsNotNull(Overflow(retained), "최신 보존 항목의 overflow 비트는 아직 큐 소유다");
            server.ReplicationBudgetPerTickBytes = 0;
            b.FailureMode = 0;
            server.TickReplication(server.SnapshotObjects(), 0.3);
            Assert.AreEqual(2, LastValue(owner, 91200), "송신이 회복되면 보존된 최신 값은 재변경 없이 전달된다");
            Assert.IsNull(Overflow(retained));
            Assert.AreEqual(0, server.DeferredSendCount);
        }

        [Test]
        public void 포화된_메인큐에서_detach_정리는_예약_FIFO로_정확히_한번_보장된다()
        {
            var (server, _, _, owner, other) = Setup();
            server.SetPlayerObject(owner.UniNetConnId, 91100);
            int events = 0, callbackThread = 0;
            int mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var order = new List<string>();
            server.ClientDisconnected += id =>
            {
                if (id != owner.UniNetConnId) return;
                events++;
                callbackThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                order.Add("cleanup");
            };
            UniNetEnvironment.MainQueueCapacity = 2;
            try
            {
                long reservedBefore = UniNetEnvironment.ReservedCleanupCount;
                UniNetEnvironment.QueueOnMain(() => order.Add("first"));
                UniNetEnvironment.QueueOnMain(() => order.Add("second"));
                var detach = Task.Run(() => server.DetachConnection(owner));
                Assert.IsTrue(detach.Wait(TimeSpan.FromSeconds(5)), "네트워크 스레드 detach는 메인을 기다리며 블로킹하지 않는다");
                Assert.AreEqual(0, events, "네트워크 스레드에서 Unity/게임 콜백을 인라인 실행하지 않는다");
                Assert.AreEqual(3, UniNetEnvironment.PendingMainActions, "관측 깊이는 일반 2개와 예약 1개를 포함한다");
                UniNetEnvironment.PumpMain();
                Assert.AreEqual(mainThread, callbackThread, "정리는 펌프를 호출한 메인 스레드에서 실행한다");
                CollectionAssert.AreEqual(new[] { "first", "second", "cleanup" }, order, "일반 FIFO 후 예약 FIFO 순서를 보존한다");

                Assert.AreEqual(other.UniNetConnId, server.GetOwner(91100), "포화 큐에서도 죽은 소유자는 재배정된다 (dead-owner 불변식)");
                Assert.AreEqual(other.UniNetConnId, server.GetOwner(91200), "두 객체 모두 재배정된다");
                Assert.AreEqual(0, server.GetPlayerObject(owner.UniNetConnId), "플레이어 매핑도 정리된다");
                Assert.AreEqual(1, events, "연결 해제 콜백은 정확히 한 번 발화한다");
                Assert.AreEqual(1, UniNetEnvironment.ReservedCleanupCount - reservedBefore,
                    "거부된 cleanup은 예약 FIFO로 입장했다 (일반 큐 거부 → 보장 슬롯)");
                Assert.AreEqual(UniNetEnvironment.ReservedCleanupCount, UniNetEnvironment.ReservedCleanupDrainedCount,
                    "예약 cleanup은 드레인되어 깊이 0으로 수렴한다");

                server.DetachConnection(owner);   // repeated detach — bounded, still one cleanup
                UniNetEnvironment.PumpMain();
                Assert.AreEqual(1, events, "반복 detach는 추가 콜백을 내지 않는다 (일회성 보장)");
                Assert.AreEqual(0, UniNetEnvironment.PendingMainActions);
            }
            finally
            {
                UniNetEnvironment.MainQueueCapacity = _prevCapacity;
                UniNetEnvironment.PumpMain();
            }
        }

        [Test]
        public void 정상작업_예외와_연결_churn은_예약정리를_무한히_늘리지_못한다()
        {
            UniNetEnvironment.PumpMain();
            var server = new NetworkServer();
            UniNetEnvironment.MainQueueCapacity = 2;
            long before = UniNetEnvironment.ReservedCleanupCount - UniNetEnvironment.ReservedCleanupDrainedCount;
            int accepted = 0;
            for (int i = 0; i < 12; i++)
            {
                var channel = new ProbeChannel();
                long id = server.AttachConnection(channel);
                if (id == 0)
                {
                    Assert.AreEqual(0, channel.UniNetConnId, "예약 적체 중 거부도 등록·채널 ID를 남기지 않는다");
                    continue;
                }
                accepted++;
                var expected = new InvalidOperationException("expected normal work fault");
                UniNetEnvironment.QueueOnMain(() => throw expected);
                server.DetachConnection(channel);
                Assert.AreSame(expected, Assert.Throws<InvalidOperationException>(() => UniNetEnvironment.PumpMain()),
                    "기존 펌프의 예외 전파 계약을 바꾸지 않는다");
            }
            Assert.AreEqual(1, accepted, "일반 큐에 빈자리가 나도 예약 정리가 남아 있으면 신규 입장은 막힌다");
            Assert.AreEqual(1, UniNetEnvironment.ReservedCleanupCount - UniNetEnvironment.ReservedCleanupDrainedCount - before,
                "예외와 반복 접속이 과거 연결 수에 비례한 무한 예약 큐를 만들지 않는다");
            Assert.AreEqual(0, server.SnapshotConnections().Count);
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.AttachConnection(new ProbeChannel()), "예약 드레인 뒤에는 입장이 다시 열린다");
            UniNetEnvironment.PumpMain();
        }

        [Test]
        public void 포화_detach도_유예토큰과_플레이어_재부착을_보존한다()
        {
            var (server, _, _, owner, _) = Setup();
            server.SessionGraceSeconds = 30;
            server.SetPlayerObject(owner.UniNetConnId, 91200);
            string token = server.SnapshotConnections().Single(c => c.ConnId == owner.UniNetConnId).SessionToken;
            UniNetEnvironment.MainQueueCapacity = 2;
            UniNetEnvironment.QueueOnMain(() => { });
            UniNetEnvironment.QueueOnMain(() => { });
            server.DetachConnection(owner);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(owner.UniNetConnId, server.GetOwner(91200), "유예 기간에는 소유권을 임의 입양하지 않는다");
            var replacement = AddPeer(server);
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(token, replacement.UniNetConnId));
            Assert.AreEqual(replacement.UniNetConnId, server.GetOwner(91200));
            Assert.AreEqual(91200ul, server.GetPlayerObject(replacement.UniNetConnId));
            Assert.AreEqual(0ul, server.GetPlayerObject(owner.UniNetConnId));
        }

        [Test]
        public void 포화된_메인큐에서_새_연결_입장은_명시적으로_거부되고_잔존이_없다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.MainQueueCapacity = 2;
            try
            {
                UniNetEnvironment.QueueOnMain(() => { });
                UniNetEnvironment.QueueOnMain(() => { });   // saturate the normal queue
                long droppedBefore = UniNetEnvironment.DroppedMainActions;
                var rejected = new ProbeChannel();
                long connId = server.AttachConnection(rejected);

                Assert.AreEqual(0, connId, "입장 거부는 0 반환으로 명시된다");
                Assert.AreEqual(0, server.SnapshotConnections().Count, "거부된 연결은 등록 잔존이 없다");
                Assert.IsFalse(server.IsVersionApproved(rejected.UniNetConnId), "승인 인덱스에도 잔존이 없다");
                Assert.IsFalse(rejected.Welcomed, "Welcome도 발송되지 않았다");
                Assert.Greater(UniNetEnvironment.DroppedMainActions, droppedBefore, "거부는 드롭 카운터로 관측된다");
            }
            finally
            {
                UniNetEnvironment.MainQueueCapacity = _prevCapacity;
                UniNetEnvironment.PumpMain();
            }
        }
    }

    /// <summary>Engine-free replicated object — FailureMode injects WriteFull faults (1 = both views, 2 = owner view only).</summary>
    internal sealed class FaultyThing : IUniNetReplicationPolicy
    {
        public int Value;
        public int FailureMode;
        public int PayloadPadding;
        public float Priority;
        public Delivery ReplicationDelivery = Delivery.ReliableOrdered;
        public readonly HashSet<long> Excluded = new();
        public float NetworkPriority => Priority;
        public float NetworkUpdateFrequencyHz => 0;
        public Delivery NetworkReplicationDelivery => ReplicationDelivery;
        public float NetworkCullDistance => 0;
        public bool IsNetworkDormant => false;
        public bool IsNetworkRelevant(long viewerConnId) => !Excluded.Contains(viewerConnId);
    }

    /// <summary>Hand-written handler — CompareDelta succeeds, WriteFull faults under control (the public custom-handler extension contract).</summary>
    internal sealed class FaultyHandler : UniNetReplicationHandler
    {
        public FaultyHandler() { HasFields = true; MethodId = 91001; }
        public override void InitSnapshot(NetworkServer.SubObjectEntry sub) => sub.Snapshot = new object[] { ((FaultyThing)sub.Instance).Value };
        public override void InitClientSnapshot(object instance) { }
        public override (byte[] Owner, byte[] Others) CompareAndWriteDelta(NetworkServer.SubObjectEntry sub)
        {
            int value = ((FaultyThing)sub.Instance).Value;
            if ((int)sub.Snapshot[0] == value) return (null, null);
            sub.Snapshot[0] = value;
            byte[] payload = Encode((FaultyThing)sub.Instance);
            return (payload, payload);
        }
        public override byte[] WriteFull(object instance, bool isOwner)
        {
            var thing = (FaultyThing)instance;
            if (thing.FailureMode == 1 || (thing.FailureMode == 2 && isOwner))
                throw new InvalidOperationException("Injected full-state serializer fault");
            return Encode(thing);
        }
        private static byte[] Encode(FaultyThing thing)
        {
            var payload = new byte[4 + thing.PayloadPadding];
            BitConverter.GetBytes(thing.Value).CopyTo(payload, 0);
            return payload;
        }
        public override void ApplyDelta(object instance, ref MessageProtocol.Serialize.MessageBufferReader reader)
            => ((FaultyThing)instance).Value = reader.ReadInt32();
    }

    /// <summary>Recording channel — decodes batched and single replicates into (netId, value) pairs.</summary>
    internal sealed class ProbeChannel : IUniNetSystemChannel
    {
        public long UniNetConnId { get; set; }
        public bool Welcomed;
        public bool ThrowOnNextBatch;
        public List<(ulong Id, int Value)> Values = new();
        public List<(ulong Id, byte[] Payload, DRPC.RpcDeliveryMode Mode)> Entries = new();
        public void SendWelcome(long connId, string serverVersion, string sessionToken) { Welcomed = true; }
        public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
        private void Record(ulong netId, byte[] payload, DRPC.RpcDeliveryMode mode)
        {
            Entries.Add((netId, payload, mode));
            if (payload?.Length >= 4) Values.Add((netId, BitConverter.ToInt32(payload, 0)));
        }
        public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload)
            => Record(netId, payload, DRPC.RpcDeliveryMode.ReliableOrdered);
        public void SendReplicateBatch(byte[] blob, DRPC.RpcDeliveryMode mode)
        {
            if (ThrowOnNextBatch)
            {
                ThrowOnNextBatch = false;
                throw new InvalidOperationException("Injected batch send fault");
            }
            var reader = new MessageProtocol.Serialize.MessageBufferReader(blob);
            int count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                ulong id = reader.ReadUInt64();
                byte subId = reader.ReadByte();
                int method = reader.ReadInt32();
                int length = reader.ReadInt32();
                var data = reader.ReadBytes(length);
                Record(id, data.ToArray(), mode);
            }
        }
        public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
        public void SendDestroy(ulong netId) { }
        public void SendTimeSync(double serverTime) { }
        public Task SendDisconnectNoticeAsync(UniNetDisconnectReason reason) => Task.CompletedTask;
        public void SendLoadScene(string sceneName) { }
        public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
    }
}
