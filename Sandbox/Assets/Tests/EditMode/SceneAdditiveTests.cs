using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// ADR-0057 — additive scene synchronization: LoadSceneAdditive/UnloadScene transitions, the one-at-a-time
    /// guard, scene-tagged object destruction on unload, game-data hold during the transition, joiner replay of
    /// the active set (base + additive, with modes), and the completed-set bookkeeping.
    /// </summary>
    public sealed class SceneAdditiveTests
    {
        private NetworkServer _server = null!;
        private RecordingChannel _chA = null!;

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _server = new NetworkServer();
            UniNetEnvironment.SetServer(_server);
            _chA = new RecordingChannel();
            _server.AttachConnection(_chA);
            _server.OnClientVersionDeclared(_chA.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();
            _chA.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.SetServer(null);
        }

        private void ReportReady(RecordingChannel ch, string scene)
            => _server.OnClientSceneLoaded(ch.UniNetConnId, scene);

        // T1 — additive 전환: 모드가 와이어에 실리고 게임 데이터는 보고까지 유보된다.
        [Test]
        public void additive_지시는_모드와_함께_브로드캐스트되고_보고까지_게임데이터를_유보한다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _chA.Clear();

            _server.LoadSceneAdditive("Stage");

            Assert.AreEqual(1, _chA.LoadScenes.Count, "additive 지시 1회");
            Assert.AreEqual("Stage", _chA.LoadScenes[0]);
            Assert.AreEqual(SceneLoadMode.Additive, _chA.LoadSceneModes[0], "와이어 모드 Additive");
            Assert.AreEqual("Stage", _server.PendingScene, "전환 진행 중");
            Assert.IsFalse(_server.IsClientSceneReady(_chA.UniNetConnId), "보고 전 — 유보");

            ReportReady(_chA, "Stage");
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(_server.IsClientSceneReady(_chA.UniNetConnId), "보고 후 해제");
            CollectionAssert.AreEqual(new[] { "Stage" }, _server.AdditiveScenes, "완료 additive 집합");
        }

        // T2 — 동시 전환 불가: 진행 중 전환은 두 번째 시작을 명시적으로 거부한다.
        [Test]
        public void 전환_진행_중에는_두번째_전환을_거부한다()
        {
            _server.LoadScene("Base");   // 아무도 보고 전 — 전환 진행 중

            Assert.Throws<System.InvalidOperationException>(() => _server.LoadScene("Other"));
            Assert.Throws<System.InvalidOperationException>(() => _server.LoadSceneAdditive("Stage"));
        }

        // T3 — 이미 활성 씬의 additive 중복 적재는 거부된다.
        [Test]
        public void 이미_활성인_씬의_additive_적재는_거부된다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();

            Assert.Throws<System.ArgumentException>(() => _server.LoadSceneAdditive("Base"));
        }

        // T4 — unload: 해당 씬의 등록 객체 파괴가 언로드 지시보다 먼저 전파되고, 완료 시 집합에서 제거된다.
        [Test]
        public void unload는_씬_객체_파괴_후_지시하며_완료시_집합에서_제거한다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("Stage");
            ReportReady(_chA, "Stage");
            UniNetEnvironment.PumpMain();

            var baseObj = new GameObject("base-obj").AddComponent<VerifyPlayer>();
            _server.RegisterSceneObject(baseObj.NetId, new NetworkBehaviour[] { baseObj }, "Base");
            var stageObj = new GameObject("stage-obj").AddComponent<VerifyPlayer>();
            _server.RegisterSceneObject(stageObj.NetId, new NetworkBehaviour[] { stageObj }, "Stage");
            _chA.Clear();

            _server.UnloadScene("Stage");

            Assert.AreEqual(stageObj.NetId, _chA.Destroys.Count > 0 ? _chA.Destroys[0] : 0, "Stage 객체 파괴가 먼저 전파");
            Assert.AreEqual(0, _chA.Spawns.Count, "Base 객체는 파괴되지 않음");
            Assert.IsNull(_server.GetEntry(stageObj.NetId), "서버 레지스트리에서 제거");
            Assert.IsNotNull(_server.GetEntry(baseObj.NetId), "Base 객체는 레지스트리 유지");
            Assert.AreEqual("Stage", _chA.LoadScenes[_chA.LoadScenes.Count - 1], "언로드 지시 후속");
            Assert.AreEqual(SceneLoadMode.Unload, _chA.LoadSceneModes[_chA.LoadSceneModes.Count - 1], "와이어 모드 Unload");

            ReportReady(_chA, "Stage");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, _server.AdditiveScenes.Count, "완료 — additive 집합에서 제거");
        }

        // T5 — unload 대상 검증: base 씬·미등록 additive 씬은 언로드할 수 없다.
        [Test]
        public void base_씬이나_미등록_씬은_unload할_수_없다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();

            Assert.Throws<System.ArgumentException>(() => _server.UnloadScene("Base"));
            Assert.Throws<System.ArgumentException>(() => _server.UnloadScene("NeverLoaded"));
        }

        // T6 — 완료 후 조인: 기본 씬 + additive 집합이 모드와 함께 순서대로 전달되고, 전 보고 전까지 게임 데이터가 유보된다.
        [Test]
        public void 완료_후_조인자는_활성_집합_전체를_순서대로_받는다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("StageA");
            ReportReady(_chA, "StageA");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("StageB");
            ReportReady(_chA, "StageB");
            UniNetEnvironment.PumpMain();

            int connected = 0;
            _server.ClientConnected += _ => connected++;
            var chJoin = new RecordingChannel();
            _server.AttachConnection(chJoin);
            _server.OnClientVersionDeclared(chJoin.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();

            CollectionAssert.AreEqual(new[] { "Base", "StageA", "StageB" }, chJoin.LoadScenes, "활성 집합 순서 재생");
            CollectionAssert.AreEqual(
                new[] { SceneLoadMode.Replace, SceneLoadMode.Additive, SceneLoadMode.Additive },
                chJoin.LoadSceneModes, "각 씬의 와이어 모드");
            Assert.AreEqual(0, connected, "전 보고 전 ClientConnected 없음");

            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "Base");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, connected, "첫 보고만으로는 유지 — 잔여 대기 씬 존재");

            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "StageA");
            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "StageB");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, connected, "전체 보고 완료 — attach 꼬리 실행");
        }

        // T7 — 진행 중 조인: 전환 중 additive는 조인 시퀀스에 포함된다 (조인자가 게이트를 막지 않는다).
        [Test]
        public void 전환_진행_중_조인자도_전환_씬을_받는다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("Stage");
            // 진행 중 조인 (아무도 보고 전)

            var chJoin = new RecordingChannel();
            _server.AttachConnection(chJoin);
            _server.OnClientVersionDeclared(chJoin.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();

            CollectionAssert.AreEqual(new[] { "Base", "Stage" }, chJoin.LoadScenes, "기본 + 진행 중 additive");

            ReportReady(_chA, "Stage");
            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "Base");
            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "Stage");
            UniNetEnvironment.PumpMain();
            Assert.IsNull(_server.PendingScene, "전원 보고 — 게이트 완료");
        }

        // T8 — 유예 보고: 조인자가 기다리지 않는 씬 보고는 무시된다 (위조/낡은 보고 방어).
        [Test]
        public void 대기중이_아닌_씬_보고는_무시된다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("Stage");

            _server.OnClientSceneLoaded(_chA.UniNetConnId, "Unknown");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual("Stage", _server.PendingScene, "무효 보고 — 전환 유지");

            ReportReady(_chA, "Stage");
            UniNetEnvironment.PumpMain();
            Assert.IsNull(_server.PendingScene, "유효 보고 — 완료");
        }

        // T9 — Replace는 additive 집합 전체를 대체한다.
        [Test]
        public void replace_완료시_additive_집합이_비워진다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("Stage");
            ReportReady(_chA, "Stage");
            UniNetEnvironment.PumpMain();

            _server.LoadScene("NewWorld");
            ReportReady(_chA, "NewWorld");
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(0, _server.AdditiveScenes.Count, "Replace — additive 집합 소거");
        }

        // T10 — r1-1 세대 인터리브: 조인 시퀀스 진행 중 새 전환이 세대를 올려도 기존 씬의 유효 보고는 기각되지 않는다 (스칼라 가드의 영구 교찰 회귀).
        [Test]
        public void 시퀀스_진행_중_새_전환이_세대를_올려도_기존_보고가_소진된다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("StageA");
            ReportReady(_chA, "StageA");
            UniNetEnvironment.PumpMain();

            var chJoin = new RecordingChannel();
            _server.AttachConnection(chJoin);
            _server.OnClientVersionDeclared(chJoin.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();
            CollectionAssert.AreEqual(new[] { "Base", "StageA" }, chJoin.LoadScenes);

            // 보고는 Base 적재 완료 시점(디스패치 수신)에 세대를 캡처한다 — 두 wire-11 송신 이후라 스칼라 최신값이다
            long captured = _server.CaptureSceneLoadGeneration(chJoin.UniNetConnId);
            // 메인 큐 처리 전에 게임이 새 additive 전환 시작 (세대 상승 — 구 스칼라 가드는 여기서 보고를 폐기해 영구 교찰했다)
            _server.LoadSceneAdditive("Extra");   // 새 전환은 조인자도 유보한다 (Extra 대기 추가)
            UniNetEnvironment.PumpMain();

            // 낡은 캡처 세대로 도착한 Base/StageA 보고는 여전히 유효 — 항목별 세대 대조(>= entry gen)
            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "Base", captured);
            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "StageA", captured);
            UniNetEnvironment.PumpMain();
            Assert.IsFalse(_server.IsClientSceneReady(chJoin.UniNetConnId), "새 전환 Extra가 유보 중 — Base/StageA는 소진됐고 Extra만 남음");

            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "Extra", _server.CaptureSceneLoadGeneration(chJoin.UniNetConnId));
            _server.OnClientSceneLoaded(_chA.UniNetConnId, "Extra", _server.CaptureSceneLoadGeneration(_chA.UniNetConnId));
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(_server.IsClientSceneReady(chJoin.UniNetConnId), "전체 소진 — 낡은 세대 보고도 기각되지 않았다 (r1-1 회귀)");
            Assert.IsNull(_server.PendingScene, "전원 보고 — 게이트 완료");
        }

        // T11 — r1-1 SCN-003 유지: 씬 송신 이전에 캡처된 보고는 항목별 세대로 기각된다 (동명 재전환 방어 유지).
        [Test]
        public void 송신_이전에_캡처된_보고는_항목별_세대로_기각된다()
        {
            long stale = _server.CaptureSceneLoadGeneration(_chA.UniNetConnId);   // wire-11 송신 이전 — 이 세대로 캡처된 보고의 dispatch가 송신보다 앞선다
            _server.LoadScene("Base");   // 송신 시점 세대 각인 — entry gen = stale + 2 (SCN-003 리셋 증분 + AddPendingScene 각인 증분)
            _server.OnClientSceneLoaded(_chA.UniNetConnId, "Base", stale);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual("Base", _server.PendingScene, "낡은 보고 — 전환 유지 (SCN-003)");

            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            Assert.IsNull(_server.PendingScene, "유효 보고 — 완료");
        }

        // T12 — r1-1 Replace 선행 소거: 진행 중 시퀀스에 Replace가 도착하면 연결의 잔여 대기 씬은 교체된다 (영구 미보고 방지).
        [Test]
        public void replace_수신시_연결의_잔여_대기_씬이_소거된다()
        {
            _server.LoadScene("Base");
            ReportReady(_chA, "Base");
            UniNetEnvironment.PumpMain();
            _server.LoadSceneAdditive("StageA");
            ReportReady(_chA, "StageA");
            UniNetEnvironment.PumpMain();

            var chJoin = new RecordingChannel();
            _server.AttachConnection(chJoin);
            _server.OnClientVersionDeclared(chJoin.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();
            CollectionAssert.AreEqual(new[] { "Base", "StageA" }, chJoin.LoadScenes);

            _server.LoadScene("NewWorld");   // 조인 시퀀스 진행 중 Replace — 세계 교체
            UniNetEnvironment.PumpMain();

            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "Base", _server.CaptureSceneLoadGeneration(chJoin.UniNetConnId));
            UniNetEnvironment.PumpMain();
            Assert.IsFalse(_server.IsClientSceneReady(chJoin.UniNetConnId), "Base 보고 무시 — 대기 집합이 NewWorld로 교체됨");

            _server.OnClientSceneLoaded(chJoin.UniNetConnId, "NewWorld", _server.CaptureSceneLoadGeneration(chJoin.UniNetConnId));
            ReportReady(_chA, "NewWorld");
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(_server.IsClientSceneReady(chJoin.UniNetConnId), "NewWorld 보고 — 해제");
            Assert.IsNull(_server.PendingScene, "게이트 완료");
        }

        // T13 — r1-2 알림 예외 불변식: 마지막 보고 해제 중 구독자가 throw해도 보고는 소진되고 게이트는 완료된다.
        [Test]
        public void 해제_알림_예외로_보고가_되살아나지_않는다()
        {
            _server.LoadScene("Base");   // 전환 진행 중 조인 — CatchupHeld=true 경로 (ClientConnected는 ready 보고 때 발화)
            var chJoin = new RecordingChannel();
            _server.AttachConnection(chJoin);
            _server.OnClientVersionDeclared(chJoin.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();

            _server.ClientConnected += _ => throw new System.InvalidOperationException("subscriber fault (r1-2)");

            // RunPostSceneReady의 ClientConnected 구독자 예외는 밖으로 새어나가되(재throw) 보고 소진·게이트 완료는 유지돼야 한다
            Assert.Throws<System.InvalidOperationException>(() => _server.OnClientSceneLoaded(chJoin.UniNetConnId, "Base", _server.CaptureSceneLoadGeneration(chJoin.UniNetConnId)));
            ReportReady(_chA, "Base");   // 기존 참여자 보고 — notifyConnected=false (예외 없음)
            UniNetEnvironment.PumpMain();

            Assert.IsTrue(_server.IsClientSceneReady(chJoin.UniNetConnId), "보고 소진 유지 — 되살아나지 않음");
            Assert.IsNull(_server.PendingScene, "finally의 게이트 평가로 완료 — 교찰 없음");
        }
    }
}
