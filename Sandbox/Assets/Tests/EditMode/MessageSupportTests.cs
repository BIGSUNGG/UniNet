using MessageProtocol;
using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// MP [Message] 타입 지원 단위 테스트 — 생성된 인코더/디스패치 경로로 왕복·다형성·리플리케이션 메시지 필드를 증명한다.
    /// </summary>
    public sealed class MessageSupportTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
            => global::UniNet.Generated.__UniNetRegistration.Register();   // EditMode 명시 등록 (멱등)

        [Test]
        public void 메시지_파라미터_인코딩은_MessageId_헤더로_다형성을_보존한다()
        {
            // 생성된 인코더로 [netId][Message: DerivedPayloadMsg] 페이로드 생성
            var msg = new DerivedPayloadMsg { Value = 42, Bonus = 7 };
            byte[] payload = VerifyPlayer.__UniNetEncode_RpcDeliver(1234UL, msg);

            // 수신측과 동일한 순서로 개봉: netId → MessageId 디스패치
            var r = new MessageBufferReader(payload);
            Assert.AreEqual(1234UL, r.ReadUInt64(), "페이로드 앞은 netId");

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

                var entry = new NetworkServer.ServerObjectEntry(player);
                handler.InitSnapshot(entry);
                handler.InitClientSnapshot(player);
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "초기 상태는 변경 없음");

                // 새 인스턴스 교체(참조 비교 dirty) — 값 복사가 아니어도 전송된다
                player.StateMsg = new PayloadMsg { Value = 99 };
                byte[] delta = handler.CompareAndWriteDelta(entry).Owner;
                Assert.Greater(delta.Length, 0, "메시지 필드 델타 생성");

                // 클라 적용 — MessageId 경유 복원 + RepNotify(이전 참조)
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
