using Unity.Multiplayer.Playmode;
using UnityEngine;

namespace Arena
{
    /// <summary>Arena network role — the scene bootstrap starts UniNet in this role.</summary>
    public enum ArenaRole
    {
        /// <summary>Detected automatically from MPPM topology and tags (default).</summary>
        Auto,

        /// <summary>Dedicated server — authority simulation only, no local player.</summary>
        Server,

        /// <summary>Client — connects to the server.</summary>
        Client,

        /// <summary>Server + client in one process (quick single-person demo).</summary>
        Host,
    }

    /// <summary>
    /// Role resolution — priority: CLI `--arena-role=server|client` (ARENA-001 — player builds have no MPPM
    /// topology to read, so the switch is the only deterministic role source there and wins in every environment)
    /// → MPPM player tags (UniNetServer/UniNetClient/UniNetHost) → MPPM topology (main editor = server,
    /// virtual players = clients). The editor MPPM path keeps its original priority when the switch is absent.
    /// </summary>
    internal static class ArenaRoleResolver
    {
        public const string ServerTag = "UniNetServer";
        public const string ClientTag = "UniNetClient";
        public const string HostTag = "UniNetHost";
        private const string RoleSwitch = "--arena-role=";

        public static ArenaRole Resolve(ArenaRole configured)
        {
            if (configured != ArenaRole.Auto)
                return configured;

            // ARENA-001 — explicit command-line role, checked before the Auto (MPPM) logic. The editor 2proc runner
            // also uses --arena-role=host|client; "host" is intentionally unmapped here (falls through to MPPM),
            // so the runner's own parsing stays the single owner of that value.
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith(RoleSwitch, System.StringComparison.Ordinal)) continue;
                var value = arg.Substring(RoleSwitch.Length);
                if (value == "server") return ArenaRole.Server;
                if (value == "client") return ArenaRole.Client;
                Debug.LogError($"[Arena] --arena-role 값이 잘못됨: '{value}' (server|client만 지원) — MPPM/기본 판정으로 폴백");
                break;
            }

            var tags = CurrentPlayer.ReadOnlyTags();
            if (System.Array.IndexOf(tags, HostTag) >= 0) return ArenaRole.Host;
            if (System.Array.IndexOf(tags, ServerTag) >= 0) return ArenaRole.Server;
            if (System.Array.IndexOf(tags, ClientTag) >= 0) return ArenaRole.Client;

            // In a player build nothing above could decide — surface the misconfiguration loudly (plan: 미지정
            // LogError) and fall back to Client (the previous deterministic default). A resolver cannot own
            // application exit; the smoke driver's timeout turns a wrong-role run into a visible FAIL.
            if (!Application.isEditor)
                Debug.LogError("[Arena] --arena-role=server|client 미지정 — 빌드에서는 Auto 판정 근거(MPPM)가 없어 Client로 폴백합니다. 서버는 --arena-role=server 로 실행하세요.");

            return CurrentPlayer.IsMainEditor ? ArenaRole.Server : ArenaRole.Client;
        }
    }
}
