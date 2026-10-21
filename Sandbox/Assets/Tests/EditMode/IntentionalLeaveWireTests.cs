using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// Wire-path intentional leave (REC-002, wire 15) — drives the GENERATED server hub's private
    /// __IntentionalLeave_Requested handler exactly like production dispatch. The 0-byte payload's content is
    /// ignored by contract (the sender's identity rides the connection), so both an empty and a garbage payload
    /// must apply the leave quietly (empty response, no log, no throw) while OTHER connections' grace state stays
    /// untouched — the leave can only ever act on the sender's own session.
    /// </summary>
    public sealed class IntentionalLeaveWireTests
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

        /// <summary>Locates the generated UniNetServerHub.__IntentionalLeave_Requested in any loaded assembly — the emitted handler is what runs in production.</summary>
        private static MethodInfo FindLeaveHandler()
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
                var m = hub.GetMethod("__IntentionalLeave_Requested", BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null) return m;
            }
            Assert.Fail("생성 UniNetServerHub.__IntentionalLeave_Requested 미발견 — 생성기 출력(0.2.12) 확인 필요");
            return null;
        }

        /// <summary>Invokes the emitted handler with a sender connId; asserts the call returns a completed empty response (no exception escapes).</summary>
        private static void InvokeLeave(MethodInfo handler, long senderConnId, byte[] payload, string what)
        {
            var task = (Task<byte[]>)handler.Invoke(null, new object[] { senderConnId, payload });
            Assert.IsTrue(task.IsCompleted, $"{what} — 핸들러는 동기 완료");
            Assert.IsNotNull(task.Result, $"{what} — 응답 본문 존재");
            Assert.AreEqual(0, task.Result.Length, $"{what} — 빈 응답");
        }

        [Test]
        public void 빈_페이로드_통보는_생성_핸들러를_통해_유예_저장을_건너뛴다()
        {
            var handler = FindLeaveHandler();
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "wire-leave-empty");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;

            InvokeLeave(handler, ch.UniNetConnId, Array.Empty<byte>(), "빈 페이로드");
            Assert.AreEqual(1, server.IntentionalLeaves, "빈 페이로드 — 통보 적용");
            server.DetachConnection(ch);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "와이어 경유 — 유예 저장 건너뛰고 신규 연결 즉시 입양");
            Assert.AreEqual(GraceResumeResult.UnknownToken, server.ResumeSession(token, chB.UniNetConnId), "낡은 토큰 — 소비 툼스톤");
        }

        [Test]
        public void 과대_페이로드는_조용히_무시되고_타_세션_상태는_불변이다()
        {
            var handler = FindLeaveHandler();
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "wire-leave-garbage");

            // victim connection — departs WITHOUT a notice, so its grace session is saved (legacy behavior)
            var chVictim = new RecordingChannel();
            server.AttachConnection(chVictim);
            server.OnClientVersionDeclared(chVictim.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string victimToken = server.SnapshotConnections()[0].SessionToken;
            long victimOwner = server.GetOwner(player.NetId);
            server.DetachConnection(chVictim);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "준비 — 피해자 유예 보존");

            // garbage sender: the oversized payload must be ignored content-wise — quiet empty response, no log
            // (the handler never parses), the leave still applies to THIS connection only, and the victim's
            // grace session stays untouched (상태 무변경)
            var chGarbage = new RecordingChannel();
            server.AttachConnection(chGarbage);
            server.OnClientVersionDeclared(chGarbage.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            long garbageConn = chGarbage.UniNetConnId;
            var garbage = new byte[4096];
            new System.Random(1234).NextBytes(garbage);

            InvokeLeave(handler, garbageConn, garbage, "과대 페이로드");
            LogAssert.NoUnexpectedReceived();   // quiet by contract — the handler never logs on any payload
            Assert.AreEqual(1, server.IntentionalLeaves, "과대 페이로드 — 내용 무시, 통보는 자기 세션에만 적용");
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "피해자 유예 소유권 불변 — 타 세션 상태 무변경");
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(victimToken, garbageConn), "피해자 세션 미오염 — 정상 재부착 성립");
        }

        [Test]
        public void 미지_연결의_통보는_조용히_무시된다()
        {
            var handler = FindLeaveHandler();
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "wire-leave-unknown");

            // a live connection departs WITHOUT a notice — its grace session must be saved (legacy behavior)
            var chLive = new RecordingChannel();
            server.AttachConnection(chLive);
            server.OnClientVersionDeclared(chLive.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string liveToken = server.SnapshotConnections()[0].SessionToken;

            // a notice for an UNKNOWN connection id — the documented contract is quiet ignorance: no throw, the
            // counter stays put, and the live connection's grace state is untouched (the notice can only ever act
            // on the sender's own session)
            long before = server.IntentionalLeaves;
            InvokeLeave(handler, 987654321, Array.Empty<byte>(), "미지 연결");
            Assert.AreEqual(before, server.IntentionalLeaves, "미지 connId — 카운터 불변");

            server.DetachConnection(chLive);
            UniNetEnvironment.PumpMain();
            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(liveToken, chB.UniNetConnId), "라이브 세션 — 유예 보존(미지 통보의 오염 없음)");
        }
    }
}
