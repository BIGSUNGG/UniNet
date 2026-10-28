using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UniNet.Unity;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UniNet.Tests
{
    /// <summary>
    /// AUDIT-20261027-F07 regression — a REAL StampPrefabs/BakeTable pass must persist ONLY the assets it owns.
    /// NetworkSpawnablePostprocessor lives in UniNet.Unity.Editor (not referenced by this asmdef) — invoked via
    /// reflection, the same seam the editor import pipeline hits. Flow: a NEW test-owned sentinel ScriptableObject
    /// is saved once, then modified and left dirty; a NEW test-owned spawnable prefab is created; the real
    /// Rebuild(pathList) stamps it and bakes the table; only the package's actual scheduled save callback is run
    /// (no unrelated Editor delay callbacks and NEVER SaveAssets); the sentinel must remain dirty with identical disk bytes
    /// while the operation's own prefab/table assets persist. Cleanup removes only test-owned assets and restores
    /// the user's baked table bytes (byte-compared before/after).
    /// </summary>
    public sealed class TargetedPersistenceTests
    {
        private const string TempDir = "Assets/Tests/EditMode/F07Temp";

        private static MethodInfo RebuildMethod()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "UniNet.Unity.Editor");
            var type = asm.GetType("UniNet.Unity.Editor.NetworkSpawnablePostprocessor");
            Assert.IsNotNull(type, "NetworkSpawnablePostprocessor 타입");
            var m = type.GetMethod("Rebuild", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m, "Rebuild 진입점");
            return m;
        }

        private static void RunDelayCalls()
        {
            var type = RebuildMethod().DeclaringType;
            var method = type.GetMethod("FlushTargetedSaves", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "패키지가 실제 delayCall로 등록하는 저장 콜백");
            var pending = typeof(EditorApplication).GetField("delayCall", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Delegate;
            Assert.AreEqual(1, pending?.GetInvocationList().Count(d => d.Method == method) ?? 0,
                "여러 Rebuild 요청은 실제 저장 delay 콜백 1개로 병합된다");
            var callback = (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), method);
            EditorApplication.delayCall -= callback;
            callback();   // execute only the operation's actual scheduled callback, not unrelated editor callbacks
        }

        private static byte[] ReadIfExists(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

        [Test]
        public void Stamp과_Bake는_무관한_dirty_자산을_저장하지_않고_자기_자산만_지속한다()
        {
            var rebuild = RebuildMethod();
            string sentinelPath = $"{TempDir}/F07Sentinel.asset";
            string prefabPath = $"{TempDir}/F07StampTarget.prefab";
            const string tablePath = "Assets/Resources/UniNet/UniNetPrefabTable.asset";
            byte[] tableBefore = ReadIfExists(tablePath);
            Assert.IsNotNull(tableBefore, "기존 베이크 테이블");

            try
            {
                Directory.CreateDirectory(TempDir);   // new test-owned folder — removed in cleanup
                AssetDatabase.ImportAsset(TempDir);

                // sentinel — saved ONCE (initial bytes), then modified and left dirty; nobody may persist it
                var sentinel = ScriptableObject.CreateInstance<UniNetPrefabTable>();
                sentinel.name = "F07SentinelOriginal";   // an existing asset type avoids a test-only nested MonoScript
                AssetDatabase.CreateAsset(sentinel, sentinelPath);
                byte[] sentinelBytes = ReadIfExists(sentinelPath);
                Assert.IsNotNull(sentinelBytes, "센티넬 초기 저장");
                sentinel.name = "F07SentinelDirty";   // serialized name changed, on disk still the initial bytes
                EditorUtility.SetDirty(sentinel);

                // a real spawnable prefab (test-owned) — the stamp target
                var root = new GameObject("F07StampTarget");
                root.AddComponent<HealthTank>();
                root.AddComponent<NetworkSpawnable>();
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Object.DestroyImmediate(root);

                // REAL stamp + bake (reflection seam — the editor pipeline's own entry)
                rebuild.Invoke(null, new object[] { new List<string> { prefabPath } });
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.IsNotNull(prefab);
                var marker = prefab.GetComponent<NetworkSpawnable>();
                Assert.AreNotEqual(0ul, marker.PrefabId, "각인 수행");
                Assert.IsNotNull(UniNetPrefabTable.Lookup(marker.PrefabId), "동기 in-memory 테이블 즉시 사용 가능");

                RunDelayCalls();   // deferred targeted persistence flush — the test never calls SaveAssets

                Assert.IsTrue(EditorUtility.IsDirty(sentinel), "무관 센티넬 — 여전히 dirty (전역 저장 금지, F07)");
                Assert.AreEqual(sentinelBytes, ReadIfExists(sentinelPath), "무관 센티넬 — 디스크 바이트 불변");

                Assert.IsFalse(EditorUtility.IsDirty(prefab), "각인 프리팹 — 자기 자산 지속 완료");
                string yaml = File.ReadAllText(prefabPath);
                StringAssert.Contains(marker.PrefabId.ToString(), yaml, "각인값이 디스크에 기록됨");
                var table = AssetDatabase.LoadAssetAtPath<UniNetPrefabTable>(tablePath);
                Assert.IsFalse(EditorUtility.IsDirty(table), "베이크 테이블 — 자기 자산 지속 완료");
            }
            finally
            {
                // cleanup — test-owned assets only; the user's table is rebaked without the temp prefab and
                // byte-compared back to its original content
                AssetDatabase.DeleteAsset(sentinelPath);
                AssetDatabase.DeleteAsset(prefabPath);
                rebuild.Invoke(null, new object[] { null });   // table rebuild drops the deleted temp prefab
                RunDelayCalls();
                Assert.AreEqual(tableBefore, ReadIfExists(tablePath), "기존 베이크 테이블 바이트 복원 (정리 후)");
                AssetDatabase.DeleteAsset(TempDir);
                UniNetPrefabTable.ResetCache();
            }
        }
    }
}
