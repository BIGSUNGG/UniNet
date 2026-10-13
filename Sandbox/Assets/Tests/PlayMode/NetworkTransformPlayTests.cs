using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// P4 NetworkTransform over a real loopback RUDP session (ADR-0038) — the injected simulation drives
    /// authoritative server movement, the jump state (velocity) runs its arc through the component, and
    /// SetNetworkPosition applies immediately. Host mode: the single instance is the authority, so these
    /// verify the server leg + replication; owner prediction convergence is verified by the engine and
    /// component tests plus the 2-process runner.
    /// </summary>
    public sealed class NetworkTransformPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 8;   // random port per run — avoids listeners lingering in the editor process after play mode ends

        private struct TIn { public sbyte Dx; public bool Jump; }
        private struct TSt { public float Vy; }

        private static void Step(ref TSt st, ref float x, ref float y, ref float z, in TIn i, float dt)
        {
            bool grounded = y <= 0.001f && st.Vy <= 0f;
            if (i.Jump && grounded)
                st.Vy = 5f;
            st.Vy = grounded && !i.Jump && st.Vy <= 0f ? 0f : st.Vy - 20f * dt;
            y += st.Vy * dt;
            if (y < 0f)
            {
                y = 0f;
                st.Vy = 0f;
            }
            x += i.Dx * 4f * dt;
        }

        private static void WriteInput(ref MessageProtocol.Serialize.MessageBufferWriter w, in TIn i)
        {
            w.WriteSByte(i.Dx);
            w.WriteBoolean(i.Jump);
        }

        private static TIn ReadInput(ref MessageProtocol.Serialize.MessageBufferReader r)
            => new TIn { Dx = r.ReadSByte(), Jump = r.ReadBoolean() };

        private static void WriteState(ref MessageProtocol.Serialize.MessageBufferWriter w, in TSt s)
            => w.WriteSingle(s.Vy);

        private static TSt ReadState(ref MessageProtocol.Serialize.MessageBufferReader r)
            => new TSt { Vy = r.ReadSingle() };

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        private static IEnumerator StartHost()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
        }

        [UnityTest]
        public IEnumerator 시뮬레이션이_서버_권위_이동을_구동한다()
        {
            yield return StartHost();

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.NetworkTransformPrefab));
            var nt = go.GetComponent<NetworkTransform>();
            // inject AFTER spawn — the simulation is a plain object (not serialized, not replicated), like the old rule delegate
            nt.Simulation = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, 60f);
            ((MoveSimulation<TIn, TSt>)nt.Simulation).SetInput(new TIn { Dx = 1 });   // the input stream keeps applying it every step

            float deadline = Time.realtimeSinceStartup + 5f;
            while (go.transform.position.x <= 0.3f && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.Greater(go.transform.position.x, 0.3f, "주입 시뮬레이션이 서버 스텝을 구동 (입력 스트림 유지)");
        }

        [UnityTest]
        public IEnumerator 점프_상태_시뮬레이션이_궤적을_완주한다()
        {
            yield return StartHost();

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.NetworkTransformPrefab));
            var nt = go.GetComponent<NetworkTransform>();
            var sim = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, 60f);
            nt.Simulation = sim;
            sim.SetInput(new TIn { Jump = true });   // hold — launches on the first grounded step

            // rise: apex at 5/20 = 0.25s, apex height 0.625
            float deadline = Time.realtimeSinceStartup + 5f;
            while (go.transform.position.y <= 0.4f && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.Greater(go.transform.position.y, 0.4f, "점프 상승 — 속도 상태가 궤적을 구동");

            sim.SetInput(default);   // release — gravity lands

            float landing = Time.realtimeSinceStartup + 5f;
            while (go.transform.position.y > 0.01f && Time.realtimeSinceStartup < landing)
                yield return null;
            Assert.AreEqual(0f, go.transform.position.y, 0.01f, "착지 — 상태(속도)가 궤적을 완주");
        }

        [UnityTest]
        public IEnumerator SetNetworkPosition이_권위_좌표를_즉시_반영한다()
        {
            yield return StartHost();

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.NetworkTransformPrefab));
            var nt = go.GetComponent<NetworkTransform>();

            nt.SetNetworkPosition(new Vector3(5f, 1f, 6f));
            yield return null;   // one server tick frame — confirm the authoritative position holds

            Assert.AreEqual(5f, go.transform.position.x, 0.01f, "SetNetworkPosition — 권위 좌표 반영");
            Assert.AreEqual(6f, go.transform.position.z, 0.01f);
        }
    }
}
