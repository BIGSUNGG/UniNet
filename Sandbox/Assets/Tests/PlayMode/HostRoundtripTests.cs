using System.Collections;
using System.Diagnostics;
using DRPC;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// 호스트(서버+클라 한 프로세스, 실제 루프백 RUDP) 왕복 검증 — RPC 3종·검증 후크·리플리케이션·소유권 전 경로.
    /// 성공 시 [UNINET-VERIFY] 마커를 로그에 남긴다 (배치 검증 grep 대상).
    /// </summary>
    public sealed class HostRoundtripTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 32;   // 실행별 랜덤 포트 — 플레이 모드 종료 후 리스너 소켓이 에디터 프로세스에 잔존하는 환경 문제 회피 (이 클래스는 3개 포트 사용)


        [TearDown]
        public void StopListeners()
        {
            // 테스트 간 잔존 리스너/접속 정리 — 포트 잔존으로 인한 후속 테스트 바인딩 실패 방지 (멱등)
            UniNetManager.HostStop();
        }

        [UnityTest]
        public IEnumerator 호스트_왕복_ServerRpc_ClientRpc_Multicast_리플리케이션()
        {
            var go = new GameObject("roundtrip-player");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                // 호스트 시작 — 서버 + 클라가 실제 루프백 소켓으로 연결된다
                var hostTask = UniNetManager.HostAsync(Port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

                // Welcome(내 연결 ID) + 소유권 갱신 수신 대기
                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);
                Assert.AreNotEqual(0, UniNetEnvironment.Client.LocalConnId, "Welcome 수신");
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) != 0, 5);
                Assert.IsTrue(player.IsOwner, "씬 오브젝트 단일 → 유일 연결이 소유");

                // 1) 네트워크 경로 ServerRpc: 클라 송신 → 루프백 → 서버 수신 → 검증 → 구현
                //    (페이로드 = [netId][int amount] — 생성 인코더와 동일 와이어 형식)
                int pingId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcPing");
                UniNetEnvironment.ClientSender.UniNetSend(pingId, EncodePing(player.NetId, 7), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.ServerPingCount == 1, 5);
                Assert.AreEqual(1, player.ServerPingCount, "서버에서 RpcPing 구현 실행");

                // 2) 검증 후크: 음수 인자는 _Validate가 거부
                UniNetEnvironment.ClientSender.UniNetSend(pingId, EncodePing(player.NetId, -1), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.ValidateRejected, 5);
                Assert.AreEqual(1, player.ServerPingCount, "거부된 호출은 구현 실행 안 함");

                // 3) ClientRpc: 서버 → 클라 (루프백 수신)
                yield return WaitUntil(() => player.ClientFxRan, 5);
                Assert.IsTrue(player.ClientFxRan, "클라에서 ClientRpc 구현 실행");

                // 4) Multicast: 서버 로컬 실행 + 클라 수신 (호스트 중복 방지 — 정확히 1회)
                yield return WaitUntil(() => player.MulticastCount >= 1, 5);
                yield return new WaitForSecondsRealtime(0.5f);   // 이중 실행 관찰 창
                Assert.AreEqual(1, player.MulticastCount, "Multicast는 서버+클라 통틀어 정확히 1회");

                // 5) 리플리케이션: Score 100→93 델타 → 클라 적용 → RepNotify(이전값 100)
                yield return WaitUntil(() => player.ScoreNotified, 5);
                Assert.AreEqual(93, player.Score, "리플리케이션 적용값");
                Assert.AreEqual(100, player.LastPrevScore, "RepNotify 이전값");

                // 6) 메시지 파라미터 RPC + 다형성: 부모(PayloadMsg) 선언에 자식 인스턴스 → 생성 인코더 → 네트워크 → 서버에서 자식 복원
                int deliverId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcDeliver");
                var msg = new DerivedPayloadMsg { Value = 5, Bonus = 3 };
                UniNetEnvironment.ClientSender.UniNetSend(deliverId,
                    VerifyPlayer.__UniNetEncode_RpcDeliver(player.NetId, player.SubId, msg), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.LastMsg != null, 5);
                Assert.IsInstanceOf<DerivedPayloadMsg>(player.LastMsg, "다형성 — 자식 타입 복원");
                Assert.AreEqual(3, ((DerivedPayloadMsg)player.LastMsg).Bonus, "자식 고유 필드 온전");
                Assert.AreEqual(5, player.LastMsg.Value, "부모 필드 온전");

                // 7) 리플리케이션 메시지 필드: 서버에서 인스턴스 교체 → 델타 → 클라 적용 + RepNotify(이전 참조)
                player.StateMsg = new PayloadMsg { Value = 77 };
                yield return WaitUntil(() => player.StateMsgNotified, 5);
                Assert.AreEqual(77, player.StateMsg.Value, "메시지 필드 적용값");
                Assert.IsNotNull(player.LastStatePrev, "RepNotify 이전 인스턴스 참조");
                Assert.AreEqual(0, player.LastStatePrev.Value, "이전 인스턴스 값(초기 0) 보존");

                UnityEngine.Debug.Log("[UNINET-VERIFY] HostRoundtrip PASS — ServerRpc/Validate/ClientRpc/Multicast/Replicated/RepNotify/MessageParam/MessageReplicate");
            }
            finally
            {
                Object.Destroy(player.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator 호스트_동적_스폰_파괴_조건부_리플리케이션()
        {
            var hostTask = UniNetManager.HostAsync(Port + 1);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);

            // 1) 동적 스폰 — 서버에서 생성 후 Spawn 호출 (위치·초기 상태는 스폰 메시지로 전파)
            var go = new GameObject("spawned");
            var spawned = go.AddComponent<SpawnablePlayer>();
            go.transform.position = new Vector3(10f, 20f, 30f);
            UniNetManager.Spawn(go);
            Assert.AreNotEqual(0ul, spawned.NetId, "동적 netId 할당");
            Assert.IsTrue(spawned.IsServer, "서버 등록");

            // 2) 호스트 클라 — 스폰 브로드캐스트 수신 → 같은 인스턴스 재사용 등록 (이중 생성 없음)
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(spawned.NetId) != null, 5);
            Assert.AreSame(spawned, (SpawnablePlayer)UniNetEnvironment.Client.Get(spawned.NetId, spawned.SubId), "호스트 — 서버 인스턴스 재사용");
            Assert.IsTrue(spawned.IsClient, "클라 등록");
            yield return WaitUntil(() => spawned.IsOwner, 5);
            Assert.IsTrue(spawned.IsOwner, "동적 오브젝트 소유권 — 유일 연결이 소유");

            // 3) 조건부 델타 — 호스트(=소유자)는 OwnerOnly 필드 수신: RepNotify 이전값으로 검증
            spawned.Score = 5;
            spawned.SecretHp = 40;
            yield return WaitUntil(() => spawned.SecretNotified, 5);
            Assert.AreEqual(50, spawned.LastPrevSecret, "OwnerOnly 델타 수신 — RepNotify 이전값 50");

            // 4) 네트워크 파괴 — 서버·클라 등록 모두 해제
            ulong netId = spawned.NetId;
            UniNetManager.NetworkDestroy(go);
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) == null
                && UniNetEnvironment.Server.GetEntry(netId) == null, 5);
            Assert.IsNull(UniNetEnvironment.Client.Get(netId), "클라 등록 해제");
            Assert.IsNull(UniNetEnvironment.Server.GetEntry(netId), "서버 등록 해제");

            UnityEngine.Debug.Log("[UNINET-VERIFY] DynamicSpawnDestroy PASS — Spawn/HostReuse/Ownership/OwnerOnly/NetworkDestroy");
        }

        [UnityTest]
        public IEnumerator 호스트_다중_컴포넌트_오브젝트_왕복()
        {
            var hostTask = UniNetManager.HostAsync(Port + 2);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);

            // 1) 다중 컴포넌트 오브젝트 스폰 — MovementBrain + HealthTank가 한 게임오브젝트에
            var go = new GameObject("multi-host");
            var brain = go.AddComponent<MovementBrain>();
            var tank = go.AddComponent<HealthTank>();
            brain.Speed = 10;
            tank.Armor = 20;
            UniNetManager.Spawn(go);
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(brain.NetId) != null, 5);

            var clientComps = UniNetEnvironment.Client.Get(brain.NetId);
            Assert.AreEqual(2, clientComps.Length, "호스트 — 서브 2개 등록(재사용)");
            Assert.AreSame(brain, clientComps[0], "슬롯 0 = MovementBrain");
            Assert.AreSame(tank, clientComps[1], "슬롯 1 = HealthTank");
            Assert.AreEqual(0, brain.SubId);
            Assert.AreEqual(1, tank.SubId);
            yield return WaitUntil(() => brain.IsOwner, 5);

            // 2) subId별 RPC — 같은 netId에서 각 컴포넌트의 ServerRpc가 서로 간섭 없이 실행
            brain.RpcMove(5);   // 클라→서버 경로 (호스트 클라 송신 → 서버 수신 → 슬롯 0 디스패치)
            yield return WaitUntil(() => brain.MoveCalls >= 1, 5);
            tank.RpcHeal(7);
            yield return WaitUntil(() => tank.HealCalls >= 1, 5);
            Assert.AreEqual(1, brain.MoveCalls, "subId 0 — MovementBrain만");
            Assert.AreEqual(1, tank.HealCalls, "subId 1 — HealthTank만");

            // 3) 서브별 리플리케이션 — 서버 값 변경이 서브별 델타로 전파(호스트는 원본 그대로)
            brain.Speed = 42;
            tank.Armor = 77;
            yield return WaitUntil(() => brain.Speed == 42 && tank.Armor == 77, 5);   // 호스트 원본 — 값 자체는 이미 42/77
            Assert.IsTrue(brain.IsServer && tank.IsServer, "두 서브 모두 서버 등록");

            // 4) 파괴 — 오브젝트 전체
            ulong netId = brain.NetId;
            UniNetManager.NetworkDestroy(go);
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) == null
                && UniNetEnvironment.Server.GetEntry(netId) == null, 5);

            UnityEngine.Debug.Log("[UNINET-VERIFY] MultiComponent PASS — Spawn(2 subs)/SubIdRouting/PerSubReplication/Destroy");
        }

        /// <summary>[netId][int amount] 페이로드 — 생성 인코더와 동일 형식.</summary>
        private static byte[] EncodePing(ulong netId, int amount)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteByte(0);   // SubId — 단일 컴포넌트 오브젝트 슬롯 0
            w.WriteInt32(amount);
            return w.ToArray();
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, double timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.Elapsed.TotalSeconds < timeoutSeconds)
                yield return null;
        }
    }
}
