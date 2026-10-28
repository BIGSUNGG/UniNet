using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// AUDIT-20261027-F10 regression — the REAL ArenaBootstrap.ManagePlayers poll (private seam invoked on a real
    /// component; its Start is never run — an inactive host GameObject) must count APPROVED participants, not raw
    /// live transport connections: a silent undeclared channel (attached, never declared — the pre-approval state
    /// every connection passes through) must cause zero avatar growth or surplus destruction, while a connection
    /// approved through the REAL declaration path gets an avatar and the connection↔avatar mapping. The player
    /// prefab resolves through the existing in-memory prefab-table path (the committed Arena prefab) — no user
    /// asset is edited.
    /// </summary>
    public sealed class ArenaApprovalPopulationTests
    {
        /// <summary>Silent channel — attached to the real server registry, never declares a version (pre-approval).</summary>
        private sealed class SilentChannel : IUniNetSystemChannel
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
            public void SendLoadScene(string sceneName) { }
            public void UniNetSend(int methodId, byte[] payload, DRPC.RpcDeliveryMode mode) { }
        }

        [TearDown]
        public void TearDown()
        {
            UniNetManager.ClientStop();
            UniNetManager.ServerStop();   // sweeps test-spawned dynamics
            UniNetEnvironment.SetClient(null);
            UniNetEnvironment.SetServer(null);
            foreach (var boot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (boot != null) Object.DestroyImmediate(boot.gameObject);
            UniNetPrefabTable.ResetCache();
        }

        [UnityTest]
        public IEnumerator 미승인_연결은_아바타를_만들지_않고_승인_후_생성과_매핑이_일어난다()
        {
            foreach (var existingBoot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                existingBoot.enabled = false;

            var server = new NetworkServer { ExpectedProtocolVersion = ArenaConfig.ProtocolVersion };
            UniNetEnvironment.SetServer(server);

            // real component, real private poll — Start never runs (inactive host object), so no listener is opened
            var hostGo = new GameObject("F10BootHost");
            hostGo.SetActive(false);
            var boot = hostGo.AddComponent<ArenaBootstrap>();
            typeof(ArenaBootstrap).GetField("_server", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(boot, server);
            var manage = typeof(ArenaBootstrap).GetMethod("ManagePlayers", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(manage, "ManagePlayers 진입점");
            int Avatars() => Object.FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;

            // 1) silent unapproved connection — attached but never declared (the state every connection starts in)
            var silent = new SilentChannel();
            server.AttachConnection(silent);
            Assert.AreNotEqual(0, silent.UniNetConnId, "연결 부착");
            Assert.IsFalse(server.IsVersionApproved(silent.UniNetConnId), "미승인 상태");

            manage.Invoke(boot, null);   // REAL poll
            Assert.AreEqual(0, Avatars(), "미승인 연결 — 아바타 증감 없음 (F06 게이트 우회 제거)");
            // the OLD code counted the raw connection here and spawned an unauthenticated avatar

            // 2) A DIFFERENT connection approves; the earlier silent transport remains unapproved. Counting or
            // automatic-owner selection must not accidentally allocate the approved player's avatar to that first ID.
            var participant = new SilentChannel();
            long connId = server.AttachConnection(participant);
            Assert.IsTrue(server.OnClientVersionDeclared(connId, ArenaConfig.ProtocolVersion, "arena-test"));
            UniNetEnvironment.PumpMain();
            Assert.IsTrue(server.IsVersionApproved(connId), "선언 승인");
            Assert.IsFalse(server.IsVersionApproved(silent.UniNetConnId), "첫 transport는 계속 미승인이다");

            manage.Invoke(boot, null);   // REAL poll again
            yield return null;
            Assert.AreEqual(1, Avatars(), "승인 후 아바타 1개 생성");

            // round-robin assigned the avatar to the approved connection → the bootstrap registered the mapping
            var avatar = Object.FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)[0];
            yield return WaitUntil(() => server.GetOwner(avatar.NetId) == connId, 5, "아바타 소유권 배정");
            Assert.AreEqual(avatar.NetId, server.GetPlayerObject(connId), "연결↔아바타 매핑 (SetPlayerObject)");

            // 3) ANOTHER silent unapproved connection — must not spawn a second avatar nor destroy the existing one
            server.AttachConnection(new SilentChannel());
            manage.Invoke(boot, null);
            yield return null;
            Assert.AreEqual(1, Avatars(), "추가 미승인 연결 — 여전히 아바타 1개 (잉여 파괴/추가 스폰 없음)");
        }

        private static IEnumerator WaitUntil(System.Func<bool> condition, float timeout, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"대기 시간 초과: {what}");
                yield return null;
            }
        }
    }
}
