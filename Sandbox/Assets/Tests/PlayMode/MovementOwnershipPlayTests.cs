using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// MOV-001 — a real ownership cycle over loopback RUDP (release → reassignment adoption → the same client
    /// re-gains), then the input stream contract: a stale-tenure input from the PREVIOUS epoch is rejected by the
    /// epoch gate (it passes the ADR-0016 current-owner gate), and the new tenure's fresh stream is adopted with
    /// acknowledgements resuming. Host topology: the single instance is the authority, so owner-client PREDICTION
    /// rebinding is covered by the EditMode component regressions — here the marker's submit path is driven through
    /// the real generated encoder + ClientSender + RUDP + server dispatch, exactly like the predicting client's wire
    /// traffic (single client; no second-client or full avatar-recovery claims).
    /// </summary>
    public sealed class MovementOwnershipPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 9;   // random port per run — offset +9 (no overlap with the other PlayMode classes)

        /// <summary>Record-only channel standing in for a second connection — attaching it triggers the reassignment pass that adopts the released object (same pattern as OwnershipTransferPlayTests).</summary>
        private sealed class GuestChannel : IUniNetSystemChannel
        {
            public long UniNetConnId { get; set; }
            public void SendWelcome(long connId, string serverVersion, string sessionToken) { }
            public void SendOwnerUpdate(ulong netId, long ownerConnId) { }
            public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload) { }
            public void SendReplicateBatch(byte[] blob, DRPC.RpcDeliveryMode mode) { }
            public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw, ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
            public void SendDestroy(ulong netId) { }
            public void SendTimeSync(double serverTime) { }
            public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason) => System.Threading.Tasks.Task.CompletedTask;
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
            public void SendLoadScene(string sceneName) { }
        }

        private struct TIn { public sbyte Dx; public bool Jump; }
        private struct TSt { public float Vy; }

        private static void Step(ref TSt st, ref float x, ref float y, ref float z, in TIn i, float dt)
        {
            x += i.Dx * 4f * dt;
        }

        private static void WriteInput(ref MessageProtocol.Serialize.MessageBufferWriter w, in TIn i)
        {
            w.WriteSByte(i.Dx);
            w.WriteBoolean(i.Jump);
        }

        private static TIn ReadInput(ref MessageProtocol.Serialize.MessageBufferReader r)
            => new TIn { Dx = r.ReadSByte(), Jump = r.ReadBoolean() };

        private static void WriteState(ref MessageProtocol.Serialize.MessageBufferWriter w, in TSt s)
            => w.WriteSingle(s.Vy);

        private static TSt ReadState(ref MessageProtocol.Serialize.MessageBufferReader r)
            => new TSt { Vy = r.ReadSingle() };

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            // Tests run on top of the Arena scene; the scene bootstrap would otherwise adopt the test server as its
            // own — disable it explicitly (a disabled component's Update is never called; the scene is never saved)
            foreach (var boot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                boot.enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetManager.HostStop();   // listeners/connections left between tests are cleaned up (idempotent)
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator 소유권_사이클_후_이전_임기_입력은_epoch로_기각되고_새_스트림_ack가_재개된다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            // the client-owned marker — a real dynamically spawned NetworkTransform with an injected simulation
            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.NetworkTransformPrefab));
            var nt = go.GetComponent<NetworkTransform>();
            var sim = new MoveSimulation<TIn, TSt>(Step, WriteInput, ReadInput, WriteState, ReadState, 60f);
            nt.Simulation = sim;

            var client = UniNetEnvironment.Client;
            yield return WaitUntil(() => client.LocalConnId != 0, 5, "Welcome 수신");
            yield return WaitUntil(() => client.GetOwner(nt.NetId) == client.LocalConnId, 5, "초기 소유 (마커)");
            long ownerConn = client.LocalConnId;

            var transitions = new List<(ulong NetId, long Prev, long Next)>();
            var gate = new object();
            client.OwnershipChanged += (netId, prev, next) => { lock (gate) transitions.Add((netId, prev, next)); };   // fires on the dispatch thread

            try
            {
                // tenure 1 — a real owner-sent stream over RUDP (generated encoder + ClientSender + server dispatch)
                SendMove(nt, 1, 0);
                SendMove(nt, 2, 0);
                yield return WaitUntil(() => ((INetworkMoveSimulation)sim).LastAdoptedSeq >= 2, 5, "1차 임기 입력 채택");
                uint epoch0 = GetU(nt, "_moveEpoch");

                // release — the real SetOwner API; the client observes the loss over the wire
                Assert.IsTrue(UniNetEnvironment.Server.SetOwner(nt.NetId, 0), "해제 성립");
                yield return WaitUntil(() => client.GetOwner(nt.NetId) == 0, 5, "방치 관찰 (OwnerUpdate 왕복)");
                yield return WaitUntil(() => GetU(nt, "_moveEpoch") == epoch0 + 1, 5, "해제 epoch 증가");
                Assert.AreEqual(0u, ((INetworkMoveSimulation)sim).LastAdoptedSeq, "서버 스트림 리셋 (범프와 짝)");

                // reassignment adoption — a new attach triggers the pass; the first live connection re-gains the marker
                var guest = new GuestChannel();
                UniNetEnvironment.Server.AttachConnection(guest);
                UniNetEnvironment.PumpMain();
                yield return WaitUntil(() => client.GetOwner(nt.NetId) == ownerConn, 5, "재배정 입양 — 같은 클라 재획득");
                uint epoch1 = GetU(nt, "_moveEpoch");
                Assert.AreEqual(epoch0 + 1, epoch1, "해제만 범프 — 입양(0→conn)은 베이스라인 (무범프·무리셋)");

                // stale-tenure input: the CURRENT owner sends with the PREVIOUS epoch — passes the ownership gate,
                // must die at the epoch gate (probe-2's poisoning, now observable and rejected)
                SendMove(nt, 900, epoch0);
                yield return WaitUntil(() => nt.MoveEpochMismatches >= 1, 5, "구 epoch 입력 — epoch 게이트 기각 관측");
                yield return new WaitForSecondsRealtime(0.2f);
                Assert.AreEqual(0u, ((INetworkMoveSimulation)sim).LastAdoptedSeq, "구 임기 seq 900 재오염 없음");

                // the new tenure's fresh stream — adopted, acknowledged, resumed
                SendMove(nt, 1, epoch1);
                SendMove(nt, 2, epoch1);
                SendMove(nt, 3, epoch1);
                yield return WaitUntil(() => ((INetworkMoveSimulation)sim).LastAdoptedSeq >= 3, 5, "새 스트림 채택");
                yield return WaitUntil(() => GetU(nt, "_ackSeq") >= 3, 5, "ack 재개 (OwnerOnly 복제)");

                lock (gate)
                {
                    CollectionAssert.Contains(transitions, (nt.NetId, ownerConn, 0L), "와이어 관찰 — 해제 전이 이벤트");
                    CollectionAssert.Contains(transitions, (nt.NetId, 0L, ownerConn), "와이어 관찰 — 입양 전이 이벤트");
                }
                UnityEngine.Debug.Log($"[UNINET-VERIFY] MovementOwnership PASS — epoch {epoch0}->{epoch1}, stale rejected, fresh stream acked");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>Sends one move input through the real wire path: generated encoder → ClientSender → loopback RUDP → server dispatch (ownership gate) → implementation (epoch gate) → adoption.</summary>
        private static void SendMove(NetworkTransform nt, uint seq, uint epoch)
        {
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteSByte(1);
            w.WriteBoolean(false);
            byte[] data = (byte[])typeof(NetworkTransform).GetMethod("EncodeMoveInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { epoch, w.ToArray() });
            int methodId = Fnv1a.MethodId("UniNet.Unity.NetworkTransform.RpcSubmitMove");
            UniNetEnvironment.ClientSender.UniNetSend(methodId,
                NetworkTransform.__UniNetEncode_RpcSubmitMove(nt.NetId, nt.SubId, seq, new UniNetBlobEnvelope { Data = data }),
                DRPC.RpcDeliveryMode.ReliableOrdered);
        }

        private static uint GetU(NetworkTransform nt, string name)
            => (uint)typeof(NetworkTransform).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(nt);

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
