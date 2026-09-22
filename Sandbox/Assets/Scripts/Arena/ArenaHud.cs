using UnityEngine;

namespace Arena
{
    /// <summary>
    /// 아레나 HUD — OnGUI 기반 (에셋·UI 패키지 없이). 클라이언트는 내 상태·네임플레이트·킬피드,
    /// 서버는 연결·오브젝트 통계를 표시한다. 킬피드는 ClientRpc 구현이 채운다.
    /// </summary>
    public sealed class ArenaHud : MonoBehaviour
    {
        private static readonly string[] KillFeed = new string[4];
        private static readonly float[] KillFeedAt = new float[4];
        private static ArenaRole _role = ArenaRole.Auto;
        private static float _hitFlashUntil;
        private static string _hitFlashText = "";
        private static float _scorePopupUntil;
        private static string _scorePopupText = "";

        /// <summary>마지막 킬피드 — ClientRpc 수신 관찰용 (테스트·검증이 읽는다).</summary>
        public static string LastKillFeed { get; internal set; } = "";

        /// <summary>마지막 피격 표시 — RepNotify(HP) 관찰용.</summary>
        public static string LastHitText { get; internal set; } = "";

        /// <summary>마지막 점수 팝업 — RepNotify(점수) 관찰용.</summary>
        public static string LastScorePopup { get; internal set; } = "";

        /// <summary>마지막 연결 수명주기 이벤트 — 접속/퇴장 표시·테스트 관찰용.</summary>
        public static string LastLifecycle { get; internal set; } = "";

        private ArenaPlayer[] _players = System.Array.Empty<ArenaPlayer>();
        private ArenaPlayer _mine;
        private float _nextScan;
        private Camera _camera;

        public static void SetRole(ArenaRole role) => _role = role;

        /// <summary>연결 수명주기 이벤트 기록 — HUD 표시·테스트 관찰용.</summary>
        internal static void NoteLifecycle(string text) => LastLifecycle = text;

        /// <summary>RepNotify(HP 감소) — 피격 플래시.</summary>
        public static void NotifyHit(string victimName, int amount)
        {
            _hitFlashUntil = Time.time + 0.35f;
            _hitFlashText = $"{victimName} -{amount}";
            LastHitText = _hitFlashText;
        }

        /// <summary>RepNotify(점수 증가) — 점수 팝업.</summary>
        public static void NotifyScore(string scorerName, int gained)
        {
            _scorePopupUntil = Time.time + 1.2f;
            _scorePopupText = $"{scorerName} +{gained} KILL";
            LastScorePopup = _scorePopupText;
        }

        /// <summary>RepNotify(탄약 감소) — 데모용 로그.</summary>
        public static void NotifyShot() { /* HUD는 매 프레임 필드를 읽는다 */ }

        /// <summary>ClientRpc 구현이 호출 — 킬피드 링에 추가.</summary>
        public static void AddKillFeed(string killerName, string victimName)
        {
            int index = (KillFeedIndex + 1) % KillFeed.Length;
            KillFeedIndex = index;
            KillFeed[index] = $"{killerName} ▶ {victimName}";
            KillFeedAt[index] = Time.time;
            LastKillFeed = KillFeed[index];
        }

        private static int KillFeedIndex;

        private void Update()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 0.5f;

            _players = FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            _mine = null;
            foreach (var player in _players)
            {
                if (player.IsOwner)
                {
                    _mine = player;
                    break;
                }
            }
        }

        private void OnGUI()
        {
            _camera = _camera != null ? _camera : Camera.main;

            // 서버 통계 (좌상단)
            var server = UniNet.Core.Hosting.UniNetEnvironment.Server;
            if (server != null)
            {
                int conns = 0;
                foreach (var conn in server.SnapshotConnections())
                    if (!conn.Disconnected) conns++;
                GUI.Label(new Rect(10, 10, 360, 24), $"[서버] 연결 {conns} · 오브젝트 {playerCount()}");
            }

            GUI.Label(new Rect(10, 30, 360, 24), $"역할: {_role}" + (_mine == null ? " · 관전" : "") + (string.IsNullOrEmpty(LastLifecycle) ? "" : $"   {LastLifecycle}"));

            if (_mine != null)
            {
                GUI.Label(new Rect(10, 50, 360, 24), $"내 이름: {_mine.DisplayName}   점수: {scoreOf(_mine)}");
                GUI.Label(new Rect(10, 70, 360, 24), $"HP: {hpOf(_mine)} / {ArenaConfig.MaxHp}");
                GUI.Label(new Rect(10, 90, 360, 24), $"탄약: {ammoOf(_mine)} / {ArenaConfig.MaxAmmo}   (WASD 이동 · 마우스 조준 · 좌클릭 발사)");
            }

            // 피격 플래시
            if (Time.time < _hitFlashUntil)
            {
                var full = new Rect(0, 0, Screen.width, Screen.height);
                var prev = GUI.color;
                GUI.color = new Color(1f, 0f, 0f, 0.18f);
                GUI.DrawTexture(full, Texture2D.whiteTexture);
                GUI.color = prev;
                GUI.Label(new Rect(Screen.width / 2f - 60, Screen.height / 2f - 40, 200, 24), _hitFlashText);
            }

            // 점수 팝업
            if (Time.time < _scorePopupUntil)
                GUI.Label(new Rect(Screen.width / 2f - 60, Screen.height / 2f - 80, 200, 24), _scorePopupText);

            // 킬피드 (우상단, 최근 5초)
            float y = 10;
            for (int i = 0; i < KillFeed.Length; i++)
            {
                if (KillFeed[i] == null || Time.time - KillFeedAt[i] > 5f) continue;
                GUI.Label(new Rect(Screen.width - 260, y, 250, 22), KillFeed[i]);
                y += 22;
            }

            // 네임플레이트 — 모든 플레이어 머리 위 (이름 + HP바)
            if (_camera == null) return;
            foreach (var player in _players)
            {
                if (player == null) continue;
                var world = player.transform.position + Vector3.up * 1.1f;
                var screen = _camera.WorldToScreenPoint(world);
                if (screen.z <= 0) continue;
                var rect = new Rect(screen.x - 40, Screen.height - screen.y - 10, 80, 30);
                GUI.Label(new Rect(rect.x, rect.y, rect.width, 18), player.DisplayName);
                GUI.DrawTexture(new Rect(rect.x, rect.y + 16, rect.width * hpOf(player) / (float)ArenaConfig.MaxHp, 4), Texture2D.whiteTexture);
            }
        }

        // 복제 필드 게터 — 서버/클라 모두 같은 필드를 읽는다 (서버는 권위값)
        private static int playerCount()
        {
            int n = 0;
            foreach (var p in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) n++;
            return n;
        }

        private static int hpOf(ArenaPlayer p) => p.HudHp;
        private static int ammoOf(ArenaPlayer p) => p.HudAmmo;
        private static int scoreOf(ArenaPlayer p) => p.HudScore;
    }
}
