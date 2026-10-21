using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        // ── CON-002 — 게임 접속 승인 콜백 (connection approval callback, ADR-0047) ──

        [Test]
        public void 승인_콜백이_true면_기존_승인_순서대로_해제된다()
        {
            var server = new NetworkServer();
            server.ConnectionApprover = r => Task.FromResult(true);
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap2-approve");

            int connected = 0;
            server.ClientConnected += _ => connected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            Assert.IsFalse(server.IsVersionApproved(ch.UniNetConnId), "승인 콜백 대기 — 유보 유지");

            Assert.IsTrue(server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "arena-demo"), "선언 수용");
            UniNetEnvironment.PumpMain();   // RunApprovalAsync → (동기 완료) → FinishApproval → RunPostApproval
            UniNetEnvironment.PumpMain();   // await 재개가 큐를 한 번 더 탈 경우를 위한 안전 펌프

            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "콜백 승인");
            Assert.AreEqual(1, connected, "ClientConnected — 콜백 완료 후 발화");
            Assert.AreEqual(ch.UniNetConnId, server.GetOwner(player.NetId), "소유권 재배정 — 콜백 완료 후");
            var replicates = ReplicatesOf(ch, player.NetId);
            Assert.GreaterOrEqual(replicates.Count, 1, "캐치업 실행");
            int ownerIdx = ch.Order.FindIndex(e => e.StartsWith("owner:" + player.NetId + ":"));
            int stateIdx = ch.Order.FindIndex(e => replicates.Contains(e));
            Assert.Less(ownerIdx, stateIdx, "소유권 통보가 상태보다 먼저 (기존 순서 계약 유지)");
        }

        [Test]
        public void 승인_콜백이_false면_ApprovalRejected로_킥되고_게임_노출이_없다()
        {
            var server = new NetworkServer { SessionGraceSeconds = 10 };
            server.ConnectionApprover = r => Task.FromResult(false);
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap2-reject");

            int connected = 0, disconnected = 0;
            server.ClientConnected += _ => connected++;
            server.ClientDisconnected += _ => disconnected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "bad-cred");
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, ch.DisconnectNotices.Count, "거부 킥 — 공지 1회");
            Assert.AreEqual(UniNet.Core.UniNetDisconnectReason.ApprovalRejected, ch.DisconnectNotices[0], "거부 사유");
            Assert.AreEqual(0, connected, "ClientConnected 없음");
            Assert.AreEqual(0, server.GetOwner(player.NetId), "소유권 배정 없음");

            server.DetachConnection(ch);   // 킥 후 해제 관측 — never-approved 경로
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, disconnected, "미승인 연결 — ClientDisconnected 미발화 (게임 노출·유예 저장 없음)");
        }

        [Test]
        public void 승인_콜백이_없으면_CON001_동작이_불변이다()
        {
            var server = new NetworkServer();   // ConnectionApprover == null (기본)
            UniNetEnvironment.SetServer(server);
            int connected = 0;
            server.ClientConnected += _ => connected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            Assert.IsFalse(server.IsVersionApproved(ch.UniNetConnId), "미선언 — 미승인 (CON-001)");

            Assert.IsTrue(server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, ""), "선언 수용 (자격 증명 빈 문자열)");
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "콜백 없음 — 선언 즉시 승인 (CON-001)");
            Assert.AreEqual(1, connected, "즉시 해제 — 기존 동작");
        }

        [Test]
        public void 검사_비활성과_승인_콜백_조합에서는_선언까지_유보되고_스윕이_활성이다()
        {
            var server = new NetworkServer { ExpectedProtocolVersion = string.Empty, VersionDeclareTimeoutMs = 1000 };
            server.ConnectionApprover = r => Task.FromResult(r.Credential == "tok");
            UniNetEnvironment.SetServer(server);

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();
            Assert.IsFalse(server.IsVersionApproved(ch.UniNetConnId), "검사 비활성이어도 콜백 있으면 선언 대기 (즉시 승인 아님)");

            Assert.IsTrue(server.OnClientVersionDeclared(ch.UniNetConnId, "inactive-check-ignores-version", "tok"), "검사 비활성 — 버전 무관 수용");
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "콜백 게이트 통과 — 승인");

            var silent = new RecordingChannel();
            server.AttachConnection(silent);
            UniNetEnvironment.PumpMain();
            server.SweepUnapprovedConnections(UniNetTime.LocalClock() + 2.0);   // 검사 비활성+콜백 조합 — 스윕 활성
            Assert.AreEqual(1, silent.DisconnectNotices.Count, "무선언 연결 — 스윕 킥");
            Assert.AreEqual(UniNet.Core.UniNetDisconnectReason.VersionMismatch, silent.DisconnectNotices[0], "킥 사유");
        }

        [Test]
        public void 콜백은_선언_버전과_자격증명을_그대로_전달받는다()
        {
            var server = new NetworkServer();
            UniNetApprovalRequest seen = default;
            server.ConnectionApprover = r => { seen = r; return Task.FromResult(true); };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "tok-123");
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(ch.UniNetConnId, seen.ConnId, "connId 전달");
            Assert.AreEqual(server.ExpectedProtocolVersion, seen.ProtocolVersion, "선언 버전 전달");
            Assert.AreEqual("tok-123", seen.Credential, "자격 증명 전달");

            // 무증명 경로 (구버전 허브·2인자 오버로드) — 빈 문자열로 정규화된다
            var ch2 = new RecordingChannel();
            server.AttachConnection(ch2);
            UniNetEnvironment.PumpMain();
            UniNetApprovalRequest seen2 = default;
            server.ConnectionApprover = r => { seen2 = r; return Task.FromResult(true); };
            server.OnClientVersionDeclared(ch2.UniNetConnId, server.ExpectedProtocolVersion);   // 2인자 — credential 없음
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(string.Empty, seen2.Credential, "무증명 — 빈 문자열 정규화");
        }

        [Test]
        public void 비동기_승인_대기_중에는_게임_데이터가_풀리지_않는다()
        {
            var server = new NetworkServer();
            server.ConnectionApprover = async r => { await Task.Delay(80).ConfigureAwait(false); return true; };   // ConfigureAwait: context-free I/O model — a main-thread-captured continuation would starve while this EditMode test blocks the main thread
            UniNetEnvironment.SetServer(server);
            var player = NewSceneObject(server, "cap2-async");
            int connected = 0;
            server.ClientConnected += _ => connected++;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "cred");
            UniNetEnvironment.PumpMain();   // 콜백 시작 — await 대기 진입
            Assert.AreEqual(0, connected, "대기 중 ClientConnected 없음");
            Assert.AreEqual(0, ReplicatesOf(ch, player.NetId).Count, "대기 중 캐치업·복제 없음");

            for (int i = 0; i < 200 && !server.IsVersionApproved(ch.UniNetConnId); i++)
            {
                System.Threading.Thread.Sleep(10);   // 테스트 스레드 = 메인 스레드 — 드라이버 Update 대역
                UniNetEnvironment.PumpMain();
            }
            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "완료 후 승인");
            Assert.AreEqual(1, connected, "완료 후 ClientConnected");
            Assert.GreaterOrEqual(ReplicatesOf(ch, player.NetId).Count, 1, "완료 후 캐치업");
        }

        [Test]
        public void 콜백_예외는_LogFault로_관측되고_ApprovalRejected로_마감된다()
        {
            var server = new NetworkServer();
            server.ConnectionApprover = r => Task.FromException<bool>(new InvalidOperationException("승인 검증기 폭주"));
            UniNetEnvironment.SetServer(server);
            var faults = new List<Exception>();
            var prev = UniNetEnvironment.LogFault;
            UniNetEnvironment.LogFault = ex => faults.Add(ex);
            try
            {
                int connected = 0;
                server.ClientConnected += _ => connected++;
                var ch = new RecordingChannel();
                server.AttachConnection(ch);
                UniNetEnvironment.PumpMain();

                server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "cred");
                UniNetEnvironment.PumpMain();
                UniNetEnvironment.PumpMain();

                Assert.AreEqual(1, faults.Count, "LogFault 관측");
                Assert.AreEqual(1, ch.DisconnectNotices.Count, "거부 킥");
                Assert.AreEqual(UniNet.Core.UniNetDisconnectReason.ApprovalRejected, ch.DisconnectNotices[0], "킥 사유");
                Assert.AreEqual(0, connected, "승인 없음");
            }
            finally { UniNetEnvironment.LogFault = prev; }
        }

        [Test]
        public void 승인_대기_중_재선언은_콜백을_한_번만_호출한다()
        {
            var server = new NetworkServer();
            int calls = 0;
            server.ConnectionApprover = r => { calls++; return Task.FromResult(true); };
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            Assert.IsTrue(server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "c1"), "1차 선언");
            Assert.IsTrue(server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "c2"), "재선언 수용 (멱등)");
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, calls, "콜백 1회 — 게임 판단은 연결당 1회");
            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "승인");
        }

        [Test]
        public void 승인_대기_중_게임_ServerRpc는_승인_게이트에_걸린다()
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new NetworkServer();
            server.ConnectionApprover = r => pending.Task;
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "cred");
            UniNetEnvironment.PumpMain();   // 콜백 대기 진입

            Assert.IsFalse(server.IsVersionApproved(ch.UniNetConnId), "콜백 대기 — 미승인");
            Assert.AreEqual(0, server.UnapprovedServerRpcDrops, "초기 카운터 0");
            server.ReportUnapprovedServerRpc(ch.UniNetConnId);   // 생성 디스패치 게이트가 호출하는 지점
            Assert.AreEqual(1, server.UnapprovedServerRpcDrops, "대기 중 RPC 기각 관측");

            pending.SetResult(true);   // RunContinuationsAsynchronously — the verdict lands on the thread pool, then QueueOnMain carries it back
            for (int i = 0; i < 100 && !server.IsVersionApproved(ch.UniNetConnId); i++)
            {
                System.Threading.Thread.Sleep(10);   // pump until the thread-pool continuation → QueueOnMain round trip lands
                UniNetEnvironment.PumpMain();
            }
            Assert.IsTrue(server.IsVersionApproved(ch.UniNetConnId), "지연된 승인 적용");
        }

        [Test]
        public void 승인_콜백_대기도_총예산을_초과하면_VersionMismatch로_마감된다()
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);   // 영원히 끝나지 않는 검증기 (hang)
            var server = new NetworkServer { VersionDeclareTimeoutMs = 1000 };
            server.ConnectionApprover = r => pending.Task;
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();

            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion, "cred");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, ch.DisconnectNotices.Count, "대기 중 — 킥 없음");

            server.SweepUnapprovedConnections(UniNetTime.LocalClock() + 2.0);   // 선언+승인 총예산 초과
            Assert.AreEqual(1, ch.DisconnectNotices.Count, "콜백 hang — 스윕 마감 (무기한 유보 없음)");
            Assert.AreEqual(UniNet.Core.UniNetDisconnectReason.VersionMismatch, ch.DisconnectNotices[0], "킥 사유");
        }

        [Test]
        public void 와이어7_페이로드_상한_계약은_4096바이트다()
        {
            // 생성 서버 핸들러가 파싱 전에 이 상수로 초과 페이로드를 조용히 폐기한다 (미인증 경로, ADR-0041 선례).
            // EditMode는 생성 핸들러를 직접 호출할 수 없어 (세션 팩토리 필요) 상수 계약만 고정한다 — 폐기 동작은
            // 생성기 0.2.13 DLL 재배포 후 실RUDP 경로가 담당한다.
            Assert.AreEqual(4096, NetworkServer.MaxVersionDeclarationPayloadBytes, "선언 페이로드 상한");
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
