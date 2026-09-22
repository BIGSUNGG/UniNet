using System;
using System.Collections;
using NUnit.Framework;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using Arena;

namespace UniNet.Tests
{
    /// <summary>
    /// Host round-trip verification of the arena game scenario — asserts over a real loopback RUDP connection:
    /// the three-RPC chain, dynamic spawn/destroy, replication application, RepNotify, the kill flow, and respawn.
    /// Cross-client checks (SkipOwner propagation, OwnerOnly non-propagation, InitialOnly spawn propagation)
    /// are covered by ArenaTwoProcessRunner (2 clients).
    /// </summary>
    public sealed class ArenaRoundtripTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 0;   // random port per run — works around environments where a listener socket lingers in the editor process after play mode ends

        [SetUp]
        public void DisableSceneBootstrap()
        {
            // Tests run on top of the Arena scene; the scene bootstrap would otherwise adopt the test server
            // as its own and destroy players it considers "surplus" — tests set up their own environment
            // (a disabled component's Update is never called)
            int disabled = 0;
            foreach (var boot in UnityEngine.Object.FindObjectsByType<ArenaBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                boot.enabled = false;
                disabled++;
            }
            UnityEngine.Debug.Log($"[ArenaDBG] SetUp — 씬 부트스트랩 비활성화 {disabled}개");
        }

        [TearDown]
        public void StopListeners()
        {
            // Clean up listeners/connections left between tests (idempotent)
            UniNetManager.HostStop();
        }

        [UnityTest]
        public IEnumerator 호스트_아레나_왕복_스폰_RPC_리플리케이션_킬플로우()
        {
            // Start the host — the server and client connect over a real loopback socket
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            // Server — dynamically spawn two players (CreatePlayer clones, configures, registers, and broadcasts;
            // InitialOnly values ride the spawn baseline via the configure callback)
            // LocalInputEnabled=false — keeps the host client's local keyboard handler from overriding programmatic input
            var alpha = CreatePlayer("Alpha", 123, new Vector3(0f, 0.5f, 0f));
            var bravo = CreatePlayer("Bravo", 456, new Vector3(5f, 0.5f, 0f));
            alpha.LocalInputEnabled = false;
            bravo.LocalInputEnabled = false;

            // Client registration + ownership wait — the only connection (host client) owns the first spawn (Alpha)
            yield return WaitUntil(() => alpha.IsOwner, 5, "Alpha 소유권 (IsOwner)");
            Assert.AreEqual("Alpha", alpha.DisplayName, "InitialOnly 이름이 스폰 상태에 실렸다");

            // 1) Move ServerRpc — client sends → server implements → authoritative movement
            alpha.SubmitMoveInput(1, 0);
            yield return WaitUntil(() => alpha.transform.position.x > 0.3f, 5, "이동 ServerRpc → 서버 이동");
            alpha.SubmitMoveInput(0, 0);
            yield return new WaitForSecondsRealtime(0.2f);
            float alphaX = alpha.transform.position.x;

            // 2) Damage — direct server-authoritative path (same API bullets use) + RepNotify observation
            ArenaHud.LastHitText = "";
            bravo.ServerApplyDamage(ArenaConfig.Damage, crit: false);
            bravo.ServerApplyDamage(ArenaConfig.Damage, crit: false);
            Assert.AreEqual(ArenaConfig.MaxHp - ArenaConfig.Damage * 2, bravo.HudHp, "피해 적용");
            yield return WaitUntil(() => ArenaHud.LastHitText.Contains("Bravo"), 5, "RepNotify(HP) 관찰");

            // 3) Fire ServerRpc — ammo decrease (OwnerOnly) + bullet dynamic-spawn propagation
            alpha.TryFire(1f, 0f, UniNet.Core.Hosting.UniNetTime.Now);   // straight along +x — toward Bravo at (5,0) (P4 hit-scan)
            yield return WaitUntil(() => CountNamed("FxTracer") > 0, 5, "히트스캔 트레이서");
            yield return WaitUntil(() => alpha.HudAmmo == ArenaConfig.MaxAmmo - 1, 5, "탄약 감소 (OwnerOnly 델타)");

            // 4) Sustained fire → death → kill credit + ClientRpc kill feed
            //    Keep firing at the server cooldown rate (0.25s of game time) so death is guaranteed regardless of
            //    crit randomness. Waits are based on game time (accumulated dt) — the simulation (bullet travel,
            //    respawn timers, cooldowns) advances by game dt, so even if editor throttling skews wall-clock time
            //    the game-time upper bound stays physically accurate.
            yield return WaitForSim(
                () =>
                {
                    if (bravo.IsDead) return true;
                    if (alpha.HudAmmo > 0)
                        alpha.TryFire(1f, 0f, UniNet.Core.Hosting.UniNetTime.Now);   // calls inside the server cooldown are rejected authoritatively by the server
                    return false;
                },
                8f, 20000, "연사 → 사망");
            yield return WaitForSim(() => alpha.HudScore == 1, 3f, 6000, "킬 크레딧 (점수 리플리케이션)");
            yield return WaitForSim(() => ArenaHud.LastKillFeed == "Alpha ▶ Bravo", 3f, 6000, "ClientRpc 킬피드 수신");

            // 5) Tracer FX despawn (unreliable FX — removes itself after its lifetime)
            yield return WaitForSim(() => CountNamed("FxTracer") == 0, 3f, 6000, "트레이서 FX 소멸");

            // 6) Respawn — HP restored (delta replication). RespawnDelay 2s counts in game time
            yield return WaitForSim(() => !bravo.IsDead && bravo.HudHp == ArenaConfig.MaxHp, 10f, 30000, "리스폰");

            // Confirm stopping after movement — the server keeps applying the last input (0,0)
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.LessOrEqual(Mathf.Abs(alpha.transform.position.x - alphaX), 0.5f, "정지 입력 적용");
        }

        private static int CountNamed(string name)
        {
            int count = 0;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (t.name == name) count++;
            return count;
        }

        private static ArenaPlayer CreatePlayer(string name, int seed, Vector3 pos)
        {
            // Clone-spawn from a template — private [Replicated] initialization (InitServerState) rides the spawn baseline via the configure callback
            var template = new GameObject("TestPlayer_" + name);
            template.transform.position = pos;
            template.AddComponent<ArenaPlayer>();
            var go = UniNetManager.NetworkInstantiate(template, clone => clone.GetComponent<ArenaPlayer>().InitServerState(name, seed));
            UnityEngine.Object.Destroy(template);
            return go.GetComponent<ArenaPlayer>();
        }

        private static IEnumerator WaitUntil(Func<bool> condition, float timeout, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"대기 시간 초과: {what}");
                yield return null;
            }
        }

        /// <summary>Waits under a dual cap of game time (accumulated dt) and frame count — because the simulation advances by game dt
        /// this bound stays physically accurate even under editor throttling that skews dt against wall-clock time.</summary>
        private static IEnumerator WaitForSim(Func<bool> condition, float maxSimSeconds, int maxFrames, string what)
        {
            float sim = 0f;
            int frames = 0;
            while (!condition())
            {
                sim += Time.deltaTime;
                if (sim > maxSimSeconds)
                    Assert.Fail($"게임 시간 상한 초과({sim:0.00}s/{maxSimSeconds}s, {frames}프레임): {what}");
                if (++frames > maxFrames)
                    Assert.Fail($"프레임 상한 초과({frames}): {what}");
                yield return null;
            }
        }
    }
}
