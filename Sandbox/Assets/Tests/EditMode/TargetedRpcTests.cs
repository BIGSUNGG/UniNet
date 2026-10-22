using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DRPC;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// RPC-001 — targeted ClientRpc companions ({Name}To(connId, args) / {Name}ToOwner(args)) emitted per [ClientRpc].
    /// Server-side send loop coverage on the RecordingChannel pattern: broadcast invariance (T1), owner targeting (T2),
    /// connId targeting + wire identity (T3), ownerless warning (T4), unknown/disconnected connId silence (T5),
    /// and the server-authoritative guard on the client side (T6). T7 (real loopback) lives in PlayMode.
    /// </summary>
    public sealed class TargetedRpcTests
    {
        /// <summary>RecordingChannel pattern with UniNetSend recording — the generated ClientRpc send path targets IUniNetSystemChannel.UniNetSend.</summary>
        private sealed class RpcChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal readonly List<(int methodId, byte[] payload, RpcDeliveryMode mode)> RpcSends = new();

            public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) => RpcSends.Add((methodId, payload, mode));
            public void SendWelcome(long connId, string serverVersion, string sessionToken) { }
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
        private RpcChannel _ownerCh = null!;
        private RpcChannel _guestCh = null!;

        private static readonly int ToastId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcToast");
        private static readonly int AwardId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcAward");

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _server = new NetworkServer();
            UniNetEnvironment.SetServer(_server);
            _go = new GameObject("targeted-rpc");
            _player = _go.AddComponent<VerifyPlayer>();
            _server.RegisterSceneObject(_player.NetId, new NetworkBehaviour[] { _player });
            typeof(NetworkBehaviour).GetMethod("MarkServerRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_player, null);   // activates IsServer — the generated sender guard (NetworkTransformComponentTests precedent)
            _ownerCh = new RpcChannel();
            _guestCh = new RpcChannel();
            _server.AttachConnection(_ownerCh);
            _server.OnClientVersionDeclared(_ownerCh.UniNetConnId, _server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            _server.AttachConnection(_guestCh);
            _server.OnClientVersionDeclared(_guestCh.UniNetConnId, _server.ExpectedProtocolVersion ?? "");
            UniNetEnvironment.PumpMain();   // welcome + ownership assignment — single object, so the first connection owns it
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.PumpMain();   // drain the pending queue — keep delayed dispatch from leaking into other tests
            UniNetEnvironment.SetServer(null);
            Object.DestroyImmediate(_go);
        }

        private int Count(RpcChannel ch, int methodId) => ch.RpcSends.Count(s => s.methodId == methodId);

        // T1 — broadcast invariance: the existing {Name}(args) form still reaches every live channel exactly once,
        // with one shared payload (same encoder) and the declared delivery — wire unchanged.
        [Test]
        public void 브로드캐스트는_모든_연결에_한_번씩_동일_페이로드로_송신한다()
        {
            _player.RpcToast();
            Assert.AreEqual(1, Count(_ownerCh, ToastId), "owner 채널 정확 1회");
            Assert.AreEqual(1, Count(_guestCh, ToastId), "guest 채널 정확 1회");

            _player.RpcAward(3, "headshot");
            var ownerSend = _ownerCh.RpcSends.Single(s => s.methodId == AwardId);
            var guestSend = _guestCh.RpcSends.Single(s => s.methodId == AwardId);
            CollectionAssert.AreEqual(ownerSend.payload, guestSend.payload, "동일 인자 → 모든 채널 동일 페이로드");
            Assert.AreEqual(RpcDeliveryMode.ReliableOrdered, ownerSend.mode, "속성 delivery 불변");
        }

        // T2 — ToOwner: only the owner's channel receives, non-owner channels stay silent.
        [Test]
        public void ToOwner는_소유자_채널에만_정확히_한_번_송신한다()
        {
            Assert.AreEqual(_ownerCh.UniNetConnId, _server.GetOwner(_player.NetId), "사전 조건 — 첫 연결이 단일 오브젝트 소유");

            _player.RpcAwardToOwner(3, "headshot");
            Assert.AreEqual(1, Count(_ownerCh, AwardId), "owner 채널 정확 1회");
            Assert.AreEqual(0, Count(_guestCh, AwardId), "비소유 채널 미수신");
            StringAssert.Contains("headshot", DecodeReason(_ownerCh.RpcSends[0].payload), "인자가 페이로드에 실린다");
        }

        // T3 — To(connId): only the addressed connection receives; methodId/payload identical to the broadcast (encode reuse).
        [Test]
        public void To는_지정_연결에만_송신하고_methodId와_payload가_브로드캐스트와_동일하다()
        {
            _player.RpcAward(5, "combo");   // broadcast baseline — reaches both channels
            _player.RpcAwardTo(_guestCh.UniNetConnId, 5, "combo");   // targeted — guest only

            Assert.AreEqual(1, Count(_ownerCh, AwardId), "미지정 채널은 브로드캐스트 1회뿐");
            Assert.AreEqual(2, Count(_guestCh, AwardId), "브로드캐스트 + 타깃 1회");

            var broadcast = _guestCh.RpcSends.First(s => s.methodId == AwardId);
            var targeted = _guestCh.RpcSends.Last(s => s.methodId == AwardId);
            Assert.AreEqual(broadcast.methodId, targeted.methodId, "methodId 동일 — 수신 경로 무변경");
            CollectionAssert.AreEqual(broadcast.payload, targeted.payload, "encode 재사용 — payload 동일성");
            Assert.AreEqual(broadcast.mode, targeted.mode, "delivery 불변 — 와이어 무변경");
        }

        // T4 — ToOwner on an ownerless object: no send, one warning, no exception.
        [Test]
        public void ToOwner는_소유자_미배정이면_경고를_남기고_송신하지_않는다()
        {
            var go2 = new GameObject("ownerless");
            var lone = go2.AddComponent<VerifyPlayer>();
            _server.RegisterSceneObject(lone.NetId, new NetworkBehaviour[] { lone });   // registered after attach — owner stays 0 until the next reassignment pass
            typeof(NetworkBehaviour).GetMethod("MarkServerRegistered", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(lone, null);   // sender guard needs IsServer on THIS component
            try
            {
                LogAssert.Expect(LogType.Warning, new Regex("ToOwner.*소유자 미배정"));
                Assert.DoesNotThrow(() => lone.RpcAwardToOwner(1, "nobody"));
                Assert.AreEqual(0, Count(_ownerCh, AwardId), "owner 채널 미수신");
                Assert.AreEqual(0, Count(_guestCh, AwardId), "guest 채널 미수신");
            }
            finally
            {
                Object.DestroyImmediate(go2);
            }
        }

        // T5 — To with a never-assigned or already-disconnected connId: no send, no exception.
        [Test]
        public void To는_알수없거나_끊긴_connId에_예외없이_무송신한다()
        {
            Assert.DoesNotThrow(() => _player.RpcAwardTo(999, 1, "ghost"));   // never assigned
            Assert.AreEqual(0, Count(_ownerCh, AwardId));
            Assert.AreEqual(0, Count(_guestCh, AwardId));

            _server.DetachConnection(_guestCh);
            UniNetEnvironment.PumpMain();   // disconnect cleanup + orphan reassignment — the object stays with the owner
            Assert.DoesNotThrow(() => _player.RpcAwardTo(_guestCh.UniNetConnId, 2, "late"));
            Assert.AreEqual(0, Count(_ownerCh, AwardId), "끊긴 연결 대상 송신 없음");
        }

        // T6 — client context (no Server in the environment — exactly the generated guard condition): nothing is sent or executed.
        [Test]
        public void 클라에서의_To_ToOwner_호출은_무송신_무실행이다()
        {
            UniNetEnvironment.SetServer(null);   // a client has no Server in its environment — the generated guard's condition
            Assert.DoesNotThrow(() => _player.RpcAwardTo(_ownerCh.UniNetConnId, 1, "x"));
            Assert.DoesNotThrow(() => _player.RpcAwardToOwner(1, "x"));
            Assert.AreEqual(0, Count(_ownerCh, AwardId), "서버 권위 가드 — 송신 없음");
            Assert.AreEqual(0, Count(_guestCh, AwardId), "서버 권위 가드 — 송신 없음");
            Assert.IsFalse(_player.AwardRan, "서버 권위 가드 — 구현 실행 없음");
        }

        /// <summary>Reads the trailing string argument back out of an award payload (wire: [ulong netId][byte subId][int coins][string reason]).</summary>
        private static string DecodeReason(byte[] payload)
        {
            var r = new global::MessageProtocol.Serialize.MessageBufferReader(payload);
            r.ReadUInt64();
            r.ReadByte();
            r.ReadInt32();
            return r.ReadString();
        }
    }
}
