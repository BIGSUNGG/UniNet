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
        [UnityTest]
        public IEnumerator 호스트_왕복_ServerRpc_ClientRpc_Multicast_리플리케이션()
        {
            var go = new GameObject("roundtrip-player");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                // 호스트 시작 — 서버 + 클라가 실제 루프백 소켓으로 연결된다
                var hostTask = UniNetManager.HostAsync(7791);
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
                    VerifyPlayer.__UniNetEncode_RpcDeliver(player.NetId, msg), RpcDeliveryMode.ReliableOrdered);
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

        /// <summary>[netId][int amount] 페이로드 — 생성 인코더와 동일 형식.</summary>
        private static byte[] EncodePing(ulong netId, int amount)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
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
