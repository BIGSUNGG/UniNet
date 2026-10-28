using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UniNet.Tests
{
    /// <summary>F08 — exercise actual runtime destruction. A runtime-only MonoBehaviour does not receive OnDestroy
    /// in EditMode; manually invoking that method would not prove Unity's real lifecycle subscription symmetry.</summary>
    public sealed class DriverLifetimePlayTests
    {
        [Test]
        public void 실제_driver_파괴와_재생성은_sceneLoaded_구독을_누적하지_않는다()
        {
            Assert.IsTrue(Application.isPlaying, "실제 runtime OnDestroy 경계에서 검증한다");
            var type = typeof(NetworkBehaviour).Assembly.GetType("UniNet.Unity.UniNetDriver");
            var ensure = type.GetMethod("Ensure", BindingFlags.Public | BindingFlags.Static);
            var instance = type.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
            var sceneMethod = type.GetMethod("OnSceneLoaded", BindingFlags.NonPublic | BindingFlags.Static);
            var backing = typeof(SceneManager).GetField("sceneLoaded", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(backing);
            int Count() => (backing.GetValue(null) as Delegate)?.GetInvocationList().Count(d => Equals(d.Method, sceneMethod)) ?? 0;
            void DestroyDriver()
            {
                var driver = instance.GetValue(null) as MonoBehaviour;
                if (driver != null) Object.DestroyImmediate(driver.gameObject);
            }
            var clockBefore = UniNetTime.LocalClock;
            int before = Count();
            bool hadDriver = instance.GetValue(null) is MonoBehaviour existing && existing != null;
            try
            {
                ensure.Invoke(null, null);
                int withDriver = Count();
                Assert.AreEqual(hadDriver ? before : before + 1, withDriver);
                ensure.Invoke(null, null);
                Assert.AreEqual(withDriver, Count(), "반복 Ensure는 멱등이다");
                DestroyDriver();
                Assert.AreEqual(withDriver - 1, Count(), "실제 Unity OnDestroy가 구독을 해제한다");
                ensure.Invoke(null, null);
                Assert.AreEqual(withDriver, Count(), "재생성은 구독 한 개만 복원한다");
            }
            finally
            {
                if (!hadDriver) DestroyDriver();
                else ensure.Invoke(null, null);
                UniNetTime.LocalClock = clockBefore;
            }
        }
    }
}
