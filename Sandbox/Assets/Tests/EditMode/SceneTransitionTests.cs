using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// Scene transition synchronization (ADR-0035) — server broadcast, client report, and the all-ready gate.
    /// </summary>
    public sealed class SceneTransitionTests
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
        public void LoadScene가_전_연결에_브로드캐스트된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            chA.Clear(); chB.Clear();

            server.LoadScene("BattleScene");

            Assert.AreEqual(1, chA.LoadScenes.Count, "연결 A — 씬 지시 수신");
            Assert.AreEqual("BattleScene", chA.LoadScenes[0], "씬 식별자");
            Assert.AreEqual(1, chB.LoadScenes.Count, "연결 B — 씬 지시 수신");
            Assert.AreEqual("BattleScene", server.PendingScene, "서버 대기 씬");
        }

        [Test]
        public void 전원_보고_시_게이트가_발화한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            string? gateScene = null;
            server.AllClientsSceneLoaded += s => gateScene = s;

            server.LoadScene("Battle");
            server.OnClientSceneLoaded(chA.UniNetConnId, "Battle");
            Assert.IsNull(gateScene, "1명만 보고 — 게이트 미발화");
            Assert.IsTrue(server.IsClientSceneReady(chA.UniNetConnId), "A 준비");
            Assert.IsFalse(server.IsClientSceneReady(chB.UniNetConnId), "B 미준비");

            server.OnClientSceneLoaded(chB.UniNetConnId, "Battle");
            Assert.AreEqual("Battle", gateScene, "전원 보고 — 게이트 발화 (ADR-0035)");
            Assert.IsNull(server.PendingScene, "게이트 후 대기 해제");
        }

        [Test]
        public void 이씬_보고는_무시된다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();

            string? gateScene = null;
            server.AllClientsSceneLoaded += s => gateScene = s;

            server.LoadScene("Battle");
            server.OnClientSceneLoaded(ch.UniNetConnId, "WrongScene");   // stale
            Assert.IsNull(gateScene, "이씬 보고 — 게이트 미발화");
            Assert.IsFalse(server.IsClientSceneReady(ch.UniNetConnId), "준비 미표시");
        }
    
        [Test]
        public void 후발_접속자도_진행_중_전환에_참여한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            chA.Clear();

            server.LoadScene("Battle");

            // Late joiner — AttachConnection sends Welcome + LoadScene
            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            // no Clear — the late-joiner test needs LoadScenes from the catchup SendLoadScene

            Assert.AreEqual(1, chB.LoadScenes.Count, "후발 접속자 — 씬 지시 수신 (ADR-0035)");
            Assert.AreEqual("Battle", chB.LoadScenes[0], "씬 식별자 일치");
        }

    }
}