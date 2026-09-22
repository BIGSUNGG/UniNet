using DRPC;
using DRPC.Shared.Network;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>Core pure-logic unit tests (no networking) — FNV stability, options mapping, ownership policy, and delta serialization.</summary>
    public sealed class UniNetCoreLogicTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            // RuntimeInitializeOnLoadMethod only runs in play mode, so EditMode registers explicitly (idempotent)
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [Test]
        public void Fnv1a_메서드ID는_안정적이고_시스템예약을_피한다()
        {
            Assert.AreEqual(Fnv1a.MethodId("Usage.Player.RpcRequestHit"), Fnv1a.MethodId("Usage.Player.RpcRequestHit"));
            Assert.GreaterOrEqual(Fnv1a.MethodId("a"), 64);
            Assert.GreaterOrEqual(Fnv1a.MethodId("b"), 64);
            Assert.AreNotEqual(Fnv1a.MethodId("Usage.Player.RpcRequestHit"), Fnv1a.MethodId("Usage.Player.RpcPlayHitFx"));
        }

        [Test]
        public void 엔드포인트_옵션은_DRPC_옵션으로_동등하게_매핑된다()
        {
            var options = new UniNetEndpointOptions
            {
                ConnectionKey = "secret",
                ConnectTimeoutMs = 1500,
                MaxConnections = 8,
                EnableCrc32c = true,
                TlsTargetHost = "game.ds",
            };
            var rpc = options.ToRpcEndpointOptions();
            Assert.AreEqual("secret", rpc.ConnectionKey);
            Assert.AreEqual(1500, rpc.ConnectTimeoutMs);
            Assert.AreEqual(8, rpc.MaxConnections);
            Assert.IsTrue(rpc.EnableCrc32c);
            Assert.AreEqual("game.ds", rpc.TlsTargetHost);
        }

        [Test]
        public void 소유권_라운드로빈_정책이_연결순서대로_배정된다()
        {
            var server = new NetworkServer();
            server.RegisterSceneObject(100, new object[] { new object() });
            server.RegisterSceneObject(200, new object[] { new object() });

            var ch1 = new FakeChannel();
            var ch2 = new FakeChannel();
            server.AttachConnection(ch1);
            server.AttachConnection(ch2);
            UniNetEnvironment.PumpMain();   // run the deferred Welcome/reassignment batch (against the final set of 2 connections)

            Assert.AreEqual(1, server.GetEntry(100).OwnerConnId, "첫 오브젝트는 첫 연결 소유");
            Assert.AreEqual(2, server.GetEntry(200).OwnerConnId, "둘째 오브젝트는 둘째 연결 소유");
            Assert.AreEqual(1, ch1.WelcomeConnId);
            Assert.AreEqual(2, ch2.WelcomeConnId);
            // 2 reassignments × 2 objects × 2 connections = 8 notifications
            Assert.AreEqual(8, ch1.OwnerUpdates.Count + ch2.OwnerUpdates.Count);
        }

        [Test]
        public void 리플리케이션_델타는_변경분만_담고_이전값으로_알림한다()
        {
            var go = new GameObject("delta-player");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                var handler = UniNetTypeRegistry.Find(player.GetType());
                Assert.IsNotNull(handler, "픽스처 리플리케이션 핸들 등록(생성 코드)");
                Assert.IsTrue(handler.HasFields);

                var entry = new NetworkServer.SubObjectEntry(player);
                Assert.AreSame(player, entry.Instance);

                // server: snapshot → no change, no delta → change, delta
                handler.InitSnapshot(entry);
                handler.InitClientSnapshot(player);   // also initializes the client's previous-value snapshot (100) before any change
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "변경 없으면 델타 없음");

                player.Score = 93;
                byte[] delta = handler.CompareAndWriteDelta(entry).Owner;
                Assert.Greater(delta.Length, 0, "변경분이 있으면 델타 생성");

                // client: apply → RepNotify (previous value)
                Assert.IsFalse(player.ScoreNotified);
                var reader = new MessageProtocol.Serialize.MessageBufferReader(delta);
                handler.ApplyDelta(player, ref reader);
                Assert.AreEqual(93, player.Score, "델타 적용 후 현재값");
                Assert.IsTrue(player.ScoreNotified, "RepNotify 호출");
                Assert.AreEqual(100, player.LastPrevScore, "RepNotify는 이전값을 받는다");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private sealed class FakeChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            internal long WelcomeConnId;
            internal readonly System.Collections.Generic.List<(ulong, long)> OwnerUpdates = new();
            internal readonly System.Collections.Generic.List<ulong> Spawns = new();
            internal readonly System.Collections.Generic.List<ulong> Destroys = new();

            public void SendWelcome(long connId) => WelcomeConnId = connId;
            public void SendOwnerUpdate(ulong netId, long ownerConnId) => OwnerUpdates.Add((netId, ownerConnId));
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, byte subCount, ulong[] typeKeys, byte[][] states)
                => Spawns.Add(netId);
            public void SendDestroy(ulong netId) => Destroys.Add(netId);
            public void SendTimeSync(double serverTime) { }
            public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) { }
        }
    }
}
