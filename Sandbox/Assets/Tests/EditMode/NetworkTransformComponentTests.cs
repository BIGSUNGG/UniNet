using System;
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
            UniNetEnvironment.SetServer(_prevServer);   // restore the ambient server (other fixtures/user state stay untouched)
            foreach (var go in UnityEngine.Object.FindObjectsByType<NetworkTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(go.gameObject);
        }

        private static NetworkServer _server;
        private static NetworkServer _prevServer;

        /// <summary>Authority instance — real server entry + server registration flag (the watch polls Server.GetOwner).</summary>
        private static NetworkTransform NewAuthority(out MoveSimulation<TIn, TSt> sim)
        {
            var go = new GameObject("nt-authority");
            var nt = go.AddComponent<NetworkTransform>();
            sim = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, StepHz);
            nt.Simulation = sim;

            var server = new NetworkServer();
            server.RegisterSceneObject(nt.NetId, new object[] { nt });
            typeof(NetworkBehaviour).GetMethod("MarkServerRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nt, null);
            _prevServer = UniNetEnvironment.Server;
            _server = server;
            UniNetEnvironment.SetServer(server);
            return nt;
        }

        /// <summary>Feeds a move input through the real server implementation using the production envelope layout.</summary>
        private static void SubmitMove(NetworkTransform nt, uint seq, uint epoch)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            WriteInput(ref w, new TIn { Dx = 1 });
            byte[] data = (byte[])typeof(NetworkTransform).GetMethod("EncodeMoveInput", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { epoch, w.ToArray() });
            typeof(NetworkTransform).GetMethod("RpcSubmitMove_Implementation", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(nt, new object[] { seq, new UniNetBlobEnvelope { Data = data } });
        }

        /// <summary>Drives one authoritative step via the component Update (accumulator ≥ one interval).</summary>
        private static void TickOnce(NetworkTransform nt)
        {
            SetField(nt, "_accumulator", 1f / StepHz);
            InvokeUpdate(nt);
        }

        private static uint GetU(NetworkTransform nt, string name)
            => (uint)typeof(NetworkTransform).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(nt);

        private static void SetU(NetworkTransform nt, string name, uint value)
            => typeof(NetworkTransform).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(nt, value);

        // System wire messages (1~12, e.g. OwnerUpdate) live in the generated hubs' MethodCallActions — NOT in the
        // general UniNetDispatch client table. Mirror OwnerBaselineTests: locate the emitted UniNetClientHub.__OwnerUpdate_Requested
        // by reflection and invoke what runs in production.
        private static MethodInfo _ownerUpdateHandler;

        private static MethodInfo FindOwnerUpdateHandler()
        {
            if (_ownerUpdateHandler != null) return _ownerUpdateHandler;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type hub = null;
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "UniNetClientHub") { hub = t; break; }
                }
                catch (ReflectionTypeLoadException) { continue; }
                if (hub == null) continue;
                var m = hub.GetMethod("__OwnerUpdate_Requested", BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null) return _ownerUpdateHandler = m;
            }
            Assert.Fail("생성 UniNetClientHub.__OwnerUpdate_Requested 미발견 — 생성기 출력 확인 필요");
            return null;
        }

        /// <summary>Feeds an OwnerUpdate through the actual generated wire-2 receiver (same envelope the sender writes).</summary>
        private static void ReplayOwnerUpdate(ulong netId, long owner)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteInt64(owner);
            var task = (System.Threading.Tasks.Task<byte[]>)FindOwnerUpdateHandler().Invoke(null, new object[] { w.ToArray() });
            Assert.IsTrue(task.Wait(1000), "실제 생성 수신기 — 완료 미반환");
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
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void 이전_임기_지연_입력은_epoch_불일치로_기각되고_새_스트림이_채택된다()
        {
            var nt = NewAuthority(out var sim);
            var isim = (INetworkMoveSimulation)sim;

            // tenure-1 assignment BEFORE the first watch observation — 0→conn is an adoption baseline, no bump
            // (the watch detects transitions, not assignments; timing-independent by design)
            _server.SetOwner(nt.NetId, 1);
            TickOnce(nt);
            Assert.AreEqual(0u, GetU(nt, "_moveEpoch"), "초기 입양 — 베이스라인 (무범프)");

            SubmitMove(nt, 900, 0);   // tenure 1 ran to seq 900 (epoch 0)
            TickOnce(nt);
            Assert.AreEqual(900u, isim.LastAdoptedSeq, "1차 임기 입력 채택");

            // A → B → A across frames: tenure→tenure transfers bump + reset (probe-2 reproduction)
            _server.SetOwner(nt.NetId, 2);
            TickOnce(nt);   // 1→2 — bump 1 + reset
            _server.SetOwner(nt.NetId, 1);
            TickOnce(nt);   // 2→1 — bump 2 + reset
            Assert.AreEqual(2u, GetU(nt, "_moveEpoch"), "임기→임기 이전 2회 — epoch 2 (리셋과 짝)");
            Assert.AreEqual(0u, isim.LastAdoptedSeq, "서버 스트림 리셋 — 시퀀스 가드 0");

            // stale in-flight input from the OLD tenure: the current owner passes any ownership gate — it must die at the epoch gate
            SubmitMove(nt, 900, 0);
            TickOnce(nt);
            Assert.AreEqual(0u, isim.LastAdoptedSeq, "구 epoch seq 900 — 기각 (probe-2 반례 수정 증명)");
            Assert.AreEqual(1L, nt.MoveEpochMismatches, "불일치 카운터 관측");

            // the new tenure's fresh stream is adopted normally
            SubmitMove(nt, 1, 2);
            TickOnce(nt);
            Assert.AreEqual(1u, isim.LastAdoptedSeq, "새 임기 seq 1 채택");
            SubmitMove(nt, 2, 2);
            TickOnce(nt);
            Assert.AreEqual(2u, isim.LastAdoptedSeq, "이어지는 스트림 채택");
        }

        [Test]
        public void 소유자_값_불변시_epoch_범프_없이_스트림이_유지된다()
        {
            var nt = NewAuthority(out var sim);
            var isim = (INetworkMoveSimulation)sim;

            // tenure-1 assignment BEFORE the first watch observation — 0→conn is an adoption baseline, no bump
            _server.SetOwner(nt.NetId, 1);
            TickOnce(nt);
            uint epoch = GetU(nt, "_moveEpoch");
            Assert.AreEqual(0u, epoch, "초기 입양 — 베이스라인 (무범프)");

            SubmitMove(nt, 5, 0);
            TickOnce(nt);
            Assert.AreEqual(5u, isim.LastAdoptedSeq, "1차 임기 스트림");

            // same value — the broadcast still goes out (redundant SetOwner / re-sent baseline), but no bump, no reset
            _server.SetOwner(nt.NetId, 1);
            TickOnce(nt);
            Assert.AreEqual(epoch, GetU(nt, "_moveEpoch"), "동일 소유자 재전파 — 범프 없음");
            Assert.AreEqual(5u, isim.LastAdoptedSeq, "리셋 없음 — 스트림 연속");

            SubmitMove(nt, 6, epoch);
            TickOnce(nt);
            Assert.AreEqual(6u, isim.LastAdoptedSeq, "현재 epoch 입력 통과 — 스트림 연속");
            Assert.AreEqual(0L, nt.MoveEpochMismatches, "불일치 없음");
        }

        [Test]
        public void 실제_OwnerUpdate_수신기_경유_소유권_획득시_클라_스트림이_재바인딩된다()
        {
            var nt = NewOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            var client = UniNetEnvironment.Client;

            SetField(nt, "_accumulator", -10000f);   // edit-mode Time.deltaTime is not guaranteed 0 — guarantee no steps
            InvokeUpdate(nt);   // binds the ownership subscription (client instance)
            client.ApplyOwner(nt.NetId, 7);   // value change → event fires, but owner 7 ≠ local 1: the marshalled reset is inert
            UniNetEnvironment.PumpMain();

            // seed a stale stream from the previous tenure
            sim.SetInput(new TIn { Dx = 1 });
            float hx = 5f, hy = 0f, hz = 0f;
            isim.PredictStep(41, ref hx, ref hy, ref hz);
            isim.PredictStep(42, ref hx, ref hy, ref hz);
            SetU(nt, "_clientSeq", 42u);
            SetField(nt, "_predX", 5f);
            SetField(nt, "_px", 0f);

            ReplayOwnerUpdate(nt.NetId, 1);   // actual generated wire-2 receiver — the local connection GAINS ownership
            UniNetEnvironment.PumpMain();     // the marshalled rebinding runs

            Assert.AreEqual(0u, GetU(nt, "_clientSeq"), "클라 시퀀스 재시작 (다음 전송 1)");
            Assert.AreEqual(0, isim.PendingCount, "히스토리 클리어");
            Assert.AreEqual(0f, GetField(nt, "_predX"), 1e-6f, "권위 좌표 재앵커");

            // unknown-object notices ride the same event — the netId guard drops them harmlessly
            client.ApplyOwner(999999ul, 3);
            ReplayOwnerUpdate(999999ul, 5);
            Assert.DoesNotThrow(() => UniNetEnvironment.PumpMain(), "미등록 netId — 널가드 안전");
        }

        [Test]
        public void 호스트_권위_인스턴스는_이벤트로_이중_리셋하지_않는다()
        {
            var nt = NewAuthority(out var sim);
            var client = new NetworkClient();
            client.SetLocalConnId(1);
            client.ApplyOwner(nt.NetId, 1);   // first assign — baseline, no event
            UniNetEnvironment.SetClient(client);
            typeof(NetworkBehaviour).GetMethod("MarkClientRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nt, null);

            // the host instance never auto-subscribes (authority branch) — bind like a swapped-in client to prove the guard still holds
            typeof(NetworkTransform).GetMethod("BindOwnershipEvent", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nt, null);

            SubmitMove(nt, 10, 0);
            TickOnce(nt);
            SetU(nt, "_clientSeq", 42u);
            Assert.AreEqual(10u, ((INetworkMoveSimulation)sim).LastAdoptedSeq, "서버 스트림 진행");

            client.ApplyOwner(nt.NetId, 2);   // value changes on the client cache (loopback notice equivalent)
            client.ApplyOwner(nt.NetId, 1);   // back to local — a GAIN, but this instance IS the authority
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(42u, GetU(nt, "_clientSeq"), "권위 인스턴스 — 클라측 재설정 스킵 (이중 리셋 가드)");
        }

        [Test]
        public void 다른_객체의_소유권_변화는_이_스트림을_건드리지_않는다()
        {
            var a = NewOwner(out _);
            var client = UniNetEnvironment.Client;

            var go = new GameObject("nt-other");
            var b = go.AddComponent<NetworkTransform>();
            b.Simulation = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, StepHz);
            client.Register(b.NetId, new object[] { b });
            typeof(NetworkBehaviour).GetMethod("MarkClientRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(b, null);
            client.ApplyOwner(b.NetId, 1);   // the local connection owns both

            SetField(a, "_accumulator", -10000f);
            SetField(b, "_accumulator", -10000f);
            InvokeUpdate(a);
            InvokeUpdate(b);   // both bind subscriptions
            SetU(a, "_clientSeq", 42u);
            SetU(b, "_clientSeq", 42u);

            client.ApplyOwner(b.NetId, 9);   // loss — no reset expected anywhere
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(42u, GetU(a, "_clientSeq"), "손실 통보 — 무간섭");
            Assert.AreEqual(42u, GetU(b, "_clientSeq"), "손실은 재바인딩 아님");

            client.ApplyOwner(b.NetId, 1);   // b re-gains → ONLY b rebinds
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(42u, GetU(a, "_clientSeq"), "다른 객체 소유권 변화 — 무간섭");
            Assert.AreEqual(0u, GetU(b, "_clientSeq"), "본인 객체 획득 — 재바인딩");
        }

        // ── FR-05 (v0.23.0) — 위치 일치 ack의 상태 전용 리컨사일 ─────────────
        // Positions matching within RollbackThresholdSq used to return early (DiscardThrough only), leaving diverged
        // game state — a dash cooldown the server never adopted — stuck until the next POSITION divergence
        // (reproduction: client cooldown >0 vs server 0.000 at identical positions). Engine semantics are in
        // MoveSimulationTests (ReconcileState); these drive the REAL component ack path.

        /// <summary>FR-05 input — horizontal axis plus a dash trigger (Dx = 0 keeps the position identical while the cooldown state diverges).</summary>
        private struct DIn { public sbyte Dx; public bool Dash; }

        /// <summary>FR-05 state — the dash cooldown: diverges without any positional footprint, exactly the FR-05 class.</summary>
        private struct DSt { public float DashCd; }

        private static int _dashStepCalls;   // cost probe — counts step-rule invocations (prediction and replay both go through here)

        private static void DashStep(ref DSt st, ref float x, ref float y, ref float z, in DIn i, float dt)
        {
            _dashStepCalls++;
            if (i.Dash && st.DashCd <= 0f)
                st.DashCd = 1f;   // fresh cooldown — a dash burst only affects position when Dx != 0
            st.DashCd = Math.Max(0f, st.DashCd - dt);
            x += i.Dx * (st.DashCd > 0.8f ? 20f : 5f) * dt;
        }

        private static void DashWriteInput(ref MessageProtocol.Serialize.MessageBufferWriter w, in DIn i)
        {
            w.WriteSByte(i.Dx);
            w.WriteBoolean(i.Dash);
        }

        private static DIn DashReadInput(ref MessageProtocol.Serialize.MessageBufferReader r)
            => new DIn { Dx = r.ReadSByte(), Dash = r.ReadBoolean() };

        private static void DashWriteState(ref MessageProtocol.Serialize.MessageBufferWriter w, in DSt s)
            => w.WriteSingle(s.DashCd);

        private static DSt DashReadState(ref MessageProtocol.Serialize.MessageBufferReader r)
            => new DSt { DashCd = r.ReadSingle() };

        private static NetworkTransform NewDashOwner(out MoveSimulation<DIn, DSt> sim)
        {
            var go = new GameObject("nt-state-reconcile");
            var nt = go.AddComponent<NetworkTransform>();
            sim = new MoveSimulation<DIn, DSt>(DashStep, DashWriteInput, DashReadInput, DashWriteState, DashReadState, StepHz);
            nt.Simulation = sim;

            var client = new NetworkClient();
            client.SetLocalConnId(1);
            client.ApplyOwner(nt.NetId, 1);   // owner = connId 1 = LocalConnId → IsOwner = true
            UniNetEnvironment.SetClient(client);
            typeof(NetworkBehaviour).GetMethod("MarkClientRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(nt, null);
            return nt;
        }

        private static void InvokeAck(NetworkTransform nt)
            => typeof(NetworkTransform).GetMethod("OnAckSeqChanged", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(nt, new object[] { 0u });

        private static float ReadDashCd(INetworkMoveSimulation sim)
        {
            var r = new MessageProtocol.Serialize.MessageBufferReader(sim.WriteState());
            return DashReadState(ref r).DashCd;
        }

        /// <summary>Owner predicted seq 1..3 with a dash at seq 1 (Dx = 0) — cooldown > 0, position identical to the server's. The server never adopted the dash: its echo carries cooldown 0.</summary>
        private static void PredictDivergedOwnerState(NetworkTransform nt, MoveSimulation<DIn, DSt> sim)
        {
            var isim = (INetworkMoveSimulation)sim;
            sim.SetInput(new DIn { Dash = true });
            float px = 0f, py = 0f, pz = 0f;
            isim.PredictStep(1, ref px, ref py, ref pz);
            sim.SetInput(default);
            isim.PredictStep(2, ref px, ref py, ref pz);
            isim.PredictStep(3, ref px, ref py, ref pz);
            Assert.Greater(ReadDashCd(isim), 0.9f, "클라 예측 쿨다운 진행 중 — 위치와 무관하게 상태가 어긋난 전제 (FR-05 재현)");
            SetField(nt, "_predX", px);
            SetField(nt, "_predY", py);
            SetField(nt, "_predZ", pz);   // all zero — Dx never moved
        }

        [Test]
        public void 권위_TState가_다르면_위치가_같아도_ack가_상태를_보정한다()
        {
            var nt = NewDashOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            PredictDivergedOwnerState(nt, sim);

            // server: never adopted the dash — same position (0,0,0), authoritative cooldown 0
            var authority = new MoveSimulation<DIn, DSt>(DashStep, DashWriteInput, DashReadInput, DashWriteState, DashReadState, StepHz);
            byte[] serverState = ((INetworkMoveSimulation)authority).WriteState();   // default state — cooldown 0
            SetField(nt, "_ackState", new UniNetBlobEnvelope { Data = serverState });
            SetField(nt, "_px", 0f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 0f);
            SetU(nt, "_ackSeq", 3u);

            InvokeAck(nt);

            Assert.AreEqual(0f, ReadDashCd(isim), 1e-5f, "위치가 같아도 권위 TState(쿨다운 0)로 수렴 — FR-05 반례 수정");
            Assert.AreEqual(0f, GetField(nt, "_predX"), 1e-6f, "위치 불변 — 보정은 상태 전용");
            Assert.AreEqual(0f, GetField(nt, "_prevPredX"), 1e-6f, "렌더 기준점 동기 재설정 — 재생 후 이전 좌표에서 lerp하지 않는다");
            Assert.AreEqual(0, isim.PendingCount, "승인 입력 폐기");
        }

        [Test]
        public void 상태가_같으면_ack는_재시뮬_비용을_내지_않는다()
        {
            var nt = NewDashOwner(out var sim);
            var isim = (INetworkMoveSimulation)sim;
            PredictDivergedOwnerState(nt, sim);

            // an agreeing echo — the state predicted for seq 3 itself (nothing pending beyond it)
            byte[] echo = isim.WriteState();
            SetField(nt, "_ackState", new UniNetBlobEnvelope { Data = echo });
            SetField(nt, "_px", 0f);
            SetField(nt, "_py", 0f);
            SetField(nt, "_pz", 0f);
            SetU(nt, "_ackSeq", 3u);

            _dashStepCalls = 0;
            float cdBefore = ReadDashCd(isim);
            float predXBefore = (float)GetField(nt, "_predX");
            float prevPredYBefore = (float)GetField(nt, "_prevPredY");
            float prevPredZBefore = (float)GetField(nt, "_prevPredZ");
            InvokeAck(nt);

            Assert.AreEqual(0, isim.PendingCount, "ack 경로는 실행됨 — 승인 히스토리 폐기");
            Assert.AreEqual(0, _dashStepCalls, "스텝 룰 재호출 0회 — 일치 상태의 ack는 비교만 하고 재생하지 않는다 (비용 최소화)");
            Assert.AreEqual(cdBefore, ReadDashCd(isim), 1e-6f, "상태 무변경 — 예측 상태 그대로");
            Assert.AreEqual(predXBefore, (float)GetField(nt, "_predX"), 1e-6f, "위치 무변경");
            Assert.AreEqual(prevPredYBefore, (float)GetField(nt, "_prevPredY"), 0f, "렌더 기준점 Y 무변경 — 일치 ack는 어떤 축의 기준점도 덮지 않는다 (dangling-if 회귀)");
            Assert.AreEqual(prevPredZBefore, (float)GetField(nt, "_prevPredZ"), 0f, "렌더 기준점 Z 무변경");
        }
    }
}
