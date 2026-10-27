using System.Collections.Generic;
using System.Text.RegularExpressions;
using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// FR-003 — RPC collection argument boundaries on the full generated receive path (ownership gate → decode →
    /// QueueOnMain): non-empty List + trailing scalar round-trip (count/elements/tail/wire bytes), truncated and
    /// unconsumed-bytes payloads never reaching the game implementation, and the 65535/65536 length limits for
    /// arrays and Lists. Harness follows GeneratorTypeSupportTests (Fixtures assembly registers via reflection).
    /// </summary>
    public sealed class CollectionRpcBoundaryTests
    {
        private readonly List<GameObject> _created = new();
        private NetworkServer _prevServer;
        private NetworkServer _server = null!;
        private Gen005CollectionRpcObject _fixture = null!;
        private RecordingChannel _ownerCh = null!;

        private static readonly int ListTailId = Fnv1a.MethodId("UniNet.Tests.Gen005CollectionRpcObject.RpcListTail");

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

        private void StartFixture()
        {
            _server = new NetworkServer();
            UniNetEnvironment.SetServer(_server);
            var go = new GameObject("gen005-boundary");
            _created.Add(go);
            _fixture = go.AddComponent<Gen005CollectionRpcObject>();
            _server.RegisterSceneObject(_fixture.NetId, new NetworkBehaviour[] { _fixture });
            _ownerCh = new RecordingChannel();
            _server.AttachConnection(_ownerCh);
            _server.OnClientVersionDeclared(_ownerCh.UniNetConnId, _server.ExpectedProtocolVersion ?? "");   // CON-001 — fake channels must declare too
            UniNetEnvironment.PumpMain();   // 승인 + 소유권 배정 — 첫 연결이 단일 오브젝트 소유
        }

        private static void DispatchServer(long senderConnId, int methodId, byte[] wire)
            => UniNetDispatch.ServerHandlers()[methodId].Handler(senderConnId, wire).GetAwaiter().GetResult();

        // T1 — 비어 있지 않은 List + 후속 인자: 개수·원소·후속 인자·와이어 바이트가 정확히 복원된다.
        [Test]
        public void nonempty_List와_후속인자는_왕복한다()
        {
            StartFixture();
            var values = new List<int> { 11, 22, 33 };
            byte[] wire = Gen005CollectionRpcObject.__UniNetEncode_RpcListTail(_fixture.NetId, _fixture.SubId, values, 99);

            byte[] expected;
            var w = MessageBufferWriter.Create();
            w.WriteUInt64(_fixture.NetId);
            w.WriteByte(_fixture.SubId);
            w.WriteBoolean(false);
            w.WriteUInt16(3);
            w.WriteInt32(11); w.WriteInt32(22); w.WriteInt32(33);
            w.WriteInt32(99);
            expected = w.ToArray();
            CollectionAssert.AreEqual(expected, wire, "와이어 계약 — null 플래그 + UInt16 개수 + 원소 순서 + 후속 int");

            DispatchServer(_ownerCh.UniNetConnId, ListTailId, wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, _fixture.ListTailCalls, "구현 정확 1회 실행");
            Assert.IsNotNull(_fixture.LastValues, "수신 List — null 아님");
            Assert.AreEqual(3, _fixture.LastValues.Count, "개수 보존 (capacity/Count 혼동 회귀 방지)");
            CollectionAssert.AreEqual(new[] { 11, 22, 33 }, _fixture.LastValues, "원소 순서·값 보존");
            Assert.AreEqual(99, _fixture.LastTail, "List 이후 후속 인자 정렬 보존");
        }

        // T2 — 잘린 컬렉션 페이로드: 리더 경계 예외가 은닉되고 구현은 호출되지 않는다.
        [Test]
        public void 잘린_컬렉션_페이로드는_구현을_호출하지_않는다()
        {
            StartFixture();
            byte[] wire = Gen005CollectionRpcObject.__UniNetEncode_RpcListTail(_fixture.NetId, _fixture.SubId, new List<int> { 11, 22, 33 }, 99);
            byte[] truncated = new byte[9 + 1 + 2 + 4 + 4];   // 헤더 9 + 플래그 1 + 개수 2(=3) + 원소 2개만
            System.Array.Copy(wire, truncated, truncated.Length);

            LogAssert.Expect(LogType.Exception, new Regex("Attempted to read past the end of the buffer"));
            Assert.DoesNotThrow(() => DispatchServer(_ownerCh.UniNetConnId, ListTailId, truncated), "디코드 실패는 핸들러 경계에서 은닉");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, _fixture.ListTailCalls, "잘린 페이로드 — 구현 미호출");
        }

        // T3 — 미소비 bytes가 남는 페이로드: 생성 디코드의 트레일링 검증이 구현 호출을 차단한다.
        [Test]
        public void 미소비_bytes가_남는_페이로드는_구현을_호출하지_않는다()
        {
            StartFixture();
            byte[] wire = Gen005CollectionRpcObject.__UniNetEncode_RpcListTail(_fixture.NetId, _fixture.SubId, new List<int> { 11, 22, 33 }, 99);
            byte[] trailed = new byte[wire.Length + 1];   // 정상 페이로드 + 잉여 1바이트
            System.Array.Copy(wire, trailed, wire.Length);
            trailed[^1] = 0xFF;

            LogAssert.Expect(LogType.Exception, new Regex("RPC payload decode mismatch"));
            Assert.DoesNotThrow(() => DispatchServer(_ownerCh.UniNetConnId, ListTailId, trailed), "트레일링 불일치도 핸들러 경계에서 은닉");
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(0, _fixture.ListTailCalls, "미소비 bytes — 구현 미호출");
        }

        // T4 — 길이 상한: 65536은 직렬화 전 명시 거부, 65535는 UInt16 개수로 그대로 인코딩된다 (배열·List 공통).
        [Test]
        public void 배열과_List는_65536을_거부하고_65535를_인코딩한다()
        {
            StartFixture();

            var bigArr = new int[65536];
            Assert.Throws<System.ArgumentException>(
                () => Gen005CollectionRpcObject.__UniNetEncode_RpcArrTail(_fixture.NetId, _fixture.SubId, bigArr, 7),
                "int[] 65536 — 명시 거부 (자른 직렬화 금지)");
            var bigList = new List<int>(bigArr);
            Assert.Throws<System.ArgumentException>(
                () => Gen005CollectionRpcObject.__UniNetEncode_RpcListTail(_fixture.NetId, _fixture.SubId, bigList, 7),
                "List<int> 65536 — 명시 거부");

            var maxArr = new int[65535];
            byte[] arrWire = Gen005CollectionRpcObject.__UniNetEncode_RpcArrTail(_fixture.NetId, _fixture.SubId, maxArr, 7);
            Assert.AreEqual(9 + 1 + 2 + 65535 * 4 + 4, arrWire.Length, "int[] 65535 — null 플래그 + 개수 prefix + 전체 원소 인코딩");

            var maxList = new List<int>(maxArr);
            byte[] listWire = Gen005CollectionRpcObject.__UniNetEncode_RpcListTail(_fixture.NetId, _fixture.SubId, maxList, 7);
            Assert.AreEqual(9 + 1 + 2 + 65535 * 4 + 4, listWire.Length, "List<int> 65535 — 전체 원소 인코딩");
        }
    }
}
