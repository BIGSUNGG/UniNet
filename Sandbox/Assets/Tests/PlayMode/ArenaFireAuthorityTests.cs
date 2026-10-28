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
    /// AUDIT-20261027-F09 regression — Arena fire authority on the REAL component path: the client submits
    /// RpcFire through TryFire (real generated ServerRpc), the server validates and implements. The per-component
    /// (≤1) direction check alone let (1,1) reach sqrt(2)×HitscanRange because the hitscan projection assumed a
    /// unit vector — the server implementation now normalizes any nonzero direction, so a client-sent magnitude
    /// cannot scale the authoritative range. Targets are placed far OUTSIDE the playable square with their
    /// NetworkTransform simulation disabled so the simulation and the transform agree (the movement sim would
    /// otherwise clamp them back into the ±Half arena) — no ambient avatars/pillars exist at those coordinates.
    /// Rendering is not involved.
    /// </summary>
    public sealed class ArenaFireAuthorityTests
    {
        [SetUp]
        public void DisableSceneBootstrap()
        {
            foreach (var boot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                boot.enabled = false;   // never adopt the test runtime or manage players away
        }

        [TearDown]
        public void StopHost()
        {
            UniNetManager.HostStop();   // idempotent — sweeps test-spawned dynamics
        }

        [UnityTest]
        public IEnumerator 발사_검증은_대각선_방향_크기로_사거리를_확장하지_못한다()
        {
            int port = TestPortAllocator.Next();
            var hostTask = UniNetManager.HostAsync(port);
            yield return WaitUntil(() => hostTask.IsCompleted, 10, "호스트 시작");
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            const float off = 300f;   // far from the ±18 arena, the pillars (±7) and every spawn point — ambient-free
            var shooter = CreateStaticPlayer("Shooter", new Vector3(off, ArenaConfig.GroundY, off));
            var farTarget = CreateStaticPlayer("FarTarget", new Vector3(off + 39.7f, ArenaConfig.GroundY, off + 39.7f));   // diagonal ≈ 56.1 = 1.40×HitscanRange — the OLD math hit this
            var nearTarget = CreateStaticPlayer("NearTarget", new Vector3(off + 20f, ArenaConfig.GroundY, off));          // straight ahead, in range — positive control
            yield return WaitUntil(() => shooter.IsOwner, 5, "사수 소유권");

            int hp0Far = farTarget.HudHp, hp0Near = nearTarget.HudHp, ammo0 = shooter.HudAmmo;

            // 1) diagonal (1,1): per-component valid, but the normalized server math must NOT reach 1.4×range
            shooter.TryFire(1f, 1f, UniNetTime.Now);
            yield return WaitUntil(() => shooter.HudAmmo == ammo0 - 1, 5, "대각선 발사 처리 (탄약 소모)");
            Assert.AreEqual(hp0Far, farTarget.HudHp, "대각선 사거리 밖 표적 — HP 불변 (F09 정규화)");

            // Control this instance's server deadlines rather than racing wall-clock sleeps against a 0.25 s
            // cooldown. RPC validation/dispatch/implementation remain the actual component path.
            var cooldown = typeof(ArenaPlayer).GetField("_nextFireAllowed", BindingFlags.Instance | BindingFlags.NonPublic);
            var regen = typeof(ArenaPlayer).GetField("_nextAmmoRegen", BindingFlags.Instance | BindingFlags.NonPublic);
            cooldown.SetValue(shooter, Time.time + 10f);
            regen.SetValue(shooter, float.PositiveInfinity);
            var validate = typeof(ArenaPlayer).GetMethod("RpcFire_Validate", BindingFlags.Instance | BindingFlags.NonPublic);
            var zeroVerdict = (System.Threading.Tasks.Task<bool>)validate.Invoke(shooter, new object[] { 0f, 0f, UniNetTime.Now });
            Assert.IsFalse(zeroVerdict.GetAwaiter().GetResult(), "실제 검증 훅은 0 방향을 거부한다");
            shooter.TryFire(0f, 0f, UniNetTime.Now);
            yield return null;
            yield return null;
            Assert.AreEqual(ammo0 - 1, shooter.HudAmmo, "영(0) 방향 거부 — 탄약 미소모");

            shooter.TryFire(1f, 0f, UniNetTime.Now);
            yield return null;
            yield return null;
            Assert.AreEqual(ammo0 - 1, shooter.HudAmmo, "서버 cooldown이 남은 발사는 탄약을 소비하지 않는다");

            // Release only this instance's deadline, then exercise an actual in-range positive shot.
            cooldown.SetValue(shooter, 0f);
            shooter.TryFire(1f, 0f, UniNetTime.Now);
            yield return WaitUntil(() => shooter.HudAmmo == ammo0 - 2, 5, "사거리 내 발사 처리");
            yield return WaitUntil(() => nearTarget.HudHp < hp0Near, 5, "사거리 내 명중 — HP 감소");
            Assert.AreEqual(hp0Far, farTarget.HudHp, "대각선 표적은 계속 불변");
        }

        /// <summary>Spawns a real prefab player far outside the arena and freezes it in place (sim disabled BEFORE the
        /// first tick, so the ±Half clamp never runs and the simulation/transform stay exactly at the spawn pose —
        /// the hitscan origin/target coordinates are the coordinates under test).</summary>
        private static ArenaPlayer CreateStaticPlayer(string name, Vector3 pos)
        {
            var go = UniNetManager.NetworkInstantiate(TestPrefabs.Load(TestPrefabs.ArenaPlayerPrefab), pos, Quaternion.identity,
                clone => clone.GetComponent<ArenaPlayer>().InitServerState(name, name.GetHashCode()));
            var player = go.GetComponent<ArenaPlayer>();
            player.LocalInputEnabled = false;   // no local input overrides the programmatic fire
            go.GetComponent<NetworkTransform>().enabled = false;   // same frame — never clamped back into the arena
            return player;
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
