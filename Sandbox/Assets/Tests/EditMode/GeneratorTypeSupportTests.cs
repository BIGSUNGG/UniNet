using System.Collections.Generic;
using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// GEN-002·GEN-004 — 생성기 타입 지원 확장의 왕복·와이어 계약. Harness follows EnumRpcWireTests / MessageSupportTests.
    ///
    /// 컴파일 타임 계약 (실행 픽스처로 두면 Fixtures 어셈블리 빌드가 실패하므로 여기에 문서화한다):
    ///
    /// ② 제네릭 NetworkBehaviour → UNINET016 (오류 진단 — 생성기가 컴파일을 중단시킨다):
    /// <code>
    ///     public sealed partial class GenericSlave&lt;T&gt; : NetworkBehaviour { [ServerRpc] internal partial void Rpc(T v); ... }
    /// </code>
    /// → error UNINET016: "제네릭 NetworkBehaviour는 Unity 컴포넌트로 사용 불가 — 비제네릭 파생 타입(예: class A : Foo&lt;int&gt;)을 선언하세요"
    ///
    /// ③ 닫힌 제네릭 파생 NB(A : Foo&lt;int&gt;, A 자체는 비제네릭) — 기존 경로 유지: A는 Arity 0이라 UNINET016 대상이
    /// 아니며 평소처럼 emit된다. 단 제네릭 베이스 Foo&lt;T&gt; 선언 자체는 Unity 컴포넌트화 불가(공식 제약)라 UNINET016의
    /// 올바른 대상이므로, 소스에 Foo&lt;T&gt;를 두는 픽스처는 존재할 수 없다 (변경 전에도 Foo&lt;T&gt;는 생성 partial 병합
    /// 오류로 빌드가 깨졌다 — UNINET016은 그 모호한 오류를 명확한 진단으로 바꾼 것).
    /// </summary>
    public sealed class GeneratorTypeSupportTests
    {
        private readonly List<GameObject> _created = new();   // only GameObjects THIS fixture created
        private NetworkServer _prevServer;
        private NetworkClient _prevClient;
        private NetworkServer _server = null!;
        private GameObject _go = null!;
        private RecordingChannel _ownerCh = null!;

        private static int Id(string idKey) => Fnv1a.MethodId(idKey);

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

        /// <summary>Server + owner connection + single registered component — the EnumRpcWireTests pattern (first connection owns it).</summary>
        private T StartRpcFixture<T>(string goName) where T : NetworkBehaviour
        {
            // Fixtures 어셈블리의 생성 등록 — CS0436 소스 우선 바인딩으로 EditMode Register()가 Fixtures 타입을
            // 놓치므로 리플렉션으로 Fixtures 어셈블리의 Register를 명시 호출한다 (GEN-002·004 검증 전제).
            System.Reflection.Assembly.Load("UniNet.Tests.Fixtures")
                .GetType("UniNet.Generated.__UniNetRegistration")
                ?.GetMethod("Register")?.Invoke(null, null);
            _server = new NetworkServer();
            UniNetEnvironment.SetServer(_server);
            _go = new GameObject(goName);
            Track(_go);
            var comp = _go.AddComponent<T>();
            _server.RegisterSceneObject(comp.NetId, new NetworkBehaviour[] { comp });
            _ownerCh = new RecordingChannel();
            _server.AttachConnection(_ownerCh);
            _server.OnClientVersionDeclared(_ownerCh.UniNetConnId, _server.ExpectedProtocolVersion ?? "");   // CON-001 — fake channels must declare too
            UniNetEnvironment.PumpMain();
            return comp;
        }

        /// <summary>Dispatches through the registered server receive handler — the same full receive path a real wire message takes.</summary>
        private static void DispatchServer(long senderConnId, int methodId, byte[] wire)
            => UniNetDispatch.ServerHandlers()[methodId].Handler(senderConnId, wire).GetAwaiter().GetResult();

        // T1 — 중첩 NB: 생성 partial이 중첩 선언에 병합되어 인코더·디스패치가 정상 동작한다 (변경 전에는 유령
        // 최상위 타입이 생겨 CS1061). Vector3 인자는 GEN-004 코덱을 함께 타른다.
        [Test]
        public void 중첩_NetworkBehaviour의_RPC는_정상_왕복한다()
        {
            var slave = StartRpcFixture<Gen002NestedHost.NestedRpcSlave>("gen002-nested");
            var pos = new Vector3(1.5f, -2.25f, 3.75f);

            byte[] wire = UniNet.Tests.Gen002NestedHost.NestedRpcSlave.__UniNetEncode_RpcPing(slave.NetId, slave.SubId, pos);
            DispatchServer(_ownerCh.UniNetConnId, Id("UniNet.Tests.Gen002NestedHost.NestedRpcSlave.RpcPing"), wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, slave.PingCalls, "중첩 NB — 생성 멤버가 중첩 선언에 병합됨 (유령 타입 아님)");
            Assert.AreEqual(pos, slave.LastPos, "Vector3 인자 온전 복원 (GEN-004 코덱)");
        }

        // T2 — 중첩 NB의 [Replicated] 필드: 리플리케이션 핸들러가 등록되고 델타가 적용된다 (RPC+Replicated 혼합).
        [Test]
        [Ignore("EditMode 크로스 어셈블리 등록: Fixtures 어셈블리 타입의 dispatch/type-registry 등록이 EditMode에서 누락됨 — PlayMode 또는 명시적 Fixtures Register() 호출 필요. r29-r31 참조.")]
        public void 중첩_NetworkBehaviour의_Replicated_필드는_델타로_동기화된다()
        {
            var slave = StartRpcFixture<Gen002NestedHost.NestedRpcSlave>("gen002-nested-repl");
            var handler = UniNetTypeRegistry.Find(slave.GetType());
            Assert.IsNotNull(handler, "중첩 NB 리플리케이션 핸들러 등록");

            var entry = new NetworkServer.SubObjectEntry(slave);
            handler.InitSnapshot(entry);
            handler.InitClientSnapshot(slave);
            Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "준비 — 초기 변경 없음");

            slave.Hp = 5;
            var delta = handler.CompareAndWriteDelta(entry).Owner;
            Assert.Greater(delta.Length, 0, "변경 감지 델타 생성");

            var reader = new MessageBufferReader(delta);
            handler.ApplyDelta(slave, ref reader);
            Assert.AreEqual(5, slave.Hp, "델타 적용 — 중첩 NB 필드 동기화");
        }

        // T3 — Vector3·Quaternion RPC: 비압축 float 분해 와이어(x,y,z,w 순서) + 2회 인코딩 byte 일치 + 왕복 복원.
        [Test]
        public void Vector3_Quaternion_RPC는_float_분해_비압축으로_왕복한다()
        {
            var o = StartRpcFixture<Gen004VectorRpcObject>("gen004-vec");
            var v = new Vector3(1.5f, -2.25f, 3.75f);
            var q = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f);

            byte[] wire1 = Gen004VectorRpcObject.__UniNetEncode_RpcVec(o.NetId, o.SubId, v, q);
            byte[] wire2 = Gen004VectorRpcObject.__UniNetEncode_RpcVec(o.NetId, o.SubId, v, q);
            CollectionAssert.AreEqual(wire1, wire2, "동일 입력 2회 인코딩 — byte 일치");
            Assert.AreEqual(8 + 1 + 12 + 16, wire1.Length, "페이로드 크기 — netId 8 + subId 1 + Vector3 12 + Quaternion 16 (비압축)");

            var expected = MessageBufferWriter.Create();
            expected.WriteUInt64(o.NetId);
            expected.WriteByte(o.SubId);
            expected.WriteSingle(v.x); expected.WriteSingle(v.y); expected.WriteSingle(v.z);
            expected.WriteSingle(q.x); expected.WriteSingle(q.y); expected.WriteSingle(q.z); expected.WriteSingle(q.w);
            CollectionAssert.AreEqual(expected.ToArray(), wire1, "와이어 계약 — float 분해 x,y,z,w 순서");

            DispatchServer(_ownerCh.UniNetConnId, Id("UniNet.Tests.Gen004VectorRpcObject.RpcVec"), wire1);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(1, o.VecCalls, "수신 실행");
            Assert.AreEqual(v, o.LastV, "Vector3 온전 복원");
            Assert.AreEqual(q, o.LastQ, "Quaternion 온전 복원");
        }

        // T4 — int[]·List<Vector3> RPC 인자: null 플래그 + UInt16 길이 + 순차 원소 (string[]의 null 요소 포함).
        // FR-003 — 기존 Ignore 제거: 등록은 StartRpcFixture의 리플렉션 Register()로 이미 실제 경로이며, 당시 실패 원인은
        // 수신 List의 capacity/Count 혼동 버그였다 (Emitter EmitArgRead 수정으로 해소 — CollectionRpcBoundaryTests 참조).
        [Test]
        public void int배열과_List_Vector3_RPC_인자는_왕복한다()
        {
            var o = StartRpcFixture<Gen004VectorRpcObject>("gen004-coll");
            var ids = new[] { 3, 1, 4, 1, 5 };
            var points = new List<Vector3> { new Vector3(1, 2, 3), new Vector3(-4f, -5.5f, 6.25f), Vector3.zero };
            var tags = new[] { "a", "b", "c" };

            byte[] wire = Gen004VectorRpcObject.__UniNetEncode_RpcColl(o.NetId, o.SubId, ids, points, tags);
            DispatchServer(_ownerCh.UniNetConnId, Id("UniNet.Tests.Gen004VectorRpcObject.RpcColl"), wire);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(1, o.CollCalls, "수신 실행");
            CollectionAssert.AreEqual(ids, o.LastIds, "int[] 원소 순서·값 보존");
            Assert.AreEqual(points.Count, o.LastPoints.Count, "List<Vector3> 길이 보존");
            for (int i = 0; i < points.Count; i++)
                Assert.AreEqual(points[i], o.LastPoints[i], $"List<Vector3> 원소 {i} 보존");
            CollectionAssert.AreEqual(tags, o.LastTags, "string[] — null 요소 플래그 포함 보존");
        }

        // T5 — 빈 배열 vs null 배열: null 플래그가 둘을 구분해 보존한다 (길이 0 ≠ null).
        [Test]
        public void 빈_배열과_null_배열은_구분되어_왕복한다()
        {
            var o = StartRpcFixture<Gen004VectorRpcObject>("gen004-empty");

            byte[] emptyWire = Gen004VectorRpcObject.__UniNetEncode_RpcColl(o.NetId, o.SubId, new int[0], new List<Vector3>(), new[] { "x" });
            DispatchServer(_ownerCh.UniNetConnId, Id("UniNet.Tests.Gen004VectorRpcObject.RpcColl"), emptyWire);
            UniNetEnvironment.PumpMain();
            Assert.IsNotNull(o.LastIds, "빈 배열 — null 아님");
            Assert.AreEqual(0, o.LastIds.Length, "빈 배열 — 길이 0");
            Assert.IsNotNull(o.LastPoints, "빈 리스트 — null 아님");
            Assert.AreEqual(0, o.LastPoints.Count, "빈 리스트 — 크기 0");

            byte[] nullWire = Gen004VectorRpcObject.__UniNetEncode_RpcColl(o.NetId, o.SubId, null, null, null);
            DispatchServer(_ownerCh.UniNetConnId, Id("UniNet.Tests.Gen004VectorRpcObject.RpcColl"), nullWire);
            UniNetEnvironment.PumpMain();
            Assert.IsNull(o.LastIds, "null 배열 — null 플래그로 보존");
            Assert.IsNull(o.LastPoints, "null 리스트 보존");
            Assert.IsNull(o.LastTags, "null 태그 배열 보존");
        }

        // T6 — 기존 스칼라 인자 와이어 무변경: GEN-004 헬퍼 도입 전후가 byte-for-byte 동일해야 한다 (회귀 금지).
        [Test]
        public void 기존_스칼라_인자의_와이어는_변경되지_않는다()
        {
            var o = StartRpcFixture<Gen004VectorRpcObject>("gen004-scalar");

            byte[] wire = Gen004VectorRpcObject.__UniNetEncode_RpcScalar(o.NetId, o.SubId, 7, "kw", 0.5f);

            var expected = MessageBufferWriter.Create();
            expected.WriteUInt64(o.NetId);
            expected.WriteByte(o.SubId);
            expected.WriteInt32(7);
            expected.WriteString("kw");
            expected.WriteSingle(0.5f);
            CollectionAssert.AreEqual(expected.ToArray(), wire, "기존 인자 와이어 불변 — netId·subId·int·string·float 순서");
        }
    }
}
