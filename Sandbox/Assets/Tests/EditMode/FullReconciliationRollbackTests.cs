using System.Reflection;
using NUnit.Framework;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Full CSP&R rollback + re-simulation (ADR-0037) — exercises the internal OnAckSeqChanged path via reflection:
    /// records history, simulates a server position mismatch, triggers the ack handler, and verifies rollback occurred.
    /// </summary>
    public sealed class FullReconciliationRollbackTests
    {
        [Test]
        public void 서버_불일치_시_롤백이_발생하고_히스토리가_정리된다()
        {
            var go = new GameObject("csp-rollback");
            var nt = go.AddComponent<NetworkTransform>();
            try
            {
                // Set up client so IsOwner = true (NetworkBehaviour.IsOwner checks the environment)
                var client = new UniNet.Core.Hosting.NetworkClient();
                client.SetLocalConnId(1);
                client.ApplyOwner(nt.NetId, 1);   // owner = connId 1 = LocalConnId → IsOwner = true
                UniNet.Core.Hosting.UniNetEnvironment.SetClient(client);

                nt.FullReconciliation = true;
                nt.MovementRule = (ref float x, ref float y, ref float z, float ix, float iy, float iz, float dt)
                    => { x += ix * dt; };

                // Access private fields via reflection to set up the state
                var type = typeof(NetworkTransform);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;

                // Set _predInit = true (baseline anchored)
                type.GetField("_predInit", flags).SetValue(nt, true);
                // Set _clientSeq to simulate some inputs were sent
                type.GetField("_clientSeq", flags).SetValue(nt, 5u);

                // Record history entries by simulating RecordHistory calls
                var recordMethod = type.GetMethod("RecordHistory", flags);
                var historyField = type.GetField("_history", flags);
                var headField = type.GetField("_historyHead", flags);
                var countField = type.GetField("_historyCount", flags);

                // Set predicted position
                type.GetField("_predX", flags).SetValue(nt, 10f);
                type.GetField("_predY", flags).SetValue(nt, 0f);
                type.GetField("_predZ", flags).SetValue(nt, 0f);

                // Record 3 entries (seq 3, 4, 5)
                for (uint seq = 3; seq <= 5; seq++)
                {
                    type.GetField("_clientSeq", flags).SetValue(nt, seq);
                    type.GetField("_inX", flags).SetValue(nt, 1f);
                    type.GetField("_predX", flags).SetValue(nt, 10f + seq);   // predicted drifts
                    recordMethod.Invoke(nt, null);
                }

                Assert.Greater(nt.HistoryCount, 0, "히스토리 기록됨");

                // Set server position (mismatch — server says x=8 at seq 3, prediction was 13)
                type.GetField("_px", flags).SetValue(nt, 8f);
                type.GetField("_py", flags).SetValue(nt, 0f);
                type.GetField("_pz", flags).SetValue(nt, 0f);
                // Set _ackSeq to 3 (server acknowledges seq 3)
                type.GetField("_ackSeq", flags).SetValue(nt, 3u);

                // Invoke OnAckSeqChanged — triggers rollback + re-simulation
                var ackMethod = type.GetMethod("OnAckSeqChanged", flags);
                Assert.IsNotNull(ackMethod, "OnAckSeqChanged 메서드 존재");
                ackMethod.Invoke(nt, new object[] { 0u });   // prev = 0

                // Verify rollback occurred
                Assert.Greater(nt.LastRollbackSeq, 0, "롤백 발생 (LastRollbackSeq > 0)");
                // Verify history was compacted (acknowledged entries discarded)
                Assert.Less(nt.HistoryCount, 3, "승인된 히스토리 폐기 (HistoryCount < 기록 수)");
            }
            finally
            {
                UniNet.Core.Hosting.UniNetEnvironment.SetClient(null);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void 텔레포트_시_재앵커가_동작한다()
        {
            var go = new GameObject("csp-teleport");
            var nt = go.AddComponent<NetworkTransform>();
            try
            {
                // Client setup for IsOwner = true (same as rollback test)
                var client = new UniNet.Core.Hosting.NetworkClient();
                client.SetLocalConnId(1);
                client.ApplyOwner(nt.NetId, 1);
                UniNet.Core.Hosting.UniNetEnvironment.SetClient(client);

                nt.FullReconciliation = true;
                nt.SnapThreshold = 0.5f;
                nt.MovementRule = (ref float x, ref float y, ref float z, float ix, float iy, float iz, float dt)
                    => { x += ix * dt; };
                nt.SimulationEnabled = true;
                nt.PredictOwner = true;

                var type = typeof(NetworkTransform);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;

                // Simulate: prediction anchored, far from server (teleport scenario)
                type.GetField("_predInit", flags).SetValue(nt, true);
                type.GetField("_predX", flags).SetValue(nt, 50f);
                type.GetField("_predY", flags).SetValue(nt, 0f);
                type.GetField("_predZ", flags).SetValue(nt, 0f);
                type.GetField("_px", flags).SetValue(nt, 0f);   // server = origin (respawn)
                type.GetField("_py", flags).SetValue(nt, 0f);
                type.GetField("_pz", flags).SetValue(nt, 0f);
                type.GetField("_historyCount", flags).SetValue(nt, 0);   // no pending inputs

                // Invoke private Update() — the teleport re-anchor (BEFORE RecordHistory) should snap _predX to _px
                var updateMethod = type.GetMethod("Update", flags);
                Assert.IsNotNull(updateMethod, "Update 메서드 존재");
                updateMethod.Invoke(nt, null);

                float predX = (float)type.GetField("_predX", flags).GetValue(nt);
                Assert.AreEqual(0f, predX, 0.01f, "텔레포트 재앵커 — _predX가 _px(0)로 스냅됨 (ADR-0037)");
            }
            finally
            {
                UniNet.Core.Hosting.UniNetEnvironment.SetClient(null);
                Object.DestroyImmediate(go);
            }
        }
    }
}
