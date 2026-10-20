using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// REC-001 — the resume result lifecycle: every terminal outcome of a grace-session resume reports to the
    /// requesting connection (wire 14 envelope: ok / reason / retryableRemainingMs), the client surfaces it via
    /// LastResumeResult + ResumeResultReceived, and a SessionNotFound (detach race) converges by re-sending the
    /// resume request until the server's retryable window closes. SEC-001 intact: failures never consume the session.
    /// </summary>
    public sealed class ResumeResultTests
    {
        private readonly List<GameObject> _created = new();
        private NetworkServer _prevServer;
        private NetworkClient _prevClient;
        private IUniNetClientSender _prevSender;

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _prevServer = UniNetEnvironment.Server;
            _prevClient = UniNetEnvironment.Client;
            _prevSender = UniNetEnvironment.ClientSender;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(_prevServer);
            UniNetEnvironment.SetClient(_prevClient);
            UniNetEnvironment.SetClientSender(_prevSender);
            foreach (var go in _created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _created.Clear();
        }

        /// <summary>Recording channel that captures wire-14 resume results (inherits every other default).
        /// Explicit interface implementation — SendResumeResult is an interface DIM, not a base virtual member, so this
        /// is the standard way to take over the interface route (CS0115 otherwise).</summary>
        private sealed class ResultChannel : RecordingChannel, IUniNetSystemChannel   // listing the interface is required for an explicit implementation (CS0540)
        {
            internal readonly List<(bool Ok, byte Reason, double RetryMs)> Results = new();
            void IUniNetSystemChannel.SendResumeResult(bool ok, byte result, double retryableRemainingMs)
                => Results.Add((ok, result, retryableRemainingMs));
        }

        private sealed class SenderStub : IUniNetClientSender
        {
            internal readonly List<(int MethodId, byte[] Payload)> Sent = new();
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode)
                => Sent.Add((methodId, payload));
        }

        private SpawnablePlayer NewSceneObject(NetworkServer server, string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            var player = go.AddComponent<SpawnablePlayer>();
            server.RegisterSceneObject(player.NetId, new object[] { player });
            return player;
        }

        private static long Attach(NetworkServer server, RecordingChannel ch)
        {
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // real clients declare automatically
            UniNetEnvironment.PumpMain();
            return ch.UniNetConnId;
        }

        [Test]
        public void 각_종결_사유가_요청_연결에_정확히_전달된다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "rec-reasons");
            var chA = new ResultChannel();
            Attach(server, chA);
            string token = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();   // grace saved

            // SessionNotFound — a token the server has never seen (bogus), grace armed → retryable window > 0
            var chB = new ResultChannel();
            long connB = Attach(server, chB);
            Assert.AreEqual(GraceResumeResult.SessionNotFound, server.ResumeSession("bogus-token", connB), "미지 토큰 — SessionNotFound");
            Assert.AreEqual((false, (byte)GraceResumeResult.SessionNotFound, 30000.0), chB.Results[^1], "wire 14 — 거부 사유 + 재시도 창 30s");

            // Expired — the sweep closed the session; the tombstone reports Expired exactly (not a retryable NotFound)
            server.SweepGraceSessions(double.MaxValue);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.Expired, server.ResumeSession(token, connB), "스윕 폐기 — Expired");
            Assert.AreEqual((false, (byte)GraceResumeResult.Expired, 0.0), chB.Results[^1], "wire 14 — 만료는 재시도 불가");

            // UnknownToken — a VALID live session presented with an INVALID connection id: the !IsLiveConnection
            // branch only reaches with a live, unexpired session (a bogus token string is SessionNotFound, case 1)
            var chX = new ResultChannel();
            Attach(server, chX);
            string tokenX = null;
            foreach (var c in server.SnapshotConnections())
                if (c.ConnId == chX.UniNetConnId) tokenX = c.SessionToken;
            server.DetachConnection(chX);
            UniNetEnvironment.PumpMain();   // grace session saved
            Assert.AreEqual(GraceResumeResult.UnknownToken, server.ResumeSession(tokenX, 999), "유효 세션 + 무효 connId — UnknownToken");
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(tokenX, connB), "거부는 미소진 — 유효 연결로 재개 성립");

            // Reattached — the genuine token on a fresh session (new attach/detach pair). Explicit ownership first:
            // the case-2 sweep adopted the orphan to connB, and live-owner KEEP would leave chC's grace session with
            // an EMPTY OwnedNetIds — the resume would be true but move no ownership. SetOwner (ADR-0030) makes the
            // session genuinely own the player so the final assertion has real meaning.
            var chC = new ResultChannel();
            Attach(server, chC);
            server.SetOwner(player.NetId, chC.UniNetConnId);   // explicit transfer — the grace session must own the object
            string tokenC = null;
            foreach (var c in server.SnapshotConnections())
                if (c.ConnId == chC.UniNetConnId) tokenC = c.SessionToken;   // capture BEFORE the detach — tokens are only reachable on live connections
            var chD = new ResultChannel();
            long connD = Attach(server, chD);
            server.DetachConnection(chC);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(tokenC, connD), "진짜 토큰 — Reattached");
            Assert.AreEqual((true, (byte)GraceResumeResult.Reattached, 0.0), chD.Results[^1], "wire 14 — 성공");
            Assert.AreEqual(connD, server.GetOwner(player.NetId), "소유권 회복");
        }

        [Test]
        public void SessionNotFound_재시도가_디태치_경합에서_재부착으로_수렴한다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "rec-race");
            var chA = new ResultChannel();
            Attach(server, chA);
            string token = server.SnapshotConnections()[0].SessionToken;
            long oldOwner = server.GetOwner(player.NetId);

            // the RACE: the new connection presents the token BEFORE the server noticed the old one died
            var client = new NetworkClient { SessionToken = token };
            UniNetEnvironment.SetClient(client);
            var sender = new SenderStub();
            UniNetEnvironment.SetClientSender(sender);
            var chB = new ResultChannel();
            long connB = Attach(server, chB);

            Assert.AreEqual(GraceResumeResult.SessionNotFound, server.ResumeSession(token, connB), "경합 — 아직 세션 미생성");
            var raised = new List<GraceResumeResult>();
            client.ResumeResultReceived += raised.Add;
            client.ApplyResumeResult(false, (byte)GraceResumeResult.SessionNotFound, 30000.0);   // wire-14 arrival (dispatch thread in production)

            client.TickResumeRetry();
            Assert.AreEqual(1, sender.Sent.Count, "재시도 1회 전송");
            Assert.AreEqual(10, sender.Sent[0].MethodId, "wire 10 재전송");
            client.TickResumeRetry();
            Assert.AreEqual(1, sender.Sent.Count, "0.5s 간격 — 즉시 재전송 없음");

            // the server finally observes the old connection's death — the grace session appears
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(token, connB), "재시도 후 — 재부착 성립");
            Assert.AreEqual(connB, server.GetOwner(player.NetId), "소유권 회복 (구 소유자 " + oldOwner + " → 신규)");
            client.ApplyResumeResult(true, (byte)GraceResumeResult.Reattached, 0.0);
            client.TickResumeRetry();
            Assert.AreEqual(1, sender.Sent.Count, "종결 후 재시도 중단");
        }

        [Test]
        public void 이중_소비_패자는_UnknownToken으로_정확히_종결된다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 30 };
            UniNetEnvironment.SetServer(server);
            var chA = new ResultChannel();
            Attach(server, chA);
            string token = server.SnapshotConnections()[0].SessionToken;
            server.DetachConnection(chA);
            UniNetEnvironment.PumpMain();

            var chB = new ResultChannel();
            long connB = Attach(server, chB);
            var chC = new ResultChannel();
            long connC = Attach(server, chC);

            Assert.AreEqual(GraceResumeResult.Reattached, server.ResumeSession(token, connB), "승자 — 1차 재개");
            Assert.AreEqual(GraceResumeResult.UnknownToken, server.ResumeSession(token, connC), "패자 — 소비된 토큰은 UnknownToken (SessionNotFound 재시도 오인 없음)");
            Assert.AreEqual((false, (byte)GraceResumeResult.UnknownToken, 0.0), chC.Results[^1], "wire 14 — 패자에게 종결 사유 전달");
        }

        [Test]
        public void 실제_생성_수신기가_결과를_클라에_적용한다()
        {
            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            var raised = new List<GraceResumeResult>();
            client.ResumeResultReceived += raised.Add;

            // wire-14 payload [bool ok][byte reason][double retryMs] — replayed through the actual generated receiver
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteBoolean(true);
            w.WriteByte((byte)GraceResumeResult.Reattached);
            w.WriteDouble(0.0);
            byte[] payload = w.ToArray();

            Assert.DoesNotThrow(() =>
                UniNetDispatch.ClientHandlers()[14].Handler(0, payload).Wait(1000), "실제 wire 14 수신기 경유");
            Assert.AreEqual(GraceResumeResult.Reattached, client.LastResumeResult, "결과 적용");
            CollectionAssert.AreEqual(new[] { GraceResumeResult.Reattached }, raised, "이벤트 발화");
        }
    }
}
