using System.Text.RegularExpressions;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>ServerRpc 소유자 자동 강제 (ADR-0016) — 소유자 허용·비소유 거부·옵트아웃·로컬 권위 경로.</summary>
    public sealed class ServerRpcOwnershipTests
    {
        private NetworkServer _server = null!;
        private GameObject _go = null!;
        private MovementBrain _brain = null!;
        private HealthTank _tank = null!;
        private RecordingChannel _ownerCh = null!;
        private RecordingChannel _guestCh = null!;

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _server = new NetworkServer();
            UniNetEnvironment.SetServer(_server);
            _go = new GameObject("ownership");
            _brain = _go.AddComponent<MovementBrain>();
            _tank = _go.AddComponent<HealthTank>();
            _server.RegisterSceneObject(_brain.NetId, new NetworkBehaviour[] { _brain, _tank });
            _ownerCh = new RecordingChannel();
            _guestCh = new RecordingChannel();
            _server.AttachConnection(_ownerCh);
            _server.AttachConnection(_guestCh);
            UniNetEnvironment.PumpMain();   // 환영·소유권 배정 — 오브젝트 1개라 첫 연결이 소유
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.PumpMain();   // 잔여 큐 배출 — 다른 테스트로 지연 디스패치 새지 않게
            UniNetEnvironment.SetServer(null);
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void 소유자_발신_ServerRpc는_실행된다()
        {
            byte[] wire = MovementBrain.__UniNetEncode_RpcMove(_brain.NetId, _brain.SubId, 5);
            DispatchServer(_ownerCh.UniNetConnId, MoveId, wire);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, _brain.MoveCalls, "소유자 발신은 소유자 강제를 통과한다");
        }

        [Test]
        public void 비소유_발신은_거부되고_경고를_남긴다()
        {
            LogAssert.Expect(LogType.Warning, new Regex("ServerRpc 거부"));
            byte[] wire = MovementBrain.__UniNetEncode_RpcMove(_brain.NetId, _brain.SubId, 5);
            DispatchServer(_guestCh.UniNetConnId, MoveId, wire);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, _brain.MoveCalls, "비소유 발신은 구현을 실행하지 않는다");
            Assert.AreEqual(1, _brain.Speed, "상태도 변경되지 않는다");
        }

        [Test]
        public void 옵트아웃_RPC는_비소유_발신도_실행된다()
        {
            byte[] wire = HealthTank.__UniNetEncode_RpcHeal(_brain.NetId, 1, 4);   // 서브슬롯 1 = HealthTank (RegisterSceneObject는 컴포넌트 SubId 미주입 — 슬롯 순서 계약)
            DispatchServer(_guestCh.UniNetConnId, HealId, wire);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, _tank.HealCalls, "RequireOwnership=false는 비소유 발신도 실행한다");
        }

        [Test]
        public void 로컬_권위_경로는_대조_없이_실행된다()
        {
            MovementBrain.__UniNetServerDispatch_RpcMove(_brain, 0, 3);   // senderConnId 0 — 서버·호스트·오프라인 직접 경로
            Assert.AreEqual(1, _brain.MoveCalls, "senderConnId 0은 소유자 대조 없이 실행된다");
        }

        private static readonly int MoveId = Fnv1a.MethodId("UniNet.Tests.MovementBrain.RpcMove");
        private static readonly int HealId = Fnv1a.MethodId("UniNet.Tests.HealthTank.RpcHeal");

        /// <summary>등록된 서버 수신 핸들 경유 — 실제 와이어와 동일한 전체 수신 경로 (디스패치 테이블 → 디스패치).</summary>
        private static void DispatchServer(long senderConnId, int methodId, byte[] wire)
        {
            UniNetDispatch.ServerHandlers()[methodId].Handler(senderConnId, wire).GetAwaiter().GetResult();
        }
    }
}
