using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>Driver-level relevancy guard — the tick on the frame right after a plain Destroy must not touch the destroyed component (review round 1, item 1).</summary>
    public sealed class RelevancyDriverTests
    {
        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator 일반_Destroy_직후_프레임의_틱은_예외_없이_돈다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab));
            var player = go.GetComponent<SpawnablePlayer>();
            player.NetworkCullDistance = 10f;   // activates the cull path — the path where touching a destroyed transform.position was the problem (a server-side flag, so set it after spawn)

            var server = UniNetEnvironment.Server;
            server.SetViewerPosition(1L, 0f, 0f, 0f);   // viewer position of the first connection (host client) — distance computation touches transforms

            LogAssert.ignoreFailingMessages = false;
            Object.Destroy(go);   // plain Destroy — the driver sweep path (explicitly supported)
            yield return null;    // one sweep + tick frame — must pass without touching the destroyed entry
            yield return null;

            Assert.IsTrue(player == null, "오브젝트가 파괴됐다");
            Assert.IsNull(server.Get(player.NetId), "스윕 후 등록이 제거된다");
        }
    }
}
