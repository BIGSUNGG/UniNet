using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Unit tests for custom field serializers (see ADR-0020) — proves quantized round-trip,
    /// delta suppression via the optional Equals contract, unlocked non-primitive field types
    /// (Vector3), WriteFull spawn payloads, and RepNotify previous-value delivery.
    /// </summary>
    public sealed class NetSerializeTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
            => global::UniNet.Generated.__UniNetRegistration.Register();   // explicit EditMode registration (idempotent)

        private static (NetworkServer.SubObjectEntry entry, QuantizedTank tank) CreateRegistered()
        {
            var go = new GameObject("quantized-tank");
            var tank = go.AddComponent<QuantizedTank>();
            var handler = UniNetTypeRegistry.Find(tank.GetType());
            Assert.IsNotNull(handler, "커스텀 직렬화기 필드만 있는 타입도 핸들러 생성");
            var entry = new NetworkServer.SubObjectEntry(tank);
            handler.InitSnapshot(entry);
            handler.InitClientSnapshot(tank);
            return (entry, tank);
        }

        [Test]
        public void 양자화_직렬화기는_왕복시_정밀도_단위로_복원한다()
        {
            var (_, tank) = CreateRegistered();
            try
            {
                tank.Position = new Vector3(1.2345f, -2.7182f, 30.0f);
                var w = MessageBufferWriter.Create();
                PositionQuantized.Write(ref w, tank.Position);
                var r = new MessageBufferReader(w.ToArray());
                var restored = PositionQuantized.Read(ref r);

                Assert.AreEqual(restored.x, PositionQuantized.QuantizeAxis(tank.Position.x) / PositionQuantized.Scale, 0.0001f, "x 축 양자화 단위 복원");
                Assert.AreEqual(restored.y, PositionQuantized.QuantizeAxis(tank.Position.y) / PositionQuantized.Scale, 0.0001f, "y 축 양자화 단위 복원");
                Assert.AreEqual(restored.z, PositionQuantized.QuantizeAxis(tank.Position.z) / PositionQuantized.Scale, 0.0001f, "z 축 양자화 단위 복원");
                Assert.AreEqual(6, w.ToArray().Length, "12바이트 float×3 → 6바이트 int16×3 대역폭 절감");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }

        [Test]
        public void 커스텀_직렬화_델타는_양자화_값으로_클라에_적용된다()
        {
            var (entry, tank) = CreateRegistered();
            try
            {
                var handler = UniNetTypeRegistry.Find(tank.GetType());
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "초기 상태는 변경 없음");

                tank.Position = new Vector3(5f, 0f, 0f);
                tank.Packed = 0b1010;
                byte[] delta = handler.CompareAndWriteDelta(entry).Owner;
                Assert.IsNotNull(delta, "양 필드 변경 → 델타 생성");

                int notifyBefore = tank.NotifyCalls;
                var reader = new MessageBufferReader(delta);
                handler.ApplyDelta(tank, ref reader);

                Assert.AreEqual(new Vector3(5f, 0f, 0f), tank.Position, "양자화 정확 값은 그대로 복원");
                Assert.AreEqual(0b1010, tank.Packed, "직렬화기 지정 기본형 필드 복원");
                Assert.AreEqual(notifyBefore + 1, tank.NotifyCalls, "RepNotify 1회 발화");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }

        [Test]
        public void 선택_Equals는_양자화_동일값의_재전송을_건너뛴다()
        {
            var (entry, tank) = CreateRegistered();
            try
            {
                var handler = UniNetTypeRegistry.Find(tank.GetType());
                tank.Position = new Vector3(1.001f, 0f, 0f);
                Assert.IsNotNull(handler.CompareAndWriteDelta(entry).Owner, "실제 변경 → 1회 전송");

                tank.Position = new Vector3(1.009f, 0f, 0f);   // 같은 양자화 버킷(100) — 값은 다름
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "양자화 동일 → 재전송 생략");

                tank.Position = new Vector3(1.02f, 0f, 0f);    // 다음 버킷(102)
                Assert.IsNotNull(handler.CompareAndWriteDelta(entry).Owner, "버킷 넘어가면 전송");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }

        [Test]
        public void 두_커스텀_Equals_필드는_독립적으로_억제된다()
        {
            var (entry, tank) = CreateRegistered();
            try
            {
                var handler = UniNetTypeRegistry.Find(tank.GetType());

                tank.Position = new Vector3(1.0f, 0f, 0f);   // 0.01 스케일 버킷
                tank.AimVector = new Vector3(1.0f, 0f, 0f);  // 0.1 스케일 버킷 — 서로 다른 직렬화기
                Assert.IsNotNull(handler.CompareAndWriteDelta(entry).Owner, "양 필드 실제 변경 → 1회 전송");

                tank.Position = new Vector3(1.009f, 0f, 0f);   // Position 동일 버킷(100)
                tank.AimVector = new Vector3(1.09f, 0f, 0f);    // AimVector 동일 버킷(10)
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "양 필드 모두 동일 버킷 → 미전송");

                tank.AimVector = new Vector3(1.2f, 0f, 0f);     // AimVector만 버킷 통과
                byte[] delta = handler.CompareAndWriteDelta(entry).Owner;
                Assert.IsNotNull(delta, "AimVector 단독 변경 → 전송");
                var r = new MessageBufferReader(delta);
                uint mask = r.ReadUInt32();
                Assert.AreEqual(2u, mask, "마스크는 AimVector 비트만 (Position은 억제)");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }

        [Test]
        public void operator_없는_구조체도_커스텀_Equals_경로에서_동작한다()
        {
            var (entry, tank) = CreateRegistered();
            try
            {
                var handler = UniNetTypeRegistry.Find(tank.GetType());

                tank.PolarAim = new PolarAim { Radius = 1.5f, Angle = 0.25f };
                Assert.IsNotNull(handler.CompareAndWriteDelta(entry).Owner, "구조체 변경 → 전송 (CS0019 회귀 — 생성 코드 컴파일 완료 자체가 증명)");

                tank.PolarAim = new PolarAim { Radius = 1.509f, Angle = 0.25f };   // 같은 0.1 버킷(15, 2)
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "동일 버킷 → 억제");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }

        [Test]
        public void Equals_계약이_없는_직렬화기는_기본_값_비교를_따른다()
        {
            var (entry, tank) = CreateRegistered();
            try
            {
                var handler = UniNetTypeRegistry.Find(tank.GetType());
                tank.Packed = 7;
                Assert.IsNotNull(handler.CompareAndWriteDelta(entry).Owner, "값 변경 → 전송");

                tank.Packed = 7;   // 동일 값 재할당
                Assert.IsNull(handler.CompareAndWriteDelta(entry).Owner, "값 동일 → 미전송 (object.Equals 경로)");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }

        [Test]
        public void 스폰_전체_상태도_커스텀_직렬화_경로로_부호화된다()
        {
            var (_, tank) = CreateRegistered();
            try
            {
                var handler = UniNetTypeRegistry.Find(tank.GetType());
                tank.Position = new Vector3(-3.5f, 2.25f, 9.99f);
                tank.Packed = uint.MaxValue;

                byte[] full = handler.WriteFull(tank, isOwner: true);
                Assert.IsNotNull(full, "전체 상태 페이로드 생성");

                tank.Position = default;   // 클라는 빈 상태에서 시작했다고 가정
                tank.Packed = 0;
                var reader = new MessageBufferReader(full);
                handler.ApplyDelta(tank, ref reader);

                Assert.AreEqual(new Vector3(-3.5f, 2.25f, 9.99f), tank.Position, "WriteFull 경로 양자화 복원");
                Assert.AreEqual(uint.MaxValue, tank.Packed, "WriteFull 경로 직렬화기 기본형 복원");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }

        [Test]
        public void RepNotify는_양자화_전_원본_이전값을_전달한다()
        {
            var (entry, tank) = CreateRegistered();
            try
            {
                var handler = UniNetTypeRegistry.Find(tank.GetType());

                // first change — applied so the client-side seen snapshot tracks (1,0,0)
                tank.Position = new Vector3(1.0f, 0f, 0f);
                var reader1 = new MessageBufferReader(handler.CompareAndWriteDelta(entry).Owner);
                handler.ApplyDelta(tank, ref reader1);

                tank.Position = new Vector3(2.0f, 0f, 0f);
                byte[] delta = handler.CompareAndWriteDelta(entry).Owner;
                var reader2 = new MessageBufferReader(delta);
                handler.ApplyDelta(tank, ref reader2);

                Assert.AreEqual(new Vector3(1.0f, 0f, 0f), tank.LastPrev, "prev는 원본 값 도메인 (양자화 전)");
            }
            finally { Object.DestroyImmediate(tank.gameObject); }
        }
    }
}
