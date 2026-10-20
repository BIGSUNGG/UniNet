using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// CON-001 — the connection approval lifecycle. While version checking is active (non-empty ExpectedProtocolVersion),
    /// a connection that has not declared an accepted wire-7 version receives no game data: ownership reassignment,
    /// catch-up, the ClientConnected event, replication/spawn sends, and game ServerRpcs are held; an accepted
    /// declaration releases the held work in the legacy order; an undeclared connection is swept by the timeout kick.
    /// Empty ExpectedProtocolVersion keeps the exact legacy behavior (immediate synchronous approval).
    /// </summary>
    public sealed class ConnectionApprovalTests
    {
        private readonly List<GameObject> _created = new();
        private NetworkServer _prevServer;

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _prevServer = UniNetEnvironment.Server;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(_prevServer);
            foreach (var go in _created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _created.Clear();
        }

        private SpawnablePlayer NewSceneObject(NetworkServer server, string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            var player = go.AddComponent<SpawnablePlayer>();
            player.Score = 7;
            server.RegisterSceneObject(player.NetId, new object[] { player });
            return player;
        }

        [Test]
        public void 빈_기대버전은_즉시_승인되어_현행_동작을_유지한다()
        {
            var server = new NetworkServer { ExpectedProtocolVersion = string.Empty };
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap-inactive");

            int connected = 0;
            server.ClientConnected += _ => connected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // no declaration — approval must already be synchronous

            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "비활성 검사 — 즉시 승인");
            Assert.AreEqual(1, connected, "ClientConnected 즉시 발화 (현행 동작)");
            Assert.AreEqual(ch.UniNetConnId, server.GetOwner(player.NetId), "소유권 재배정 즉시 (현행 동작)");
            Assert.GreaterOrEqual(ReplicatesOf(ch, player.NetId).Count, 1, "캐치업 즉시 (현행 동작)");
        }

        [Test]
        public void 미선언_연결은_소유권_캐치업_이벤트_복제에서_제외된다()
        {
            var server = new NetworkServer();   // default expected version — checking ACTIVE
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap-held");
            player.Score = 42;

            int connected = 0;
            server.ClientConnected += _ => connected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            Assert.IsFalse(server.IsVersionApproved(ch.UniNetConnId), "미선언 — 미승인");
            Assert.AreEqual(0, connected, "ClientConnected 유보");
            Assert.AreEqual(0, server.GetOwner(player.NetId), "소유권 재배정 유보 (고아 유지)");
            Assert.AreEqual(0, ReplicatesOf(ch, player.NetId).Count, "캐치업·복제 송신 없음");
            Assert.AreEqual(0, CountOwners(ch, player.NetId), "소유권 통보 송신 없음");
        }

        [Test]
        public void 승인_선언_도착_시_보류_작업이_순서대로_실행된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap-approve");

            int connected = 0;
            server.ClientConnected += _ => connected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // held

            bool accepted = server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion);
            Assert.IsTrue(accepted, "일치 선언 수용");
            UniNetEnvironment.PumpMain();   // the queued post-approval work runs

            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "승인");
            Assert.AreEqual(1, connected, "ClientConnected 발화");
            Assert.AreEqual(ch.UniNetConnId, server.GetOwner(player.NetId), "소유권 재배정 실행");
            var replicates = ReplicatesOf(ch, player.NetId);
            Assert.GreaterOrEqual(replicates.Count, 1, "캐치업 실행");
            int ownerIdx = ch.Order.FindIndex(e => e.StartsWith("owner:" + player.NetId + ":"));
            int stateIdx = ch.Order.FindIndex(e => replicates.Contains(e));
            Assert.GreaterOrEqual(ownerIdx, 0, "소유권 기준선 존재");
            Assert.Less(ownerIdx, stateIdx, "소유권 통보가 상태보다 먼저 (기존 순서 계약)");
        }

        [Test]
        public void 재선언은_멱등이다_보류_작업_재실행_없음()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap-idempotent");

            int connected = 0;
            server.ClientConnected += _ => connected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, connected, "1차 승인 — 1회 발화");
            // the approval path legitimately sends TWO owner notices (OWN-001 standard): the reassignment-adoption
            // broadcast (ReassignOwnership) plus the catch-up baseline (SendBaseline) — same contract as the
            // round-robin adoption tests. Capture the settled count; idempotency means NO resend after this.
            int ownersAfterFirst = CountOwners(ch, player.NetId);

            Assert.IsTrue(server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion), "재선언 수용");
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, connected, "재선언 — 재발화 없음");
            Assert.AreEqual(ownersAfterFirst, CountOwners(ch, player.NetId), "재선언 — 통보 재전송 없음");
        }

        [Test]
        public void 미승인_발신_게임_RPC는_조용히_기각되고_카운터에_관측된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            Assert.IsFalse(server.IsVersionApproved(ch.UniNetConnId), "미승인");
            Assert.AreEqual(0, server.UnapprovedServerRpcDrops, "초기 카운터 0");
            server.ReportUnapprovedServerRpc(ch.UniNetConnId);   // the generated dispatch gate calls exactly this
            server.ReportUnapprovedServerRpc(ch.UniNetConnId);
            Assert.AreEqual(2, server.UnapprovedServerRpcDrops, "게이트 기각 관측 카운터");
            Assert.IsTrue(server.IsVersionApproved(999999) == false, "미지 연결 — 미승인 (안전 기본)");
        }

        [Test]
        public void 미선언_타임아웃은_VersionMismatch_킥으로_마감한다()
        {
            var server = new NetworkServer { VersionDeclareTimeoutMs = 1000 };
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap-timeout");
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // held — no game data
            Assert.AreEqual(0, ch.DisconnectNotices.Count, "킥 전 공지 없음");

            server.SweepUnapprovedConnections(UniNetTime.LocalClock() + 2.0);   // past the 1s timeout
            Assert.AreEqual(1, ch.DisconnectNotices.Count, "타임아웃 — VersionMismatch 공지 1회");
            Assert.AreEqual(UniNet.Core.UniNetDisconnectReason.VersionMismatch, ch.DisconnectNotices[0], "킥 사유");

            // the sweep is idempotent while the close is in flight — one notice per sweep pass until detach
            server.SweepUnapprovedConnections(UniNetTime.LocalClock() + 3.0);
            Assert.AreEqual(2, ch.DisconnectNotices.Count, "여전히 미승인 — 스윕 재통보 (DetachConnection 전)");
        }

        [Test]
        public void 타임아웃_0은_스윕을_비활성화한다()
        {
            var server = new NetworkServer { VersionDeclareTimeoutMs = 0 };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            server.SweepUnapprovedConnections(UniNetTime.LocalClock() + 3600.0);
            Assert.AreEqual(0, ch.DisconnectNotices.Count, "비활성 — 한 시간 후에도 킥 없음 (보류 유지)");
        }

        [Test]
        public void AttachConnection은_네트워크_스레드에서도_안전하다()
        {
            // regression: the attach section runs on the DRPC accept thread — any Unity-clock call there throws
            // (UnityException off the main thread) and silently aborts the attach. Swap in a clock that enforces
            // exactly that and attach from a worker thread: the attach must survive, and the stamp happens later
            // on the main queue (PumpMain), before the Welcome.
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            int mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var previous = UniNetTime.LocalClock;
            UniNetTime.LocalClock = () =>
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                    throw new InvalidOperationException("Unity clock called off the main thread (simulated)");
                return previous();
            };
            RecordingChannel ch = null;
            System.Threading.Tasks.Task<long> attach = null;
            try
            {
                ch = new RecordingChannel();
                attach = System.Threading.Tasks.Task.Run(() => server.AttachConnection(ch));
                Assert.IsTrue(attach.Wait(2000), "attach 완료");
                Assert.IsFalse(attach.IsFaulted, "네트워크 스레드 attach — 예외 없음: " + attach.Exception?.GetBaseException().Message);
            }
            finally
            {
                UniNetTime.LocalClock = previous;   // restore — later tests keep the real clock
            }

            Assert.AreEqual(attach.Result, ch.UniNetConnId, "연결 등록 — connId 반환");
            Assert.IsFalse(server.IsVersionApproved(attach.Result), "미선언 — 미승인 (기대 상태)");
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(GetConn(server, attach.Result).WelcomeSent, "Welcome 송신 (메인 큐)");
            Assert.IsFalse(double.IsNaN(GetConn(server, attach.Result).AttachedAtClock), "메인 큐에서 타임아웃 스탬프 — Welcome과 함께 개시");
        }

        private static NetworkServer.ServerConnection GetConn(NetworkServer server, long connId)
        {
            foreach (var c in server.SnapshotConnections())
                if (c.ConnId == connId) return c;
            return null;
        }

        private static List<string> ReplicatesOf(RecordingChannel ch, ulong netId)
        {
            var list = new List<string>();
            foreach (var e in ch.Order)
                if (e.StartsWith("replicate:" + netId + ":")) list.Add(e);
            return list;
        }

        private static int CountOwners(RecordingChannel ch, ulong netId)
        {
            int n = 0;
            foreach (var (id, _) in ch.Owners)
                if (id == netId) n++;
            return n;
        }
    }
}
