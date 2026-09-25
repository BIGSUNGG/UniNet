using System.Collections.Generic;
using MessageProtocol.Serialize;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Unit tests for FastArray element-level delta (see ADR-0020) — op replay for append/middle-change/clear,
    /// no-change suppression, full-state spawn path, RepNotify shallow-copy prev, null elements, and
    /// custom-serializer/array-field combinations. Deltas are applied to a SEPARATE client instance
    /// (mirrors the real topology — collections mutate additively, so re-applying on the server instance would double).
    /// </summary>
    public sealed class FastArrayTests
    {
        [SetUp]
        public void RegisterGeneratedCode()
            => global::UniNet.Generated.__UniNetRegistration.Register();   // explicit EditMode registration (idempotent)

        /// <summary>Server-side entry plus a separate client instance receiving deltas (EditMode stand-in for two peers).</summary>
        private sealed class Pair
        {
            public NetworkServer.SubObjectEntry Entry;
            public FastArrayTank Server;
            public FastArrayTank Client;
            public GameObject ServerGo, ClientGo;

            public void SendAndApply()
            {
                var handler = UniNetTypeRegistry.Find(Server.GetType())!;
                byte[] delta = handler.CompareAndWriteDelta(Entry).Owner;
                Assert.IsNotNull(delta, "변경이 있으므로 델타가 생성되어야 한다");
                var r = new MessageBufferReader(delta);
                handler.ApplyDelta(Client, ref r);
            }
        }

        private static Pair CreatePair()
        {
            var pair = new Pair();
            pair.ServerGo = new GameObject("fa-server");
            pair.ClientGo = new GameObject("fa-client");
            pair.Server = pair.ServerGo.AddComponent<FastArrayTank>();
            pair.Client = pair.ClientGo.AddComponent<FastArrayTank>();
            var handler = UniNetTypeRegistry.Find(pair.Server.GetType());
            Assert.IsNotNull(handler, "컬렉션 필드 타입도 핸들러 생성");
            pair.Entry = new NetworkServer.SubObjectEntry(pair.Server);
            handler.InitSnapshot(pair.Entry);
            handler.InitClientSnapshot(pair.Client);
            return pair;
        }

        [Test]
        public void append는_단일_Insert_옵으로_동기화된다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Scores.AddRange(new[] { 1, 2 });
                pair.SendAndApply();
                CollectionAssert.AreEqual(new[] { 1, 2 }, pair.Client.Scores, "초기 상태 동기화");

                pair.Server.Scores.Add(3);   // append — prefix covers 1,2 → single Insert op
                pair.SendAndApply();
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, pair.Client.Scores, "append 리플레이");
                Assert.AreEqual(2, pair.Client.ScoreNotifyCalls, "RepNotify — 배치당 1회씩 2회");
            }
            finally { Destroy(pair); }
        }

        [Test]
        public void 중간_변경은_리플레이_후_동일한_컬렉션이_된다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Scores.AddRange(new[] { 1, 2, 3, 4 });
                pair.SendAndApply();

                pair.Server.Scores.Remove(2);      // [1,3,4]
                pair.Server.Scores.Remove(3);      // [1,4]
                pair.Server.Scores.Insert(1, 9);   // [1,9,4]
                pair.SendAndApply();
                CollectionAssert.AreEqual(new[] { 1, 9, 4 }, pair.Client.Scores, "중간 삭제+삽입 리플레이");

                pair.Server.Scores[1] = 7;   // 동일 길이 변경 — Set 경로
                pair.SendAndApply();
                CollectionAssert.AreEqual(new[] { 1, 7, 4 }, pair.Client.Scores, "Set 경로 리플레이");
            }
            finally { Destroy(pair); }
        }

        [Test]
        public void clear는_단일_Clear_옵으로_전송된다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Scores.AddRange(new[] { 5, 6, 7 });
                pair.SendAndApply();

                pair.Server.Scores.Clear();
                var handler = UniNetTypeRegistry.Find(pair.Server.GetType())!;
                byte[] delta = handler.CompareAndWriteDelta(pair.Entry).Owner!;

                // wire check: tag 0 + opCount 1 + op 3 (Clear)
                var wr = new MessageBufferReader(delta);
                Assert.AreEqual(1u, wr.ReadUInt32(), "마스크 — Scores 비트");
                Assert.AreEqual(0, wr.ReadByte(), "델타 태그");
                Assert.AreEqual(1, wr.ReadUInt16(), "옵 1개");
                Assert.AreEqual(3, wr.ReadByte(), "Clear 옵");

                var r = new MessageBufferReader(delta);
                handler.ApplyDelta(pair.Client, ref r);
                Assert.AreEqual(0, pair.Client.Scores.Count, "클라 컬렉션 비움");
            }
            finally { Destroy(pair); }
        }

        [Test]
        public void 무변경은_델타를_생성하지_않는다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Scores.AddRange(new[] { 1, 2 });
                pair.SendAndApply();

                pair.Server.Scores = new List<int> { 1, 2 };   // 동일 내용 재할당
                var handler = UniNetTypeRegistry.Find(pair.Server.GetType())!;
                Assert.IsNull(handler.CompareAndWriteDelta(pair.Entry).Owner, "요소 동일 → 미전송");
            }
            finally { Destroy(pair); }
        }

        [Test]
        public void 스폰_전체_상태는_전체_카운트_인코딩으로_복원된다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Scores.AddRange(new[] { 10, 20, 30 });
                pair.Server.Names = new[] { "alpha", null, "gamma" };   // null 요소 — null 플래그 경로
                pair.Server.Flags = new uint[] { 1, 2, 3 };             // 배열 + 커스텀 직렬화 요소

                var handler = UniNetTypeRegistry.Find(pair.Server.GetType())!;
                byte[] full = handler.WriteFull(pair.Server, isOwner: true);
                Assert.IsNotNull(full);

                var r = new MessageBufferReader(full);
                handler.ApplyDelta(pair.Client, ref r);

                CollectionAssert.AreEqual(new[] { 10, 20, 30 }, pair.Client.Scores, "List 전체 복원");
                CollectionAssert.AreEqual(new[] { "alpha", null, "gamma" }, pair.Client.Names, "배열+null 요소 복원");
                CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, pair.Client.Flags, "커스텀 직렬화 요소 배열 복원");
            }
            finally { Destroy(pair); }
        }

        [Test]
        public void RepNotify_prev는_변경_전_얕은_복사다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Scores.AddRange(new[] { 1, 2 });
                pair.SendAndApply();

                pair.Server.Scores.Add(3);
                pair.SendAndApply();

                CollectionAssert.AreEqual(new[] { 1, 2 }, pair.Client.LastPrevScores, "prev는 마지막 동기화 상태의 복사본");
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, pair.Client.Scores, "현재 값은 리플레이 결과");
            }
            finally { Destroy(pair); }
        }

        [Test]
        public void null_요소는_플래그로_왕복한다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Names = new[] { "x" };
                pair.SendAndApply();

                pair.Server.Names = new[] { "x", null, "y" };
                pair.SendAndApply();
                CollectionAssert.AreEqual(new[] { "x", null, "y" }, pair.Client.Names, "참조형 요소 null 왕복");
            }
            finally { Destroy(pair); }
        }

        [Test]
        public void 배열_필드_중간_변경도_리플레이된다()
        {
            var pair = CreatePair();
            try
            {
                pair.Server.Flags = new uint[] { 1, 2, 3 };
                pair.SendAndApply();

                pair.Server.Flags = new uint[] { 1, 9 };   // 중간 변경 + 길이 축소 — T[] 경로
                pair.SendAndApply();
                CollectionAssert.AreEqual(new uint[] { 1, 9 }, pair.Client.Flags, "T[] 필드 리플레이");
            }
            finally { Destroy(pair); }
        }

        private static void Destroy(Pair pair)
        {
            Object.DestroyImmediate(pair.ServerGo);
            Object.DestroyImmediate(pair.ClientGo);
        }
    }
}
