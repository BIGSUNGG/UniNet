using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// SCN-001/002 over a real loopback RUDP session — a client that joins AFTER a transition completed still
    /// receives the current scene (wire 11), reloads it, auto-reports (registration precedes the report —
    /// UniNetManager.OnSceneLoaded), and the held attach tail (ClientConnected) releases only then. Afterwards
    /// replicated deltas flow to the re-registered object (a real round trip on the loopback connection).
    /// Honest scope: single-process host topology — the object instance is shared, so the cross-instance
    /// full-state application VALUE is covered by the EditMode replay (SceneTransitionTests); here the control
    /// plane (wire 11 for a completed transition, held ClientConnected, post-report delta flow) is asserted
    /// over the real transport. ClientStop-then-reconnect models the late joiner (fresh session, REC-002 token
    /// burn is the documented contract).
    /// </summary>
    public sealed class CurrentSceneJoinPlayTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            foreach (var boot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                boot.enabled = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            UniNetManager.HostStop();
            // Unload LateScene only when it is NOT the active scene. The active-scene case (a
            // Single-mode transition ended with LateScene as the only scene) is left to consumer
            // guards: reloading the captured environment scene here re-imports a full game scene
            // mid-session, and stale queued RPCs then detonate on its destroyed players in later
            // fixtures.
            var late = SceneManager.GetSceneByName("LateScene");
            if (late.IsValid() && late.isLoaded && SceneManager.GetActiveScene() != late)
            {
                var op = SceneManager.UnloadSceneAsync(late);
                while (op != null && !op.isDone) yield return null;
            }
        }

        [OneTimeTearDown]
        public void RestoreLogChecks() => LogAssert.ignoreFailingMessages = false;

        [UnityTest]
        public IEnumerator 완료된_전환_후_재접속_클라는_현재_씬을_받고_보고_후_해제된다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            var server = UniNetEnvironment.Server;
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "1차 접속 Welcome");

            // transition to the baked LateScene — the real client receives wire 11, loads, registers, auto-reports
            string gateScene = null;
            server.AllClientsSceneLoaded += s => gateScene = s;
            server.LoadScene("LateScene");
            yield return WaitUntil(() => gateScene == "LateScene", 8, "전환 게이트 완료 (실RUDP 왕복 + 실씬 로드 + 자동 보고)");
            Assert.IsNull(server.PendingScene, "전환 완료");

            // the LATE JOINER — stop and reconnect with a fresh session (token burn is the documented contract)
            UniNetManager.ClientStop();
            int connectedCount = 0;
            long secondConn = -1;
            server.ClientConnected += c => { connectedCount++; secondConn = c; };
            var reconnect = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!reconnect.IsCompleted) yield return null;
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "재접속 Welcome");
            Assert.AreEqual(0, connectedCount, "SCN-002 — 씬 ready 보고 전 ClientConnected 유보");

            // SCN-001 — the reconnecting client receives wire 11 for the COMPLETED transition and reloads the scene;
            // registration runs (ADR-0025 hook) and the auto-report releases the held tail
            yield return WaitUntil(() =>
            {
                var player = Object.FindFirstObjectByType<VerifyPlayer>(FindObjectsInactive.Exclude);
                return player != null && player.IsClient;
            }, 8, "재접속 클라 — 현재 씬 wire 11 수신·재로드·등록 (SCN-001)");
            yield return WaitUntil(() => connectedCount == 1, 5, "보고 후 ClientConnected 해제 (SCN-002)");
            Assert.AreEqual(UniNetEnvironment.Client.LocalConnId, secondConn, "해제된 연결 = 재접속 연결");

            var verify = Object.FindFirstObjectByType<VerifyPlayer>(FindObjectsInactive.Exclude);
            Assert.NotNull(verify, "LateScene VerifyPlayer");
            // The host receiver keeps authority fields but still calls Notify and updates __uninetSeen (Emitter.cs).
            // Await the baseline first; resetting the signal prevents it from satisfying the later delta wait.
            yield return WaitUntil(() => verify.ScoreNotified, 5, "보고 후 전체상태 RepNotify 수신");
            Assert.AreEqual(100, verify.Score, "델타 전 기준값");
            verify.ScoreNotified = false;
            verify.LastPrevScore = -999;
            verify.Score = 777;   // local assignment is NOT receive evidence
            Assert.IsFalse(verify.ScoreNotified, "대입만으로 Notify 발화 없음");
            yield return WaitUntil(() => verify.ScoreNotified, 5, "변경 후 실제 수신 RepNotify");
            Assert.AreEqual(100, verify.LastPrevScore, "생성 수신기가 보존한 이전 수신값");
            UnityEngine.Debug.Log("[UNINET-VERIFY] CurrentSceneJoin PASS — completed-transition joiner got wire 11, held event released on report, post-report RepNotify received (prev=100)");
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, float timeout, string label)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"대기 시간 초과: {label}");
                yield return null;
            }
        }
    }
}
