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
    /// 아레나 게임 시나리오 호스트 왕복 검증 — 실제 루프백 RUDP로 RPC 3종 체인·동적 스폰/파괴·
    /// 리플리케이션 적용·RepNotify·킬플로우·리스폰을 단언한다.
    /// 교차 클라 검증(SkipOwner 전파·OwnerOnly 비전파·InitialOnly 스폰 전파)은
    /// ArenaTwoProcessRunner(2클라이언트)가 담당한다.
    /// </summary>
    public sealed class ArenaRoundtripTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 0;   // 실행별 랜덤 포트 — 플레이 모드 종료 후 리스너 소켓이 에디터 프로세스에 잔존하는 환경 문제 회피

        [SetUp]
        public void DisableSceneBootstrap()
        {
            // 테스트는 Arena 씬 위에서 실행되므로 씬의 부트스트랩이 테스트 서버를 자기 관리 대상으로
            // 잘못 잡아 플레이어를 "잉여"로 파괴할 수 있다 — 테스트는 자기 환경을 직접 셋업한다 (disabled 컴포넌트의 Update는 미호출)
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
            // 테스트 간 잔존 리스너/접속 정리 (멱등)
            UniNetManager.HostStop();
        }

        [UnityTest]
        public IEnumerator 호스트_아레나_왕복_스폰_RPC_리플리케이션_킬플로우()
        {
            // 호스트 시작 — 서버 + 클라가 실제 루프백 소켓으로 연결된다
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            // 서버 — 플레이어 2 동적 스폰 (InitialOnly 값은 Spawn 전에 세팅)
            // LocalInputEnabled=false — 호스트 클라의 로컬 키보드 입력 핸들러가 프로그램 입력을 덮어쓰지 않게 한다
            var alpha = CreatePlayer("Alpha", 123, new Vector3(0f, 0.5f, 0f));
            var bravo = CreatePlayer("Bravo", 456, new Vector3(5f, 0.5f, 0f));
            alpha.LocalInputEnabled = false;
            bravo.LocalInputEnabled = false;
            UniNetManager.Spawn(alpha.gameObject);
            UniNetManager.Spawn(bravo.gameObject);

            // 클라 등록 + 소유권 대기 — 유일 연결(호스트 클라)이 첫 스폰(Alpha)을 소유한다
            yield return WaitUntil(() => alpha.IsOwner, 5, "Alpha 소유권 (IsOwner)");
            Assert.AreEqual("Alpha", alpha.DisplayName, "InitialOnly 이름이 스폰 상태에 실렸다");

            // 1) 이동 ServerRpc — 클라 송신 → 서버 구현 → 권위 이동
            alpha.SubmitMoveInput(1, 0);
            yield return WaitUntil(() => alpha.transform.position.x > 0.3f, 5, "이동 ServerRpc → 서버 이동");
            alpha.SubmitMoveInput(0, 0);
            yield return new WaitForSecondsRealtime(0.2f);
            float alphaX = alpha.transform.position.x;

            // 2) 피해 — 서버 권위 직접 경로 (총알이 쓰는 것과 동일 API) + RepNotify 관찰
            ArenaHud.LastHitText = "";
            bravo.ServerApplyDamage(ArenaConfig.Damage, crit: false);
            bravo.ServerApplyDamage(ArenaConfig.Damage, crit: false);
            Assert.AreEqual(ArenaConfig.MaxHp - ArenaConfig.Damage * 2, bravo.HudHp, "피해 적용");
            yield return WaitUntil(() => ArenaHud.LastHitText.Contains("Bravo"), 5, "RepNotify(HP) 관찰");

            // 3) 발사 ServerRpc — 탄약 감소(OwnerOnly) + 총알 동적 스폰 전파
            alpha.TryFire(1f, 0f, UniNet.Core.Hosting.UniNetTime.Now);   // +x 직진 — Bravo(5,0) 방향 (P4 히트스캔)
            yield return WaitUntil(() => CountNamed("FxTracer") > 0, 5, "히트스캔 트레이서");
            yield return WaitUntil(() => alpha.HudAmmo == ArenaConfig.MaxAmmo - 1, 5, "탄약 감소 (OwnerOnly 델타)");

            // 4) 연사 → 사망 → 킬 크레딧 + ClientRpc 킬피드
            //    치명타 여부(seed 무작위)와 무관하게 사망을 보장하기 위해 서버 쿨다운(0.25s 게임시간) 속도로 계속 발사한다.
            //    대기는 게임 시간(dt 합산) 기반 — 시뮬레이션(총알 이동·리스폰 타이머·쿨다운)은 게임 dt로 진행되므로
            //    에디터 스로틀링으로 실제 시간과 왜곡되어도 게임 시간 기준 상한이 물리적으로 정확하다
            yield return WaitForSim(
                () =>
                {
                    if (bravo.IsDead) return true;
                    if (alpha.HudAmmo > 0)
                        alpha.TryFire(1f, 0f, UniNet.Core.Hosting.UniNetTime.Now);   // 서버 쿨다운에 걸리는 호출은 서버가 권위로 거부한다
                    return false;
                },
                8f, 20000, "연사 → 사망");
            yield return WaitForSim(() => alpha.HudScore == 1, 3f, 6000, "킬 크레딧 (점수 리플리케이션)");
            yield return WaitForSim(() => ArenaHud.LastKillFeed == "Alpha ▶ Bravo", 3f, 6000, "ClientRpc 킬피드 수신");

            // 5) 트레이서 FX 소멸 (비신뢰 FX — 수명 뒤 스스로 제거)
            yield return WaitForSim(() => CountNamed("FxTracer") == 0, 3f, 6000, "트레이서 FX 소멸");

            // 6) 리스폰 — HP 복구 (델타 리플리케이션). RespawnDelay 2s는 게임 시간 기준
            yield return WaitForSim(() => !bravo.IsDead && bravo.HudHp == ArenaConfig.MaxHp, 10f, 30000, "리스폰");

            // 이동 후 정지 확인 — 서버가 마지막 입력(0,0)을 유지한다
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
            var go = new GameObject("TestPlayer_" + name);
            go.transform.position = pos;
            var player = go.AddComponent<ArenaPlayer>();
            player.InitServerState(name, seed);
            return player;
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

        /// <summary>게임 시간(dt 합산) + 프레임 수 이중 상한 대기 — 시뮬레이션은 게임 dt로 진행되므로
        /// 에디터 스로틀링(실제 시간과 dt 왜곡) 환경에서도 물리적으로 정확한 상한이 된다.</summary>
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
