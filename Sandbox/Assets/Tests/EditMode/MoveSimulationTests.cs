using System;
using System.Collections.Generic;
using System.IO;
using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// MoveSimulation engine (ADR-0038) — the full CSP&amp;R core over a stateful test rule (gravity/jump + dash):
    /// wire round-trip, rollback state restoration (velocity survives re-simulation), dash cooldown restore,
    /// history compaction, teleport reset, server queue discipline, and malformed-input containment.
    /// The component-level paths (Update stepping, ack notify, re-anchor) are covered by NetworkTransformComponentTests.
    /// </summary>
    public sealed class MoveSimulationTests
    {
        /// <summary>Test input — horizontal axis plus edge-triggered buttons.</summary>
        private struct TIn
        {
            public sbyte Dx;
            public bool Jump;
            public bool Dash;
        }

        /// <summary>Test state — vertical velocity (jump arc) + dash cooldown: exactly the state class ADR-0038 exists for.</summary>
        private struct TSt
        {
            public float Vy;
            public float DashCd;
        }

        private const float Dt = 1f / 60f;
        private const float JumpSpeed = 4f;
        private const float Gravity = 20f;
        private const float RunSpeed = 5f;
        private const float DashSpeed = 20f;

        /// <summary>Pure stateful rule — gravity arc, dash burst (fast while the cooldown is fresh), horizontal motion.</summary>
        private static void Step(ref TSt st, ref float x, ref float y, ref float z, in TIn i, float dt)
        {
            bool grounded = y <= 0.001f && st.Vy <= 0f;
            if (i.Jump && grounded)
                st.Vy = JumpSpeed;
            st.Vy = grounded && !i.Jump && st.Vy <= 0f ? 0f : st.Vy - Gravity * dt;
            y += st.Vy * dt;
            if (y < 0f)
            {
                y = 0f;
                st.Vy = 0f;
            }

            if (i.Dash && st.DashCd <= 0f)
                st.DashCd = 1f;   // fresh cooldown = dash window
            st.DashCd = Math.Max(0f, st.DashCd - dt);

            x += i.Dx * (st.DashCd > 0.8f ? DashSpeed : RunSpeed) * dt;
        }

        private static void WriteInput(ref MessageBufferWriter w, in TIn i)
        {
            w.WriteSByte(i.Dx);
            w.WriteBoolean(i.Jump);
            w.WriteBoolean(i.Dash);
        }

        private static TIn ReadInput(ref MessageBufferReader r)
            => new TIn { Dx = r.ReadSByte(), Jump = r.ReadBoolean(), Dash = r.ReadBoolean() };

        private static void WriteState(ref MessageBufferWriter w, in TSt s)
        {
            w.WriteSingle(s.Vy);
            w.WriteSingle(s.DashCd);
        }

        private static TSt ReadState(ref MessageBufferReader r)
            => new TSt { Vy = r.ReadSingle(), DashCd = r.ReadSingle() };

        private static MoveSimulation<TIn, TSt> NewSim()
            => new(Step, WriteInput, ReadInput, WriteState, ReadState, 60f);

        private static byte[] Serialize(in TIn i)
        {
            var w = MessageBufferWriter.Create();
            WriteInput(ref w, in i);
            return w.ToArray();
        }

        [Test]
        public void 와이어_왕복_입력이_동일하게_적용된다()
        {
            var a = NewSim();
            var b = NewSim();
            var isimA = (INetworkMoveSimulation)a;
            var isimB = (INetworkMoveSimulation)b;

            a.SetInput(new TIn { Dx = 1, Jump = true, Dash = true });
            float ax = 0f, ay = 0f, az = 0f;
            byte[] payload = isimA.PredictStep(1, ref ax, ref ay, ref az);

            float bx = 0f, by = 0f, bz = 0f;
            isimB.AdoptInput(1, payload);
            isimB.TickServer(ref bx, ref by, ref bz);

            isimA.TryGetPredicted(1, out float px, out _, out _);
            Assert.AreEqual(px, bx, 1e-5f, "직렬화 왕복 후 동일 스텝 적용 (x)");
            Assert.AreEqual(ay, by, 1e-5f, "직렬화 왕복 후 동일 스텝 적용 (y)");
            Assert.AreEqual(1u, isimB.LastAdoptedSeq, "서버 채택 시퀀스");
        }

        [Test]
        public void 점프_궤적_롤백은_속도_상태를_복원해_정확히_수렴한다()
        {
            // client predicted 8 steps WITHOUT jumping (packet-delay fiction) — its history diverges from the server's
            var client = NewSim();
            var csim = (INetworkMoveSimulation)client;
            float cx = 0f, cy = 0f, cz = 0f;
            client.SetInput(default);
            for (uint seq = 1; seq <= 8; seq++)
                csim.PredictStep(seq, ref cx, ref cy, ref cz);
            Assert.AreEqual(0f, cy, 1e-5f, "클라 예측 — 점프 없이 평면 유지");

            // reference ("server") jumped at step 1, released at step 3 — capture its authoritative echo at ack 5
            var reference = NewSim();
            var rsim = (INetworkMoveSimulation)reference;
            float rx = 0f, ry = 0f, rz = 0f;
            byte[] serverStateAt5 = null;
            float sx = 0f, sy = 0f, sz = 0f;
            for (uint seq = 1; seq <= 8; seq++)
            {
                reference.SetInput(seq <= 2 ? new TIn { Jump = true } : default);
                rsim.PredictStep(seq, ref rx, ref ry, ref rz);
                if (seq == 5)
                {
                    rsim.TryGetPredicted(5, out sx, out sy, out sz);
                    Assert.Greater(sy, 0.05f, "서버는 점프 중 (궤적 진행) — 상태가 자명하지 않은 시점");
                    serverStateAt5 = rsim.WriteState();   // state after step 5 — the rollback anchor
                }
            }

            // client rolls back to (server pos, server state) at ack 5 and re-simulates 6..8
            csim.Rollback(5, serverStateAt5, sx, sy, sz, ref cx, ref cy, ref cz);

            Assert.AreEqual(ry, cy, 1e-4f, "롤백 재시뮬 — 서버 상태(속도 포함)에서 재생한 결과와 정확히 일치 (y)");
            Assert.AreEqual(rx, cx, 1e-4f, "롤백 재시뮬 (x)");
            Assert.AreEqual(3, csim.PendingCount, "미승인 입력 3개만 재생 후 잔여");

            // counterfactual — a STATELESS replay (position restored, velocity zeroed) would diverge:
            float zy = sy;
            var zero = new TSt { Vy = 0f };
            TIn none = default;
            float zx = sx, zz = sz;
            for (int i = 0; i < 3; i++)
            {
                var s = zero;
                Step(ref s, ref zx, ref zy, ref zz, in none, Dt);
                zero = s;
            }
            Assert.IsTrue(Math.Abs(zy - cy) > 1e-4f, "무상태 재생(속도 미복원)과는 다른 결과 — 속도 복원이 실제로 작동");
        }

        [Test]
        public void 대시_쿨다운도_롤백_후_유지된다()
        {
            // client: dash at step 1, then idle to step 15
            var client = NewSim();
            var csim = (INetworkMoveSimulation)client;
            float cx = 0f, cy = 0f, cz = 0f;
            client.SetInput(new TIn { Dash = true });
            csim.PredictStep(1, ref cx, ref cy, ref cz);
            client.SetInput(default);
            for (uint seq = 2; seq <= 15; seq++)
                csim.PredictStep(seq, ref cx, ref cy, ref cz);

            // server: adopted the same dash at seq 1 and stepped 14 inputs — cooldown ≈ 1 − 14dt (window over, cooldown running).
            // Adopt+tick interleaved like a real server (queue depth stays ~1 — the queue cap never trips on honest clients).
            var server = NewSim();
            var ssim = (INetworkMoveSimulation)server;
            float sx = 0f, sy = 0f, sz = 0f;
            ssim.AdoptInput(1, Serialize(new TIn { Dash = true }));
            ssim.TickServer(ref sx, ref sy, ref sz);
            for (uint seq = 2; seq <= 14; seq++)
            {
                ssim.AdoptInput(seq, Serialize(default));
                ssim.TickServer(ref sx, ref sy, ref sz);
            }
            Assert.AreEqual(14u, ssim.LastAdoptedSeq, "서버 채택 완료");
            byte[] serverState = ssim.WriteState();

            // client rolls back to ack 14 — cooldown restored from the server state
            csim.Rollback(14, serverState, sx, sy, sz, ref cx, ref cy, ref cz);
            Assert.AreEqual(1, csim.PendingCount, "step 15만 미승인");

            // a dash attempt now must NOT re-trigger the burst (cooldown > 0, window over) — displacement proves it
            client.SetInput(new TIn { Dx = 1, Dash = true });
            float before = cx;
            csim.PredictStep(16, ref cx, ref cy, ref cz);
            Assert.AreEqual(RunSpeed * Dt, cx - before, 1e-3f, "쿨다운 복원 — 재대시 불가 (기본 속도만)");
        }

        [Test]
        public void 승인_히스토리는_폐기되고_조회는_정확하다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;
            float x = 0f, y = 0f, z = 0f;
            sim.SetInput(new TIn { Dx = 1 });
            for (uint seq = 1; seq <= 5; seq++)
                isim.PredictStep(seq, ref x, ref y, ref z);

            Assert.IsTrue(isim.TryGetPredicted(3, out _, out _, out _), "seq 3 조회");
            Assert.IsFalse(isim.TryGetPredicted(9, out _, out _, out _), "미기록 seq");

            isim.DiscardThrough(3);
            Assert.AreEqual(2, isim.PendingCount, "3 이하 폐기");
            Assert.IsFalse(isim.TryGetPredicted(3, out _, out _, out _), "폐기 후 미조회");
            Assert.IsTrue(isim.TryGetPredicted(4, out _, out _, out _), "잔여 조회");
        }

        [Test]
        public void 텔레포트는_상태와_히스토리를_초기화한다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;
            float x = 0f, y = 10f, z = 0f;
            sim.SetInput(new TIn { Jump = true });
            for (uint seq = 1; seq <= 4; seq++)
                isim.PredictStep(seq, ref x, ref y, ref z);
            Assert.Greater(isim.PendingCount, 0, "점프 예측 진행 중");

            isim.OnTeleport();
            Assert.AreEqual(0, isim.PendingCount, "히스토리 클리어");
            Assert.IsFalse(isim.TryGetPredicted(1, out _, out _, out _), "클리어 후 과거 조회 불가");

            // hard-snap path — rollback with an unknown ack lands on the server state and empties history
            float px = 5f, py = 3f, pz = 5f;
            isim.Rollback(999, null, 5f, 0f, 5f, ref px, ref py, ref pz);
            Assert.AreEqual(5f, px, 1e-5f, "미지정 ack — 하드 스냅 (x)");
            Assert.AreEqual(0f, py, 1e-5f, "하드 스냅 (y)");
            Assert.AreEqual(5f, pz, 1e-5f, "하드 스냅 (z)");
            Assert.AreEqual(0, isim.PendingCount, "하드 스냅 후 히스토리 비움");
        }

        [Test]
        public void 서버_큐는_오래된_입력을_무시하고_포화_시_선두를_버린다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;

            isim.AdoptInput(1, Serialize(new TIn { Dx = 1 }));
            isim.AdoptInput(1, Serialize(new TIn { Dx = -1 }));   // stale/replay — ignored
            isim.AdoptInput(0, Serialize(new TIn { Dx = -1 }));   // older — ignored

            for (uint seq = 2; seq <= 30; seq++)
                isim.AdoptInput(seq, Serialize(default));   // overflow the queue (capacity 8) — the 22 oldest are dropped

            float x = 0f, y = 0f, z = 0f;
            isim.TickServer(ref x, ref y, ref z);
            Assert.AreEqual(23u, isim.LastAdoptedSeq, "포화 선두 폐기 — 큐에 남은 최신(23..30)부터 채택");

            for (int i = 0; i < 10; i++)
                isim.TickServer(ref x, ref y, ref z);
            Assert.AreEqual(30u, isim.LastAdoptedSeq, "드레인 후 최신 시퀀스로 수렴");
        }

        [Test]
        public void 잘못된_페이로드는_버려지고_예외가_새지_않는다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;

            // truncated garbage — 1 byte cannot satisfy the 3-byte input contract
            var w = MessageBufferWriter.Create();
            w.WriteSByte(7);
            byte[] garbage = w.ToArray();

            // LogFault is process-global: UniNetManager's static ctor wires it to Debug.LogException, and the Unity
            // test runner fails any test that logs an exception. Swap in a capture so the containment contract —
            // dropped, not adopted, not thrown, but OBSERVED — is asserted instead of tripping the runner.
            // Restored in finally so later tests keep the engine logger.
            var faults = new List<Exception>();
            Action<Exception> previous = UniNetEnvironment.LogFault;
            UniNetEnvironment.LogFault = faults.Add;
            try
            {
                Assert.DoesNotThrow(() => isim.AdoptInput(1, garbage), "잘린 페이로드 — 파싱 실패는 무시");
                Assert.DoesNotThrow(() => isim.AdoptInput(2, null), "null 페이로드 무시");
            }
            finally
            {
                UniNetEnvironment.LogFault = previous;
            }

            Assert.AreEqual(1, faults.Count, "관측은 잘린 페이로드 1건뿐 — null은 관측 없음");
            Assert.IsInstanceOf<EndOfStreamException>(faults[0], "관측된 장애는 EndOfStreamException (버퍼 끝 초과 읽기)");

            float x = 0f, y = 0f, z = 0f;
            isim.TickServer(ref x, ref y, ref z);
            Assert.AreEqual(0u, isim.LastAdoptedSeq, "버려진 입력은 채택되지 않음");
        }

        [Test]
        public void 소유권_스트림_리셋_후_새_소유자_seq1이_채택된다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;
            float x = 0f, y = 0f, z = 0f;

            isim.AdoptInput(900, Serialize(new TIn { Dx = 1 }));   // previous tenure ran far
            isim.TickServer(ref x, ref y, ref z);
            Assert.AreEqual(900u, isim.LastAdoptedSeq, "이전 임기 — 구 시퀀스 채택");

            isim.ResetOwnershipStream();

            isim.AdoptInput(1, Serialize(new TIn { Dx = 1 }));   // the new owner starts at seq 1
            isim.TickServer(ref x, ref y, ref z);
            Assert.AreEqual(1u, isim.LastAdoptedSeq, "리셋 후 새 소유자의 seq 1 채택 (MOV-001)");
        }

        [Test]
        public void OnTeleport는_스트림_리셋_대용이_될_수_없다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;
            float x = 0f, y = 0f, z = 0f;

            isim.AdoptInput(900, Serialize(new TIn { Dx = 1 }));
            isim.TickServer(ref x, ref y, ref z);

            isim.OnTeleport();   // the ONLY pre-existing reset — the pre-fix probe showed it cannot substitute for the ownership reset

            isim.AdoptInput(1, Serialize(new TIn { Dx = 1 }));
            isim.TickServer(ref x, ref y, ref z);
            Assert.AreEqual(900u, isim.LastAdoptedSeq, "OnTeleport는 시퀀스 가드를 지우지 않는다 — 새 owner seq 1은 여전히 기각 (probe-1 반례)");
        }

        [Test]
        public void 스트림_리셋은_권위_게임_상태를_보존한다()
        {
            var sim = NewSim();
            var isim = (INetworkMoveSimulation)sim;
            float x = 0f, y = 0f, z = 0f;

            isim.AdoptInput(1, Serialize(new TIn { Jump = true }));   // build live state — mid-jump velocity
            isim.TickServer(ref x, ref y, ref z);
            isim.AdoptInput(2, Serialize(default));
            isim.TickServer(ref x, ref y, ref z);
            float vyBefore = ReadStateVy(isim.WriteState());
            Assert.AreNotEqual(0f, vyBefore, "점프 진행 — 상태가 살아 있다", 1e-6f);   // NUnit float overload: (expected, actual, message, delta)

            isim.ResetOwnershipStream();

            Assert.AreEqual(vyBefore, ReadStateVy(isim.WriteState()), 1e-6f, "권위 TState 보존 (OnTeleport와의 계약 차이)");
            Assert.AreEqual(0u, isim.LastAdoptedSeq, "시퀀스 가드 리셋");
            Assert.AreEqual(0, isim.PendingCount, "히스토리 클리어");
        }

        private static float ReadStateVy(byte[] blob)
        {
            var r = new MessageBufferReader(blob);
            return ReadState(ref r).Vy;
        }
    }
}
