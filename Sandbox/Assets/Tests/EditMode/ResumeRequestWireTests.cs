using System;
using System.Reflection;
using System.Threading.Tasks;
using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Wire-path resume regression (ADR-0041/SEC-001) — drives the GENERATED server hub's private
    /// __ResumeRequest_Requested handler with real MessageBufferWriter payloads (int32 UTF8 length prefix + token,
    /// 48 bytes for a 44-char token) and pumps the main queue. This is the layer the core-direct
    /// ReconnectGraceTests cannot see: the parse bound, malformed isolation, and the queued ResumeSession call.
    /// Rejections are silent by contract (unauthenticated flood path — no per-message logging), so the
    /// assertions are behavioral: session/ownership must be untouched and a later genuine request must succeed.
    /// </summary>
    public sealed class ResumeRequestWireTests
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

        /// <summary>Locates the generated UniNetServerHub.__ResumeRequest_Requested in any loaded assembly — the emitted handler is what runs in production.</summary>
        private static MethodInfo FindResumeHandler()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type hub = null;
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "UniNetServerHub") { hub = t; break; }
                }
                catch (ReflectionTypeLoadException) { continue; }   // skip assemblies we cannot fully load (Unity dynamic)
                if (hub == null) continue;
                var m = hub.GetMethod("__ResumeRequest_Requested", BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null) return m;
            }
            Assert.Fail("생성 UniNetServerHub.__ResumeRequest_Requested 미발견 — 생성기 출력 확인 필요");
            return null;
        }

        /// <summary>Encodes a wire-10 payload exactly as the generated client hub does (int32 UTF8 length prefix + token).</summary>
        private static byte[] EncodeToken(string token)
        {
            var w = MessageBufferWriter.Create();
            w.WriteString(token);
            return w.ToArray();
        }

        /// <summary>Invokes the emitted handler with a sender connId; asserts the call returns a completed empty response (no exception escapes).</summary>
        private static void InvokeResume(MethodInfo handler, long senderConnId, byte[] payload, string what)
        {
            var task = (Task<byte[]>)handler.Invoke(null, new object[] { senderConnId, payload });
            Assert.IsTrue(task.IsCompleted, $"{what} — 핸들러는 동기 완료 (fire-and-forget 응답 없음)");
            Assert.IsNotNull(task.Result, $"{what} — 응답 본문 존재");
            Assert.AreEqual(0, task.Result.Length, $"{what} — 빈 응답");
        }

        [Test]
        public void 정상_와이어_재개_요청은_생성_핸들러를_통해_소유권을_이전한다()
        {
            var handler = FindResumeHandler();
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "wire-resume-ok");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;
            long victimOwner = server.GetOwner(player.NetId);
            Assert.AreEqual(chA.UniNetConnId, victimOwner, "준비 — 피해자가 마커 소유");
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "유예 보존 — 해제 후에도 소유권 유지");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            byte[] payload = EncodeToken(token);
            Assert.AreEqual(NetworkServer.MaxResumeRequestPayloadBytes, payload.Length, "정상 페이로드 — 4바이트 접두 + 44자 = 48바이트");

            InvokeResume(handler, chB.UniNetConnId, payload, "정상 요청");
            UniNetEnvironment.PumpMain();   // run the queued ResumeSession on the main thread

            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "생성 핸들러 경유 재부착 — 소유권 이전");
        }

        [Test]
        public void 잘림_초과_위조_접두_요청은_세션을_소진하지_않고_이후_정상_재개가_성공한다()
        {
            var handler = FindResumeHandler();
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "wire-resume-bad");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;
            long victimOwner = server.GetOwner(player.NetId);
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chProbe = new RecordingChannel();
            server.AttachConnection(chProbe);
            server.OnClientVersionDeclared(chProbe.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            long probeConn = chProbe.UniNetConnId;

            // 1) truncated — valid prefix (claims 44 chars) but the payload is cut short; the reader throws BEFORE allocating
            byte[] full = EncodeToken(token);
            var truncated = new byte[24];
            Array.Copy(full, truncated, 24);

            // 2) overlong — 45 chars + 4-byte prefix = 49 bytes: exceeds the pre-parse format bound
            byte[] overlong = EncodeToken(new string('A', 45));
            Assert.AreEqual(NetworkServer.MaxResumeRequestPayloadBytes + 1, overlong.Length, "상한 초과 1바이트");

            // 3) forged length prefix — claims 1,000,000 chars in an 8-byte payload (passes the total bound; the reader must throw, not allocate)
            var forged = new byte[] { 0x40, 0x42, 0x0F, 0x00, (byte)'A', (byte)'A', (byte)'A', (byte)'A' };

            InvokeResume(handler, probeConn, truncated, "잘림 요청");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "잘림 — 소유권 불변");

            InvokeResume(handler, probeConn, overlong, "초과 요청");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "초과 — 소유권 불변");

            InvokeResume(handler, probeConn, forged, "위조 접두 요청");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "위조 접두 — 소유권 불변·할당 폭발 없음");

            // the session survived every probe — a genuine request from a live connection still resumes
            var chGenuine = new RecordingChannel();
            server.AttachConnection(chGenuine);
            server.OnClientVersionDeclared(chGenuine.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            byte[] genuine = EncodeToken(token);
            InvokeResume(handler, chGenuine.UniNetConnId, genuine, "이후 정상 요청");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(chGenuine.UniNetConnId, server.GetOwner(player.NetId), "공격 시도 후 정상 재개 성공 — 세션 미소진");

            // observability regression — rejected requests still count: the handler's entry-point OnReceived sums all
            // four dispatched payloads (bytes) and all four dispatches (packets). Direct Core calls and RecordingChannel
            // sends never raise this server's OnReceived, so the counters isolate exactly the wire-handler dispatches.
            Assert.AreEqual(truncated.Length + overlong.Length + forged.Length + genuine.Length,
                server.TrafficStats.BytesReceived, "거절 포함 페이로드 바이트 합 — 조용히 폐기도 카운터 관측");
            Assert.AreEqual(4, server.TrafficStats.PacketsReceived, "불량 3 + 정상 1 = 4회 dispatch 관측");
        }
    }
}
