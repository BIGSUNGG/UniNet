using MessageProtocol;
using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Unit tests for MP [Message] type support — proves round-trip, polymorphism, and replication
    /// message fields through the generated encoder/dispatch path.
    /// </summary>
    public sealed class MessageSupportTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
            => global::UniNet.Generated.__UniNetRegistration.Register();   // explicit EditMode registration (idempotent)

        [Test]
        public void 메시지_파라미터_인코딩은_MessageId_헤더로_다형성을_보존한다()
        {
            // build a [netId][Message: DerivedPayloadMsg] payload with the generated encoder
            var msg = new DerivedPayloadMsg { Value = 42, Bonus = 7 };
            byte[] payload = VerifyPlayer.__UniNetEncode_RpcDeliver(1234UL, 0, msg);   // [netId][subId][Message]

            // unwrap in the same order the receiver does: netId → subId → MessageId dispatch
            var r = new MessageBufferReader(payload);
            Assert.AreEqual(1234UL, r.ReadUInt64(), "페이로드 앞은 netId");
            Assert.AreEqual(0, r.ReadByte(), "그 다음은 subId");

            object decoded = MessageSerializer.DeserializeFromReader(ref r);
            Assert.IsInstanceOf<DerivedPayloadMsg>(decoded, "구체 타입 복원 — 다형성");
            var derived = (DerivedPayloadMsg)decoded;
            Assert.AreEqual(42, derived.Value, "부모 필드 온전");
            Assert.AreEqual(7, derived.Bonus, "자식 고유 필드 온전");
        }

        [Test]
        public void 리플리케이션_메시지_필드는_참조_교체시_델타가_전송되고_이전값으로_알린다()
        {
            var go = new GameObject("msg-repl");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                var handler = UniNetTypeRegistry.Find(player.GetType());
                Assert.IsNotNull(handler);

                var entry = new NetworkServer.SubObjectEntry(player);
                handler.InitSnapshot(entry);
                handler.InitClientSnapshot(player);
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "초기 상태는 변경 없음");

                // swapping in a new instance dirties via reference comparison — sent even without a value-level change
                player.StateMsg = new PayloadMsg { Value = 99 };
                byte[] delta = handler.CompareAndWriteDelta(entry).Owner;
                Assert.Greater(delta.Length, 0, "메시지 필드 델타 생성");

                // client-side apply — restore via MessageId + RepNotify (receives the previous reference)
                var reader = new MessageBufferReader(delta);
                handler.ApplyDelta(player, ref reader);
                Assert.AreEqual(99, player.StateMsg.Value, "메시지 필드 적용값");
                Assert.IsTrue(player.StateMsgNotified, "RepNotify 호출");
                Assert.IsNotNull(player.LastStatePrev, "RepNotify는 이전 인스턴스 참조를 받는다");
                Assert.AreNotSame(player.StateMsg, player.LastStatePrev, "이전 참조 ≠ 새 인스턴스");
                Assert.AreEqual(0, player.LastStatePrev.Value, "이전 인스턴스 값(초기 0) 유지");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
