using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>P4 grid spatial-partition visibility (RepGraph style) — verifies cell-membership culling, fail-open, and AND with per-object relevancy.</summary>
    public sealed class GridVisibilityTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
        {
            global::UniNet.Generated.__UniNetRegistration.Register();
        }

        [Test]
        public void 그리드_반경_밖_오브젝트는_컬되고_뷰어_이동_시_다시_보인다()
        {
            var server = new NetworkServer();
            var near = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grid-near");
            near.transform.position = new Vector3(5f, 0f, 0f);
            var far = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grid-far");
            far.transform.position = new Vector3(500f, 0f, 0f);

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // catch-up — no viewer set → both relevant (fail-open) → full state + seed
            ch.Clear();

            server.SetVisibilityGrid(10f, 30f);           // cell size 10, radius 30
            server.SetViewerPosition(ch.UniNetConnId, 0f, 0f, 0f);   // near (5 m) inside radius, far (500 m) outside

            near.Score = 11;   // differs from the initial Score of 1 — creates a real delta
            far.Score = 11;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(1, CountNetId(ch, near.NetId), "반경 내 오브젝트 — 델타 전송");
            Assert.AreEqual(0, CountNetId(ch, far.NetId), "반경 밖 오브젝트 — 그리드 컬");

            server.SetViewerPosition(ch.UniNetConnId, 495f, 0f, 0f);   // viewer moves toward far
            near.Score = 12;
            far.Score = 12;
            server.TickReplication(server.SnapshotObjects(), 1.5);
            Assert.AreEqual(0, CountNetId(ch, near.NetId, 2), "이제 반경 밖이 된 near — 델타 중단");
            Assert.GreaterOrEqual(CountNetId(ch, far.NetId, 2), 1, "far 재진입 — 기준선 복구(전체 상태) + 델타");
        }

        [Test]
        public void 뷰어_미설정_연결은_그리드와_무관하게_모두_받는다()
        {
            var server = new NetworkServer();
            var near = ReplicationTestSupport.RegisterScene<SpawnablePlayer>(server, "grid-open");
            near.transform.position = new Vector3(5f, 0f, 0f);
            server.SetVisibilityGrid(10f, 30f);   // grid on — but viewer position is never set

            var ch = new RecordingChannel();
            server.AttachConnection(ch);
            UniNetEnvironment.PumpMain();   // catch-up — no viewer → fail-open (always relevant) → receives full state + seed
            ch.Clear();

            near.Score = 21;
            server.TickReplication(server.SnapshotObjects(), 1.0);
            Assert.AreEqual(1, CountNetId(ch, near.NetId), "P3 계약 — 뷰어 위치 미설정 연결은 컬을 받지 않는다");
        }

        private static int CountNetId(RecordingChannel ch, ulong targetNetId, int fromIndex = 0)
        {
            int count = 0;
            for (int i = fromIndex; i < ch.Replicates.Count; i++)
                if (ch.Replicates[i].netId == targetNetId)
                    count++;
            return count;
        }
    }
}
