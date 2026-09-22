using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// 연결 수명주기 이벤트 실전 왕복 검증 — 실제 루프백 RUDP 세션에서 서버 이벤트·클라 해제 콜백·
    /// 접속 상태 조회를 단언하고, Arena 방식(퇴장 연결 소유 아바타 파괴)의 전파를 확인한다.
    /// </summary>
    public sealed class LifecycleEventPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 48;   // 실행별 랜덤 포트 (이 클래스는 2개 포트 사용)

        private static bool _clientBye;

        [SetUp]
        public void AllowStopNoise()
        {
            // 정지 과정의 fire-and-forget 송신 노이즈는 기능 단언과 무관 (LifecycleStopTests와 동일 처리)
            LogAssert.ignoreFailingMessages = true;
            _clientBye = false;
        }

        [TearDown]
        public void StopListeners()
        {
            UniNetManager.ClientDisconnected -= OnClientBye;   // static 이벤트 — 테스트 간 구독 잔존 방지
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // 멱등 잔존 정리
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        private static void OnClientBye() => _clientBye = true;

        [UnityTest]
        public IEnumerator 서버와_클라_수명주기_이벤트가_실제_세션에서_발화한다()
        {
            var serverTask = UniNetManager.ServerAsync(Port);
            while (!serverTask.IsCompleted) yield return null;
            Assert.IsFalse(serverTask.IsFaulted, serverTask.Exception?.ToString());

            long connected = 0;
            long disconnected = 0;
            UniNetEnvironment.Server.ClientConnected += id => connected = id;
            UniNetEnvironment.Server.ClientDisconnected += id => disconnected = id;
            UniNetManager.ClientDisconnected += OnClientBye;
            Assert.IsFalse(UniNetManager.IsClientConnected, "접속 전 상태");

            var clientTask = UniNetManager.ClientAsync("127.0.0.1", Port);
            while (!clientTask.IsCompleted) yield return null;
            Assert.IsFalse(clientTask.IsFaulted, "접속 실패: " + clientTask.Exception);

            yield return WaitUntil(() => connected != 0, 5, "서버 ClientConnected 발화");
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "클라 Welcome 수신");
            Assert.AreEqual(UniNetEnvironment.Client.LocalConnId, connected, "환영 ID와 이벤트 ID 일치");
            Assert.IsTrue(UniNetManager.IsClientConnected, "접속 상태 조회");

            // 서버가 먼저 종료 — 클라 세션 종료 관측 경로
            var stop = UniNetManager.ServerStopAsync();
            while (!stop.IsCompleted) yield return null;

            yield return WaitUntil(() => disconnected != 0, 5, "서버 ClientDisconnected 발화");
            yield return WaitUntil(() => _clientBye, 5, "클라 해제 콜백 발화 (원격 종료)");
            Assert.IsFalse(UniNetManager.IsClientConnected, "해제 후 상태");
        }

        [UnityTest]
        public IEnumerator 퇴장_연결의_소유_아바타가_파괴되고_서버_등록이_해제된다()
        {
            var hostTask = UniNetManager.HostAsync(Port + 2);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            // Arena 방식 — 퇴장 연결이 소유한 동적 오브젝트를 이벤트 핸들러에서 파괴한다 (소유권 재배정 전 발화 계약 활용)
            var server = UniNetEnvironment.Server;
            server.ClientDisconnected += connId =>
            {
                foreach (var player in Object.FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    var entry = server.GetEntry(player.NetId);
                    if (entry == null || !entry.IsDynamic || entry.OwnerConnId != connId) continue;
                    UniNetManager.NetworkDestroy(player.gameObject);
                }
            };

            // 호스트 클라가 소유하는 아바타 동적 스폰 (ArenaRoundtripTests와 동일 패턴 — private InitialOnly는 configure 콜백으로 기준선에)
            var template = new GameObject("LifePlayer");
            template.transform.position = new Vector3(0f, 0.5f, 0f);
            template.AddComponent<ArenaPlayer>();
            var go = UniNetManager.NetworkInstantiate(template, clone => clone.GetComponent<ArenaPlayer>().InitServerState("Gone", 7));
            var alpha = go.GetComponent<ArenaPlayer>();
            alpha.LocalInputEnabled = false;
            UnityEngine.Object.Destroy(template);
            ulong netId = alpha.NetId;

            yield return WaitUntil(() => alpha.IsOwner, 5, "호스트 클라 소유권");
            Assert.IsTrue(UniNetManager.IsClientConnected, "호스트 클라 접속 중");

            UniNetManager.ClientStop();
            yield return WaitUntil(() =>
            {
                var entry = server.GetEntry(netId);
                return entry == null;
            }, 5, "퇴장 이벤트 → 아바타 파괴 → 서버 등록 해제");

            yield return null;   // Object.Destroy 프레임 말 반영 대기
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
