using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// Late-scene registration (ADR-0025) — NetworkBehaviours in scenes loaded AFTER connecting must register
    /// on both ends (previously: registration ran once at ServerAsync/ClientAsync time, so late-loaded scene
    /// objects stayed unregistered and their RPC sends were silent no-ops). Loads the baked LateScene
    /// (LateSceneBaker) additively over the live connection. On success it logs a [UNINET-VERIFY] marker
    /// (grep target for batch runs).
    /// </summary>
    public sealed class SceneLoadRegistrationTests
    {
        private static readonly int Port = 32000 + (System.Environment.TickCount % 2000) * 8 + 16;   // random port per run — same rationale as HostRoundtripTests

        [TearDown]
        public void StopListeners()
        {
            UniNetManager.HostStop();
            var late = SceneManager.GetSceneByName("LateScene");
            if (late.IsValid() && late.isLoaded) SceneManager.UnloadSceneAsync(late);
        }

        [UnityTest]
        public IEnumerator 늦게_로드된_씬의_오브젝트가_양단에_등록되고_RPC가_전송된다()
        {
            // 1) Start hosting in the test scene — it holds no network objects, startup registration finds nothing
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);

            // 2) Load a scene holding a NetworkBehaviour AFTER the connection — the driver's sceneLoaded
            //    hook must register it on both ends (the old behavior left it unregistered: IsServer false).
            //    Registration is deferred to the next main-pump drain (ADR-0025 revision), so WAIT for it.
            SceneManager.LoadScene("LateScene", LoadSceneMode.Additive);
            yield return WaitUntil(() => SceneManager.GetSceneByName("LateScene").isLoaded, 5);   // PlayMode LoadScene 은 프레임 경계에서 완료된다

            var player = Object.FindFirstObjectByType<VerifyPlayer>();
            Assert.NotNull(player, "LateScene 의 VerifyPlayer");
            yield return WaitUntil(() => player.IsServer && player.IsClient, 5);
            Assert.IsTrue(player.IsServer, "서버 등록 — 늦게 로드된 씬 오브젝트");
            Assert.IsTrue(player.IsClient, "클라 등록 — 동일 인스턴스");
            Assert.AreSame(player, (VerifyPlayer)UniNetEnvironment.Client.Get(player.NetId, player.SubId), "클라 등록 사전 — 동일 인스턴스");

            // 3) The regression itself: an RPC sent from the late-registered object must reach the wire
            //    (unregistered IsServer=false made this a silent no-op before the fix)
            player.RpcDeath();
            yield return WaitUntil(() => player.MulticastCount >= 1, 5);
            Assert.AreEqual(1, player.MulticastCount, "멀티캐스트 정확히 1회 — 늦은 등록 오브젝트의 RPC 실제 전송");

            // 4) Idempotency — the hook body's exact calls re-run directly: everything is already
            //    registered, so both return 0 (skip-guard — no snapshot re-init over live state)
            Assert.AreEqual(0, NetworkBehaviour.RegisterAllToServer(), "재등록 스킵 — 서버 0건");
            Assert.AreEqual(0, NetworkBehaviour.RegisterAllToClient(), "재등록 스킵 — 클라 0건");

            Debug.Log("[UNINET-VERIFY] SceneLoadRegistration PASS — late scene objects registered on both ends + multicast round-trip + idempotent re-run");
        }

        private static IEnumerator WaitUntil(System.Func<bool> cond, float seconds)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond())
            {
                if (sw.Elapsed.TotalSeconds > seconds) Assert.Fail($"{seconds}초 내 조건 미충족");
                yield return null;
            }
        }
    }
}
