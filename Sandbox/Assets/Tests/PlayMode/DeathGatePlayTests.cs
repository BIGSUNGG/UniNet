using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// MOV-002 T4 — death->respawn->revive flow smoke over a real loopback session. Host mode: the single
    /// instance is the server AND the owner, so the predictor leg (gate edge, history drop, hard-snap) does
    /// NOT run here (isPredictor is false; pending is always 0) — those paths are pinned by the EditMode
    /// tests T2a/T2b/T3. This test guards the integrated server-side flow: death gate -> SetNetworkPosition
    /// respawn -> re-enable continues from the server position. A wire-inclusive 2-process owner-leg test
    /// is a deliberate follow-up (r17 ISSUE-2).
    /// </summary>
    public sealed class DeathGatePlayTests
    {
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
            int port = TestPortAllocator.Next();
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
        }

        [UnityTest]
        public IEnumerator 사망_게이트_후_리스폰하면_재개_렌더가_서버_좌표에서_계속된다()
        {
            yield return StartHost();

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.NetworkTransformPrefab));
            var nt = go.GetComponent<NetworkTransform>();
            var sim = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, 60f);
            nt.Simulation = sim;
            sim.SetInput(new TIn { Jump = true });   // rising arc — server-leg trajectory

            // rise into the arc so the death happens mid-motion
            float deadline = Time.realtimeSinceStartup + 5f;
            while (go.transform.position.y <= 0.2f && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.Greater(go.transform.position.y, 0.2f, "점프 상승 — 예측 궤적 진입");

            // death: gate off mid-arc (MOV-002 — pending history must be dropped here)
            nt.SimulationEnabled = false;

            // respawn: authoritative relocation while dead
            const float respawnX = 9f;
            nt.SetNetworkPosition(new Vector3(respawnX, 0f, 0f));
            yield return null;

            // revive: the component must re-anchor to the server position, not the dead arc
            nt.SimulationEnabled = true;
            sim.SetInput(default);   // no further movement — position must stay at the respawn point

            float settle = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < settle)
                yield return null;

            Assert.AreEqual(respawnX, go.transform.position.x, 0.05f,
                "재개 렌더 — 리스폰 좌표에서 계속 (사망 전 예측 좌표 회귀 없음)");
            Assert.AreEqual(0f, go.transform.position.y, 0.05f,
                "재개 높이 — 죽은 시대 점프 궤적이 재생되지 않는다");
        }
    }
}
