using System.Collections.Generic;
using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// ADR-0056 — trailing RpcSender injection. A non-ownership ServerRpc's _Implementation/_Validate receive
    /// the transport-assigned sender connection ID (never a client-supplied argument); local authority calls
    /// report IsLocal; PlayerNetId resolves through the declared player-object mapping; an implementation
    /// without the trailing parameter keeps the exact legacy behavior (codegen output identical).
    /// </summary>
    public sealed class SenderContextTests
    {
        private readonly List<GameObject> _created = new();
        private NetworkServer _prevServer;

        private static readonly int ChatId = Fnv1a.MethodId("UniNet.Tests.SenderContextPlayer.RpcChat");
        private static readonly int OwnedPingId = Fnv1a.MethodId("UniNet.Tests.SenderContextPlayer.RpcOwnedPing");
        private static readonly int PlainId = Fnv1a.MethodId("UniNet.Tests.SenderContextPlayer.RpcPlain");

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            // Fixtures 어셈블리의 생성 등록 — CS0436 소스 우선 바인딩으로 EditMode Register()가 Fixtures 타입을 놓치므로
            // 리플렉션으로 명시 호출한다 (GeneratorTypeSupportTests 선례).
            System.Reflection.Assembly.Load("UniNet.Tests.Fixtures")
                .GetType("UniNet.Generated.__UniNetRegistration")
                ?.GetMethod("Register")?.Invoke(null, null);
            _prevServer = UniNetEnvironment.Server;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.SetServer(_prevServer);
            foreach (var go in _created)
                if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        private SenderContextPlayer _fixture = null!;
        private NetworkServer _server = null!;
        private RecordingChannel _ch = null!;

        private void StartFixture()
        {
            _server = new NetworkServer();
            UniNetEnvironment.SetServer(_server);
            var go = new GameObject("sender-ctx");
            _created.Add(go);
            _fixture = go.AddComponent<SenderContextPlayer>();
            _server.RegisterSceneObject(_fixture.NetId, new NetworkBehaviour[] { _fixture });
            _ch = new RecordingChannel();
            _server.AttachConnection(_ch);
            _server.OnClientVersionDeclared(_ch.UniNetConnId, _server.ExpectedProtocolVersion ?? "");   // CON-001 — fake channels must declare too
            UniNetEnvironment.PumpMain();
        }

        private static void DispatchServer(long senderConnId, int methodId, byte[] wire)
            => UniNetDispatch.ServerHandlers()[methodId].Handler(senderConnId, wire).GetAwaiter().GetResult();

        // T1 — 비소유 발신: _Validate/_Implementation 모두 전송 계층이 부여한 발신자를 그대로 받는다.
        [Test]
        public void 비소유_RPC는_실제_발신자를_구현과_검증에_주입한다()
        {
            StartFixture();
            byte[] wire = SenderContextPlayer.__UniNetEncode_RpcChat(_fixture.NetId, _fixture.SubId, "hello");
            DispatchServer(_ch.UniNetConnId, ChatId, wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual("hello", _fixture.LastChat, "구현 실행·인자 보존");
            Assert.AreEqual(_ch.UniNetConnId, _fixture.ImplSenderConnId, "구현이 받은 발신자 = 전송 계층 connId");
            Assert.AreEqual(_ch.UniNetConnId, _fixture.ValidateSenderConnId, "검증 훅도 같은 발신자");
            Assert.IsFalse(_fixture.ImplSenderIsLocal, "원격 발신 — IsLocal 거짓");
        }

        // T2 — PlayerNetId 매핑: SetPlayerObject로 선언된 플레이어 오브젝트가 발신자에게서 해석된다.
        [Test]
        public void 발신자_PlayerNetId는_선언된_플레이어_오브젝트로_해석된다()
        {
            StartFixture();
            _server.SetPlayerObject(_ch.UniNetConnId, _fixture.NetId);
            byte[] wire = SenderContextPlayer.__UniNetEncode_RpcChat(_fixture.NetId, _fixture.SubId, "who");
            DispatchServer(_ch.UniNetConnId, ChatId, wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(_fixture.NetId, _fixture.ValidateSenderPlayerNetId, "검증 시점 PlayerNetId");
            Assert.AreEqual(_fixture.NetId, _fixture.ImplSenderPlayerNetId, "구현 시점 PlayerNetId");
        }

        // T3 — 검증 거부: 거짓 반환 시 구현은 실행되지 않는다 (기존 Validate 계약 유지).
        [Test]
        public void 검증_거부는_구현을_건너뛴다()
        {
            StartFixture();
            _fixture.AllowChat = false;
            byte[] wire = SenderContextPlayer.__UniNetEncode_RpcChat(_fixture.NetId, _fixture.SubId, "nope");
            DispatchServer(_ch.UniNetConnId, ChatId, wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(_ch.UniNetConnId, _fixture.ValidateSenderConnId, "검증은 실행됨");
            Assert.AreEqual(-999, _fixture.ImplSenderConnId, "구현 미실행 (주입값 없음)");
        }

        // T4 — 로컬 권위 호출: 서버에서 직접 호출하면 connId 0·IsLocal로 주입된다.
        [Test]
        public void 로컬_호출은_IsLocal로_주입된다()
        {
            StartFixture();
            _fixture.RpcChat("local");   // IsServer == true → QueueOnMain local dispatch with senderConnId 0
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(0, _fixture.ImplSenderConnId, "로컬 경로 connId 0");
            Assert.IsTrue(_fixture.ImplSenderIsLocal, "IsLocal 참");
            Assert.AreEqual(0, _fixture.ImplSenderPlayerNetId, "로컬 발신자에는 플레이어 매핑 없음");
        }

        // T5 — 소유권 강제와 직교: RequireOwnership=true + 발신자 주입은 독립 계약이다.
        [Test]
        public void 소유권_강제_RPC에도_발신자가_주입된다()
        {
            StartFixture();
            _server.SetOwner(_fixture.NetId, _ch.UniNetConnId);   // make the caller the owner
            byte[] wire = SenderContextPlayer.__UniNetEncode_RpcOwnedPing(_fixture.NetId, _fixture.SubId, 1);
            DispatchServer(_ch.UniNetConnId, OwnedPingId, wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(_ch.UniNetConnId, _fixture.OwnedSenderConnId, "소유자 발신 — 주입됨");

            // 비소유 발신은 기존 거부 경로 (ADR-0016) — 발신자 주입과 무관하게 차단
            _server.SetOwner(_fixture.NetId, 0);
            _fixture.OwnedSenderConnId = -999;
            byte[] wire2 = SenderContextPlayer.__UniNetEncode_RpcOwnedPing(_fixture.NetId, _fixture.SubId, 2);
            DispatchServer(_ch.UniNetConnId, OwnedPingId, wire2);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(-999, _fixture.OwnedSenderConnId, "비소유 발신 — 구현 미실행");
        }

        // T6 — 호환 계약: 끝에 RpcSender가 없는 구현은 기존 동작 그대로 (주입 없이도 정상 실행).
        [Test]
        public void 발신자_없는_구현은_기존_동작을_유지한다()
        {
            StartFixture();
            byte[] wire = SenderContextPlayer.__UniNetEncode_RpcPlain(_fixture.NetId, _fixture.SubId, 5);
            DispatchServer(_ch.UniNetConnId, PlainId, wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, _fixture.PlainCount, "기존 시그니처 — 영향 없음");
        }
    }
}
