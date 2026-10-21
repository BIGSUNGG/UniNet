using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// Scene transition synchronization (ADR-0035) — server broadcast, client report, and the all-ready gate.
    /// </summary>
    public sealed class SceneTransitionTests
    {
        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(null);
        }

        [Test]
        public void LoadScene가_전_연결에_브로드캐스트된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            chA.Clear(); chB.Clear();

            server.LoadScene("BattleScene");

            Assert.AreEqual(1, chA.LoadScenes.Count, "연결 A — 씬 지시 수신");
            Assert.AreEqual("BattleScene", chA.LoadScenes[0], "씬 식별자");
            Assert.AreEqual(1, chB.LoadScenes.Count, "연결 B — 씬 지시 수신");
            Assert.AreEqual("BattleScene", server.PendingScene, "서버 대기 씬");
        }

        [Test]
        public void 전원_보고_시_게이트가_발화한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            string? gateScene = null;
            server.AllClientsSceneLoaded += s => gateScene = s;

            server.LoadScene("Battle");
            server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
            Assert.IsNull(gateScene, "1명만 보고 — 게이트 미발화");
            Assert.IsTrue(server.IsClientSceneReady(chA.UniNetConnId), "A 준비");
            Assert.IsFalse(server.IsClientSceneReady(chB.UniNetConnId), "B 미준비");

            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");
            Assert.AreEqual("Battle", gateScene, "전원 보고 — 게이트 발화 (ADR-0035)");
            Assert.IsNull(server.PendingScene, "게이트 후 대기 해제");
        }

        [Test]
        public void 이씬_보고는_무시된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            string? gateScene = null;
            server.AllClientsSceneLoaded += s => gateScene = s;

            server.LoadScene("Battle");
            server.OnClientSceneLoaded(ch.UniNetConnId, "WrongScene");   // stale
            Assert.IsNull(gateScene, "이씬 보고 — 게이트 미발화");
            Assert.IsFalse(server.IsClientSceneReady(ch.UniNetConnId), "준비 미표시");
        }
    
        [Test]
        public void 후발_접속자도_진행_중_전환에_참여한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            chA.Clear();

            server.LoadScene("Battle");

            // Late joiner — AttachConnection sends Welcome + LoadScene
            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            // no Clear — the late-joiner test needs LoadScenes from the catchup SendLoadScene

            Assert.AreEqual(1, chB.LoadScenes.Count, "후발 접속자 — 씬 지시 수신 (ADR-0035)");
            Assert.AreEqual("Battle", chB.LoadScenes[0], "씬 식별자 일치");
        }

        // ── SCN-001/002/003 (ADR-0045) ────────────────────────────────────────

        private static long AttachApproved(NetworkServer server, RecordingChannel ch)
        {
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // real clients declare automatically
            return ch.UniNetConnId;
        }

        [Test]
        public void 완료된_전환_후발_접속자는_현재_씬을_받고_보고_후_캐치업된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-current");
            player.Score = 42;
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();   // no transition yet — full legacy post-approval
            server.LoadScene("Battle");
            server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");   // gate completes → _currentScene = Battle
            UniNetEnvironment.PumpMain();
            Assert.IsNull(server.PendingScene, "전환 완료");

            // the SCN-001 case: a joiner AFTER the transition completed still receives the current scene
            var chB = new RecordingChannel();
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, chB.LoadScenes.Count, "후발 접속자 — 완료된 전환의 씬 수신 (SCN-001)");
            Assert.AreEqual("Battle", chB.LoadScenes[0], "씬 식별자");
            Assert.AreEqual(0, ReplicatesOf(chB, player.NetId).Count, "SCN-002 — 보고 전 캐치업 유보");

            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");   // ready report (registration precedes it on real clients)
            UniNetEnvironment.PumpMain();
            var repls = ReplicatesOf(chB, player.NetId);
            Assert.GreaterOrEqual(repls.Count, 1, "보고 후 캐치업 실행 — 전체상태 유보 해제");

            // VALUE assertion (not a count): replay the caught-up payload through the REAL generated client
            // receiver onto a registered client instance — the full state APPLIES instead of being dropped (SCN-002)
            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            var clientGo = new UnityEngine.GameObject("scn-current-client");
            var clientPlayer = clientGo.AddComponent<SpawnablePlayer>();
            typeof(NetworkBehaviour).GetField("_sceneNetId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(clientPlayer, player.NetId);   // same scene netId — registration + lookup by the instance's NetId
            client.Register(player.NetId, new object[] { clientPlayer });
            foreach (var (id, sub, methodId, payload) in chB.Replicates)
            {
                if (id != player.NetId || payload.Length == 0) continue;
                var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
                w.WriteUInt64(id);
                w.WriteByte(sub);
                w.WriteBytes(payload);
                UniNetDispatch.ClientHandlers()[methodId].Handler(0, w.ToArray()).Wait(1000);
            }
            UniNetEnvironment.PumpMain();   // generated receivers apply replication on the main queue
            Assert.AreEqual(42, clientPlayer.Score, "전체상태 적용 — 값 도달 (폐기 아님, SCN-002)");
            UnityEngine.Object.DestroyImmediate(clientGo);
        }

        [Test]
        public void 진행_중_참여자의_캐치업은_보고_후에_실행된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-mid");
            int connected = 0;
            server.ClientConnected += _ => connected++;
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, connected, "전환 전 접속 — 즉시 이벤트 (현행)");
            server.LoadScene("Battle");

            var chB = new RecordingChannel();
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();   // mid-transition joiner — wire 11, catch-up HELD
            Assert.AreEqual(1, chB.LoadScenes.Count, "진행 중 참여 — 씬 지시");
            Assert.AreEqual(0, ReplicatesOf(chB, player.NetId).Count, "SCN-002 — 보고 전 캐치업 유보");
            Assert.AreEqual(1, connected, "보고 전 ClientConnected 유보");

            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");
            UniNetEnvironment.PumpMain();
            Assert.GreaterOrEqual(ReplicatesOf(chB, player.NetId).Count, 1, "보고 후 캐치업");
            Assert.AreEqual(2, connected, "보고 후 ClientConnected");
            // order proof is the staging itself: wire 11 (LoadScenes) observed BEFORE the report with zero
            // replicates, replicates only after — RecordingChannel records LoadScenes separately from Order
        }

        [Test]
        public void 기존_참여자는_유보_중_놓친_소유권과_상태를_보고_후_복구한다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-existing");
            var receiver = new UnityEngine.GameObject("scn-existing-receiver").AddComponent<SpawnablePlayer>();
            try
            {
                int connected = 0;
                server.ClientConnected += _ => connected++;
                var chA = new RecordingChannel();
                var chB = new RecordingChannel();
                AttachApproved(server, chA);
                AttachApproved(server, chB);
                UniNetEnvironment.PumpMain();
                server.SetOwner(player.NetId, chB.UniNetConnId);
                server.LoadScene("Battle");
                server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");   // B receives deltas while A is still held
                chA.Clear();
                chB.Clear();

                server.SetOwner(player.NetId, chA.UniNetConnId);
                player.Score = 42;
                player.SecretHp = 77;
                server.TickReplication(server.SnapshotObjects());
                Assert.AreEqual(42, server.GetEntry(player.NetId).Subs[0].Snapshot[0], "다른 참여자 전송으로 공유 스냅샷 전진");
                Assert.Greater(chB.Replicates.Count, 0, "준비된 B는 델타 수신");
                Assert.AreEqual(0, chA.Order.Count, "유보된 A는 소유권·상태 미수신");
                Assert.AreEqual(0, chA.Batches.Count, "유보된 A는 배치 미수신");

                server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
                Assert.AreEqual(chA.UniNetConnId, server.GetOwner(player.NetId), "유보 중 명시한 소유권 유지");
                CollectionAssert.Contains(chA.Owners, (player.NetId, chA.UniNetConnId), "놓친 소유권 통보 복구");
                CollectionAssert.AreEqual(new[] { $"owner:{player.NetId}:{chA.UniNetConnId}", $"replicate:{player.NetId}:0" },
                    chA.Order, "소유권 기준선이 전체상태보다 먼저");
                var handler = UniNetTypeRegistry.Find(player.GetType());
                CollectionAssert.AreEqual(handler.WriteFull(player, true), chA.Replicates[0].payload, "현재 소유자용 전체상태 (이미 전진한 델타가 아님)");
                var reader = new MessageProtocol.Serialize.MessageBufferReader(chA.Replicates[0].payload);
                handler.ApplyDelta(receiver, ref reader);
                Assert.AreEqual(42, receiver.Score, "놓친 값 복구");
                Assert.AreEqual(77, receiver.SecretHp, "새 소유자 전용 값 복구");
                Assert.AreEqual(2, connected, "기존 참여자의 ClientConnected 재발화 없음");
                server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
                Assert.AreEqual(1, chA.Replicates.Count, "중복 보고는 캐치업 재실행 없음");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
                UnityEngine.Object.DestroyImmediate(receiver.gameObject);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(64)]
        public void 레디_캐치업은_보고자의_연기_델타만_무효화하고_다른_수신자는_보존한다(int departingPrefixCount)
        {
            var server = new NetworkServer { ReplicationBudgetPerTickBytes = 8 };
            var high = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-queue-high");
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-queue-player");
            var receiverA = new UnityEngine.GameObject("scn-queue-receiver-a").AddComponent<SpawnablePlayer>();
            var receiverB = new UnityEngine.GameObject("scn-queue-receiver-b").AddComponent<SpawnablePlayer>();
            try
            {
                high.NetworkPriority = 100f;
                high.NetworkUpdateFrequencyHz = player.NetworkUpdateFrequencyHz = 0f;
                var departing = new System.Collections.Generic.List<RecordingChannel>();
                for (int i = 0; i < departingPrefixCount; i++)
                {
                    var ch = new RecordingChannel();
                    AttachApproved(server, ch);
                    departing.Add(ch);
                }
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
                Assert.AreEqual(1, server.DeferredSendCount, "예산으로 유보 전 Score=10 델타 연기");
                Assert.IsFalse(chA.Replicates.Exists(e => e.netId == player.NetId), "낮은 우선순위 값은 아직 미전송");

                server.LoadScene("Battle");
                server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");   // B stays ready while A misses subsequent changes
                ApplyRecorded(chB, receiverB);
                Assert.AreEqual(10, receiverB.Score, "B의 준비 시점 기준선");
                foreach (var ch in departing) server.DetachConnection(ch);
                UniNetEnvironment.PumpMain();   // shift A/B's indexes after the old delta was queued (also covers >64 recipients)
                chA.Clear();
                chB.Clear();

                high.Score = 3;
                player.Score = 42;
                server.TickReplication(server.SnapshotObjects(), 1.1);
                Assert.AreEqual(42, server.GetEntry(player.NetId).Subs[0].Snapshot[0], "유보 중 스냅샷은 최신 값으로 전진");
                Assert.AreEqual(2, server.DeferredSendCount, "오래된 델타와 B용 최신 델타 모두 예산 연기");
                Assert.AreEqual(0, chA.Replicates.Count, "A는 유보 중 상태 미수신");
                Assert.IsFalse(chB.Replicates.Exists(e => e.netId == player.NetId), "B의 최신 변경도 아직 큐에 있음");

                server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
                ApplyRecorded(chA, receiverA);
                Assert.AreEqual(42, receiverA.Score, "A 보고 시 현재 전체상태 복구");
                chA.Clear();
                chB.Clear();
                server.ReplicationBudgetPerTickBytes = 0;
                server.TickReplication(server.SnapshotObjects(), 1.2);
                server.TickReplication(server.SnapshotObjects(), 1.3);   // no subsequent change can repair a stale overwrite
                ApplyRecorded(chA, receiverA);
                ApplyRecorded(chB, receiverB);

                Assert.AreEqual(42, receiverA.Score, "캐치업 이후 오래된 Score=10으로 되돌아가지 않음");
                Assert.IsFalse(chA.Replicates.Exists(e => e.netId == player.NetId), "A의 캐치업 이전 연기 전달만 무효화");
                Assert.AreEqual(42, receiverB.Score, "B의 예산 연기 변경은 유실 없이 전달");
                Assert.AreEqual(1, chB.Replicates.FindAll(e => e.netId == player.NetId).Count, "B에게는 최신 연기 델타만 1회 전달");
                Assert.AreEqual(0, server.DeferredSendCount, "큐 배출 완료");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(high.gameObject);
                UnityEngine.Object.DestroyImmediate(player.gameObject);
                UnityEngine.Object.DestroyImmediate(receiverA.gameObject);
                UnityEngine.Object.DestroyImmediate(receiverB.gameObject);
            }

            void ApplyRecorded(RecordingChannel channel, SpawnablePlayer receiver)
            {
                var handler = UniNetTypeRegistry.Find(player.GetType());
                foreach (var (netId, _, _, payload) in channel.Replicates)
                {
                    if (netId != player.NetId) continue;
                    var reader = new MessageProtocol.Serialize.MessageBufferReader(payload);
                    handler.ApplyDelta(receiver, ref reader);
                }
            }
        }

        [TestCase(2, false)]
        [TestCase(66, false)]
        [TestCase(130, false)]
        [TestCase(2, true)]
        [TestCase(66, true)]
        [TestCase(130, true)]
        public void 연기_델타는_현재_비관련_연결에_누출되지_않고_재진입_기준선보다_우선하지_않는다(int connectionCount, bool reenterBeforeRelease)
        {
            var server = new NetworkServer { ReplicationBudgetPerTickBytes = 8 };
            var high = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-relevance-high");
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-relevance-player");
            var receiver = new UnityEngine.GameObject("scn-relevance-receiver").AddComponent<SpawnablePlayer>();
            try
            {
                high.NetworkPriority = 100f;
                high.NetworkUpdateFrequencyHz = player.NetworkUpdateFrequencyHz = 0f;
                player.NetworkCullDistance = 10f;
                var channels = new System.Collections.Generic.List<RecordingChannel>();
                for (int i = 0; i < connectionCount; i++)
                {
                    var ch = new RecordingChannel();
                    AttachApproved(server, ch);
                    channels.Add(ch);
                }
                UniNetEnvironment.PumpMain();
                foreach (var ch in channels) ch.Clear();
                var target = channels[connectionCount - 1];   // covers inline bits and multiple overflow words
                var peer = channels[0];

                high.Score = 2;
                player.Score = 10;
                server.TickReplication(server.SnapshotObjects(), 1.0);
                Assert.AreEqual(1, server.DeferredSendCount, "관련 중 값 10을 예산으로 연기");
                server.SetViewerPosition(target.UniNetConnId, 100f, 0f, 0f);
                high.Score = 3;
                player.Score = 42;
                server.TickReplication(server.SnapshotObjects(), 1.1);
                Assert.AreEqual(2, server.DeferredSendCount, "다른 수신자용 과거·현재 델타 모두 연기");
                Assert.IsFalse(target.Replicates.Exists(e => e.netId == player.NetId), "비관련 전환 중 미전송");

                if (reenterBeforeRelease)
                {
                    server.SetViewerPosition(target.UniNetConnId, 0f, 0f, 0f);
                    high.Score = 4;
                    server.TickReplication(server.SnapshotObjects(), 1.2);   // full baseline bypasses the still-exhausted budget
                    var baseline = target.Replicates.FindAll(e => e.netId == player.NetId);
                    Assert.AreEqual(1, baseline.Count, "재진입 전체 기준선 1회");
                    Apply(baseline[0].payload);
                    Assert.AreEqual(42, receiver.Score, "재진입 현재값 적용");
                    Assert.AreEqual(2, server.DeferredSendCount, "다른 수신자의 큐는 재진입에도 보존");
                    target.Clear();
                }

                server.ReplicationBudgetPerTickBytes = 0;
                server.TickReplication(server.SnapshotObjects(), 1.3);
                server.TickReplication(server.SnapshotObjects(), 1.4);
                Assert.IsFalse(target.Replicates.Exists(e => e.netId == player.NetId),
                    reenterBeforeRelease ? "새 기준선 뒤 낡은 델타 전달 없음" : "송신 시 비관련이면 과거 관련 수신자도 제외");
                var deliveries = peer.Replicates.FindAll(e => e.netId == player.NetId);
                Assert.AreEqual(2, deliveries.Count, "다른 수신자의 연기 변경은 손실 없음");
                Apply(deliveries[0].payload);
                Assert.AreEqual(10, receiver.Score, "다른 수신자 과거 델타");
                Apply(deliveries[1].payload);
                Assert.AreEqual(42, receiver.Score, "다른 수신자 최신 델타");
                Assert.AreEqual(0, server.DeferredSendCount, "큐 배출 완료");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(high.gameObject);
                UnityEngine.Object.DestroyImmediate(player.gameObject);
                UnityEngine.Object.DestroyImmediate(receiver.gameObject);
            }

            void Apply(byte[] payload)
            {
                var reader = new MessageProtocol.Serialize.MessageBufferReader(payload);
                UniNetTypeRegistry.Find(player.GetType()).ApplyDelta(receiver, ref reader);
            }
        }

        [Test]
        public void 유보_중_후보가_없던_고아는_기존_참여자의_보고_시_입양된다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-orphan");
            try
            {
                int connected = 0;
                server.ClientConnected += _ => connected++;
                var chA = new RecordingChannel();
                var chB = new RecordingChannel();
                AttachApproved(server, chA);
                AttachApproved(server, chB);
                UniNetEnvironment.PumpMain();
                server.SetOwner(player.NetId, chB.UniNetConnId);
                server.LoadScene("Battle");
                chA.Clear();
                server.DetachConnection(chB);
                UniNetEnvironment.PumpMain();   // reassignment has no ready candidate
                Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "후보가 없어 입양 대기");
                Assert.AreEqual(0, chA.Order.Count, "유보 중 통보 없음");

                server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
                Assert.AreEqual(chA.UniNetConnId, server.GetOwner(player.NetId), "기존 참여자 보고가 재배정 재실행");
                CollectionAssert.Contains(chA.Owners, (player.NetId, chA.UniNetConnId), "입양 소유자 통보");
                Assert.AreEqual(1, chA.Replicates.Count, "입양 후 전체상태");
                Assert.AreEqual(2, connected, "ClientConnected 재발화 없음");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [Test]
        public void 마지막_보고의_접속_구독자가_던져도_게이트는_완료된다()
        {
            var server = new NetworkServer();
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();
            server.LoadScene("Battle");
            var chB = new RecordingChannel();
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();
            server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");

            var fault = new System.InvalidOperationException("post-ready subscriber fault");
            bool isolatedSubscriberRan = false;
            int gates = 0;
            server.ClientConnected += _ => throw fault;
            server.ClientConnected += _ => isolatedSubscriberRan = true;
            server.AllClientsSceneLoaded += scene => { Assert.AreEqual("Battle", scene); gates++; };

            var propagated = Assert.Throws<System.InvalidOperationException>(() => server.OnClientSceneLoaded(chB.UniNetConnId, "Battle"));
            Assert.AreSame(fault, propagated, "구독자 예외 전파 유지");
            Assert.IsTrue(isolatedSubscriberRan, "뒤 구독자 격리 유지");
            Assert.IsTrue(server.IsClientSceneReady(chB.UniNetConnId), "유효 보고 보존");
            Assert.IsNull(server.PendingScene, "예외와 무관하게 게이트 완료");
            Assert.AreEqual(1, gates, "게이트 1회");
            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");
            Assert.AreEqual(1, gates, "중복 보고로 재발화 없음");
        }

        [Test]
        public void 보고_전_게임_데이터는_유보된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-hold-src");
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();
            var go = new UnityEngine.GameObject("scn-hold-dyn");
            var bullet = go.AddComponent<SpawnablePlayer>();
            ulong netId = server.RegisterDynamicObject(new object[] { bullet });

            server.LoadScene("Battle");
            var chB = new RecordingChannel();
            AttachApproved(server, chB);
            UniNetEnvironment.PumpMain();   // chB held
            chB.Clear();

            server.BroadcastSpawn(netId);   // spawn broadcast
            server.SetOwner(netId, chB.UniNetConnId);   // ownership notice
            server.TickReplication(server.SnapshotObjects());   // replication tick (immediate mode)
            Assert.AreEqual(0, chB.Order.Count, "SCN-002 — 씬 유보 중 스폰·통보·복제 모두 차단");
            Assert.AreEqual(0, chB.Batches.Count, "씬 유보 중 배치 복제도 차단");

            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");
            UniNetEnvironment.PumpMain();
            Assert.GreaterOrEqual(chB.Order.Count, 1, "보고 후 게임 데이터 재개");
            CollectionAssert.Contains(chB.Spawns, netId, "보고 후 동적 오브젝트 기준선 복구");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [TestCase(false, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        public void 전송_불가_연결은_가시성_기준선과_기록을_받지_않는다(bool approved, bool disconnected, bool sceneHeld)
        {
            var server = new NetworkServer();
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            if (approved)
                server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();   // attach before registering objects — no visibility state yet
            if (sceneHeld) server.LoadScene("Battle");
            server.SnapshotConnections()[0].Disconnected = disconnected;   // exercise a disconnected connection still in the tick snapshot
            ch.Clear();

            var go = new UnityEngine.GameObject("scn-blocked-visibility");
            try
            {
                var player = go.AddComponent<SpawnablePlayer>();
                server.RegisterDynamicObject(new object[] { player });
                player.Score = 42;
                server.TickReplication(server.SnapshotObjects());
                server.TickReplication(server.SnapshotObjects(), 1.0);

                Assert.AreEqual(0, ch.Order.Count, "전송 불가 — owner/spawn/기준선 없음");
                Assert.AreEqual(0, ch.Batches.Count, "전송 불가 — 배치 없음");
                foreach (string field in new[] { "_visible", "_everVisible" })
                {
                    var state = (System.Collections.IDictionary)typeof(NetworkServer)
                        .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(server);
                    Assert.IsFalse(state.Contains(ch.UniNetConnId), field + " — 수신하지 않은 기준선의 가시성 기록 없음");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void wire11_송신_전_보고는_거부된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();
            string gate = null;
            server.AllClientsSceneLoaded += s => gate = s;
            server.LoadScene("Battle");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);   // NOT approved, NOT sent wire 11 yet — its report is stale/bogus (SCN-003)
            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");
            UniNetEnvironment.PumpMain();

            Assert.IsFalse(server.IsClientSceneReady(chB.UniNetConnId), "송신 전 보고 — 준비 미표시 (SCN-003)");
            Assert.IsNull(gate, "게이트 미완성 — 미수신 연결의 보고로 조기 완성 불가");
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();   // approval path delivers wire 11 + sets the sent flag
            Assert.AreEqual(1, chB.LoadScenes.Count, "승인 후 wire 11 수신");
            server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
            Assert.IsNull(gate, "A 보고만으로는 미완성 — B의 유효 보고 대기");
            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");   // now it counts
            Assert.AreEqual("Battle", gate, "유효 보고 — 게이트 완성");
        }

        [Test]
        public void 동명_재전환에서_수신_이력_없는_보고는_조기_게이트를_만들지_못한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();
            string gate = null;
            server.AllClientsSceneLoaded += s => gate = s;
            server.LoadScene("Battle");
            server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");   // transition 1 completes
            Assert.AreEqual("Battle", gate, "1차 전환 완료");

            gate = null;
            server.LoadScene("Battle");   // same-name re-transition — per-connection sent flags reset
            var chB = new RecordingChannel();
            server.AttachConnection(chB);   // unapproved — LoadScene broadcast skipped it: sent flag false
            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");   // stale/bogus report predates any wire 11 to chB
            Assert.IsNull(gate, "재전환 — 미수신 연결의 보고 거부 (조기 게이트 차단)");
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();
            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");
            Assert.IsNull(gate, "chB 보고 후에도 chA 미보고 — 게이트 대기");
            server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
            Assert.AreEqual("Battle", gate, "전원 보고 — 완성");
        }

        [Test]
        public void 전환_이력_없는_신규_서버는_현행_동작을_유지한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-fresh");
            int connected = 0;
            server.ClientConnected += _ => connected++;
            var chA = new RecordingChannel();
            AttachApproved(server, chA);
            UniNetEnvironment.PumpMain();   // no _currentScene — legacy immediate post-approval
            Assert.AreEqual(0, chA.LoadScenes.Count, "wire 11 없음 (현행)");
            Assert.GreaterOrEqual(ReplicatesOf(chA, player.NetId).Count, 1, "즉시 캐치업 (현행)");
            Assert.AreEqual(1, connected, "즉시 ClientConnected (현행)");
        }

        [Test]
        public void 승인과_씬_레디_2단계가_순서대로_해제된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-twostage");
            int connected = 0;
            server.ClientConnected += _ => connected++;
            server.LoadScene("Battle");
            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();   // stage 0: unapproved — nothing but Welcome
            Assert.AreEqual(0, chB.LoadScenes.Count, "미승인 — wire 11 유보 (CON-001)");
            Assert.AreEqual(0, connected, "미승인 — 이벤트 유보");

            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // stage 1: approval
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, chB.LoadScenes.Count, "승인 — wire 11 수신");
            Assert.AreEqual(0, ReplicatesOf(chB, player.NetId).Count, "씬 유보 — 캐치업 대기 (SCN-002)");
            Assert.AreEqual(0, connected, "씬 유보 — 이벤트 대기");

            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");   // stage 2: scene ready
            UniNetEnvironment.PumpMain();
            Assert.GreaterOrEqual(ReplicatesOf(chB, player.NetId).Count, 1, "2단계 해제 — 캐치업");
            Assert.AreEqual(1, connected, "2단계 해제 — ClientConnected");
        }

        [Test]
        public void 승인_꼬리_대기_중_LoadScene은_중복_지시_없이_한_보고로_완료된다()
        {
            var server = new NetworkServer();
            var ch = new RecordingChannel();
            int connected = 0, gates = 0;
            server.ClientConnected += _ => connected++;
            server.AllClientsSceneLoaded += _ => gates++;
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // Welcome only; the declaration still has to approve the connection
            Assert.IsTrue(server.SnapshotConnections()[0].WelcomeSent);
            Assert.IsTrue(server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion));

            server.LoadScene("Battle");   // approval is visible, but RunPostApproval is still queued
            Assert.AreEqual(0, ch.LoadScenes.Count, "승인 꼬리가 씬 전달을 소유");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, ch.LoadScenes.Count, "wire 11 정확히 1회");
            Assert.AreEqual(0, connected, "보고 전 접속 이벤트 유보");
            server.OnClientSceneLoaded(ch.UniNetConnId, "Battle");   // one valid report suffices
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(server.IsClientSceneReady(ch.UniNetConnId));
            Assert.AreEqual(1, connected);
            Assert.AreEqual(1, gates);
            Assert.IsNull(server.PendingScene);
            Assert.AreEqual(1, ch.LoadScenes.Count, "보고 후 재전송 없음");
        }

        [TestCase(2, false)]
        [TestCase(66, false)]
        [TestCase(2, true)]
        [TestCase(66, true)]
        public void 예산_압박_중_마지막_수신자_제거는_연기_항목과_풀_배열을_해제한다(int count, bool disconnect)
        {
            var server = new NetworkServer { ReplicationBudgetPerTickBytes = 8 };
            var high = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-prune-high");
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-prune-player");
            try
            {
                high.NetworkPriority = 100f;
                high.NetworkUpdateFrequencyHz = player.NetworkUpdateFrequencyHz = 0f;
                player.NetworkCullDistance = 10f;
                var channels = new System.Collections.Generic.List<RecordingChannel>();
                for (int i = 0; i < count; i++)
                {
                    var ch = new RecordingChannel();
                    AttachApproved(server, ch);
                    channels.Add(ch);
                }
                UniNetEnvironment.PumpMain();
                // OWN-002: ownership reassignment now supersedes deferred recipients with baselines — this test
                // exercises the deferred-queue lifecycle itself, so park ownership on the LAST peer: the pruned
                // peers own nothing (no reassignment baselines during prunes) and the last peer’s own departure
                // has no adoption target (owner left dangling — ReassignOwnership early-returns), leaving the queue lifecycle untouched.
                server.SetOwner(high.NetId, channels[count - 1].UniNetConnId);
                server.SetOwner(player.NetId, channels[count - 1].UniNetConnId);
                UniNetEnvironment.PumpMain();
                high.Score = 2;
                player.Score = 10;
                server.TickReplication(server.SnapshotObjects(), 1.0);
                var queue = (System.Collections.IList)typeof(NetworkServer)
                    .GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(server);
                Assert.AreEqual(1, queue.Count);
                object item = queue[0];
                var overflow = item.GetType().GetField("RelevantOverflow");
                if (count > 64) Assert.IsNotNull(overflow.GetValue(item), "초과 비트 풀 배열 소유");
                for (int i = 0; i < count - 1; i++) Prune(channels[i]);
                Assert.AreEqual(1, server.DeferredSendCount, "마지막 peer의 변경 보존");
                Prune(channels[count - 1]);
                Assert.AreEqual(0, server.DeferredSendCount, "예산 해제 없이 빈 항목 제거");
                Assert.IsNull(overflow.GetValue(item), "풀 배열 소유권 반환");

                void Prune(RecordingChannel ch)
                {
                    if (disconnect)
                    {
                        server.DetachConnection(ch);
                        UniNetEnvironment.PumpMain();
                    }
                    else
                    {
                        server.SetViewerPosition(ch.UniNetConnId, 100f, 0f, 0f);
                        high.Score++;
                        server.TickReplication(server.SnapshotObjects(), 1.1);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(high.gameObject);
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void 씬_유보_중_파괴는_보고_후_한번만_전달하고_재등록은_현재_기준선을_받는다(bool reregister)
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-destroy");
            var survivor = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-destroy-survivor");
            var replacement = new UnityEngine.GameObject("scn-destroy-replacement").AddComponent<SpawnablePlayer>();
            var receiver = new UnityEngine.GameObject("scn-destroy-receiver").AddComponent<SpawnablePlayer>();
            try
            {
                var held = new RecordingChannel();
                var ready = new RecordingChannel();
                var unapproved = new RecordingChannel();
                AttachApproved(server, held);
                AttachApproved(server, ready);
                server.AttachConnection(unapproved);
                UniNetEnvironment.PumpMain();
                server.LoadScene("Battle");
                server.OnClientSceneLoaded(ready.UniNetConnId, "Battle");
                held.Clear(); ready.Clear(); unapproved.Clear();

                Assert.IsTrue(server.DestroyObject(player.NetId));
                Assert.IsFalse(server.DestroyObject(player.NetId), "중복 파괴는 새 통보를 만들지 않음");
                Assert.IsEmpty(held.Destroys, "등록 완료 보고까지 파괴 유보");
                CollectionAssert.AreEqual(new[] { player.NetId }, ready.Destroys, "준비된 연결은 즉시 파괴");
                Assert.IsEmpty(unapproved.Destroys, "미승인 연결은 파괴 미수신");
                if (reregister)
                {
                    replacement.Score = 42;
                    server.RegisterSceneObject(player.NetId, new object[] { replacement });
                }
                server.OnClientSceneLoaded(held.UniNetConnId, "WrongScene");
                Assert.IsEmpty(held.Order, "무효 보고는 유보를 해제하지 않음");
                server.OnClientSceneLoaded(held.UniNetConnId, "Battle");
                if (reregister)
                {
                    Assert.IsEmpty(held.Destroys, "같은 netId의 대체 객체를 낡은 파괴로 제거하지 않음");
                    var baseline = held.Replicates.FindAll(e => e.netId == player.NetId);
                    Assert.AreEqual(1, baseline.Count, "대체 객체의 전체상태 캐치업");
                    var reader = new MessageProtocol.Serialize.MessageBufferReader(baseline[0].payload);
                    UniNetTypeRegistry.Find(replacement.GetType()).ApplyDelta(receiver, ref reader);
                    Assert.AreEqual(42, receiver.Score, "대체 객체의 현재값 적용");
                }
                else
                {
                    CollectionAssert.AreEqual(new[] { player.NetId }, held.Destroys, "보고 후 정확히 1회 파괴");
                    Assert.AreEqual("destroy:" + player.NetId, held.Order[0], "소유권·상태 기준선보다 파괴가 먼저");
                    Assert.IsFalse(held.Replicates.Exists(e => e.netId == player.NetId));
                }
                Assert.Greater(ReplicatesOf(held, survivor.NetId).Count, 0, "살아 있는 객체는 캐치업");
                int sends = held.Order.Count;
                server.OnClientSceneLoaded(held.UniNetConnId, "Battle");
                Assert.AreEqual(sends, held.Order.Count, "중복 보고는 재송신하지 않음");
                server.LoadScene("Next");
                server.OnClientSceneLoaded(held.UniNetConnId, "Next");
                Assert.AreEqual(reregister ? 0 : 1, held.Destroys.Count, "다음 전환에 낡은 파괴가 남지 않음");
                Assert.IsEmpty(unapproved.Destroys);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
                UnityEngine.Object.DestroyImmediate(survivor.gameObject);
                UnityEngine.Object.DestroyImmediate(replacement.gameObject);
                UnityEngine.Object.DestroyImmediate(receiver.gameObject);
            }
        }

        [Test]
        public void 씬_유보_중_해제는_대기_파괴를_정리하고_후속_연결에_넘기지_않는다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-destroy-disconnect");
            try
            {
                var ch = new RecordingChannel();
                AttachApproved(server, ch);
                UniNetEnvironment.PumpMain();
                var conn = server.SnapshotConnections()[0];
                server.LoadScene("Battle");
                ch.Clear();
                Assert.IsTrue(server.DestroyObject(player.NetId));
                var pending = typeof(NetworkServer.ServerConnection).GetField("PendingSceneDestroys",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.IsNotNull(pending.GetValue(conn), "유보 파괴가 연결에 귀속");
                server.DetachConnection(ch);
                server.OnClientSceneLoaded(ch.UniNetConnId, "Battle");
                UniNetEnvironment.PumpMain();
                Assert.IsNull(pending.GetValue(conn), "메인 큐 해제 정리에서 대기 집합 폐기");
                Assert.IsEmpty(ch.Destroys, "해제된 연결에는 송신하지 않음");

                var next = new RecordingChannel();
                AttachApproved(server, next);
                UniNetEnvironment.PumpMain();
                server.OnClientSceneLoaded(next.UniNetConnId, "Battle");
                Assert.IsEmpty(next.Destroys, "새 연결은 이전 연결의 대기 파괴를 상속하지 않음");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player.gameObject);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void wire12_수신_후_LoadScene이_먼저_실행돼도_대기열의_낡은_보고는_거부된다(bool previousLoadSent)
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            AttachApproved(server, ch);
            UniNetEnvironment.PumpMain();
            if (previousLoadSent) server.LoadScene("Battle");
            int gates = 0;
            server.AllClientsSceneLoaded += _ => gates++;
            var handler = typeof(global::UniNet.Generated.UniNetServerHub).GetMethod("__SceneLoaded_Requested",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(handler, "실제 생성 wire-12 핸들러");
            var writer = MessageProtocol.Serialize.MessageBufferWriter.Create();
            writer.WriteString("Battle");
            byte[] payload = writer.ToArray();
            Receive();   // dispatch receipt queues the old report BEFORE the new LoadScene
            server.LoadScene("Battle");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, gates, "이전 수신 세대는 현재 송신 플래그를 통과하지 못함");
            Assert.AreEqual("Battle", server.PendingScene);
            Assert.IsFalse(server.IsClientSceneReady(ch.UniNetConnId));
            Assert.IsTrue(server.SnapshotConnections()[0].AwaitingSceneReady);
            Receive();   // receipt AFTER the current wire 11 — valid without changing the payload
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, gates);
            Assert.IsNull(server.PendingScene);

            void Receive()
            {
                var task = (System.Threading.Tasks.Task<byte[]>)handler.Invoke(null, new object[] { ch.UniNetConnId, payload });
                Assert.IsTrue(task.IsCompleted);
                Assert.IsEmpty(task.Result);
            }
        }

        [Test]
        public void 대기_파괴_4096개_초과는_한번만_Kicked하고_수신_자격과_메모리를_정리한다()
        {
            var server = new NetworkServer();
            server.LoadScene("Battle");
            var ch = new RecordingChannel();
            int connected = 0;
            server.ClientConnected += _ => connected++;
            AttachApproved(server, ch);
            UniNetEnvironment.PumpMain();
            var conn = server.SnapshotConnections()[0];
            for (ulong id = 1; id <= 4096; id++) Destroy(id);
            Assert.AreEqual(4096, conn.PendingSceneDestroys.Count, "고정 상한까지 수용");
            for (int i = 0; i < 32; i++) Destroy(1);   // duplicate netIds do not consume another slot
            Assert.AreEqual(4096, conn.PendingSceneDestroys.Count);
            Assert.IsEmpty(ch.DisconnectNotices);
            Destroy(4097);
            Assert.IsTrue(conn.Disconnected, "전송 폐쇄 유예와 무관하게 즉시 수신 자격 취소");
            Assert.IsFalse(server.IsVersionApproved(ch.UniNetConnId));
            Assert.IsNull(conn.PendingSceneDestroys, "초과 직후 집합 폐기");
            for (ulong id = 4098; id <= 4352; id++) Destroy(id);
            server.OnClientSceneLoaded(ch.UniNetConnId, "Battle");
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion);
            UniNetEnvironment.PumpMain();
            CollectionAssert.AreEqual(new[] { UniNet.Core.UniNetDisconnectReason.Kicked }, ch.DisconnectNotices);
            Assert.IsNull(conn.PendingSceneDestroys, "후속 파괴는 집합 재생성 없음");
            Assert.IsEmpty(server.SnapshotConnections());
            Assert.IsFalse(server.IsClientSceneReady(ch.UniNetConnId));
            Assert.IsEmpty(ch.Destroys);
            Assert.IsEmpty(ch.Replicates);
            Assert.AreEqual(0, connected, "킥된 attach는 후속 보고로 완료되지 않음");

            void Destroy(ulong id)
            {
                server.RegisterSceneObject(id, new object[] { new object() });
                Assert.IsTrue(server.DestroyObject(id));
                Assert.IsTrue(conn.PendingSceneDestroys == null || conn.PendingSceneDestroys.Count <= 4096);
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        public void 파괴_전송_실패는_미전송_ID와_씬_유보를_보존하고_유효_보고로_재시도한다(int throwOnAttempt)
        {
            var server = new NetworkServer();
            var survivor = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "scn-destroy-retry");
            try
            {
                server.LoadScene("Battle");
                var ch = new ThrowOnceDestroyChannel { ThrowOnAttempt = throwOnAttempt };
                int connected = 0, gates = 0;
                server.ClientConnected += _ => connected++;
                server.AllClientsSceneLoaded += _ => gates++;
                AttachApproved(server, ch);
                UniNetEnvironment.PumpMain();
                var conn = server.SnapshotConnections()[0];
                foreach (ulong id in new ulong[] { 1, 2 })
                {
                    server.RegisterSceneObject(id, new object[] { new object() });
                    server.DestroyObject(id);
                }
                Assert.Throws<System.InvalidOperationException>(() => server.OnClientSceneLoaded(ch.UniNetConnId, "Battle"));
                Assert.AreEqual(3 - throwOnAttempt, conn.PendingSceneDestroys.Count, "실패 ID와 이후 ID 보존");
                foreach (ulong sent in ch.Destroys) Assert.IsFalse(conn.PendingSceneDestroys.Contains(sent), "성공 ID는 재시도하지 않음");
                Assert.IsTrue(conn.AwaitingSceneReady);
                Assert.IsTrue(conn.SceneLoadSent);
                Assert.IsTrue(conn.CatchupHeld);
                Assert.IsFalse(server.IsClientSceneReady(ch.UniNetConnId));
                Assert.AreEqual("Battle", server.PendingScene);
                server.TickReplication(server.SnapshotObjects());
                Assert.IsEmpty(ch.Owners, "실패 시 소유권 기준선 없음");
                Assert.IsEmpty(ch.Replicates, "실패 시 캐치업·틱 누출 없음");
                Assert.AreEqual(0, connected);
                Assert.AreEqual(0, gates);

                server.OnClientSceneLoaded(ch.UniNetConnId, "Battle");
                CollectionAssert.AreEquivalent(new ulong[] { 1, 2 }, ch.Destroys, "파괴는 ID마다 성공 1회");
                Assert.AreEqual(2, ch.Destroys.Count);
                Assert.IsNull(conn.PendingSceneDestroys);
                Assert.IsFalse(conn.AwaitingSceneReady);
                Assert.IsTrue(server.IsClientSceneReady(ch.UniNetConnId));
                Assert.Greater(ReplicatesOf(ch, survivor.NetId).Count, 0);
                Assert.AreEqual(1, connected);
                Assert.AreEqual(1, gates);
                Assert.IsNull(server.PendingScene);
                int sends = ch.Order.Count;
                server.OnClientSceneLoaded(ch.UniNetConnId, "Battle");
                Assert.AreEqual(sends, ch.Order.Count, "완료 후 중복 보고 재전송 없음");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(survivor.gameObject);
            }
        }

        private sealed class ThrowOnceDestroyChannel : RecordingChannel
        {
            internal int ThrowOnAttempt;
            private int _attempts;
            public override void SendDestroy(ulong netId)
            {
                if (++_attempts == ThrowOnAttempt) throw new System.InvalidOperationException("injected destroy failure");
                base.SendDestroy(netId);
            }
        }

        private static System.Collections.Generic.List<string> ReplicatesOf(RecordingChannel ch, ulong netId)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var e in ch.Order)
                if (e.StartsWith("replicate:" + netId + ":")) list.Add(e);
            return list;
        }
    }
}
