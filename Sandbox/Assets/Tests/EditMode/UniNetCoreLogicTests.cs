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
            server.OnClientVersionDeclared(ch1.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(ch2);
            server.OnClientVersionDeclared(ch2.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // run the deferred Welcome/reassignment batch (against the final set of 2 connections)

            Assert.AreEqual(1, server.GetEntry(100).OwnerConnId, "첫 오브젝트는 첫 연결 소유");
            Assert.AreEqual(2, server.GetEntry(200).OwnerConnId, "둘째 오브젝트는 둘째 연결 소유");
            Assert.AreEqual(1, ch1.WelcomeConnId);
            Assert.AreEqual(2, ch2.WelcomeConnId);
            // KEEP policy — surviving owners are not reassigned. With both connections attached before the pump,
            // the reassignment batch notifies 2 adoptions × 2 connections = 4, and the OWN-001 catch-up baseline adds
            // each receiver's per-object owner notice (2 objects × 2 receivers = 4) — 8 total. Per receiver: 4 notices,
            // and each object (100, 200) exactly twice — its adoption broadcast plus that receiver's own baseline.
            Assert.AreEqual(8, ch1.OwnerUpdates.Count + ch2.OwnerUpdates.Count);
            foreach (var updates in new[] { ch1.OwnerUpdates, ch2.OwnerUpdates })
            {
                Assert.AreEqual(4, updates.Count, "수신자별 통보 4건 (재배정 2 + 자기 기준선 2)");
                int for100 = 0, for200 = 0;
                foreach (var (netId, owner) in updates)
                    if (netId == 100) { Assert.AreEqual(1L, owner, "netId 100 통보의 소유자 값"); for100++; }
                    else { Assert.AreEqual(200UL, netId, "통보 netId는 100/200 뿐"); Assert.AreEqual(2L, owner, "netId 200 통보의 소유자 값"); for200++; }
                Assert.AreEqual(2, for100, "이 수신자의 netId 100 — 재배정 통보 + 자기 기준선 = 2회");
                Assert.AreEqual(2, for200, "이 수신자의 netId 200 — 재배정 통보 + 자기 기준선 = 2회");
            }
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

            public void SendWelcome(long connId, string serverVersion, string sessionToken) => WelcomeConnId = connId;
            public void SendOwnerUpdate(ulong netId, long ownerConnId) => OwnerUpdates.Add((netId, ownerConnId));
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states)
                => Spawns.Add(netId);
            public void SendDestroy(ulong netId) => Destroys.Add(netId);
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason) => System.Threading.Tasks.Task.CompletedTask;
            public void SendLoadScene(string sceneName) { }
            public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) { }
        }
    }
}
