using System;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// OWN-001 — the per-object ownership baseline. Late-join catch-up, relevancy re-entry (scene and dynamic),
    /// and multi-sub objects must send the owner notice (including owner 0) BEFORE the state/spawn baseline,
    /// so the client's owner cache is correct before full state applies and Notify observes the real owner.
    /// </summary>
    public sealed class OwnerBaselineTests
    {
        private readonly System.Collections.Generic.List<GameObject> _created = new();   // only GameObjects THIS fixture created — no scene-wide finds/sweeps
        private NetworkServer _prevServer;
        private NetworkClient _prevClient;

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _prevServer = UniNetEnvironment.Server;   // preserve the ambient globals — other fixtures/user state stay untouched
            _prevClient = UniNetEnvironment.Client;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(_prevServer);   // restore instead of nulling — no cross-fixture pollution
            UniNetEnvironment.SetClient(_prevClient);
            foreach (var go in _created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);   // caller owns the created objects (RegisterScene contract)
            _created.Clear();
        }

        private T Track<T>(T comp) where T : Component
        {
            _created.Add(comp.gameObject);
            return comp;
        }

        [Test]
        public void 씬_후발_접속은_살아있는_기존_소유자를_상태_전에_받는다()
        {
            var server = new NetworkServer();
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own-scene"));
            player.Score = 42;

            var ch1 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // conn 1 adopts the orphan — owner = 1
            ch1.Clear();

            var ch2 = new RecordingChannel();
            server.AttachConnection(ch2);
            server.OnClientVersionDeclared(ch2.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // late join — existing live owner KEEPed

            Assert.AreEqual(ch1.UniNetConnId, server.GetOwner(player.NetId), "살아 있는 기존 소유자 KEEP");
            CollectionAssert.Contains(ch2.Owners, (player.NetId, ch1.UniNetConnId), "후발 클라 — 기존 소유자 통보");
            AssertStartsWithOwnerThenState(ch2, player.NetId, "replicate");
        }

        [Test]
        public void 동적_후발_접속은_기존_소유자를_스폰_전에_받는다()
        {
            var server = new NetworkServer();
            var ch1 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            var go = new GameObject("own-dyn");
            _created.Add(go);
            var bullet = go.AddComponent<SpawnablePlayer>();
            var netId = server.RegisterDynamicObject(new object[] { bullet });   // round-robin → conn 1
            server.BroadcastSpawn(netId);
            ch1.Clear();

            var ch2 = new RecordingChannel();
            server.AttachConnection(ch2);
            server.OnClientVersionDeclared(ch2.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(ch1.UniNetConnId, server.GetOwner(netId), "기존 소유자 KEEP");
            CollectionAssert.Contains(ch2.Owners, (netId, ch1.UniNetConnId), "후발 클라 — 기존 소유자 통보");
            AssertStartsWithOwnerThenState(ch2, netId, "spawn");
        }

        [Test]
        public void 초기_BroadcastSpawn도_소유자를_스폰_전에_받는다()
        {
            var server = new NetworkServer();
            var ch1 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            var go = new GameObject("own-dyn-initial");
            _created.Add(go);
            var bullet = go.AddComponent<SpawnablePlayer>();
            var netId = server.RegisterDynamicObject(new object[] { bullet });   // round-robin → conn 1
            ch1.Clear();

            server.BroadcastSpawn(netId);   // initial live broadcast — same owner-before-state baseline as catch-up (OWN-001)

            CollectionAssert.Contains(ch1.Owners, (netId, ch1.UniNetConnId), "초기 스폰 — 소유권 통보");
            AssertStartsWithOwnerThenState(ch1, netId, "spawn");
        }

        [Test]
        public void 씬_가시성_재진입도_소유자_기준선을_상태_전에_받는다()
        {
            var server = new NetworkServer();
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own-reenter"));
            player.NetworkCullDistance = 10f;

            var ch1 = new RecordingChannel();
            var ch2 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(ch2);
            server.OnClientVersionDeclared(ch2.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // owner = conn 1
            ch2.Clear();

            server.SetViewerPosition(ch2.UniNetConnId, 100f, 0f, 0f);   // outside the cull radius
            server.TickReplication(server.SnapshotObjects(), 1.0);
            ch2.Clear();
            player.Score = 42;
            server.SetViewerPosition(ch2.UniNetConnId, 5f, 0f, 0f);     // re-entry
            server.TickReplication(server.SnapshotObjects(), 1.5);

            CollectionAssert.Contains(ch2.Owners, (player.NetId, ch1.UniNetConnId), "재진입 — 소유자 기준선");
            AssertStartsWithOwnerThenState(ch2, player.NetId, "replicate");
        }

        [Test]
        public void 동적_가시성_재진입은_소유자_기준선을_보존하고_스폰_전에_보낸다()
        {
            var server = new NetworkServer();
            var ch1 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            var go = new GameObject("own-dyn-reenter");
            _created.Add(go);
            var bullet = go.AddComponent<SpawnablePlayer>();
            bullet.NetworkCullDistance = 10f;
            var netId = server.RegisterDynamicObject(new object[] { bullet });
            server.BroadcastSpawn(netId);
            ch1.Clear();

            server.SetViewerPosition(ch1.UniNetConnId, 100f, 0f, 0f);
            server.TickReplication(server.SnapshotObjects(), 1.0);
            ch1.Clear();
            server.SetViewerPosition(ch1.UniNetConnId, 5f, 0f, 0f);
            server.TickReplication(server.SnapshotObjects(), 1.5);

            CollectionAssert.Contains(ch1.Owners, (netId, ch1.UniNetConnId), "동적 재진입 — 소유권 통보 보존");
            AssertStartsWithOwnerThenState(ch1, netId, "spawn");
        }

        [Test]
        public void 소유자_0도_명시적_기준선이_되어_스테일_메타데이터를_덮는다()
        {
            var server = new NetworkServer();
            var ch1 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            var go = new GameObject("own-zero");
            _created.Add(go);
            var bullet = go.AddComponent<SpawnablePlayer>();
            bullet.NetworkCullDistance = 10f;
            var netId = server.RegisterDynamicObject(new object[] { bullet });
            server.BroadcastSpawn(netId);
            server.SetOwner(netId, 0);   // explicit release — owner 0 at the next visibility entry
            ch1.Clear();

            server.SetViewerPosition(ch1.UniNetConnId, 100f, 0f, 0f);
            server.TickReplication(server.SnapshotObjects(), 1.0);
            ch1.Clear();
            server.SetViewerPosition(ch1.UniNetConnId, 5f, 0f, 0f);
            server.TickReplication(server.SnapshotObjects(), 1.5);

            CollectionAssert.Contains(ch1.Owners, (netId, 0L), "재진입 — 소유자 0도 명시적 기준선");
            AssertStartsWithOwnerThenState(ch1, netId, "spawn");

            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            client.ApplyOwner(netId, 999);   // stale metadata (e.g. a previous session's cache)
            ReplayOwnerUpdate(netId, 0);     // actual generated wire-2 receiver
            Assert.AreEqual(0, client.GetOwner(netId), "실제 수신기 — 스테일 소유자 0으로 덮음");
        }

        [Test]
        public void 다중_서브_객체는_객체별_기준선_한번을_모든_서브_상태_전에_보낸다()
        {
            var server = new NetworkServer();
            var go = new GameObject("own-multi");
            _created.Add(go);
            var comps = new NetworkBehaviour[] { go.AddComponent<MovementBrain>(), go.AddComponent<HealthTank>() };
            const ulong netId = 9101;
            server.RegisterSceneObject(netId, comps);

            var ch1 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // owner = conn 1 — owner view catches up BOTH subs (Armor is OwnerOnly)
            CollectionAssert.AreEquivalent(new[] { "replicate:" + netId + ":0", "replicate:" + netId + ":1" },
                ReplicatesOf(ch1, netId), "소유자 뷰 — 두 서브 모두 캐치업");
            ch1.Clear();

            var ch2 = new RecordingChannel();
            server.AttachConnection(ch2);
            server.OnClientVersionDeclared(ch2.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, CountOwners(ch2, netId), "객체별 기준선 — 서브마다가 아니라 한 번");
            CollectionAssert.Contains(ch2.Owners, (netId, ch1.UniNetConnId), "기존 소유자 통보");
            AssertStartsWithOwnerThenState(ch2, netId, "replicate");
            CollectionAssert.AreEqual(new[] { "replicate:" + netId + ":0" },
                ReplicatesOf(ch2, netId), "비소유자 뷰 — OwnerOnly 서브는 제외된 서브 집합 (HealthTank Armor)");
        }

        [Test]
        public void 실제_생성_수신기에서_기준선_적용_후_Notify가_정확한_소유자를_본다()
        {
            var server = new NetworkServer();
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "own-receiver"));
            player.Score = 42;
            player.SecretHp = 77;   // OwnerOnly + Notify

            var ch1 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // catch-up recorded on the wire — owner view (conn 1 is the owner)

            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            client.SetLocalConnId(ch1.UniNetConnId);   // the owner's own client
            var clientGo = new GameObject("own-receiver-client");
            _created.Add(clientGo);
            var clientPlayer = clientGo.AddComponent<SpawnablePlayer>();
            typeof(NetworkBehaviour).GetField("_sceneNetId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(clientPlayer, player.NetId);   // same scene netId as the server fixture — IsOwner queries the cache by the instance's NetId
            client.Register(player.NetId, new object[] { clientPlayer });

            foreach (var e in ch1.Order)   // replay in wire order through the actual generated receivers
            {
                var parts = e.Split(':');
                ulong netId = ulong.Parse(parts[1]);
                if (parts[0] == "owner")
                    ReplayOwnerUpdate(netId, long.Parse(parts[2]));
                else if (parts[0] == "replicate")
                    ReplayReplicate(ch1, netId, byte.Parse(parts[2]));
            }
            UniNetEnvironment.PumpMain();   // replication applies on the main queue — after the synchronous owner cache

            Assert.AreEqual(ch1.UniNetConnId, client.GetOwner(player.NetId), "실제 캐시 — 기존 소유자 반영");
            Assert.IsTrue(clientPlayer.IsOwner, "IsOwner — 상태 적용 시점에 정확");
            Assert.AreEqual(42, clientPlayer.Score, "전체 상태 적용");
            Assert.IsTrue(clientPlayer.SecretNotified, "OwnerOnly SecretHp RepNotify 발화");
            Assert.AreEqual(50, clientPlayer.LastPrevSecret, "Notify prev — 적용 전 기본값");
        }

        private static void AssertStartsWithOwnerThenState(RecordingChannel ch, ulong netId, string stateKind)
        {
            string ownerEntry = "owner:" + netId + ":";
            string stateEntry = stateKind + ":" + netId;
            int ownerIdx = -1, stateIdx = -1;
            for (int i = 0; i < ch.Order.Count; i++)
            {
                if (ownerIdx < 0 && ch.Order[i].StartsWith(ownerEntry)) ownerIdx = i;
                if (stateIdx < 0 && ch.Order[i].StartsWith(stateEntry)) stateIdx = i;
            }
            Assert.GreaterOrEqual(ownerIdx, 0, "소유자 기준선 존재");
            Assert.GreaterOrEqual(stateIdx, 0, "상태 기준선 존재");
            Assert.Less(ownerIdx, stateIdx, "소유자 통보가 상태/스폰보다 먼저");
        }

        private static int CountOwners(RecordingChannel ch, ulong netId)
        {
            int n = 0;
            foreach (var (id, _) in ch.Owners)
                if (id == netId) n++;
            return n;
        }

        private static System.Collections.Generic.List<string> ReplicatesOf(RecordingChannel ch, ulong netId)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var e in ch.Order)
                if (e.StartsWith("replicate:" + netId + ":")) list.Add(e);
            return list;
        }

        [Test]
        public void ApplyOwner_이벤트는_값이_실제로_바뀔_때만_발화한다()
        {
            var client = new NetworkClient();
            var fired = new System.Collections.Generic.List<(ulong NetId, long Prev, long Next)>();
            client.OwnershipChanged += (netId, prev, next) => fired.Add((netId, prev, next));

            client.ApplyOwner(7001, 5);   // first-ever assignment — a baseline, not a change
            client.ApplyOwner(7001, 5);   // repeated same-owner notice (re-sent baseline / redundant SetOwner broadcast)
            Assert.AreEqual(0, fired.Count, "첫 배정·동일값 재전파 — 미발화");

            client.ApplyOwner(7001, 0);   // actual value change — release
            client.ApplyOwner(7001, 2);   // actual value change — adopt
            Assert.AreEqual(2, fired.Count, "값 변경마다 1회 발화");
            Assert.AreEqual((7001ul, 5L, 0L), fired[0], "발화 인자 — (netId, prev, next) 해제");
            Assert.AreEqual((7001ul, 0L, 2L), fired[1], "발화 인자 — 입양");
            Assert.AreEqual(2, client.GetOwner(7001), "캐시 최종값");

            // notices for objects this client never registered follow the same contract: the FIRST assignment (registered
            // or not) is a baseline and does not fire; only a later value change fires — subscribers still null-guard lookups
            client.ApplyOwner(999999, 3);   // first assignment — baseline, silent
            client.ApplyOwner(999999, 7);   // actual change on an unregistered netId — fires
            Assert.AreEqual(3, fired.Count, "미등록 netId도 값 변경은 발화 (첫 배정은 미발화)");
            Assert.AreEqual((999999ul, 3L, 7L), fired[2], "미등록 netId 실제 변경 — (netId, prev, next) 발화");
            Assert.IsNull(client.Get(999999), "미등록 객체 조회 — null (구독자 널가드 계약)");
        }

        // ── OWN-003 — owner cache hygiene on destroy: Unregister must clear the netId's cached owner so
        // GetOwner returns 0 ("unassigned") after destruction instead of the last owner ──

        [Test]
        public void 파괴_해제후_소유자_캐시가_지워져_GetOwner가_0을_반환한다()
        {
            var client = new NetworkClient();
            const ulong netId = 8101;
            client.Register(netId, new object[] { new object() });
            client.ApplyOwner(netId, 5);
            Assert.AreEqual(5, client.GetOwner(netId), "파괴 전 — 통보된 소유자");

            Assert.IsTrue(client.Unregister(netId), "등록 해제 성공");

            Assert.AreEqual(0, client.GetOwner(netId), "파괴 후 — 0 (미배정 계약, 잔존 소유자 없음)");
        }

        [Test]
        public void 미등록_netId_해제는_false를_반환하고_타_netId_캐시를_훼손하지_않는다()
        {
            var client = new NetworkClient();
            const ulong liveId = 8201;
            client.Register(liveId, new object[] { new object() });
            client.ApplyOwner(liveId, 7);

            Assert.IsFalse(client.Unregister(999999), "미등록 netId — false 반환 (반환 계약 유지)");

            Assert.AreEqual(7, client.GetOwner(liveId), "타 netId 소유자 캐시 불변");
            Assert.IsNotNull(client.Get(liveId), "타 netId 등록 불변");
            Assert.AreEqual(0, client.GetOwner(999999), "미등록 netId 조회 — 0");
        }

        [Test]
        public void 같은_netId_스폰_파괴_100회_반복후에도_캐시가_누적되지_않는다()
        {
            var client = new NetworkClient();
            const ulong netId = 8301;

            for (int cycle = 0; cycle < 100; cycle++)
            {
                client.Register(netId, new object[] { new object() });
                client.ApplyOwner(netId, (cycle % 3) + 1);   // 스폰마다 다른 소유자 통보 — 매 사이클 새 기준선
                Assert.AreEqual((cycle % 3) + 1, client.GetOwner(netId), $"사이클 {cycle} — 통보 소유자");

                client.Unregister(netId);
                Assert.AreEqual(0, client.GetOwner(netId), $"사이클 {cycle} — 파괴 후 0 (잔존 없음의 간접 검증)");
            }

            Assert.AreEqual(0, client.GetOwner(netId), "100회 반복 후 — 최종 0");

            // OWN-003 direct accumulation check (r15 ISSUE-1): the stale-GetOwner contract alone
            // could pass with a keep-entry-but-zero-on-read design — assert the cache dict is empty.
            var ownersField = typeof(NetworkClient).GetField("_owners",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(ownersField, "_owners 필드 존재");
            var owners = ownersField.GetValue(client) as System.Collections.IDictionary;
            Assert.IsNotNull(owners, "_owners 사전 접근");
            Assert.AreEqual(0, owners.Count, "100회 스폰/파괴 후 owner 캐시 0엔트리 (누적 없음의 직접 검증)");
        }

        [Test]
        public void 파괴_해제후_재통보는_첫_기준선으로_조용히_적용된다()
        {
            var client = new NetworkClient();
            const ulong netId = 8401;
            var fired = new System.Collections.Generic.List<(ulong NetId, long Prev, long Next)>();
            client.OwnershipChanged += (netId, prev, next) => fired.Add((netId, prev, next));

            client.Register(netId, new object[] { new object() });
            client.ApplyOwner(netId, 3);   // 첫 배정 — 기준선, 미발화
            client.Unregister(netId);      // 파괴 — 상태 위생, 미발화 (D2)

            client.ApplyOwner(netId, 9);   // 파괴 후 늦은 재통보 — 캐시가 비어 첫 기준선으로 처리
            Assert.AreEqual(0, fired.Count, "파괴·기준선 모두 미발화 — 이벤트는 살아있는 객체의 재배정 통보 전용 (D2)");
            Assert.AreEqual(9, client.GetOwner(netId), "재통보 값은 캐시에 적용");
        }

        // System wire messages (1~12, e.g. OwnerUpdate) live in the generated hubs' MethodCallActions — NOT in the
        // general UniNetDispatch client table (only per-type __Replicate handlers register there). Mirror ResumeRequestWireTests:
        // locate the emitted UniNetClientHub.__OwnerUpdate_Requested by reflection and invoke what runs in production.
        private static System.Reflection.MethodInfo _ownerUpdateHandler;

        private static System.Reflection.MethodInfo FindOwnerUpdateHandler()
        {
            if (_ownerUpdateHandler != null) return _ownerUpdateHandler;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type hub = null;
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "UniNetClientHub") { hub = t; break; }
                }
                catch (System.Reflection.ReflectionTypeLoadException) { continue; }   // skip assemblies we cannot fully load (Unity dynamic)
                if (hub == null) continue;
                var m = hub.GetMethod("__OwnerUpdate_Requested", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (m != null) return _ownerUpdateHandler = m;
            }
            Assert.Fail("생성 UniNetClientHub.__OwnerUpdate_Requested 미발견 — 생성기 출력 확인 필요");
            return null;
        }

        /// <summary>Feeds an OwnerUpdate through the actual generated wire-2 receiver (same envelope the sender writes).</summary>
        private static void ReplayOwnerUpdate(ulong netId, long owner)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteInt64(owner);
            var task = (System.Threading.Tasks.Task<byte[]>)FindOwnerUpdateHandler().Invoke(null, new object[] { w.ToArray() });
            Assert.IsTrue(task.Wait(1000), "실제 생성 수신기 — 완료 미반환");   // empty response, no exception escapes
        }

        /// <summary>Feeds a replicate payload through the actual generated receiver ([netId][subId][payload] envelope).</summary>
        private static void ReplayReplicate(RecordingChannel ch, ulong netId, byte subId)
        {
            foreach (var (id, sub, methodId, payload) in ch.Replicates)
            {
                if (id != netId || sub != subId) continue;
                var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
                w.WriteUInt64(netId);
                w.WriteByte(subId);
                w.WriteBytes(payload);
                UniNetDispatch.ClientHandlers()[methodId].Handler(0, w.ToArray()).Wait(1000);
                return;
            }
            Assert.Fail("재생할 replicate 기록 없음 — netId " + netId + " sub " + subId);
        }
    }
}
