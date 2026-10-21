using System.Reflection;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// MOV-002 — death-gate pending-history cleanup. When SimulationEnabled turns off on the owning client
    /// (death/cutscene) while predicted inputs are still pending, the unacknowledged history is dropped
    /// (state/input preserved by MoveSimulation): a death-window ack hard-snaps instead of replaying dead-era
    /// inputs, and the respawn re-anchor fires via the existing pending==0 path. Harness mirrors the MOV-001
    /// regression suite (NetworkTransformComponentTests): a reversible owner-side NetworkTransform driven
    /// without a server. Determinism: pending>0 is forced by PredictStep with the ack path untouched and the
    /// gate flips on the same test frame — no delay injection.
    /// </summary>
    public sealed class Mov002DeathGateTests
    {
        private struct TIn { public sbyte Dx; public bool Jump; }
        private struct TSt { public float Vy; }

        private const float StepHz = 60f;
        private const float Dt = 1f / StepHz;

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

        private static MoveSimulation<TIn, TSt> NewSim()
            => new(Step, WriteInput, ReadInput, WriteState, ReadState, StepHz);

        private static float ReadStateVy(byte[] blob)
        {
            var r = new MessageProtocol.Serialize.MessageBufferReader(blob);
            return ReadState(ref r).Vy;
        }

        private static NetworkTransform NewOwner(out MoveSimulation<TIn, TSt> sim)
        {
            var go = new GameObject("mov002-owner");
            var nt = go.AddComponent<NetworkTransform>();
            sim = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, StepHz);
            nt.Simulation = sim;

            var client = new NetworkClient();
            client.SetLocalConnId(1);
            client.ApplyOwner(nt.NetId, 1);   // owner = connId 1 = LocalConnId → IsOwner = true
            UniNetEnvironment.SetClient(client);
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
            foreach (var go in UnityEngine.Object.FindObjectsByType<NetworkTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(go.gameObject);
        }

        // ── T1 — MoveSimulation 단위: 펜딩 소거 + 상태·입력 보존 ──────────────────

        [Test]
        public void DropPendingHistory는_펜딩만_비우고_상태와_입력을_보존한다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;

            // mid-jump with a fully pending ring — the default live situation at 60 Hz (RTT worth of unacked inputs)
            sim.SetInput(new TIn { Jump = true });
            float x = 0f, y = 0f, z = 0f;
            for (uint seq = 1; seq <= 4; seq++)
                isim.PredictStep(seq, ref x, ref y, ref z);
            Assert.Greater(isim.PendingCount, 0, "전 입력 미승인 — 사망 시점의 기본 상태");
            float vyBefore = ReadStateVy(isim.WriteState());
            Assert.Greater(vyBefore, 0f, "점프 진행 — 상태가 살아 있음");

            isim.DropPendingHistory();

            Assert.AreEqual(0, isim.PendingCount, "펜딩 히스토리 소거 (MOV-002)");
            Assert.AreEqual(vyBefore, ReadStateVy(isim.WriteState()), 1e-6f, "게임 상태 보존 — OnTeleport와의 계약 차이");

            // prediction resumes cleanly from the preserved (state, input) — no SetInput in between, so the next
            // step must equal a hand-stepped application of the SAME preserved input (input-survival proof)
            var st = new TSt { Vy = vyBefore };
            var preserved = new TIn { Jump = true };
            float bx = x, by = y, bz = z;
            Step(ref st, ref bx, ref by, ref bz, in preserved, Dt);
            isim.PredictStep(5, ref x, ref y, ref z);

            Assert.AreEqual(1, isim.PendingCount, "정리 후 예측 재개 — 링 재적재 정상");
            Assert.AreEqual(by, y, 1e-5f, "보존된 입력·상태로 다음 예측 진행");
        }

        [Test]
        public void OnTeleport는_상태를_소거하고_DropPendingHistory는_보존한다()
        {
            var drop = NewSim();
            var teleport = NewSim();
            var dropI = (INetworkMoveSimulation)drop;
            var teleportI = (INetworkMoveSimulation)teleport;

            foreach (var s in new[] { drop, teleport })
            {
                s.SetInput(new TIn { Jump = true });
                float x = 0f, y = 0f, z = 0f;
                for (uint seq = 1; seq <= 3; seq++)
                    ((INetworkMoveSimulation)s).PredictStep(seq, ref x, ref y, ref z);
            }

            dropI.DropPendingHistory();
            teleportI.OnTeleport();

            Assert.Greater(ReadStateVy(dropI.WriteState()), 0f, "게이트 정리 — 점프 상태 보존");
            Assert.AreEqual(0f, ReadStateVy(teleportI.WriteState()), 1e-6f, "텔레포트 — 상태 전체 소거 (계약 차이)");
        }

        // ── T2 — 게이트 에지 drop 검출 + 재활성화 프레임 렌더 == 서버 위치 ────────────

        [Test]
        public void 게이트_진입_에지에서_펜딩이_드롭되고_재활성화시_서버_위치로_렌더된다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            SetField(nt, "_accumulator", -10000f);   // edit-mode Time.deltaTime is not guaranteed 0 — guarantee no driver steps
            InvokeUpdate(nt);   // baseline: gate on, no edge

            sim.SetInput(new TIn { Dx = 1 });
            float px = 0f, py = 0f, pz = 0f;
            for (uint seq = 1; seq <= 5; seq++)
                isim.PredictStep(seq, ref px, ref py, ref pz);
            SetField(nt, "_predX", px);
            SetField(nt, "_predY", py);
            SetField(nt, "_predZ", pz);
            Assert.Greater(isim.PendingCount, 0, "사망 시점 — 펜딩 상주");

            nt.SimulationEnabled = false;   // death — same-frame gate transition, no delay injection
            InvokeUpdate(nt);
            Assert.AreEqual(0, isim.PendingCount, "게이트 진입 에지 — 펜딩 드롭 (MOV-002)");

            // edge, not level: pending re-added while still gated survives the next frame.
            // (Direct PredictStep is the only way pending can regrow while gated — the gated Update path
            // returns early — so this asserts the anti-level guard, not a runtime flow.)
            isim.PredictStep(6, ref px, ref py, ref pz);
            InvokeUpdate(nt);
            Assert.AreEqual(1, isim.PendingCount, "에지 1회 검출 — 게이트 유지 중엔 재드롭 없음");
        }

        [Test]
        public void 재활성화시_펜딩이_비면_변경없는_재앵커가_서버_위치를_채택한다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            SetField(nt, "_accumulator", -10000f);
            InvokeUpdate(nt);   // baseline: gate on, no edge

            sim.SetInput(new TIn { Dx = 1 });
            float px = 0f, py = 0f, pz = 0f;
            for (uint seq = 1; seq <= 5; seq++)
                isim.PredictStep(seq, ref px, ref py, ref pz);
            SetField(nt, "_predX", px);
            SetField(nt, "_predY", py);
            SetField(nt, "_predZ", pz);

            nt.SimulationEnabled = false;   // death — entry edge drops the pending history
            InvokeUpdate(nt);
            Assert.AreEqual(0, isim.PendingCount, "게이트 진입 에지 — 펜딩 드롭 (MOV-002)");

            // respawn: the server moved the object while dead (fabricated replication) — re-enable with
            // no pending (the normal death flow: the game stops feeding inputs while dead)
            SetField(nt, "_px", 100f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 0f);
            nt.SimulationEnabled = true;
            SetField(nt, "_accumulator", -10000f);
            InvokeUpdate(nt);   // pending==0 + divergence > SnapThreshold → the UNCHANGED teleport re-anchor

            Assert.AreEqual(100f, GetField(nt, "_predX"), 0.01f, "재앵커 — 예측 기준이 서버 위치로");
            Assert.AreEqual(100f, nt.transform.position.x, 0.01f, "재-on 프레임 렌더 == 서버 위치");
        }

        // ── T2c — 재활성화 프레임에 스텝이 유입돼도 재앵커가 선실행된다 (r17 ISSUE-1) ──────────

        [Test]
        public void 재활성화_프레임에_스텝이_유입되어도_재앵커가_먼저_적용된다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            SetField(nt, "_accumulator", -10000f);
            InvokeUpdate(nt);   // baseline: gate on, no edge

            sim.SetInput(new TIn { Dx = 1 });
            float px = 0f, py = 0f, pz = 0f;
            for (uint seq = 1; seq <= 5; seq++)
                isim.PredictStep(seq, ref px, ref py, ref pz);
            SetField(nt, "_predX", px);
            SetField(nt, "_predY", py);
            SetField(nt, "_predZ", pz);

            nt.SimulationEnabled = false;   // death — entry edge drops the pending history
            InvokeUpdate(nt);
            Assert.AreEqual(0, isim.PendingCount, "게이트 진입 에지 — 펜딩 드롭");

            // respawn while dead, then revive on a frame where a step WILL run (accumulator >= interval):
            // the re-anchor must execute before this frame's step, or one dead-era step poisons the frame.
            SetField(nt, "_px", 100f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 0f);
            sim.SetInput(default);   // fresh respawn input — idle
            nt.SimulationEnabled = true;
            SetField(nt, "_accumulator", 1f);   // 1s >> 1/60 interval — the step block runs this frame
            InvokeUpdate(nt);

            Assert.AreEqual(100f, GetField(nt, "_predX"), 0.01f, "재앵커 선실행 — 스텝이 리스폰 좌표에서 시작");
            Assert.AreEqual(100f, nt.transform.position.x, 0.01f, "렌더 — 리스폰 좌표 (죽은 궤적 오염 없음)");
        }

        // ── T3 — 게이트 off 기간 ack: 하드 스냅 수렴, 죽은 입력 재생 없음 ──────────────

        [Test]
        public void 게이트_오프_기간의_ack는_하드_스냅으로_수렴하고_죽은_입력을_재생하지_않는다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            SetField(nt, "_accumulator", -10000f);
            InvokeUpdate(nt);

            // jump-era predictions left unacknowledged — the death lands before any ack arrives
            sim.SetInput(new TIn { Jump = true });
            float px = 0f, py = 0f, pz = 0f;
            for (uint seq = 1; seq <= 4; seq++)
                isim.PredictStep(seq, ref px, ref py, ref pz);
            Assert.Greater(py, 0f, "점프 예측 진행 중");
            SetField(nt, "_predX", px);
            SetField(nt, "_predY", py);
            SetField(nt, "_predZ", pz);

            nt.SimulationEnabled = false;
            InvokeUpdate(nt);
            Assert.AreEqual(0, isim.PendingCount, "사망 에지 — 링 비움");

            // a stale ack arrives during the death window: the empty ring misses TryGetPredicted → the hard-snap
            // branch of Rollback (server echo: grounded, respawned at (100, 0, 0))
            var grounded = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, StepHz);
            SetField(nt, "_px", 100f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 0f);
            SetField(nt, "_ackState", new UniNetBlobEnvelope { Data = ((INetworkMoveSimulation)grounded).WriteState() });
            SetField(nt, "_ackSeq", 4u);
            typeof(NetworkTransform).GetMethod("OnAckSeqChanged", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(nt, new object[] { 0u });

            Assert.AreEqual(100f, GetField(nt, "_predX"), 1e-4f, "하드 스냅 — 서버 좌표 수렴");
            Assert.AreEqual(0f, GetField(nt, "_predY"), 1e-4f, "점프 궤적(죽은 입력) 재생 없음");
            Assert.AreEqual(0, isim.PendingCount, "링 빈 상태 유지 — 재생할 입력 자체가 없음");
            Assert.AreEqual(0f, ReadStateVy(isim.WriteState()), 1e-6f, "서버 상태(grounded) 복원");
        }

        // ── T5 — 음성 대조: pending==0 사망은 기존 동작 불변 (스냅 없음) ───────────────

        [Test]
        public void 펜딩이_비어있는_사망은_기존_동작이_불변한다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            SetField(nt, "_accumulator", -10000f);
            InvokeUpdate(nt);

            // fully acknowledged mid-jump — pending==0 (D3: the existing re-anchor already keeps this path safe)
            sim.SetInput(new TIn { Jump = true });
            float x = 0f, y = 0f, z = 0f;
            for (uint seq = 1; seq <= 3; seq++)
                isim.PredictStep(seq, ref x, ref y, ref z);
            isim.DiscardThrough(3);
            Assert.AreEqual(0, isim.PendingCount);
            float vyBefore = ReadStateVy(isim.WriteState());
            SetField(nt, "_predX", x);
            SetField(nt, "_predY", y);
            SetField(nt, "_predZ", z);
            SetField(nt, "_px", x);   // server position within SnapThreshold — the re-anchor must NOT fire
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", z);

            nt.SimulationEnabled = false;
            InvokeUpdate(nt);   // the edge still fires, but on an empty ring — a state-preserving no-op
            Assert.AreEqual(vyBefore, ReadStateVy(isim.WriteState()), 1e-6f, "빈 링 정리 — 상태 무손상");
            Assert.AreEqual(0, isim.PendingCount);

            nt.SimulationEnabled = true;
            SetField(nt, "_accumulator", -10000f);
            InvokeUpdate(nt);   // divergence < SnapThreshold — no re-anchor, no snap (pre-existing behavior)
            Assert.AreEqual(y, GetField(nt, "_predY"), 1e-5f, "pending==0 사망 — 스냅 없음 (점프 예측 좌표 유지)");
        }
    }
}
