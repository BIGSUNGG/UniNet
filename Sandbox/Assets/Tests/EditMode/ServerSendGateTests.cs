using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DRPC;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// FR-001 — server-originated game RPC sends (broadcast ClientRpc, To/ToOwner, Multicast) are dropped immediately
    /// while the connection is unapproved or scene-held (AwaitingSceneReady): zero network sends, no queue/replay,
    /// multicast local execution preserved. An approved connection that never changed scenes (no-scene positive
    /// control) keeps sending normally, and the system Welcome is unaffected by the hold. Harness follows TargetedRpcTests.
    /// </summary>
    public sealed class ServerSendGateTests
    {
        /// <summary>RecordingChannel pattern with UniNetSend + Welcome recording (TargetedRpcTests.RpcChannel + handshake visibility).</summary>
        private sealed class GateChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal readonly List<(int methodId, byte[] payload, RpcDeliveryMode mode)> RpcSends = new();
            internal int Welcomes;

            public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) => RpcSends.Add((methodId, payload, mode));
            public void SendWelcome(long connId, string serverVersion, string sessionToken) => Welcomes++;
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
                ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason)
                => System.Threading.Tasks.Task.CompletedTask;
            public void SendLoadScene(string sceneName) { }
        }

        private NetworkServer _server = null!;
        private GameObject _go = null!;
        private VerifyPlayer _player = null!;
        private GateChannel _ch = null!;

        private static readonly int ToastId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcToast");
        private static readonly int AwardId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcAward");
        private static readonly int DeathId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcDeath");

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _server = new NetworkServer();   // ExpectedProtocolVersion 기본값이 비어 있지 않으므로 승인은 wire-7 선언까지 대기
            UniNetEnvironment.SetServer(_server);
            _go = new GameObject("send-gate");
            _player = _go.AddComponent<VerifyPlayer>();
            _server.RegisterSceneObject(_player.NetId, new NetworkBehaviour[] { _player });
            typeof(NetworkBehaviour).GetMethod("MarkServerRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_player, null);   // activates IsServer (TargetedRpcTests precedent)
            _ch = new GateChannel();
            _server.AttachConnection(_ch);
            UniNetEnvironment.PumpMain();   // Welcome — 승인 없이도 시스템 핸드셰이크는 진행
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.PumpMain();
            UniNetEnvironment.SetServer(null);
            Object.DestroyImmediate(_go);
        }

        private int Count(int methodId) => _ch.RpcSends.Count(s => s.methodId == methodId);

        // T1 — 승인 대기 연결: 방송·타깃·멀티캐스트 모두 즉시 드롭(송신 0, 큐/리플레이 없음). 로컬 멀티캐스트 실행은 보존.
        [Test]
        public void 승인_대기_연결에는_게임_RPC를_송신하지_않는다()
        {
            Assert.AreEqual(1, _ch.Welcomes, "FR-01 — 시스템 Welcome은 게이트와 무관하게 정상 송신");

            _player.RpcToast();
            _player.RpcAwardTo(_ch.UniNetConnId, 1, "hold");
            _player.RpcAwardToOwner(1, "hold");
            int localBefore = _player.MulticastCount;
            _player.RpcDeath();

            Assert.AreEqual(0, Count(ToastId), "미승인 — 방송 ClientRpc 송신 0");
            Assert.AreEqual(0, Count(AwardId), "미승인 — To/ToOwner 타깃 송신 0");
            Assert.AreEqual(0, Count(DeathId), "미승인 — 멀티캐스트 송신 0");
            Assert.AreEqual(localBefore + 1, _player.MulticastCount, "미승인 — 멀티캐스트 로컬 실행은 기존 계약 그대로");

            // 승인 후에도 밀린 호출이 리플레이되지 않는다 — 유보 큐 아님.
            _server.OnClientVersionDeclared(_ch.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, Count(ToastId), "승인 후에도 이전 드롭 호출은 리플레이되지 않음");
        }

        // T2 — no-scene 승인 양성 대조: 씬 전환을 한 번도 하지 않은 승인 연결은 정상 송신된다 (IsClientSceneReady 오용 방지 회귀).
        [Test]
        public void 씬_전환_없는_승인_연결은_정상_송신한다()
        {
            _server.OnClientVersionDeclared(_ch.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();

            _player.RpcToast();
            _player.RpcAwardTo(_ch.UniNetConnId, 2, "go");
            _player.RpcDeath();

            Assert.AreEqual(1, Count(ToastId), "승인(씬 전환 이력 없음) — 방송 정상 송신");
            Assert.AreEqual(1, Count(AwardId), "승인 — 타깃 정상 송신");
            Assert.AreEqual(1, Count(DeathId), "승인 — 멀티캐스트 정상 송신");
        }

        // T3 — 씬 대기(AwaitingSceneReady): ready 보고 전까지 송신 0, 보고 후 정상 복귀.
        [Test]
        public void 씬_대기_연결은_ready_보고_전까지_송신되지_않는다()
        {
            _server.OnClientVersionDeclared(_ch.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();
            _server.LoadScene("gate");
            UniNetEnvironment.PumpMain();   // wire 11 송신 — 게임 데이터 홀드 시작

            _player.RpcToast();
            _player.RpcAwardTo(_ch.UniNetConnId, 3, "scene");
            _player.RpcDeath();
            Assert.AreEqual(0, Count(ToastId), "씬 대기 — 방송 송신 0");
            Assert.AreEqual(0, Count(AwardId), "씬 대기 — 타깃 송신 0");
            Assert.AreEqual(0, Count(DeathId), "씬 대기 — 멀티캐스트 송신 0");

            _server.OnClientSceneLoaded(_ch.UniNetConnId, "gate");
            UniNetEnvironment.PumpMain();
            _player.RpcToast();
            Assert.AreEqual(1, Count(ToastId), "ready 보고 후 — 정상 송신 복귀");
        }
    }
}
