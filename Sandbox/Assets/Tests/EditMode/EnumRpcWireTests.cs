using System;
using System.Collections.Generic;
using System.Reflection;
using DRPC;
using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// GEN-003 — enum RPC parameters and replicated fields. An enum rides its underlying integer's codec:
    /// the emitted wire bytes are identical to the backing primitive (write casts to the underlying, read casts back).
    /// Out-of-range values pass through unvalidated by design (D3) — server-authoritative range checks are the user's
    /// _Validate concern. Harness follows OwnerUpdateWireTests / ServerRpcOwnershipTests.
    /// T7 ([Message] enum probe) is intentionally absent — deferred per plan D5: MessageProtocol is a prebuilt DLL,
    /// so a compile probe is the only evidence; a separate ledger item will cover it if the probe fails.
    /// </summary>
    public sealed class EnumRpcWireTests
    {
        private readonly List<GameObject> _created = new();   // only GameObjects THIS fixture created
        private NetworkServer _prevServer;
        private NetworkClient _prevClient;
        private NetworkServer _server = null!;
        private GameObject _go = null!;
        private Gen003EnumRpcPlayer _player = null!;
        private RecordingChannel _ownerCh = null!;

        private static readonly int StanceId = Fnv1a.MethodId("UniNet.Tests.Gen003EnumRpcPlayer.RpcStance");
        private static readonly int StanceRawId = Fnv1a.MethodId("UniNet.Tests.Gen003EnumRpcPlayer.RpcStanceRaw");
        private static readonly int PhaseId = Fnv1a.MethodId("UniNet.Tests.Gen003EnumRpcPlayer.RpcPhase");
        private static readonly int AccessId = Fnv1a.MethodId("UniNet.Tests.Gen003EnumRpcPlayer.RpcAccess");

        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
            _prevServer = UniNetEnvironment.Server;   // preserve the ambient globals — no cross-fixture pollution
            _prevClient = UniNetEnvironment.Client;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.PumpMain();   // drain the pending queue — keep delayed dispatch from leaking into other tests
            UniNetEnvironment.SetServer(_prevServer);
            UniNetEnvironment.SetClient(_prevClient);
            foreach (var go in _created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _created.Clear();
        }

        private void Track(GameObject go) => _created.Add(go);

        /// <summary>Server + owner connection + single registered player — the ownership tests pattern (first connection owns it).</summary>
        private Gen003EnumRpcPlayer StartRpcFixture()
        {
            _server = new NetworkServer();
            UniNetEnvironment.SetServer(_server);
            _go = new GameObject("enum-rpc");
            Track(_go);
            _player = _go.AddComponent<Gen003EnumRpcPlayer>();
            _server.RegisterSceneObject(_player.NetId, new NetworkBehaviour[] { _player });
            _ownerCh = new RecordingChannel();
            _server.AttachConnection(_ownerCh);
            _server.OnClientVersionDeclared(_ownerCh.UniNetConnId, _server.ExpectedProtocolVersion ?? "");   // CON-001 — fake channels must declare too
            UniNetEnvironment.PumpMain();
            return _player;
        }

        /// <summary>Dispatches through the registered server receive handler — the same full receive path a real wire message takes.</summary>
        private static void DispatchServer(long senderConnId, int methodId, byte[] wire)
            => UniNetDispatch.ServerHandlers()[methodId].Handler(senderConnId, wire).GetAwaiter().GetResult();

        // T1 — int-backed enum ServerRpc round trip + wire invariance: the enum payload must be byte-for-byte
        // identical to its underlying-int twin (the whole GEN-003 wire contract in one assertion).
        [Test]
        public void int_기반_enum_ServerRpc는_기본형과_바이트가_동일하게_왕복한다()
        {
            var player = StartRpcFixture();

            byte[] enumWire = Gen003EnumRpcPlayer.__UniNetEncode_RpcStance(player.NetId, player.SubId, WireStance.Chase);
            byte[] rawWire = Gen003EnumRpcPlayer.__UniNetEncode_RpcStanceRaw(player.NetId, player.SubId, (int)WireStance.Chase);
            CollectionAssert.AreEqual(rawWire, enumWire, "와이어 불변 — enum 인코딩은 underlying int와 바이트 동일");

            DispatchServer(_ownerCh.UniNetConnId, StanceId, enumWire);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, player.StanceCalls, "소유자 발신 실행");
            Assert.AreEqual(WireStance.Chase, player.LastStance, "enum 값 온전 복원");
        }

        // T2 — byte-backed enum: the codec must use the declared backing width (WriteByte), not default to int.
        [Test]
        public void byte_기반_enum도_선언된_폭으로_왕복한다()
        {
            var player = StartRpcFixture();

            byte[] wire = Gen003EnumRpcPlayer.__UniNetEncode_RpcPhase(player.NetId, player.SubId, WirePhase.Halted);
            Assert.AreEqual(8 + 1 + 1, wire.Length, "페이로드 크기 — netId 8 + subId 1 + byte 1 (int였다면 4)");

            DispatchServer(_ownerCh.UniNetConnId, PhaseId, wire);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(WirePhase.Halted, player.LastPhase, "byte 범위 끝값(200) 보존");
        }

        // T3 — ulong-backed [Flags] enum: bit 33 proves the full 64-bit width rides the wire.
        [Test]
        public void ulong_Flags_enum의_32비트_넘는_비트도_보존한다()
        {
            var player = StartRpcFixture();
            var mask = WireAccess.Move | WireAccess.Build;   // bit 33 — int 캐스트로 좁혀지면 소실

            byte[] wire = Gen003EnumRpcPlayer.__UniNetEncode_RpcAccess(player.NetId, player.SubId, mask);
            DispatchServer(_ownerCh.UniNetConnId, AccessId, wire);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(mask, player.LastAccess, "ulong underlying — bit 33 보존");
        }

        // T4 — an undefined value ((WireStance)99) passes through: enum wire support is pass-through by design (D3).
        // Range validation is the server's trust boundary (user _Validate), not the codec's job.
        [Test]
        public void 미정의_enum값은_거부나_클램프_없이_통과한다()
        {
            var player = StartRpcFixture();
            const WireStance undefined = (WireStance)99;

            byte[] wire = Gen003EnumRpcPlayer.__UniNetEncode_RpcStance(player.NetId, player.SubId, undefined);
            DispatchServer(_ownerCh.UniNetConnId, StanceId, wire);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(99, (int)player.LastStance, "미정의 값 pass-through — 거부·클램프·예외 없음");
            Assert.AreEqual(1, player.StanceCalls, "구현은 정상 실행된다");
        }

        /// <summary>
        /// CaptureReplicateFixture pattern (OwnerUpdateWireTests): a server records one genuine replicate delta for a
        /// scene Gen003EnumReplicator, and a client-side twin registers the same netId. Runs WITHOUT an ambient server —
        /// registration + PumpMain drive the initial delta on the server instance directly.
        /// </summary>
        private (ulong netId, byte subId, int methodId, byte[] delta, Gen003EnumReplicator clientPlayer, NetworkClient client)
            CaptureEnumReplicate(WireStance stance, WireStance[] stances)
        {
            var server = new NetworkServer();
            var go = new GameObject("gen003-repl");
            Track(go);
            var player = go.AddComponent<Gen003EnumReplicator>();
            server.RegisterSceneObject(player.NetId, new NetworkBehaviour[] { player });
            player.Stance = stance;
            if (stances != null) player.Stances = stances;
            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — fake channels must declare too
            UniNetEnvironment.PumpMain();
            Assert.GreaterOrEqual(ch.Replicates.Count, 1, "준비 — replicate 기록 존재");
            var (netId, subId, methodId, delta) = ch.Replicates[0];

            var client = new NetworkClient();
            UniNetEnvironment.SetClient(client);
            client.SetLocalConnId(ch.UniNetConnId);
            var clientGo = new GameObject("gen003-repl-client");
            Track(clientGo);
            var clientPlayer = clientGo.AddComponent<Gen003EnumReplicator>();
            typeof(NetworkBehaviour).GetField("_sceneNetId", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(clientPlayer, netId);   // same scene netId as the server fixture — precedent (OwnerBaselineTests)
            client.Register(netId, new object[] { clientPlayer });
            return (netId, subId, methodId, delta, clientPlayer, client);
        }

        /// <summary>Feeds a captured delta through the registered production single-Replicate handler ([netId:8][subId:1][delta]).</summary>
        private static void ApplyReplicate(ulong netId, byte subId, int methodId, byte[] delta)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteByte(subId);
            w.WriteBytes(delta);
            UniNetDispatch.ClientHandlers()[methodId].Handler(0, w.ToArray()).GetAwaiter().GetResult();
            UniNetEnvironment.PumpMain();
        }

        // T5 — a [Replicated] enum scalar syncs server → client through the production delta path.
        [Test]
        public void Replicated_enum_필드는_서버에서_클라로_동기화된다()
        {
            var fixture = CaptureEnumReplicate(WireStance.Guard, null);
            Assert.AreEqual(WireStance.Idle, fixture.clientPlayer.Stance, "준비 — 클라 기본값");

            Assert.Greater(fixture.delta.Length, 0, "초기 델타 — enum 필드 포함");
            ApplyReplicate(fixture.netId, fixture.subId, fixture.methodId, fixture.delta);
            Assert.AreEqual(WireStance.Guard, fixture.clientPlayer.Stance, "enum 필드 동기화 — underlying int 코덱");
        }

        // T6 — enum-element array elements survive the FastArray path (Insert replay on the client twin).
        [Test]
        public void Replicated_enum_배열의_원소들도_동기화된다()
        {
            var values = new[] { WireStance.Chase, (WireStance)99, WireStance.Guard };   // includes an undefined element — pass-through in arrays too
            var fixture = CaptureEnumReplicate(WireStance.Idle, values);

            Assert.Greater(fixture.delta.Length, 0, "초기 델타");
            ApplyReplicate(fixture.netId, fixture.subId, fixture.methodId, fixture.delta);
            Assert.AreEqual(WireStance.Idle, fixture.clientPlayer.Stance, "스칼라 필드도 동일 델타에 실린다");
            CollectionAssert.AreEqual(values, fixture.clientPlayer.Stances, "배열 원소 순서·값 보존 (미정의 99 포함)");
        }
    }
}
