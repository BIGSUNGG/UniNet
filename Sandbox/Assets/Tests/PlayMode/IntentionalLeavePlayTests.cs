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
    /// REC-002 over a real loopback RUDP session — a voluntary ClientStop announces wire 15 BEFORE the transport
    /// close, so the server skips the grace save and a fresh client joining WITHIN the grace window adopts the
    /// orphaned ownership immediately (no orphan blocking). Honest scope: the best-effort notice can lose the
    /// close race, so the IntentionalLeaves counter is the delivery observation — a lost race keeps the legacy
    /// grace behavior (no new failure mode); a systematically lost notice fails here and escalates to the
    /// flush-lease design review (plan BLOCKER 2).
    /// </summary>
    public sealed class IntentionalLeavePlayTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            foreach (var boot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                boot.enabled = false;   // tests run on the Arena scene — the bootstrap must not adopt the test server
        }

        [TearDown]
        public void TearDown() => UniNetManager.HostStop();

        [OneTimeTearDown]
        public void RestoreLogChecks() => LogAssert.ignoreFailingMessages = false;

        [UnityTest]
        public IEnumerator 자발_ClientStop은_유예_없이_정리되고_후발_클라가_즉시_입양한다()
        {
            int port = TestPortAllocator.Next();   // fresh port per test — listener sockets linger across runs
            var hostTask = UniNetManager.HostAsync(port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());
            var server = UniNetEnvironment.Server;
            server.SessionGraceSeconds = 10;   // grace on — the notified voluntary stop must still skip the save

            // a plain registry marker (same pattern as the SEC-001 Arena diagnostic) gives the adoption assertion
            // a subject — round-robin assigns it to the only connection
            ulong netId = Fnv1a.Hash64("rec002-play-ownership-marker");   // deterministic side id — no scene/prefab dependency
            server.RegisterSceneObject(netId, new object[] { new object() });

            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "1차 접속 Welcome");
            long firstConn = UniNetEnvironment.Client.LocalConnId;
            server.SetOwner(netId, firstConn);   // ADR-0030 explicit ownership — host mode attaches both ends BEFORE the marker is registered, so the attach-time adoption trigger has already passed
            yield return WaitUntil(() => server.GetOwner(netId) == firstConn, 5, "마커 소유권 배정");

            // voluntary stop — the wire-15 notice rides the reliable channel ahead of the transport close
            UniNetManager.ClientStop();
            yield return WaitUntil(() => server.IntentionalLeaves >= 1, 5, "와이어 15 도달 (실루프백 유실률 관측)");
            yield return WaitUntil(() =>
            {
                foreach (var conn in server.SnapshotConnections())
                    if (conn.ConnId == firstConn) return false;
                return true;
            }, 5, "1차 연결 해제 관측");

            // second fresh client joins INSIDE the grace window (10 s) — adoption must be immediate, not after expiry
            var connect = UniNetManager.ClientAsync("127.0.0.1", port);
            while (!connect.IsCompleted) yield return null;
            Assert.IsFalse(connect.IsFaulted, connect.Exception?.ToString());
            yield return WaitUntil(() => UniNetEnvironment.Client.LocalConnId != 0, 5, "2차 접속 Welcome");
            long secondConn = UniNetEnvironment.Client.LocalConnId;
            Assert.AreNotEqual(firstConn, secondConn, "커넥션 아이디 구분");
            yield return WaitUntil(() => server.GetOwner(netId) == secondConn, 5, "유예 창 내 고아 즉시 입양");

            UnityEngine.Debug.Log($"[UNINET-VERIFY] IntentionalLeave PASS — voluntary ClientStop skipped the grace save (wire 15 delivered, counter={server.IntentionalLeaves}), fresh client adopted the orphan immediately within the grace window");
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
