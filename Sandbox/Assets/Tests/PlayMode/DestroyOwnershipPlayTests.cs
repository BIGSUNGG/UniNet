using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>
    /// OWN-003 — destroy → owner-cache cleanup over a real loopback RUDP session. After a network destroy reaches
    /// the client (wire 4 → Unregister), GetOwner must return 0 for the destroyed netId (no stale last owner),
    /// other netIds and spawn/destroy cycles must not accumulate, OwnershipChanged stays silent (destruction is
    /// state hygiene, not reassignment), and the Arena departure pattern (DestroyObjectsOwnedBy) leaves the
    /// SURVIVING observer's cache clean while scene objects are untouched.
    /// </summary>
    public sealed class DestroyOwnershipPlayTests
    {
        /// <summary>Record-only channel standing in for a second connection (same pattern as OwnershipTransferPlayTests).</summary>
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

        [SetUp]
        public void AllowStopNoise()
        {
            LogAssert.ignoreFailingMessages = true;   // shutdown send noise — same handling as OwnershipTransferPlayTests
        }

        [TearDown]
        public void StopListeners()
        {
            LogAssert.ignoreFailingMessages = true;
            UniNetManager.HostStop();   // idempotent cleanup of listeners/connections left between tests
        }

        [OneTimeTearDown]
        public void RestoreLogChecks()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator 실세션_동적_스폰_파괴후_클라_소유자_캐시가_0으로_정리된다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab),
                new Vector3(1f, 2f, 3f), Quaternion.identity);
            var spawned = go.GetComponent<SpawnablePlayer>();
            ulong netId = spawned.NetId;
            Assert.AreNotEqual(0ul, netId, "동적 netId 할당");

            yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) != null, 5, "클라 스폰 등록 (실루프백 왕복)");
            yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(netId) == UniNetEnvironment.Client.LocalConnId, 5, "클라 소유자 관찰");

            int ownershipEvents = 0;
            UniNetEnvironment.Client.OwnershipChanged += (id, prev, next) => ownershipEvents++;

            // Core destroy API — the server deregisters and broadcasts destroy (wire 4); no owner-0 notice precedes it (D2)
            Assert.IsTrue(UniNetEnvironment.Server.DestroyObject(netId), "서버 파괴 성립");
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) == null, 5, "클라 등록 해제 (와이어 4 왕복)");

            Assert.AreEqual(0, UniNetEnvironment.Client.GetOwner(netId), "파괴 후 GetOwner==0 (OWN-003 — 잔존 소유자 없음)");
            Assert.IsNull(UniNetEnvironment.Server.GetEntry(netId), "서버 등록 해제");
            yield return null;   // let Object.Destroy take effect at end of frame
            Assert.IsTrue(spawned == null, "오브젝트 파괴");
            Assert.AreEqual(0, ownershipEvents, "파괴 정리 — OwnershipChanged 미발화 (D2 — 재배정 통보 전용)");
        }

        [UnityTest]
        public IEnumerator 스폰_파괴_10사이클동안_클라_소유자_캐시가_누적되지_않는다()
        {
            int port = TestPortAllocator.Next();
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            for (int cycle = 0; cycle < 10; cycle++)
            {
                var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab),
                    new Vector3(cycle, 0f, 0f), Quaternion.identity);
                var spawned = go.GetComponent<SpawnablePlayer>();
                ulong netId = spawned.NetId;

                yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) != null, 5, $"사이클 {cycle} — 스폰 등록");
                yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(netId) != 0, 5, $"사이클 {cycle} — 소유자 관찰");

                UniNetManager.NetworkDestroy(go);
                yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) == null, 5, $"사이클 {cycle} — 파괴 왕복");
                Assert.AreEqual(0, UniNetEnvironment.Client.GetOwner(netId), $"사이클 {cycle} — 파괴 후 GetOwner==0 (누적 없음)");
            }
        }

        [UnityTest]
        public IEnumerator 호스트_서버_파괴가_클라_등록과_소유자_캐시를_함께_정리한다()
        {
            int port = TestPortAllocator.Next();
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab),
                new Vector3(0f, 0.5f, 0f), Quaternion.identity);
            var spawned = go.GetComponent<SpawnablePlayer>();
            ulong netId = spawned.NetId;

            yield return WaitUntil(() => spawned.IsOwner, 5, "호스트 클라 소유권 (HostRoundtrip 하네스)");
            Assert.AreEqual(UniNetEnvironment.Client.LocalConnId, UniNetEnvironment.Client.GetOwner(netId), "파괴 전 — 호스트 클라가 소유");

            UniNetManager.NetworkDestroy(go);   // 호스트 서버 파괴 — local GO + server dereg + wire 4
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(netId) == null, 5, "호스트 파괴 왕복");
            yield return null;

            Assert.AreEqual(0, UniNetEnvironment.Client.GetOwner(netId), "파괴 후 GetOwner==0 (OWN-003)");
            Assert.IsNull(UniNetEnvironment.Server.GetEntry(netId), "서버 등록 해제");
            Assert.IsTrue(spawned == null, "로컬 게임오브젝트 파괴");
        }

        [UnityTest]
        public IEnumerator 아레나_퇴장_정리후_생존_관찰자의_소유자_캐시가_0으로_정리된다()
        {
            int port = TestPortAllocator.Next();
            var serverTask = UniNetManager.ServerAsync(port);
            while (!serverTask.IsCompleted) yield return null;
            Assert.IsFalse(serverTask.IsFaulted, serverTask.Exception?.ToString());
            var server = UniNetEnvironment.Server;

            // A — second connection (record-only channel), approved so its departure raises ClientDisconnected
            var guestA = new GuestChannel();
            server.AttachConnection(guestA);
            server.OnClientVersionDeclared(guestA.UniNetConnId, server.ExpectedProtocolVersion ?? "");   // CON-001 — fake channels must declare too
            UniNetEnvironment.PumpMain();

            // B — real RUDP client, the surviving observer
            var clientTask = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!clientTask.IsCompleted) yield return null;
            Assert.IsFalse(clientTask.IsFaulted, clientTask.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "B 접속 Welcome");

            // Arena pattern (ADR-0031) — the departure handler destroys the leaver's dynamic objects via the real server API
            long aConn = guestA.UniNetConnId;
            int destroyedByLeave = 0;
            server.ClientDisconnected += leaving =>
            {
                if (leaving != aConn) return;
                destroyedByLeave = UniNetManager.DestroyObjectsOwnedBy(leaving);
            };

            // Scene object owned by A — level furniture: must SURVIVE the departure cleanup (dynamic-only contract)
            ulong sceneNetId = Fnv1a.Hash64("own003-play-scene-marker");
            server.RegisterSceneObject(sceneNetId, new object[] { new object() });
            server.SetOwner(sceneNetId, aConn);

            // Dynamic object owned by A — B instantiates it from the baked prefab table and observes A's ownership
            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.SpawnablePlayerPrefab),
                new Vector3(0f, 0.5f, 0f), Quaternion.identity);
            var avatar = go.GetComponent<SpawnablePlayer>();
            ulong dynNetId = avatar.NetId;
            Assert.IsTrue(server.SetOwner(dynNetId, aConn), "A 소유권 배정");
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(dynNetId) != null, 5, "B — 스폰 관찰");
            yield return WaitUntil(() => UniNetEnvironment.Client.GetOwner(dynNetId) == aConn, 5, "B — A 소유자 관찰 (OwnerUpdate 왕복)");

            // A leaves → the handler runs DestroyObjectsOwnedBy → wire 4 → B's Remove → Unregister → owner cache cleaned
            server.DetachConnection(guestA);
            yield return WaitUntil(() => destroyedByLeave >= 1, 5, "퇴장 정리 발화 (ClientDisconnected → DestroyObjectsOwnedBy)");
            yield return WaitUntil(() => UniNetEnvironment.Client.Get(dynNetId) == null, 5, "B — 파괴 왕복");

            Assert.AreEqual(0, UniNetEnvironment.Client.GetOwner(dynNetId), "생존 관찰자 B — A 소유 netId GetOwner==0 (OWN-003)");
            Assert.IsNotNull(server.GetEntry(sceneNetId), "씬 객체 — 퇴장 정리 생존 (동적 객체 한정)");
            yield return null;
            Assert.IsTrue(avatar == null, "서버 측 아바타 게임오브젝트 파괴");
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
