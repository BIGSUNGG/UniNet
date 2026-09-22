using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>P4 NetworkTransform component — verifies base-component RPC, server simulation, and position replication (PlayMode loopback).</summary>
    public sealed class NetworkTransformPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 8;   // random port per run — avoids listeners lingering in the editor process after play mode ends

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator SubmitMove가_기반_컴포넌트_RPC로_서버_시뮬레이션을_구동한다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var template = new GameObject("NTMove");
            template.AddComponent<NetworkTransform>();
            var go = UniNetManager.NetworkInstantiate(template);
            var nt = go.GetComponent<NetworkTransform>();
            UnityEngine.Object.Destroy(template);
            nt.MovementRule = (ref float x, ref float y, ref float z, float ix, float iy, float iz, float dt) =>
                x += ix * 4f * dt;   // injected game movement rule (DIP) — a delegate (not serialized, not replicated), so inject it into the clone after spawn

            // SubmitMove → base-component ServerRpc (loopback) → authoritative server input → MovementRule step
            nt.SubmitMove(1f, 0f, 0f);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (go.transform.position.x <= 0.3f && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.Greater(go.transform.position.x, 0.3f, "기반 컴포넌트 RPC가 서버 시뮬레이션을 구동한다 (라우팅 검증)");
        }

        [UnityTest]
        public IEnumerator SetNetworkPosition이_권위_좌표를_즉시_반영한다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var template = new GameObject("NTPosition");
            template.AddComponent<NetworkTransform>();
            var go = UniNetManager.NetworkInstantiate(template);
            var nt = go.GetComponent<NetworkTransform>();
            UnityEngine.Object.Destroy(template);

            nt.SetNetworkPosition(new Vector3(5f, 1f, 6f));
            yield return null;   // one server tick frame — confirm the authoritative position holds

            Assert.AreEqual(5f, go.transform.position.x, 0.01f, "SetNetworkPosition — 권위 좌표 반영");
            Assert.AreEqual(6f, go.transform.position.z, 0.01f);
        }
    }
}
