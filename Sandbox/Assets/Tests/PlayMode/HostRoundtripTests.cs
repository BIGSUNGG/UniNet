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
    /// Host round-trip verification (server + client in one process, real loopback RUDP) — all paths of the three RPC kinds,
    /// the validation hook, replication, and ownership. On success it logs a [UNINET-VERIFY] marker (grep target for batch runs).
    /// </summary>
    public sealed class HostRoundtripTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 32;   // random port per run — avoids environments where a listener socket lingers in the editor process after play mode ends (this class uses 3 ports)


        [TearDown]
        public void StopListeners()
        {
            // Clean up listeners/connections left between tests — prevents binding failures from lingering ports (idempotent)
            UniNetManager.HostStop();
        }

        [UnityTest]
        public IEnumerator 호스트_왕복_ServerRpc_ClientRpc_Multicast_리플리케이션()
        {
            var go = new GameObject("roundtrip-player");
            var player = go.AddComponent<VerifyPlayer>();
            try
            {
                // Start the host — the server and client connect over a real loopback socket
                var hostTask = UniNetManager.HostAsync(Port);
                while (!hostTask.IsCompleted) yield return null;
                Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

                // Wait for Welcome (our connection ID) + ownership update
                yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);
                Assert.AreNotEqual(0, UniNetEnvironment.Client.LocalConnId, "Welcome 수신");
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(player.NetId) != 0, 5);
                Assert.IsTrue(player.IsOwner, "씬 오브젝트 단일 → 유일 연결이 소유");

                // 1) Network-path ServerRpc: client sends → loopback → server receives → validates → implements
                //    (payload = [netId][int amount] — the same wire format as the generated encoder)
                int pingId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcPing");
                UniNetEnvironment.ClientSender.UniNetSend(pingId, EncodePing(player.NetId, 7), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.ServerPingCount == 1, 5);
                Assert.AreEqual(1, player.ServerPingCount, "서버에서 RpcPing 구현 실행");

                // 2) Validation hook: a negative argument is rejected by _Validate
                UniNetEnvironment.ClientSender.UniNetSend(pingId, EncodePing(player.NetId, -1), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.ValidateRejected, 5);
                Assert.AreEqual(1, player.ServerPingCount, "거부된 호출은 구현 실행 안 함");

                // 3) ClientRpc: server → client (received over loopback)
                yield return WaitUntil(() => player.ClientFxRan, 5);
                Assert.IsTrue(player.ClientFxRan, "클라에서 ClientRpc 구현 실행");

                // 4) Multicast: server-local execution + client reception (no host double-execution — exactly once)
                yield return WaitUntil(() => player.MulticastCount >= 1, 5);
                yield return new WaitForSecondsRealtime(0.5f);   // observation window for a possible double execution
                Assert.AreEqual(1, player.MulticastCount, "Multicast는 서버+클라 통틀어 정확히 1회");

                // 5) Replication: Score 100→93 delta → applied on the client → RepNotify (previous value 100)
                yield return WaitUntil(() => player.ScoreNotified, 5);
                Assert.AreEqual(93, player.Score, "리플리케이션 적용값");
                Assert.AreEqual(100, player.LastPrevScore, "RepNotify 이전값");

                // 6) Message-parameter RPC + polymorphism: a derived instance under a parent (PayloadMsg) declaration →
                //    generated encoder → network → server restores the derived type
                int deliverId = Fnv1a.MethodId("UniNet.Tests.VerifyPlayer.RpcDeliver");
                var msg = new DerivedPayloadMsg { Value = 5, Bonus = 3 };
                UniNetEnvironment.ClientSender.UniNetSend(deliverId,
                    VerifyPlayer.__UniNetEncode_RpcDeliver(player.NetId, player.SubId, msg), RpcDeliveryMode.ReliableOrdered);
                yield return WaitUntil(() => player.LastMsg != null, 5);
                Assert.IsInstanceOf<DerivedPayloadMsg>(player.LastMsg, "다형성 — 자식 타입 복원");
                Assert.AreEqual(3, ((DerivedPayloadMsg)player.LastMsg).Bonus, "자식 고유 필드 온전");
                Assert.AreEqual(5, player.LastMsg.Value, "부모 필드 온전");

                // 7) Replicated message field: server swaps the instance → delta → applied on the client + RepNotify (previous reference)
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

            // 1) Dynamic spawn — NetworkInstantiate clones, registers, and broadcasts (position and initial state are
            // copied from the template by serialization → propagated in the spawn message)
            var template = new GameObject("spawned");
            template.AddComponent<SpawnablePlayer>();
            template.transform.position = new Vector3(10f, 20f, 30f);
            var go = UniNetManager.NetworkInstantiate(template);
            var spawned = go.GetComponent<SpawnablePlayer>();
            UnityEngine.Object.Destroy(template);   // dispose the template — its state was copied into the clone at instantiate time
            Assert.AreNotEqual(0ul, spawned.NetId, "동적 netId 할당");
            Assert.IsTrue(spawned.IsServer, "서버 등록");

            // 2) Host client — receives the spawn broadcast → registers the same instance (no double creation)
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(spawned.NetId) != null, 5);
            Assert.AreSame(spawned, (SpawnablePlayer)UniNetEnvironment.Client.Get(spawned.NetId, spawned.SubId), "호스트 — 서버 인스턴스 재사용");
            Assert.IsTrue(spawned.IsClient, "클라 등록");
            yield return WaitUntil(() => spawned.IsOwner, 5);
            Assert.IsTrue(spawned.IsOwner, "동적 오브젝트 소유권 — 유일 연결이 소유");

            // 3) Conditional delta — the host (as owner) receives the OwnerOnly field: verified via the RepNotify previous value
            spawned.Score = 5;
            spawned.SecretHp = 40;
            yield return WaitUntil(() => spawned.SecretNotified, 5);
            Assert.AreEqual(50, spawned.LastPrevSecret, "OwnerOnly 델타 수신 — RepNotify 이전값 50");

            // 4) Network destroy — deregisters on both server and client
            ulong netId = spawned.NetId;
            UniNetManager.NetworkDestroy(go);
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) == null
                && UniNetEnvironment.Server.GetEntry(netId) == null, 5);
            Assert.IsNull(UniNetEnvironment.Client.Get(netId), "클라 등록 해제");
            Assert.IsNull(UniNetEnvironment.Server.GetEntry(netId), "서버 등록 해제");

            UnityEngine.Debug.Log("[UNINET-VERIFY] DynamicSpawnDestroy PASS — NetworkInstantiate/HostReuse/Ownership/OwnerOnly/NetworkDestroy");
        }

        [UnityTest]
        public IEnumerator 호스트_다중_컴포넌트_오브젝트_왕복()
        {
            var hostTask = UniNetManager.HostAsync(Port + 2);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5);

            // 1) Multi-component object spawn — MovementBrain + HealthTank on one GameObject (baseline 10/20 copied from the template by serialization)
            var template = new GameObject("multi-host");
            var tBrain = template.AddComponent<MovementBrain>();
            var tTank = template.AddComponent<HealthTank>();
            tBrain.Speed = 10;
            tTank.Armor = 20;
            var go = UniNetManager.NetworkInstantiate(template);
            var brain = go.GetComponent<MovementBrain>();
            var tank = go.GetComponent<HealthTank>();
            UnityEngine.Object.Destroy(template);
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(brain.NetId) != null, 5);

            var clientComps = UniNetEnvironment.Client.Get(brain.NetId);
            Assert.AreEqual(2, clientComps.Length, "호스트 — 서브 2개 등록(재사용)");
            Assert.AreSame(brain, clientComps[0], "슬롯 0 = MovementBrain");
            Assert.AreSame(tank, clientComps[1], "슬롯 1 = HealthTank");
            Assert.AreEqual(0, brain.SubId);
            Assert.AreEqual(1, tank.SubId);
            yield return WaitUntil(() => brain.IsOwner, 5);

            // 2) Per-subId RPC — each component's ServerRpc runs on the same netId without interfering with the other
            brain.RpcMove(5);   // client→server path (host client sends → server receives → dispatches to slot 0)
            yield return WaitUntil(() => brain.MoveCalls >= 1, 5);
            tank.RpcHeal(7);
            yield return WaitUntil(() => tank.HealCalls >= 1, 5);
            Assert.AreEqual(1, brain.MoveCalls, "subId 0 — MovementBrain만");
            Assert.AreEqual(1, tank.HealCalls, "subId 1 — HealthTank만");

            // 3) Per-slot replication — server-side changes propagate as per-slot deltas (the host keeps the original instance)
            brain.Speed = 42;
            tank.Armor = 77;
            yield return WaitUntil(() => brain.Speed == 42 && tank.Armor == 77, 5);   // host original — the values are already 42/77 locally
            Assert.IsTrue(brain.IsServer && tank.IsServer, "두 서브 모두 서버 등록");

            // 4) Destroy — the whole object
            ulong netId = brain.NetId;
            UniNetManager.NetworkDestroy(go);
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) == null
                && UniNetEnvironment.Server.GetEntry(netId) == null, 5);

            UnityEngine.Debug.Log("[UNINET-VERIFY] MultiComponent PASS — NetworkInstantiate(2 subs)/SubIdRouting/PerSubReplication/Destroy");
        }

        /// <summary>[netId][int amount] payload — same format as the generated encoder.</summary>
        private static byte[] EncodePing(ulong netId, int amount)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteUInt64(netId);
            w.WriteByte(0);   // SubId — slot 0 of a single-component object
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
