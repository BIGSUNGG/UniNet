using Unity.Multiplayer.Playmode;

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
    /// Role resolution — priority: inspector override → MPPM player tags (UniNetServer/UniNetClient/UniNetHost)
    /// → MPPM topology (main editor = server, virtual players = clients).
    /// </summary>
    internal static class ArenaRoleResolver
    {
        public const string ServerTag = "UniNetServer";
        public const string ClientTag = "UniNetClient";
        public const string HostTag = "UniNetHost";

        public static ArenaRole Resolve(ArenaRole configured)
        {
            if (configured != ArenaRole.Auto)
                return configured;

            var tags = CurrentPlayer.ReadOnlyTags();
            if (System.Array.IndexOf(tags, HostTag) >= 0) return ArenaRole.Host;
            if (System.Array.IndexOf(tags, ServerTag) >= 0) return ArenaRole.Server;
            if (System.Array.IndexOf(tags, ClientTag) >= 0) return ArenaRole.Client;

            return CurrentPlayer.IsMainEditor ? ArenaRole.Server : ArenaRole.Client;
        }
    }
}
