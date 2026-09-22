namespace Arena
{
    /// <summary>Arena simulation constants — the single source of truth shared by scene visuals, server logic, and tests.</summary>
    internal static class ArenaConfig
    {
        /// <summary>Network port (opened by the main editor server).</summary>
        public const int Port = 7777;

        /// <summary>Half extent of the playable area — the floor is a (-Half..Half) square.</summary>
        public const float Half = 18f;

        /// <summary>Player movement speed (units/second).</summary>
        public const float MoveSpeed = 5f;

        /// <summary>Player collision radius (pillar and boundary avoidance checks).</summary>
        public const float PlayerRadius = 0.5f;

        /// <summary>Max HP.</summary>
        public const int MaxHp = 100;

        /// <summary>Max ammo.</summary>
        public const int MaxAmmo = 8;

        /// <summary>Seconds to regenerate one round of ammo.</summary>
        public const float AmmoRegenSeconds = 1.5f;

        /// <summary>Fire cooldown (seconds).</summary>
        public const float FireCooldown = 0.25f;

        /// <summary>Base damage — four hits to kill.</summary>
        public const int Damage = 25;

        /// <summary>Crit check — 2x damage when seed % CritEvery == 0.</summary>
        public const int CritEvery = 4;

        /// <summary>Bullet speed (units/second).</summary>
        public const float BulletSpeed = 12f;

        /// <summary>Bullet lifetime (seconds) — bullets destroy themselves on expiry.</summary>
        public const float BulletLifetime = 2f;

        /// <summary>P3-④ bullet replication frequency (Hz) — 30 Hz suffices for trajectories (player state replicates every tick). UE NetUpdateFrequency equivalent.</summary>
        public const float BulletUpdateFrequencyHz = 30f;

        /// <summary>P3-① bullet visibility cull distance (world-unit radius) — distant connections stop tracking bullet trajectories. UE NetCullDistance equivalent.</summary>
        public const float BulletCullDistance = 24f;

        /// <summary>P4-⑤ per-tick send budget for the player type (bytes) — per-type bandwidth management example (retargeted from bullets to players after the P4 switch).</summary>
        public const int PlayerChannelBudgetPerTickBytes = 512;

        /// <summary>P3-② player replication priority — under bandwidth pressure, player state is sent before lower-priority types (default 1). UE NetPriority equivalent.</summary>
        public const float PlayerNetworkPriority = 2f;

        /// <summary>P4-① remote object interpolation delay (seconds) — rendering is delayed this far behind server time for smooth motion.</summary>
        public const float InterpolationDelay = 0.12f;

        /// <summary>P4-② rewind window (seconds) — hit times outside this past/future window are clamped (trust boundary — rejects excessive rewinds).</summary>
        public const double RewindWindowSeconds = 1.0;

        /// <summary>P4-② hitscan range (world units) — shots beyond range stop at the tracer endpoint.</summary>
        public const float HitscanRange = 40f;

        /// <summary>Hit check distance = bullet collision radius + player radius.</summary>
        public const float HitDistance = 1.0f;

        /// <summary>Respawn delay after death (seconds).</summary>
        public const float RespawnDelay = 2f;

        /// <summary>Four spawn points (x, z).</summary>
        public static readonly (float X, float Z)[] SpawnPoints =
        {
            (-12f, -12f), (12f, -12f), (12f, 12f), (-12f, 12f),
        };

        /// <summary>Four central pillars — (center x, center z, half-width). Bullets and players avoid them in server simulation.</summary>
        public static readonly (float X, float Z, float Half)[] Pillars =
        {
            (-7f, -7f, 1.5f), (7f, -7f, 1.5f), (7f, 7f, 1.5f), (-7f, 7f, 1.5f),
        };

        /// <summary>Unique display names by spawn order — identify avatars independently of ownership reassignment (round-robin).</summary>
        public static readonly string[] DisplayNames = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot" };
    }
}
