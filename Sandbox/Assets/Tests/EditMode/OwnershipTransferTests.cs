using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// Explicit ownership transfer (ADR-0030) — SetOwner changes the server-side owner, propagates OwnerUpdate to
    /// every connection (existing wire), and integrates with the orphan-adoption reassignment policy.
    /// </summary>
    public sealed class OwnershipTransferTests
    {
        [SetUp]
        public void SetUp()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [TearDown]
        public void TearDown()
        {
            UniNetEnvironment.SetServer(null);
        }

        [Test]
        public void SetOwner는_소유권을_이전하고_전_연결에_전파한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "setowner");
            var chA = new RecordingChannel();
            var chB = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();   // welcome + ownership (single object — first connection owns it)
            chA.Clear();
            chB.Clear();

            bool ok = server.SetOwner(player.NetId, chB.UniNetConnId);

            Assert.IsTrue(ok, "알려진 netId — 이전 성립");
            Assert.AreEqual(chB.UniNetConnId, server.GetOwner(player.NetId), "서버 측 소유자 갱신");
            Assert.AreEqual(1, chA.Owners.Count, "연결 A — OwnerUpdate 수신");
            Assert.AreEqual((player.NetId, chB.UniNetConnId), chA.Owners[0], "갱신 내용 (netId, 새 소유자)");
            Assert.AreEqual(1, chB.Owners.Count, "연결 B — OwnerUpdate 수신 (본인이 새 소유자)");
        }

        [Test]
        public void SetOwner는_미지_오브젝트에_false를_반환한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);

            Assert.IsFalse(server.SetOwner(999_999UL, 1), "미지 netId — 거짓");
        }

        [Test]
        public void SetOwner_0은_방치하고_다음_재배정이_입양한다()
        {
            var server = new NetworkServer();
            UniNetEnvironment.SetServer(server);
            var player = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "setowner-release");
            var chA = new RecordingChannel();
            server.AttachConnection(chA);
            server.OnClientVersionDeclared(chA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "초기 — 소유자 배정됨");

            Assert.IsTrue(server.SetOwner(player.NetId, 0), "방치(해제) 성립");
            Assert.AreEqual(0, server.GetOwner(player.NetId), "소유자 미배정 상태");

            // A new connection triggers the reassignment pass — the orphan is adopted round-robin
            var chB = new RecordingChannel();
            server.AttachConnection(chB);
            server.OnClientVersionDeclared(chB.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — real clients declare automatically; fake channels must too
            UniNetEnvironment.PumpMain();
            Assert.AreNotEqual(0, server.GetOwner(player.NetId), "재배정 통과 후 입양 — 다시 소유자 배정");
        }
    }
}
