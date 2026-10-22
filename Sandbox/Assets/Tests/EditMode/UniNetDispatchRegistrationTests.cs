using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DRPC;
using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>GEN-001 — dispatch registration conflict policy: idempotent re-registration vs a genuine collision,
    /// the silent-overwrite reserved range, and the strict mode-mismatch handling.</summary>
    public sealed class UniNetDispatchRegistrationTests
    {
        // Test-only high IDs — 190001+ stays clear of reserved wires (<64) and generated FNV-1a user IDs,
        // so these tests never collide with entries registered by generated code (no Clear API needed).
        private const int ServerUserId = 190001;
        private const int ClientUserId = 190002;
        private const int ModeMismatchId = 190003;
        private const int ReservedWireId = 13;   // real wire-13 (batched replication) — exercised per plan, restored in the test

        private Action<Exception> _previousLogFault;
        private List<Exception> _faults;

        [SetUp]
        public void CaptureLogFault()
        {
            _faults = new List<Exception>();
            _previousLogFault = UniNetEnvironment.LogFault;
            UniNetEnvironment.LogFault = ex => _faults.Add(ex);
        }

        [TearDown]
        public void RestoreLogFault()
        {
            UniNetEnvironment.LogFault = _previousLogFault;
        }

        // Static method groups: every conversion produces a fresh delegate around the SAME MethodInfo —
        // exactly the shape of a rerun registration, which the new policy must treat as idempotent.
        private static Task<byte[]> HandlerA(long connId, byte[] payload) => Task.FromResult<byte[]>(Array.Empty<byte>());
        private static Task<byte[]> HandlerB(long connId, byte[] payload) => Task.FromResult<byte[]>(Array.Empty<byte>());

        [Test]
        public void T1_같은_정적_핸들러_재등록은_LogFault_없이_멱등_처리된다()
        {
            UniNetDispatch.RegisterServer(ServerUserId, HandlerA, RpcDeliveryMode.ReliableOrdered);
            var first = UniNetDispatch.ServerHandlers()[ServerUserId];

            UniNetDispatch.RegisterServer(ServerUserId, HandlerA, RpcDeliveryMode.ReliableOrdered);

            Assert.AreEqual(0, _faults.Count, "동일 핸들러 메서드+동일 mode 재등록은 무음 갱신이다");
            var kept = UniNetDispatch.ServerHandlers()[ServerUserId];
            Assert.AreEqual(nameof(HandlerA), kept.Handler.Method.Name, "엔트리의 핸들러 메서드가 유지된다");
            Assert.AreEqual(RpcDeliveryMode.ReliableOrdered, kept.Mode, "엔트리의 mode가 유지된다");
            Assert.IsTrue(ReferenceEquals(first.Handler, kept.Handler), "멱등 재등록은 기존 엔트리를 건드리지 않는다");
        }

        [Test]
        public void T2_다른_핸들러가_같은_ID를_요구하면_LogFault로_거부되고_기존이_유지된다()
        {
            UniNetDispatch.RegisterClient(ClientUserId, HandlerA, RpcDeliveryMode.ReliableOrdered);

            UniNetDispatch.RegisterClient(ClientUserId, HandlerB, RpcDeliveryMode.ReliableOrdered);

            Assert.AreEqual(1, _faults.Count, "실충돌은 LogFault 1회로 관측된다");
            Assert.IsInstanceOf<InvalidOperationException>(_faults[0], "충돌 폴트는 InvalidOperationException이다");
            var kept = UniNetDispatch.ClientHandlers()[ClientUserId];
            Assert.AreEqual(nameof(HandlerA), kept.Handler.Method.Name, "first-wins — 기존 핸들러가 유지된다");
            Assert.AreEqual(RpcDeliveryMode.ReliableOrdered, kept.Mode, "first-wins — 기존 mode가 유지된다");
        }

        [Test]
        public void T3_예약_구간_ID는_다른_핸들러로도_무음_덮어써진다()
        {
            var original = UniNetDispatch.ClientHandlers()[ReservedWireId];
            try
            {
                UniNetDispatch.RegisterClient(ReservedWireId, HandlerB, RpcDeliveryMode.ReliableOrdered);

                Assert.AreEqual(0, _faults.Count, "예약 구간(<64)은 충돌 검사 없이 무음 덮어쓰기된다 — 다중 어셈블리 와이어 등록 경로");
                var overwritten = UniNetDispatch.ClientHandlers()[ReservedWireId];
                Assert.AreEqual(nameof(HandlerB), overwritten.Handler.Method.Name, "와이어 13이 새 핸들러로 덮어써진다");
            }
            finally
            {
                // Wire 13 is the live batched-replication path for generated code — restore the original handler
                // (reserved range: silent overwrite) so other tests in the domain keep working.
                UniNetDispatch.RegisterClient(ReservedWireId, original.Handler, original.Mode);
            }

            var restored = UniNetDispatch.ClientHandlers()[ReservedWireId];
            Assert.IsTrue(Equals(restored.Handler.Method, original.Handler.Method), "테스트 종료 시 원래 와이어 13 핸들러로 복원된다");
        }

        [Test]
        public void T4_같은_MethodInfo라도_mode가_다르면_충돌로_거부된다()
        {
            UniNetDispatch.RegisterServer(ModeMismatchId, HandlerA, RpcDeliveryMode.ReliableOrdered);

            UniNetDispatch.RegisterServer(ModeMismatchId, HandlerA, RpcDeliveryMode.Sequenced);

            Assert.AreEqual(1, _faults.Count, "same-handler + mode 불일치도 엄격하게 충돌 처리된다");
            Assert.IsInstanceOf<InvalidOperationException>(_faults[0], "mode 불일치 폴트는 InvalidOperationException이다");
            var kept = UniNetDispatch.ServerHandlers()[ModeMismatchId];
            Assert.AreEqual(nameof(HandlerA), kept.Handler.Method.Name, "기존 핸들러가 유지된다");
            Assert.AreEqual(RpcDeliveryMode.ReliableOrdered, kept.Mode, "first-wins — 기존 mode가 유지된다");
        }
    }
}
