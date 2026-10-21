using System.Collections;
using NUnit.Framework;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// End-to-end round-trip verification of connection lifecycle events — asserts server events, the client
    /// disconnect callback, and connection-state queries over a real loopback RUDP session, and confirms the
    /// Arena pattern (destroying the avatar owned by a disconnecting connection) propagates.
    /// </summary>
    public sealed class LifecycleEventPlayTests
    {
        private static bool _clientBye;
        private static UniNetDisconnectReason _clientByeReason;

        [SetUp]
        public void AllowStopNoise()
        {
            // Fire-and-forget send noise during shutdown is unrelated to the functional assertions (same handling as LifecycleStopTests)
            LogAssert.ignoreFailingMessages = true;
            _clientBye = false;
            _clientByeReason = UniNetDisconnectReason.None;
        }

        [TearDown]
        public void StopListeners()
        {
            UniNetManager.ClientDisconnected -= OnClientBye;   // static event — prevents subscriptions leaking across tests
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // idempotent cleanup of leftovers
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        private static void OnClientBye(UniNetDisconnectReason reason)
        {
            _clientBye = true;
            _clientByeReason = reason;
        }

        [UnityTest]
        public IEnumerator 서버와_클라_수명주기_이벤트가_실제_세션에서_발화한다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var serverTask = UniNetManager.ServerAsync(port);
            while (!serverTask.IsCompleted) yield return null;
            Assert.IsFalse(serverTask.IsFaulted, serverTask.Exception?.ToString());

            long connected = 0;
            long disconnected = 0;
            UniNetEnvironment.Server.ClientConnected += id => connected = id;
            UniNetEnvironment.Server.ClientDisconnected += id => disconnected = id;
            UniNetManager.ClientDisconnected += OnClientBye;
            Assert.IsFalse(UniNetManager.IsClientConnected, "접속 전 상태");

            var clientTask = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!clientTask.IsCompleted) yield return null;
            Assert.IsFalse(clientTask.IsFaulted, "접속 실패: " + clientTask.Exception);

            yield return WaitUntil(() => connected != 0, 5, "서버 ClientConnected 발화");
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "클라 Welcome 수신");
            Assert.AreEqual(UniNetEnvironment.Client.LocalConnId, connected, "환영 ID와 이벤트 ID 일치");
            Assert.IsTrue(UniNetManager.IsClientConnected, "접속 상태 조회");

            // The server stops first — exercises the client-side session-end observation path
            var stop = UniNetManager.ServerStopAsync();
            while (!stop.IsCompleted) yield return null;

            yield return WaitUntil(() => disconnected != 0, 5, "서버 ClientDisconnected 발화");
            yield return WaitUntil(() => _clientBye, 5, "클라 해제 콜백 발화 (원격 종료)");
            Assert.AreEqual(UniNetDisconnectReason.ServerShutdown, _clientByeReason, "서버 정지 사유 전달 (ADR-0027)");
            Assert.IsFalse(UniNetManager.IsClientConnected, "해제 후 상태");
        }

        [UnityTest]
        public IEnumerator 퇴장_연결의_소유_아바타가_파괴되고_서버_등록이_해제된다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var hostTask = UniNetManager.HostAsync(port + 2);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            // Arena pattern — the event handler destroys dynamic objects owned by the disconnecting connection (relies on the fires-before-ownership-reassignment contract)
            var server = UniNetEnvironment.Server;
            // Arena pattern (ADR-0031) — the framework departure helper destroys the leaver's dynamic objects precisely
            // (fires-before-ownership-reassignment contract); the mapping must still be readable INSIDE the handler
            ulong avatarNetId = 0;   // assigned after the spawn below — captured by the handler
            server.ClientDisconnected += leaving =>
            {
                Assert.AreEqual(avatarNetId, server.GetPlayerObject(leaving), "매핑은 이벤트 발화 시점에도 읽기 가능 (ADR-0031 순서 계약)");
                int destroyed = UniNetManager.DestroyObjectsOwnedBy(leaving);
                Assert.GreaterOrEqual(destroyed, 1, "퇴장자 소유 동적 오브젝트 파괴(로컬 GO 포함)");
            };

            // Dynamically spawn an avatar owned by the host client (same pattern as ArenaRoundtripTests — private InitialOnly values ride the baseline via the configure callback)
            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.ArenaPlayerPrefab), new Vector3(0f, 0.5f, 0f), Quaternion.identity,
                clone => clone.GetComponent<ArenaPlayer>().InitServerState("Gone", 7));
            var alpha = go.GetComponent<ArenaPlayer>();
            alpha.LocalInputEnabled = false;
            ulong netId = alpha.NetId;

            yield return WaitUntil(() => alpha.IsOwner, 5, "호스트 클라 소유권");
            avatarNetId = netId;
            server.SetPlayerObject(UniNetEnvironment.Client.LocalConnId, netId);   // ADR-0031 — declare the avatar mapping (the handler above reads it)
            Assert.IsTrue(UniNetManager.IsClientConnected, "호스트 클라 접속 중");

            UniNetManager.ClientStop();
            yield return WaitUntil(() =>
            {
                var entry = server.GetEntry(netId);
                return entry == null;
            }, 5, "퇴장 이벤트 → 아바타 파괴 → 서버 등록 해제");

            yield return null;   // let Object.Destroy take effect at end of frame
            Assert.IsTrue(alpha == null, "아바타 게임오브젝트 파괴");
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, float timeout, string label)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"대기 시간 초과: {label}");
                yield return null;
            }
        }
    }
}
