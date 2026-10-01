using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>Scene netId source-of-truth tests (ADR-0023) — serialized stamped ID vs path-hash fallback vs dynamic preemption.</summary>
    public sealed class SceneNetIdTests
    {
        private static SpawnablePlayer CreatePlayer(string name)
        {
            return new GameObject(name).AddComponent<SpawnablePlayer>();
        }

        private static void Stamp(SpawnablePlayer player, ulong id)
        {
            var so = new SerializedObject(player);
            so.FindProperty("_sceneNetId").ulongValue = id;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void 직렬화된_씬_아이디는_경로_해시를_대체한다()
        {
            var player = CreatePlayer("stamped");
            try
            {
                Stamp(player, 0x123456789ABCDEF0);
                Assert.AreEqual(0x123456789ABCDEF0UL, player.NetId, "각인된 직렬화 아이디 사용");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [Test]
        public void 아이디가_없으면_폴백_해시를_쓰고_구조가_다르면_다르다()
        {
            var a = CreatePlayer("fallback-a");
            var other = CreatePlayer("fallback-b");
            try
            {
                Assert.AreNotEqual(0UL, a.NetId, "미각인 오브젝트는 경로 해시 폴백 — 0이 아니다");
                Assert.AreNotEqual(a.NetId, other.NetId, "다른 이름 — 다른 폴백 해시");
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(other.gameObject);
            }
        }

        [Test]
        public void NetId는_첫_읽기에서_고정된다()
        {
            var player = CreatePlayer("netid-cached");
            try
            {
                var first = player.NetId;   // unstamped → path-hash fallback, cached
                Stamp(player, 0x123456789ABCDEF0);
                Assert.AreEqual(first, player.NetId, "첫 읽기 값 고정 — 각인은 씬 저장(런타임 전)에 끝나므로 등록 시점에는 이미 반영되어 있다");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }

        [Test]
        public void 동적_할당은_직렬화_아이디를_선점한다()
        {
            var player = CreatePlayer("dynamic-preempts");
            try
            {
                Stamp(player, 0xFEDCBA9876543210);
                player.AssignNetId(999);
                Assert.AreEqual(999UL, player.NetId, "AssignNetId가 직렬화 값을 덮는다");
                Assert.IsTrue(player.IsDynamicSpawn, "동적 스폰 표식");
            }
            finally { Object.DestroyImmediate(player.gameObject); }
        }
    }
}
