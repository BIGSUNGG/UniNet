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
        }

        [Test]
        public void 유예기간_중_소유권이_보존된다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grace-keep");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
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
            UniNetEnvironment.PumpMain();
            long oldOwner = server.GetOwner(player.NetId);

            string token = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "재접속 후 소유자 존재");

            bool resumed = server.ResumeSession(token, chB.UniNetConnId);
            Assert.IsTrue(resumed, "토큰 매치 — 재부착 성립");
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
            UniNetEnvironment.PumpMain();

            string token2 = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            // Grace expires — sweep + reassign adopts the orphan
            server.SweepGraceSessions(double.MaxValue);
            UniNetEnvironment.PumpMain();

            bool resumed = server.ResumeSession(token2, 999);
            Assert.IsFalse(resumed, "만료된 세션 — 재부착 거부");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
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
            UniNetEnvironment.PumpMain();
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
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
            UniNetEnvironment.PumpMain();
            long attackerConn = chAttacker.UniNetConnId;

            char[] mutated = realToken.ToCharArray();
            mutated[0] = mutated[0] == 'A' ? 'B' : 'A';   // nearest-miss forgery
            var guesses = new[] { new string('A', 43) + "=", new string(mutated), "", "0", "1" };
            foreach (var guess in guesses)
                Assert.IsFalse(server.ResumeSession(guess, attackerConn), $"위조 토큰 거부 len={guess.Length}");

            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "피해자 소유권 유지 — 공격자에게 이전 없음");

            // the genuine token still works afterwards — the probes did not consume the session
            bool resumed = server.ResumeSession(realToken, attackerConn);
            Assert.IsTrue(resumed, "진짜 토큰은 공격 시도 후에도 재부착 성립 — 세션 미소진");
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
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;
            long victimOwner = server.GetOwner(player.NetId);
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            Assert.IsFalse(server.ResumeSession(token, 999), "임의 connId — 거부");
            Assert.IsFalse(server.ResumeSession(token, chA.UniNetConnId), "죽은(해제된) connId — 거부");
            Assert.AreEqual(victimOwner, server.GetOwner(player.NetId), "거부 경로에서 소유권 불변");

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(server.ResumeSession(token, chB.UniNetConnId), "이후 올바른 연결로 재개 — 토큰 미소진");
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
            UniNetEnvironment.PumpMain();
            string token = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(server.ResumeSession(token, chB.UniNetConnId), "1차 재개 성공");

            var chC = new RecordingChannel();
            server.AttachConnection(chC);
            UniNetEnvironment.PumpMain();
            Assert.IsFalse(server.ResumeSession(token, chC.UniNetConnId), "동일 토큰 재사용(토큰 재생) — 거부");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "재생 거부 후 소유권 불변");
        }
    }
}
