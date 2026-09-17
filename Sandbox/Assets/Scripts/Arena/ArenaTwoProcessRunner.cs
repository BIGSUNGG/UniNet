using System;
using System.Diagnostics;
using System.Threading;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Arena
{
    /// <summary>
    /// 아레나 2-프로세스 검증기 — 실제 프로세스 간 RUDP로 교차 클라 기능을 검증한다.
    /// 호스트(서버+클라A) + 원격 클라이언트(B) = 클라 2 접속 토폴로지.
    ///
    /// 검증 항목 (마커 [ARENA-2PROC]):
    /// - 클라 2 접속·동적 플레이어 2 스폰 전파·소유권 분배(라운드로빈)
    /// - InitialOnly 스폰 전파(이름) — 후발 클라가 스폰 상태로 합류
    /// - SkipOwner 조각 전파 — non-owner(B)만 조준각을 수신
    /// - OwnerOnly 비전파 — non-owner(B)는 탄약 감소를 수신하지 않음
    /// - 피해→사망 리플리케이션 + RepNotify, ClientRpc 킬피드, MulticastRpc FX, 파괴 전파
    ///
    /// 위치 이동 리플리케이션(서버 틱)은 PlayMode 호스트 테스트(ArenaRoundtripTests)가 담당 —
    /// 배치모드는 MonoBehaviour Update가 동작하지 않아 시뮬레이션 틱이 없다 (RPC·리플리케이션 계층만 검증).
    ///
    /// 실행: Unity.exe -batchmode -projectPath &lt;사본&gt; -executeMethod Arena.ArenaTwoProcessRunner.Run
    ///       --arena-role=host|client  (실행 방법은 Document/examples/arena-shooter.md)
    /// </summary>
    public static class ArenaTwoProcessRunner
    {
        private const int Port = 7821;

        public static void Run()
        {
            // batchmode -executeMethod는 RuntimeInitializeOnLoadMethod가 실행되지 않아 명시 등록 (멱등) — 기존 TwoProcessRunner와 동일
            global::UniNet.Generated.__UniNetRegistration.Register();

            string role = null;
            foreach (var arg in Environment.GetCommandLineArgs())
                if (arg.StartsWith("--arena-role=", StringComparison.Ordinal))
                    role = arg.Substring("--arena-role=".Length);

            if (role == "host") RunHost();
            else if (role == "client") RunClient();
            else Debug.LogError("[ARENA-2PROC] FAIL — --arena-role=host|client 미지정");
        }

        // ---------------------------------------------------------------- 호스트 (서버 + 클라A)

        private static void RunHost()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            Wait(hostTask, 15, "호스트 리슨");
            if (hostTask.IsFaulted) { Debug.LogError("[ARENA-2PROC] HOST-ABORT " + hostTask.Exception); return; }

            // 클라 2 접속 대기 (호스트 클라A + 원격 클라B) — batchmode 초기화 시간까지 여유 있게
            if (!WaitFor(() => LiveConnections() == 2, 150, "클라 2 접속")) return;
            Debug.Log("[ARENA-2PROC] HOST-TWO-CLIENTS");

            // 스폰은 접속 2명 확정 후 — 라운드로빈 소유권이 A/B에 각각 배정된다
            var alpha = CreatePlayer("Alpha", 123, new Vector3(0f, 0.5f, 0f));
            var bravo = CreatePlayer("Bravo", 456, new Vector3(5f, 0.5f, 0f));
            alpha.LocalInputEnabled = false;   // 호스트 클라의 로컬 입력이 프로그램 입력을 덮어쓰지 않게 한다
            bravo.LocalInputEnabled = false;
            UniNetManager.Spawn(alpha.gameObject);
            UniNetManager.Spawn(bravo.gameObject);

            // 클라A 소유 확인 (라운드로빈 — 첫 스폰 = 첫 연결)
            if (!WaitFor(() => alpha.IsOwner, 10, "클라A의 Alpha 소유")) return;

            // 조준 제출 — SkipOwner: B만 수신해야 한다
            alpha.SubmitAimInput(45f);
            PumpFor(2.0);   // SkipOwner 델타 전파 보장 (B의 대기 시간과 무관하게 전송 완료)

            // 발사 — OwnerOnly 탄약 감소 + 총알 동적 스폰 전파
            WaitFor(() => UniNetEnvironment.Client.LocalConnId != 0, 10, "클라A welcome");
            ThreadSleep(1.0);   // 스폰 전파 여유
            alpha.TryFire(1f, 0f);
            if (!WaitFor(() => alpha.HudAmmo == ArenaConfig.MaxAmmo - 1, 10, "클라A 탄약 감소")) return;

            // 피해 → 사망 → 킬피드 (총알 비행 없이 권위 경로로 직접 — 배치모드는 틱이 없다)
            bravo.ServerApplyDamage(ArenaConfig.MaxHp, crit: false);
            alpha.BroadcastKillFeed(alpha.DisplayName, bravo.DisplayName);

            // 델타·ClientRpc 전파 여유 — TickReplication을 돌리며 대기 (종료 전 전파 완료)
            PumpFor(3.0);

            Debug.Log($"[ARENA-2PROC] HOST-DONE ammoA={alpha.HudAmmo} aimA={alpha.HudAim} " +
                      $"hpB={bravo.HudHp} scoreA={alpha.HudScore} killfeed={ArenaHud.LastKillFeed}");
        }

        // ---------------------------------------------------------------- 클라이언트 (B)

        private static void RunClient()
        {
            var clientTask = UniNetManager.ClientAsync("127.0.0.1", Port);
            Wait(clientTask, 15, "클라 접속");
            if (clientTask.IsFaulted) { Debug.LogError("[ARENA-2PROC] CLIENT-ABORT " + clientTask.Exception); return; }

            if (!WaitFor(() => UniNetEnvironment.Client.LocalConnId != 0, 15, "welcome")) return;
            Debug.Log($"[ARENA-2PROC] CLIENT-CONNECTED connId={UniNetEnvironment.Client.LocalConnId}");

            // 2 플레이어 스폰 + 내 소유 1개
            if (!WaitFor(() => Players().Length == 2, 30, "플레이어 2 스폰 전파")) return;
            ArenaPlayer mine = null, alpha = null, bravo = null;
            foreach (var p in Players())
            {
                switch (p.DisplayName)
                {
                    case "Alpha": alpha = p; break;
                    case "Bravo": bravo = p; break;
                }
                if (p.IsOwner) mine = p;
            }
            if (mine == null || alpha == null || bravo == null)
            {
                Debug.LogError("[ARENA-2PROC] CLIENT-FAIL — 플레이어 식별 불가 (InitialOnly 이름 전파 누락)");
                return;
            }
            if (!ReferenceEquals(mine, bravo))
            {
                Debug.LogError("[ARENA-2PROC] CLIENT-FAIL — 소유권 분배 불일치 (B는 Bravo 소유 예상)");
                return;
            }
            Debug.Log("[ARENA-2PROC] CLIENT-SPAWNED-2 mine=Bravo (InitialOnly 이름 전파 OK)");

            // SkipOwner — non-owner(B)가 Alpha의 조준각을 수신하는가
            if (!WaitFor(() => Mathf.Abs(Mathf.DeltaAngle(alpha.HudAim, 45f)) < 0.5f, 15, "SkipOwner 조준 전파(B 수신)")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-SKIP-OWNER-AIM OK aim=" + alpha.HudAim);

            // OwnerOnly — non-owner(B)는 Alpha의 탄약 감소를 수신하지 않는다
            PumpFor(3.0);   // Alpha 발사 전파 여유 (무조건 로그 없는 대기)
            if (alpha.HudAmmo != ArenaConfig.MaxAmmo)
            {
                Debug.LogError("[ARENA-2PROC] CLIENT-FAIL — OwnerOnly 탄약이 B에 전파됨 ammo=" + alpha.HudAmmo);
                return;
            }
            Debug.Log("[ARENA-2PROC] CLIENT-OWNER-ONLY-AMMO OK ammo=" + alpha.HudAmmo);

            // 총알 동적 스폰 전파
            if (!WaitFor(() => Bullets().Length >= 1, 15, "총알 스폰 전파")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-BULLET-SPAWNED");

            // 피해 → 사망 리플리케이션 + RepNotify
            if (!WaitFor(() => bravo.HudHp == 0, 15, "사망 리플리케이션")) return;
            if (!WaitFor(() => ArenaHud.LastHitText.Contains("Bravo"), 15, "RepNotify(HP) 관찰")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-DAMAGE-REPLICATED hitText=" + ArenaHud.LastHitText);

            // ClientRpc 킬피드
            if (!WaitFor(() => ArenaHud.LastKillFeed == "Alpha ▶ Bravo", 15, "ClientRpc 킬피드")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-KILLFEED OK");

            // MulticastRpc — 사망 폭발 FX가 클라에도 생성된다
            if (!WaitFor(() => FindNamed("FxExplosion") != null, 15, "MulticastRpc FX")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-MULTICAST-FX OK");

            Debug.Log("[ARENA-2PROC] CLIENT-DONE");
        }

        // ---------------------------------------------------------------- 공용

        private static ArenaPlayer CreatePlayer(string name, int seed, Vector3 pos)
        {
            var go = new GameObject("Arena2Proc_" + name);
            go.transform.position = pos;
            var player = go.AddComponent<ArenaPlayer>();
            player.InitServerState(name, seed);
            return player;
        }

        private static ArenaPlayer[] Players()
            => UnityEngine.Object.FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        private static ArenaBullet[] Bullets()
            => UnityEngine.Object.FindObjectsByType<ArenaBullet>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        private static GameObject FindNamed(string name)
        {
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (t.name == name)
                    return t.gameObject;
            return null;
        }

        private static int LiveConnections()
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return 0;
            int n = 0;
            foreach (var conn in server.SnapshotConnections())
                if (!conn.Disconnected)
                    n++;
            return n;
        }

        private static void Wait(System.Threading.Tasks.Task task, double seconds, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!task.IsCompleted && sw.Elapsed.TotalSeconds < seconds)
            {
                UniNetEnvironment.PumpMain();
                Thread.Sleep(20);
            }
        }

        private static bool WaitFor(Func<bool> condition, double seconds, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                var server = UniNetEnvironment.Server;
                if (server != null)
                    server.TickReplication();

                if (condition())
                    return true;
                Thread.Sleep(20);
            }
            Debug.LogError($"[ARENA-2PROC] FAIL — 대기 시간 초과: {what}");
            return false;
        }

        private static void ThreadSleep(double seconds)
            => System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));

        /// <summary>로그 없는 전파 대기 — PumpMain + 서버 리플리케이션 틱을 지정 시간 동안 구동한다.</summary>
        private static void PumpFor(double seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                UniNetEnvironment.PumpMain();
                var server = UniNetEnvironment.Server;
                if (server != null)
                    server.TickReplication();
                Thread.Sleep(20);
            }
        }
    }
}
