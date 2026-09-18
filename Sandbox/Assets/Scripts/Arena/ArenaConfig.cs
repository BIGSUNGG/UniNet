namespace Arena
{
    /// <summary>아레나 시뮬레이션 상수 — 씬 비주얼·서버 로직·테스트가 함께 쓰는 단일 진실 공급원.</summary>
    internal static class ArenaConfig
    {
        /// <summary>네트워크 포트 (메인 에디터 서버가 연다).</summary>
        public const int Port = 7777;

        /// <summary>플레이 가능 영역 절반 크기 — 바닥은 (-Half..Half) 정사각형.</summary>
        public const float Half = 18f;

        /// <summary>플레이어 이동 속도 (유닛/초).</summary>
        public const float MoveSpeed = 5f;

        /// <summary>플레이어 충돌 반경 (기둥·경계 회피 판정).</summary>
        public const float PlayerRadius = 0.5f;

        /// <summary>최대 HP.</summary>
        public const int MaxHp = 100;

        /// <summary>최대 탄약.</summary>
        public const int MaxAmmo = 8;

        /// <summary>탄약 1발 재생 주기 (초).</summary>
        public const float AmmoRegenSeconds = 1.5f;

        /// <summary>발사 쿨다운 (초).</summary>
        public const float FireCooldown = 0.25f;

        /// <summary>기본 데미지 — 4발 사망.</summary>
        public const int Damage = 25;

        /// <summary>치명타 판정 — seed % CritEvery == 0 면 2배.</summary>
        public const int CritEvery = 4;

        /// <summary>총알 속도 (유닛/초).</summary>
        public const float BulletSpeed = 12f;

        /// <summary>총알 수명 (초) — 수명 만료 시 스스로 파괴.</summary>
        public const float BulletLifetime = 2f;

        /// <summary>P3-④ 총알 전송 주기 (Hz) — 궤적은 30Hz면 충분 (플레이어 상태는 매 틱). UE NetUpdateFrequency 상응.</summary>
        public const float BulletUpdateFrequencyHz = 30f;

        /// <summary>P3-① 총알 가시성 컬 거리 (월드 단위 반경) — 멀리 있는 연결은 총알 궤적을 추적하지 않는다. UE NetCullDistance 상응.</summary>
        public const float BulletCullDistance = 24f;

        /// <summary>P3-⑤ 총알 유형 틱당 전송 예산 (바이트) — 총알 홍수가 플레이어 상태 대역폭을 굶기지 않게 한다.</summary>
        public const int BulletChannelBudgetPerTickBytes = 512;

        /// <summary>P3-② 플레이어 리플리케이션 우선순위 — 대역폭 부족 시 총알(기본 1)보다 먼저 전송된다. UE NetPriority 상응.</summary>
        public const float PlayerNetworkPriority = 2f;

        /// <summary>총알 충돌 반경 + 플레이어 반경 = 히트 판정 거리.</summary>
        public const float HitDistance = 1.0f;

        /// <summary>사망 후 리스폰 대기 (초).</summary>
        public const float RespawnDelay = 2f;

        /// <summary>스폰 지점 4곳 (x, z).</summary>
        public static readonly (float X, float Z)[] SpawnPoints =
        {
            (-12f, -12f), (12f, -12f), (12f, 12f), (-12f, 12f),
        };

        /// <summary>중앙 기둥 4개 — (중심 x, 중심 z, 반폭). 총알·플레이어가 서버에서 회피한다.</summary>
        public static readonly (float X, float Z, float Half)[] Pillars =
        {
            (-7f, -7f, 1.5f), (7f, -7f, 1.5f), (7f, 7f, 1.5f), (-7f, 7f, 1.5f),
        };

        /// <summary>스폰 순번 기반 고유 표시 이름 — 소유권 재배정(라운드로빈)과 무관하게 아바타 식별용.</summary>
        public static readonly string[] DisplayNames = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot" };
    }
}
