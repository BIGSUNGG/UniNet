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
    /// Arena two-process verifier — validates cross-client features over real inter-process RUDP.
    /// Host (server + client A) + remote client (B) = two connected clients.
    ///
    /// Checks (marker [ARENA-2PROC]):
    /// - Two clients connect, two dynamic players spawn and replicate, ownership is split (round-robin)
    /// - InitialOnly spawn propagation (names) — the late joiner arrives with full spawn state
    /// - SkipOwner fragment propagation — only the non-owner (B) receives the aim angle
    /// - OwnerOnly non-propagation — the non-owner (B) never receives ammo decreases
    /// - Damage→death replication + RepNotify, ClientRpc kill feed, MulticastRpc FX, destroy propagation
    ///
    /// Position movement replication (server tick) is covered by the PlayMode host test (ArenaRoundtripTests) —
    /// batchmode has no MonoBehaviour Update and thus no simulation tick (only the RPC/replication layers are verified here).
    ///
    /// Run: Unity.exe -batchmode -projectPath &lt;copy&gt; -executeMethod Arena.ArenaTwoProcessRunner.Run
    ///      --arena-role=host|client  (see Document/examples/arena-shooter.md for instructions)
    /// </summary>
    public static class ArenaTwoProcessRunner
    {
        private const int Port = 7821;

        public static void Run()
        {
            // batchmode -executeMethod does not run RuntimeInitializeOnLoadMethod, so register explicitly (idempotent) — same as the existing TwoProcessRunner
            global::UniNet.Generated.__UniNetRegistration.Register();

            string role = null;
            foreach (var arg in Environment.GetCommandLineArgs())
                if (arg.StartsWith("--arena-role=", StringComparison.Ordinal))
                    role = arg.Substring("--arena-role=".Length);

            if (role == "host") RunHost();
            else if (role == "client") RunClient();
            else Debug.LogError("[ARENA-2PROC] FAIL — --arena-role=host|client 미지정");
        }

        // ---------------------------------------------------------------- Host (server + client A)

        private static void RunHost()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            Wait(hostTask, 15, "호스트 리슨");
            if (hostTask.IsFaulted) { Debug.LogError("[ARENA-2PROC] HOST-ABORT " + hostTask.Exception); return; }

            // wait for two clients (host client A + remote client B) — generous timeout for batchmode startup
            if (!WaitFor(() => LiveConnections() == 2, 150, "클라 2 접속")) return;
            Debug.Log("[ARENA-2PROC] HOST-TWO-CLIENTS");

            // spawn after both connections are confirmed — round-robin assigns ownership to A/B (CreatePlayer handles clone, register, and broadcast)
            var alpha = CreatePlayer("Alpha", 123, new Vector3(0f, 0.5f, 0f));
            var bravo = CreatePlayer("Bravo", 456, new Vector3(5f, 0.5f, 0f));
            alpha.LocalInputEnabled = false;   // keep the host client's local input from overriding programmatic input
            bravo.LocalInputEnabled = false;

            // verify client A ownership (round-robin — first spawn goes to the first connection)
            if (!WaitFor(() => alpha.IsOwner, 10, "클라A의 Alpha 소유")) return;

            // submit aim — SkipOwner: only B should receive it
            alpha.SubmitAimInput(45f);
            PumpFor(2.0);   // ensure the SkipOwner delta goes out (independent of B's polling)

            // fire — OwnerOnly ammo decrease + tracer FX propagation
            WaitFor(() => UniNetEnvironment.Client.LocalConnId != 0, 10, "클라A welcome");
            ThreadSleep(1.0);   // grace for spawn propagation
            alpha.TryFire(1f, 0f, UniNet.Core.Hosting.UniNetTime.Now);
            if (!WaitFor(() => alpha.HudAmmo == ArenaConfig.MaxAmmo - 1, 10, "클라A 탄약 감소")) return;

            // damage → death → kill feed (straight through the authority path, no bullet flight — batchmode has no tick)
            bravo.ServerApplyDamage(ArenaConfig.MaxHp, crit: false);
            alpha.BroadcastKillFeed(alpha.DisplayName, bravo.DisplayName);

            // grace for delta and ClientRpc propagation — pump TickReplication while waiting (finish before exit)
            PumpFor(3.0);

            Debug.Log($"[ARENA-2PROC] HOST-DONE ammoA={alpha.HudAmmo} aimA={alpha.HudAim} " +
                      $"hpB={bravo.HudHp} scoreA={alpha.HudScore} killfeed={ArenaHud.LastKillFeed}");
        }

        // ---------------------------------------------------------------- Client (B)

        private static void RunClient()
        {
            var clientTask = UniNetManager.ClientAsync("127.0.0.1", Port);
            Wait(clientTask, 15, "클라 접속");
            if (clientTask.IsFaulted) { Debug.LogError("[ARENA-2PROC] CLIENT-ABORT " + clientTask.Exception); return; }

            if (!WaitFor(() => UniNetEnvironment.Client.LocalConnId != 0, 15, "welcome")) return;
            Debug.Log($"[ARENA-2PROC] CLIENT-CONNECTED connId={UniNetEnvironment.Client.LocalConnId}");

            // two players spawned + exactly one owned by me
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

            // SkipOwner — does the non-owner (B) receive Alpha's aim angle
            if (!WaitFor(() => Mathf.Abs(Mathf.DeltaAngle(alpha.HudAim, 45f)) < 0.5f, 15, "SkipOwner 조준 전파(B 수신)")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-SKIP-OWNER-AIM OK aim=" + alpha.HudAim);

            // OwnerOnly — the non-owner (B) must not receive Alpha's ammo decrease
            PumpFor(3.0);   // grace for Alpha's fire to propagate (silent wait, no log)
            if (alpha.HudAmmo != ArenaConfig.MaxAmmo)
            {
                Debug.LogError("[ARENA-2PROC] CLIENT-FAIL — OwnerOnly 탄약이 B에 전파됨 ammo=" + alpha.HudAmmo);
                return;
            }
            Debug.Log("[ARENA-2PROC] CLIENT-OWNER-ONLY-AMMO OK ammo=" + alpha.HudAmmo);

            // tracer FX propagation
            if (!WaitFor(() => FindNamed("FxTracer") != null, 15, "히트스캔 트레이서 전파")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-HITSCAN-TRACER");

            // damage → death replication + RepNotify
            if (!WaitFor(() => bravo.HudHp == 0, 15, "사망 리플리케이션")) return;
            if (!WaitFor(() => ArenaHud.LastHitText.Contains("Bravo"), 15, "RepNotify(HP) 관찰")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-DAMAGE-REPLICATED hitText=" + ArenaHud.LastHitText);

            // ClientRpc kill feed
            if (!WaitFor(() => ArenaHud.LastKillFeed == "Alpha ▶ Bravo", 15, "ClientRpc 킬피드")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-KILLFEED OK");

            // MulticastRpc — the death explosion FX also spawns on the client
            if (!WaitFor(() => FindNamed("FxExplosion") != null, 15, "MulticastRpc FX")) return;
            Debug.Log("[ARENA-2PROC] CLIENT-MULTICAST-FX OK");

            Debug.Log("[ARENA-2PROC] CLIENT-DONE");
        }

        // ---------------------------------------------------------------- Shared

        private static ArenaPlayer CreatePlayer(string name, int seed, Vector3 pos)
        {
            // clone-from-template spawn — the private [Replicated] init (InitServerState) rides on the spawn baseline via the configure callback
            var template = new GameObject("Arena2Proc_" + name);
            template.transform.position = pos;
            template.AddComponent<ArenaPlayer>();
            var go = UniNetManager.NetworkInstantiate(template, clone => clone.GetComponent<ArenaPlayer>().InitServerState(name, seed));
            UnityEngine.Object.Destroy(template);
            return go.GetComponent<ArenaPlayer>();
        }

        private static ArenaPlayer[] Players()
            => UnityEngine.Object.FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

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

        /// <summary>Silent propagation wait — runs PumpMain and the server replication tick for the given duration.</summary>
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
