using System.Reflection;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// NetworkTransform component paths (ADR-0038) — the fixed-step owner loop, ack-driven rollback convergence,
    /// and teleport re-anchor, driven directly with a real MoveSimulation (engine math is covered by MoveSimulationTests).
    /// Runs without a server: the environment client is set up so IsOwner is true and the network handlers are invoked directly.
    /// </summary>
    public sealed class NetworkTransformComponentTests
    {
        /// <summary>Flat movement rule — x advances by input × dt (stateless struct, minimal).</summary>
        private struct TIn { public sbyte Dx; public bool Jump; }
        private struct TSt { public float Vy; }

        private const float StepHz = 60f;

        private static void Step(ref TSt st, ref float x, ref float y, ref float z, in TIn i, float dt)
        {
            bool grounded = y <= 0.001f && st.Vy <= 0f;
            if (i.Jump && grounded)
                st.Vy = 4f;
            st.Vy = grounded && !i.Jump && st.Vy <= 0f ? 0f : st.Vy - 20f * dt;
            y += st.Vy * dt;
            if (y < 0f)
            {
                y = 0f;
                st.Vy = 0f;
            }
            x += i.Dx * 5f * dt;
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

        private static NetworkTransform NewOwner(out MoveSimulation<TIn, TSt> sim)
        {
            var go = new GameObject("nt-component");
            var nt = go.AddComponent<NetworkTransform>();
            sim = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, StepHz);
            nt.Simulation = sim;

            var client = new NetworkClient();
            client.SetLocalConnId(1);
            client.ApplyOwner(nt.NetId, 1);   // owner = connId 1 = LocalConnId → IsOwner = true
            UniNetEnvironment.SetClient(client);
            // IsClient additionally requires client registration — invoke the internal marker like RegisterAllToClient does
            typeof(NetworkBehaviour).GetMethod("MarkClientRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nt, null);
            return nt;
        }

        private static void InvokeUpdate(NetworkTransform nt)
            => typeof(NetworkTransform).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nt, null);

        private static float GetField(NetworkTransform nt, string name)
            => (float)typeof(NetworkTransform).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(nt);

        private static void SetField(NetworkTransform nt, string name, object value)
            => typeof(NetworkTransform).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(nt, value);

        [TearDown]
        public void Cleanup()
        {
            UniNetEnvironment.SetClient(null);
            foreach (var go in Object.FindObjectsByType<NetworkTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(go.gameObject);
        }

        [Test]
        public void 예측_스텝은_고정_주기로_진행하고_입력을_기록한다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            sim.SetInput(new TIn { Dx = 1 });

            // simulate 1 second of frames via the interface — 60 fixed steps
            float x = 0f, y = 0f, z = 0f;
            for (uint seq = 1; seq <= 60; seq++)
                isim.PredictStep(seq, ref x, ref y, ref z);

            Assert.AreEqual(5f, x, 0.01f, "고정 스텝 60회 × 5u/s × dt — 정확히 1초 분");
            Assert.AreEqual(60, isim.PendingCount, "전 입력 미승인 상태");
        }

        [Test]
        public void ack_불일치_시_컴포넌트_롤백_경로가_서버_상태로_수렴한다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;

            // owner predicted a jump (5 steps) — mirror the final prediction into the component fields
            // (Update would normally keep them in lockstep with the engine)
            sim.SetInput(new TIn { Jump = true });
            float px = 0f, py = 0f, pz = 0f;
            isim.PredictStep(1, ref px, ref py, ref pz);
            sim.SetInput(default);
            for (uint seq = 2; seq <= 5; seq++)
                isim.PredictStep(seq, ref px, ref py, ref pz);
            Assert.Greater(py, 0f, "점프 예측 진행 중");
            SetField(nt, "_predX", px);
            SetField(nt, "_predY", py);
            SetField(nt, "_predZ", pz);

            // fabricate the replicated server state: grounded (y=0, vy=0) at ack 5
            var grounded = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, StepHz);
            byte[] serverState = ((INetworkMoveSimulation)grounded).WriteState();   // default state — vy 0
            SetField(nt, "_px", 0f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 0f);
            SetField(nt, "_ackSeq", 5u);
            typeof(NetworkTransform).GetMethod("OnAckSeqChanged", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(nt, new object[] { 0u });

            Assert.AreEqual(0f, GetField(nt, "_predY"), 1e-4f, "롤백 — 서버 권위 좌표로 수렴");
            Assert.AreEqual(0, isim.PendingCount, "전부 승인 처리");
        }

        [Test]
        public void 텔레포트_재앵커가_동작한다()
        {
            var nt = NewOwner(out var sim);

            // prediction anchored far from the server position with no pending inputs (respawn scenario)
            SetField(nt, "_predX", 50f);
            SetField(nt, "_predY", 3f);
            SetField(nt, "_predZ", 0f);
            SetField(nt, "_px", 0f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 0f);
            nt.SnapThreshold = 0.5f;
            SetField(nt, "_accumulator", -10000f);   // edit-mode Time.deltaTime is not guaranteed 0 — guarantee no steps run

            InvokeUpdate(nt);   // no steps — the teleport check runs

            Assert.AreEqual(0f, GetField(nt, "_predX"), 0.01f, "텔레포트 재앵커 — _predX가 _px(0)로 스냅");
            Assert.AreEqual(0f, GetField(nt, "_predY"), 0.01f, "y축 재앵커");
            Assert.AreEqual(0, ((INetworkMoveSimulation)sim).PendingCount, "상태·히스토리 리셋");
        }

        [Test]
        public void 시뮬레이션_미주입시_복제전용으로_동작한다()
        {
            var go = new GameObject("nt-replication-only");
            var nt = go.AddComponent<NetworkTransform>();
            Assert.IsNull(nt.Simulation, "기본 미주입");

            // no Simulation — Update must not throw and renders the authoritative position
            SetField(nt, "_px", 7f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 9f);
            Assert.DoesNotThrow(() => InvokeUpdate(nt), "복제전용 업데이트 무결");
            Assert.AreEqual(7f, nt.transform.position.x, 0.01f, "권위 좌표 렌더");
            Assert.AreEqual(9f, nt.transform.position.z, 0.01f);
            Object.DestroyImmediate(go);
        }
    }
}
