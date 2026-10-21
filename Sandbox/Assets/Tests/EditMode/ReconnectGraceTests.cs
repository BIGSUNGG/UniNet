using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Grace-session reconnect (ADR-0034) — grace preservation, reattach via token, and grace expiry.
    /// ADR-0041/SEC-001: the token is 32 bytes of cryptographic randomness as opaque Base64 — guessed/unissued tokens
    /// or invalid new connections must never consume a victim's grace session or move their ownership.
    /// </summary>
    public sealed class ReconnectGraceTests
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
            UniNetEnvironment.SetClient(null);   // REC-002 tests seed a client/sender for the retry-window assertions — never leak into other fixtures
            UniNetEnvironment.SetClientSender(null);
        }

        [Test]
        public void 유예기간_중_소유권이_보존된다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-keep");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            long oldOwner = server.GetOwner(player.NetId);
            Assert.AreNotEqual(0, oldOwner, "초기 소유자 배정");

            server.DetachConnection(ch);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(oldOwner, server.GetOwner(player.NetId), "유예기간 중 소유권 보존 (ADR-0034)");
        }

        [Test]
        public void 재부착이_소유권을_새_연결로_이전한다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-reattach");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            long oldOwner = server.GetOwner(player.NetId);

            string token = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "재접속 후 소유자 존재");

            var resumed = server.ResumeSession(token, chB.UniNetConnId);
            Assert.AreEqual(GraceResumeResult.Reattached, resumed, "토큰 매치 — 재부착 성립");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "소유권이 새 연결로 이전");
        }

        [Test]
        public void 유예_만료_후_재배정이_고아를_입양한다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 0.01 };   // 10ms grace — expires immediately in test time
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-expire");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            string token2 = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            // Grace expires — sweep + reassign adopts the orphan
            server.SweepGraceSessions(double.MaxValue);
            UniNetEnvironment.PumpMain();

            var resumed2 = server.ResumeSession(token2, 999);
            Assert.AreEqual(GraceResumeResult.Expired, resumed2, "만료(스윕 폐기→툼스톤) 세션 — 재부착 거부 (정확한 종결 사유)");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "만료 후 재배정 — 새 연결이 입양");
        }

        [Test]
        public void 유예_비활성_시_즉시_재배정()
        {
            var server = new NetworkServer { SessionGraceSeconds = 0 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-off");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "유예 없음 — 즉시 재배정");
        }

        [Test]
        public void 토큰은_256비트_난수_Base64이고_다중_발급이_유일하다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };
            UniNetEnvironment.SetServer(server);

            const int count = 12;
            var tokens = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var ch = new RecordingChannel();
                server.AttachConnection(ch);
                server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
                UniNetEnvironment.PumpMain();
                tokens.Add(server.SnapshotConnections()[i].SessionToken);
            }

            var seen = new HashSet<string>();
            foreach (var token in tokens)
            {
                Assert.IsFalse(string.IsNullOrEmpty(token), "발급 토큰은 비지 않음");
                Assert.AreEqual(NetworkServer.MaxSessionTokenChars, token.Length, $"형식 — Base64({NetworkServer.SessionTokenBytes}바이트)");
                byte[] decoded = Convert.FromBase64String(token);
                Assert.AreEqual(NetworkServer.SessionTokenBytes, decoded.Length, "복원 시 32바이트");
                Assert.IsTrue(seen.Add(token), "다중 발급 유일성 — 인접/예측 가능 시드 불가 (SEC-001)");
            }
        }

        [Test]
        public void 추측_토큰으로_피해자_소유권을_가로챌_수_없다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-forge");
            var chVictim = new RecordingChannel();
            server.AttachConnection(chVictim);
            server.OnClientVersionDeclared(chVictim.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            long victimOwner = server.GetOwner(player.NetId);
            string realToken = server.SnapshotConnections()[0].SessionToken;

            // victim drops — ownership is now held in a grace session keyed by the real token
            server.DetachConnection(chVictim);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "유예 보존");

            // attacker joins and presents forged guesses (neighbor-style: all-zero base64, mutated real token, empty)
            var chAttacker = new RecordingChannel();
            server.AttachConnection(chAttacker);
            server.OnClientVersionDeclared(chAttacker.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            long attackerConn = chAttacker.UniNetConnId;

            char[] mutated = realToken.ToCharArray();
            mutated[0] = mutated[0] == 'A' ? 'B' : 'A';   // nearest-miss forgery
            var guesses = new[] { new string('A', 43) + "=", new string(mutated), "", "0", "1" };
            foreach (var guess in guesses)
                Assert.AreNotEqual(GraceResumeResult.Reattached, server.ResumeSession(guess, attackerConn), $"위조 토큰 거부 len={guess.Length}");   // unknown strings → SessionNotFound; empty → UnknownToken — rejection either way

            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "피해자 소유권 유지 — 공격자에게 이전 없음");

            // the genuine token still works afterwards — the probes did not consume the session
            var resumed3 = server.ResumeSession(realToken, attackerConn);
            Assert.AreEqual(GraceResumeResult.Reattached, resumed3, "진짜 토큰은 공격 시도 후에도 재부착 성립 — 세션 미소진");
            Assert.AreEqual(attackerConn, server.GetOwner(player.NetId), "소유권 이전은 유효 토큰에만");
        }

        [Test]
        public void 잘못된_connId_거절_후_올바른_연결로_재개_가능하다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-badconn");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;
            long victimOwner = server.GetOwner(player.NetId);
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(GraceResumeResult.UnknownToken, server.ResumeSession(token, 999), "임의 connId — 거부");
            Assert.AreEqual(GraceResumeResult.UnknownToken, server.ResumeSession(token, chA.UniNetConnId), "죽은(해제된) connId — 거부");
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "거부 경로에서 소유권 불변");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(token, chB.UniNetConnId), "이후 올바른 연결로 재개 — 토큰 미소진");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "소유권 이전");
        }

        [Test]
        public void 중복_재개는_거부되고_소유권이_불변이다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-dup");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(token, chB.UniNetConnId), "1차 재개 성공");

            var chC = new RecordingChannel();
            server.AttachConnection(chC);
            server.OnClientVersionDeclared(chC.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.UnknownToken, server.ResumeSession(token, chC.UniNetConnId), "동일 토큰 재사용(토큰 재생) — 거부 (소비 툼스톤 → 정확한 종결)");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "재생 거부 후 소유권 불변");
        }

        // ── REC-002 — voluntary leave (wire 15): a voluntary ClientStop burns the resume token by contract, so a
        // notified departure must NOT save a grace session (nothing can ever resume it; it only blocked orphan
        // adoption for the whole grace window). Tombstone contract: a stale resume reports UnknownToken (terminal).

        /// <summary>Captures wire-14 resume results — same explicit-interface pattern as ResumeResultTests.ResultChannel.</summary>
        private sealed class LeaveResultChannel : RecordingChannel, IUniNetSystemChannel
        {
            internal readonly List<(bool Ok, byte Reason, double RetryMs)> Results = new();
            void IUniNetSystemChannel.SendResumeResult(bool ok, byte result, double retryableRemainingMs)
                => Results.Add((ok, result, retryableRemainingMs));
        }

        /// <summary>Records wire-10 retry sends — same pattern as ResumeResultTests.SenderStub.</summary>
        private sealed class LeaveSenderStub : IUniNetClientSender
        {
            internal readonly List<(int MethodId, byte[] Payload)> Sent = new();
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode)
                => Sent.Add((methodId, payload));
        }

        [Test]
        public void 자발_퇴장_통보_후_유예_세션이_저장되지_않는다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;

            server.OnIntentionalLeave(ch.UniNetConnId);   // wire 15 notice — the generated dispatch path in production
            Assert.AreEqual(1, server.IntentionalLeaves, "통보 적용 카운터 (조용한 관측성)");
            server.DetachConnection(ch);
            UniNetEnvironment.PumpMain();

            // a LIVE successor connection distinguishes all three outcomes: a saved session would Reattach,
            // a missing save would report retryable SessionNotFound, and the tombstone reports UnknownToken
            var chB = new LeaveResultChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.UnknownToken, server.ResumeSession(token, chB.UniNetConnId), "소각 토큰 — 유예 미저장 + 소비 툼스톤 (SessionNotFound 재시도 오인 없음)");
            Assert.AreEqual((false, (byte)GraceResumeResult.UnknownToken, 0.0), chB.Results[^1], "wire 14 — 종결 사유 + 재시도 창 0");
        }

        [Test]
        public void 자발_퇴장은_고아_입양을_즉시_허용한다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-adopt");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "준비 — 초기 소유자 배정");

            server.OnIntentionalLeave(chA.UniNetConnId);
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "유예 창(30s) 내 신규 연결 즉시 입양 — 고아 블로킹 소멸");
        }

        [Test]
        public void 자발_퇴장_토큰의_재개는_재시도_창을_무장하지_않는다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;
            server.OnIntentionalLeave(chA.UniNetConnId);
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            // REC-001 retry contract, client side: SessionNotFound arms the window (sanity — the harness really
            // sends), while the voluntary-leave tombstone's UnknownToken is terminal and must never arm it
            var client = new NetworkClient { SessionToken = token };
            UniNetEnvironment.SetClient(client);
            var sender = new LeaveSenderStub();
            UniNetEnvironment.SetClientSender(sender);
            client.ApplyResumeResult(false, (byte)GraceResumeResult.SessionNotFound, 30000.0);
            client.TickResumeRetry();
            Assert.AreEqual(1, sender.Sent.Count, "대조 — SessionNotFound는 재시도 전송 (하니스 유효)");
            Assert.AreEqual(10, sender.Sent[0].MethodId, "wire 10 재전송");

            client.ApplyResumeResult(false, (byte)GraceResumeResult.UnknownToken, 0.0);
            client.TickResumeRetry();
            Assert.AreEqual(1, sender.Sent.Count, "UnknownToken 수신 — 재시도 창 무장 없음 (wire 10 재전송 없음)");
        }

        [Test]
        public void 의도_통보_없는_해제는_기존_유예를_유지한다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-legacy");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            long oldOwner = server.GetOwner(player.NetId);
            string token = server.SnapshotConnections()[0].SessionToken;

            server.DetachConnection(chA);   // legacy client — no notice, no flag
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(oldOwner, server.GetOwner(player.NetId), "구 클라 호환 — 유예 보존 (기존 동작 유지)");
            Assert.AreEqual(0, server.IntentionalLeaves, "통보 없음 — 카운터 불변");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(token, chB.UniNetConnId), "토큰 유효 — 재부착 성립 (툼스톤 오인 없음)");
        }

        [Test]
        public void 유예_비활성_서버의_자발_퇴장_통보는_무해하다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 0 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-off-leave");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            server.OnIntentionalLeave(chA.UniNetConnId);
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, server.IntentionalLeaves, "통보는 정상 적용");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "유예 비활성 — 즉시 재배정 (통보 무해)");
        }
    }
}
