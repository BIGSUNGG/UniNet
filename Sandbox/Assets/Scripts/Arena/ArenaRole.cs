using Unity.Multiplayer.Playmode;

namespace Arena
{
    /// <summary>아레나 네트워크 역할 — 씬 부트스트랩이 이 역할로 UniNet을 시작한다.</summary>
    public enum ArenaRole
    {
        /// <summary>MPPM 구성·태그로 자동 판별한다 (기본값).</summary>
        Auto,

        /// <summary>전용 서버 — 권위 시뮬레이션만, 로컬 플레이어 없음.</summary>
        Server,

        /// <summary>클라이언트 — 서버에 접속한다.</summary>
        Client,

        /// <summary>서버+클라이언트 한 프로세스 (1인 빠른 데모용).</summary>
        Host,
    }

    /// <summary>
    /// 역할 판별 — 우선순위: 인스펙터 강제값 → MPPM 플레이어 태그(UniNetServer/UniNetClient/UniNetHost)
    /// → MPPM 토폴로지(메인 에디터=서버, 가상 플레이어=클라이언트).
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
