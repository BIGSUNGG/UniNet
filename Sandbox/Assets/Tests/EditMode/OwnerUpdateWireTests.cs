using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// RPC-002 — wire-2 (OwnerUpdate) and single Replicate receive guards. A malformed payload (truncated,
    /// oversized, forged length prefix) must die at the generated handler boundary: no exception escapes into
    /// DRPC dispatch, ownership/state stay untouched, and the drop is observable via
    /// NetworkClient.FaultedPayloadCount (log-independent counter). A well-formed message right after a malformed
    /// one must still apply — the guard isolates per message, not per connection. Batch replication (wire 13)
    /// already had the double guard; the batch assertions here only pin that existing defense (regression).
    /// </summary>
    public sealed class OwnerUpdateWireTests
    {
        private readonly System.Collections.Generic.List<GameObject> _created = new();   // only GameObjects THIS fixture created
        private NetworkServer _prevServer;
        private NetworkClient _prevClient;

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _prevServer = UniNetEnvironment.Server;   // preserve the ambient globals — no cross-fixture pollution
            _prevClient = UniNetEnvironment.Client;
            _prevLogFault = UniNetEnvironment.LogFault;
            // silence the fault hook for the fixture: LogAssert fails the test on LogType.Exception messages
            // regardless of ignoreFailingMessages, and the drop IS asserted — via FaultedPayloadCount, which is
            // log-independent by design (that is the whole point of the counter)
            UniNetEnvironment.LogFault = static _ => { };
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        }

        private Action<Exception> _prevLogFault;

        [TearDown]
        public void TearDown()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            UniNetEnvironment.LogFault = _prevLogFault;
            UniNetEnvironment.SetServer(_prevServer);
            UniNetEnvironment.SetClient(_prevClient);
            foreach (var go in _created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _created.Clear();
        }

        private T Track<T>(T comp) where T : Component
        {
            _created.Add(comp.gameObject);
            return comp;
        }

        // System wire messages (1~12, e.g. OwnerUpdate) live in the generated hubs' MethodCallActions — NOT in the
        // general UniNetDispatch client table. Mirror OwnerBaselineTests: locate the emitted
        // UniNetClientHub.__OwnerUpdate_Requested by reflection and invoke what runs in production.
        private static MethodInfo _ownerUpdateHandler;

        private static MethodInfo FindOwnerUpdateHandler()
        {
            if (_ownerUpdateHandler != null) return _ownerUpdateHandler;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type hub = null;
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "UniNetClientHub") { hub = t; break; }
                }
                catch (ReflectionTypeLoadException) { continue; }   // skip assemblies we cannot fully load (Unity dynamic)
                if (hub == null) continue;
                var m = hub.GetMethod("__OwnerUpdate_Requested", BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null) return _ownerUpdateHandler = m;
            }
            Assert.Fail("생성 UniNetClientHub.__OwnerUpdate_Requested 미발견 — 생성기 출력 확인 필요");
            return null;
        }

        /// <summary>Encodes a wire-2 OwnerUpdate exactly as the generated sender does ([netId:8][owner:8]).</summary>
        private static byte[] EncodeOwnerUpdate(ulong netId, long owner)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteInt64(owner);
            return w.ToArray();
        }

        /// <summary>First-byte prefix of a full payload — models every truncation point.</summary>
        private static byte[] Slice(byte[] full, int cut)
        {
            var payload = new byte[cut];
            Array.Copy(full, payload, cut);
            return payload;
        }

        /// <summary>Invokes the emitted wire-2 handler; a malformed payload must not escape (catch inside the handler).</summary>
        private static void InvokeOwnerUpdate(MethodInfo handler, byte[] payload, string what)
        {
            var task = (Task<byte[]>)handler.Invoke(null, new object[] { payload });
            Assert.IsTrue(task.Wait(1000), what + " — 핸들러 완료 (예외 은닉)");
            Assert.AreEqual(0, task.Result.Length, what + " — 빈 응답");
        }

        /// <summary>Builds the single-Replicate envelope ([netId:8][subId:1][delta]) and truncates it to cut bytes.</summary>
        private static byte[] BuildReplicateEnvelope(ulong netId, byte subId, byte[] delta, int cut)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteByte(subId);
            if (delta != null) w.WriteBytes(delta);
            return Slice(w.ToArray(), cut);
        }

        /// <summary>Feeds a payload through the registered production single-Replicate handler (the emitted one).</summary>
        private static void InvokeReplicate(UniNetDispatch.Entry entry, byte[] payload, string what)
        {
            var task = entry.Handler(0, payload);
            Assert.IsTrue(task.Wait(1000), what + " — 핸들러 완료 (예외 은닉)");
            Assert.AreEqual(0, task.Result.Length, what + " — 빈 응답");
        }

        /// <summary>
        /// Shared fixture: server records one genuine replicate delta for a scene SpawnablePlayer, and a client-side
        /// twin registers the same netId. Returns the recorded (netId, subId, methodId, delta) and the client twin.
        /// </summary>
        private (ulong netId, byte subId, int methodId, byte[] delta, SpawnablePlayer clientPlayer, NetworkClient client)
            CaptureReplicateFixture(int score)
        {
            var server = new NetworkServer();
            var player = Track(ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "rpc002-repl"));
            player.Score = score;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — fake channels must declare too
            UniNetEnvironment.PumpMain();
            Assert.GreaterOrEqual(ch.Replicates.Count, 1, "준비 — replicate 기록 존재");
            var (netId, subId, methodId, delta) = ch.Replicates[0];

            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            client.SetLocalConnId(ch.UniNetConnId);
            var clientGo = new GameObject("rpc002-repl-client");
            _created.Add(clientGo);
            var clientPlayer = clientGo.AddComponent<SpawnablePlayer>();
            typeof(NetworkBehaviour).GetField("_sceneNetId", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(clientPlayer, netId);   // same scene netId as the server fixture — precedent (OwnerBaselineTests)
            client.Register(netId, new object[] { clientPlayer });
            return (netId, subId, methodId, delta, clientPlayer, client);
        }

        [Test]
        public void 잘린_OwnerUpdate는_예외를_숨기고_소유권을_보존하며_고장_카운터를_올린다()
        {
            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            const ulong netId = 7301;
            client.Register(netId, new object[] { new object() });
            client.ApplyOwner(netId, 42);
            long baseline = client.FaultedPayloadCount;

            var handler = FindOwnerUpdateHandler();
            var full = EncodeOwnerUpdate(netId, 42);   // 16바이트 정상 봉투
            Assert.AreEqual(16, full.Length, "정상 봉투 — netId 8 + owner 8");

            // every parse boundary: 0 (netId 진입 전), 7 (netId 잘림), 8 (owner 진입 전), 15 (owner 잘림)
            int[] cuts = { 0, 7, 8, 15 };
            foreach (int cut in cuts)
            {
                Assert.DoesNotThrow(() => InvokeOwnerUpdate(handler, Slice(full, cut), cut + "바이트"),
                    cut + "바이트 잘림 — 핸들러 경계에서 예외 은닉");
                Assert.AreEqual(42, client.GetOwner(netId), cut + "바이트 잘림 — 소유권 불변");
            }

            Assert.AreEqual(baseline + cuts.Length, client.FaultedPayloadCount,
                "잘림마다 1회 — FaultedPayloadCount (로그 설정과 무관하게 폐기 관측)");
        }

        [Test]
        public void 잘림_직후의_정상_OwnerUpdate는_적용되고_카운터는_고장만_센다()
        {
            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            const ulong netId = 7302;
            client.Register(netId, new object[] { new object() });
            long baseline = client.FaultedPayloadCount;

            var handler = FindOwnerUpdateHandler();

            Assert.DoesNotThrow(() => InvokeOwnerUpdate(handler, Slice(EncodeOwnerUpdate(netId, 42), 8), "8바이트 잘림"),
                "잘림 — 예외 은닉");
            Assert.AreEqual(baseline + 1, client.FaultedPayloadCount, "잘림 1회 카운트");
            Assert.AreEqual(0, client.GetOwner(netId), "잘림 — 소유권 미적용");

            Assert.DoesNotThrow(() => InvokeOwnerUpdate(handler, EncodeOwnerUpdate(netId, 77), "정상 후속"),
                "정상 후속 — 예외 은닉");
            Assert.AreEqual(77, client.GetOwner(netId), "잘림 직후 정상 OwnerUpdate 적용 — 메시지 단위 격리");
            Assert.AreEqual(baseline + 1, client.FaultedPayloadCount, "정상 적용은 카운트하지 않는다 — 잘림 1회만");
        }

        [Test]
        public void 단일_Replicate_잘림은_예외를_숨기고_컴포넌트_상태를_보존하며_이후_정상_delta를_적용한다()
        {
            var fixture = CaptureReplicateFixture(42);
            Assert.AreEqual(1, fixture.clientPlayer.Score, "준비 — 클라 기본값");
            long baseline = fixture.client.FaultedPayloadCount;
            var handler = UniNetDispatch.ClientHandlers()[fixture.methodId];   // 생성 단일 Replicate 수신기 (운영 등록본)

            // 잘림: 0 (netId 진입 전) / 8 (subId 진입 전) — 파싱 예외는 경계 catch로 은닉
            Assert.DoesNotThrow(() => InvokeReplicate(handler, BuildReplicateEnvelope(fixture.netId, fixture.subId, fixture.delta, 0), "0바이트"), "0바이트 잘림 — 예외 은닉");
            Assert.DoesNotThrow(() => InvokeReplicate(handler, BuildReplicateEnvelope(fixture.netId, fixture.subId, fixture.delta, 8), "8바이트"), "8바이트 잘림 — 예외 은닉");
            Assert.AreEqual(baseline + 2, fixture.client.FaultedPayloadCount, "파싱 고장 2회 — 카운터");

            // 9바이트 — netId+subId는 읽히고 delta가 빈 슬라이스: 파싱은 성공 (고장 카운트 대상 아님), 빈 delta apply는
            // 기존 QueueOnMain 내부 가드가 막는다 — 컴포넌트 상태는 불변
            // empty-delta apply throws inside the pre-existing inner guard — expect its log
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Exception,
                "EndOfStreamException: Attempted to read past the end of the buffer.");
            Assert.DoesNotThrow(() => InvokeReplicate(handler, BuildReplicateEnvelope(fixture.netId, fixture.subId, fixture.delta, 9), "9바이트"), "9바이트 잘림 — 예외 은닉");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, fixture.clientPlayer.Score, "잘림 슬라이스 후 — 컴포넌트 상태 불변");

            // 정상 후속 delta — 잘림 이후에도 메시지 단위 격리로 적용된다
            Assert.DoesNotThrow(() => InvokeReplicate(handler,
                BuildReplicateEnvelope(fixture.netId, fixture.subId, fixture.delta, 9 + fixture.delta.Length), "정상 후속"), "정상 후속 — 예외 은닉");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(42, fixture.clientPlayer.Score, "이후 정상 delta 적용 성공");
            Assert.AreEqual(baseline + 2, fixture.client.FaultedPayloadCount,
                "카운터는 잘림 파싱 고장 2회만 — 정상 적용(내부 가드 포함)은 카운트하지 않는다");
        }

        [Test]
        public void 위조_길이_접두는_배치_경로의_기존_방비로_응답하고_단일_과대_슬라이스도_예외를_숨긴다()
        {
            var fixture = CaptureReplicateFixture(42);
            long baseline = fixture.client.FaultedPayloadCount;

            // 1) 위조 길이 접두 배치 — wire 13의 기존 outer catch가 그대로 응답 (기존 방비 회귀 확인, D3 범위 밖이라 카운터 무관)
            var forged = MessageProtocol.Serialize.MessageBufferWriter.Create();
            forged.WriteUInt16(1);
            forged.WriteUInt64(fixture.netId);
            forged.WriteByte(fixture.subId);
            forged.WriteInt32(fixture.methodId);
            forged.WriteInt32(int.MaxValue);   // 위조 길이 — 2GB 청구, 실제 4바이트
            forged.WriteBytes(new byte[] { 1, 2, 3, 4 });
            byte[] forgedBlob = forged.ToArray();   // MessageBufferWriter is a ref struct — extract before the lambda
            // forged batch dies in the batch receiver's existing outer catch — expect its log
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Exception,
                "EndOfStreamException: Attempted to read past the end of the buffer.");
            Assert.DoesNotThrow(() => InvokeReplicate(UniNetDispatch.ClientHandlers()[13], forgedBlob, "위조 배치"),
                "위조 길이 접두 배치 — 기존 outer catch 유지 (예외 은닉)");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, fixture.clientPlayer.Score, "위조 배치 — 아무것도 적용 안 됨 (파싱이 경계에서 실패)");
            Assert.AreEqual(baseline, fixture.client.FaultedPayloadCount, "배치 catch는 기존 스타일 유지 (D3 — 형제 통일 범위 밖)");

            // 2) 정상 배치 1엔트리 — 기존 배치 경로 무손상 (정상 엔트리는 여전히 적용된다)
            var valid = MessageProtocol.Serialize.MessageBufferWriter.Create();
            valid.WriteUInt16(1);
            valid.WriteUInt64(fixture.netId);
            valid.WriteByte(fixture.subId);
            valid.WriteInt32(fixture.methodId);
            valid.WriteInt32(fixture.delta.Length);
            valid.WriteBytes(fixture.delta);
            byte[] validBlob = valid.ToArray();   // ref struct — extract before the lambda
            Assert.DoesNotThrow(() => InvokeReplicate(UniNetDispatch.ClientHandlers()[13], validBlob, "정상 배치"),
                "정상 배치 — 예외 은닉");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(42, fixture.clientPlayer.Score, "배치 경로 회귀 무손상 — 정상 엔트리 적용");
            Assert.AreEqual(baseline, fixture.client.FaultedPayloadCount, "정상 배치 적용은 카운트하지 않는다");

            // 3) 단일 과대 슬라이스 — 정상 봉투 + 후행 1KB 쓰레기: 파싱은 성공 (고장 카운트 대상 아님), apply 단 방어는 기존 내부 가드
            var padded = BuildReplicateEnvelope(fixture.netId, fixture.subId, fixture.delta, 9 + fixture.delta.Length);
            var oversized = new byte[padded.Length + 1024];
            Array.Copy(padded, oversized, padded.Length);
            Assert.DoesNotThrow(() => InvokeReplicate(UniNetDispatch.ClientHandlers()[fixture.methodId], oversized, "과대 단일"),
                "단일 과대 슬라이스 — 예외 은닉");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(baseline, fixture.client.FaultedPayloadCount, "과대 슬라이스는 파싱 성공 — 고장 카운트 아님");
        }
    }
}
