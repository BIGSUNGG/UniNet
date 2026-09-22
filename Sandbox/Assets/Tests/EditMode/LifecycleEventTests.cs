using System;
using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>연결 수명주기 이벤트 — 서버 ClientConnected/ClientDisconnected 발화·발화 순서 계약·구독자 격리 검증 (네트워킹 없음).</summary>
    public sealed class LifecycleEventTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [Test]
        public void 접속과_해제에서_이벤트가_순서대로_발화한다()
        {
            var server = new NetworkServer();
            long connected = 0;
            long disconnected = 0;
            server.ClientConnected += id => connected = id;
            server.ClientDisconnected += id => disconnected = id;

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            Assert.AreEqual(0, connected, "발화는 메인 큐 예약 — AttachConnection 즉시가 아니다");

            UniNetEnvironment.PumpMain();
            Assert.AreEqual(ch.UniNetConnId, connected, "캐치업 이후 접속 이벤트 발화");
            Assert.AreEqual(0, disconnected, "해제 전");

            server.DetachConnection(ch);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(ch.UniNetConnId, disconnected, "해제 이벤트 발화");
        }

        [Test]
        public void 해제_이벤트는_소유권_재배정_전에_발화한다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "life-owner");
            long ownerAtEvent = 0;

            var ch1 = new RecordingChannel();
            var ch2 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.AttachConnection(ch2);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(ch1.UniNetConnId, server.GetEntry(player.NetId).OwnerConnId, "라운드로빈 — 첫 연결이 소유");

            server.ClientDisconnected += _ => ownerAtEvent = server.GetEntry(player.NetId).OwnerConnId;
            server.DetachConnection(ch1);
            UniNetEnvironment.PumpMain();

            Assert.AreEqual(ch1.UniNetConnId, ownerAtEvent, "이벤트 시점엔 아직 해제 연결이 소유 — 게임이 소유자로 오브젝트를 식별할 수 있다");
            Assert.AreEqual(ch2.UniNetConnId, server.GetEntry(player.NetId).OwnerConnId, "발화 후 재배정");
        }

        [Test]
        public void 구독자_예외가_격리되고_펌프_잔여_작업은_보존된다()
        {
            var server = new NetworkServer();
            var ch = new RecordingChannel();
            bool counterRan = false;
            bool pumped = false;

            server.ClientConnected += _ => throw new InvalidOperationException("구독자 버그");
            server.ClientConnected += _ => counterRan = true;   // 첫 구독자가 던져도 호출 보장

            server.AttachConnection(ch);
            UniNetEnvironment.QueueOnMain(() => pumped = true);

            // 예외는 구독자 전원 호출 후 펌프 밖으로 전파된다 — 로그는 유니티 계층(드라이버 펌프 보호)이 담당
            Assert.Throws<InvalidOperationException>(() => UniNetEnvironment.PumpMain());
            Assert.IsTrue(counterRan, "예외 구독자 뒤 구독자도 호출된다");

            UniNetEnvironment.PumpMain();
            Assert.IsTrue(pumped, "펌프 잔여 작업은 유실되지 않고 다음 드레인에서 실행된다");
        }

        [Test]
        public void 해제_경로에서도_구독자_예외가_소유권_재배정을_막지_않는다()
        {
            var server = new NetworkServer();
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "life-owner-exc");
            bool seen = false;

            var ch1 = new RecordingChannel();
            var ch2 = new RecordingChannel();
            server.AttachConnection(ch1);
            server.AttachConnection(ch2);
            UniNetEnvironment.PumpMain();
            Assert.AreEqual(ch1.UniNetConnId, server.GetEntry(player.NetId).OwnerConnId, "라운드로빈 — 첫 연결이 소유");

            server.ClientDisconnected += _ => throw new InvalidOperationException("구독자 버그");
            server.ClientDisconnected += _ => seen = true;

            server.DetachConnection(ch1);
            // 재던짐은 유지(펌프 보호가 로그 담당) — finally로 재배정은 보장된다
            Assert.Throws<InvalidOperationException>(() => UniNetEnvironment.PumpMain());
            Assert.IsTrue(seen, "예외 구독자 뒤 구독자도 호출된다");
            Assert.AreEqual(ch2.UniNetConnId, server.GetEntry(player.NetId).OwnerConnId,
                "구독자 예외와 무관하게 재배정은 항상 실행된다 — 죽은 연결이 소유자로 남지 않는다");
        }
    }
}
