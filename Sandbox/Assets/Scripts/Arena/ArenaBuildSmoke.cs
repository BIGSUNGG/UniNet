using System;
using UniNet.Core.Hosting;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// Player-build smoke verifier (ARENA-001) — a static driver, NOT a gameplay component. It activates only when
    /// the process command line contains --arena-smoke (a player-build-only switch: the editor process never has
    /// it, so editor flows — play mode, MPPM, the editor 2proc runner — are untouched).
    ///
    /// It rides the EXISTING demo flow with minimal intrusion: it drives no network calls of its own and only
    /// judges markers from what the demo already does —
    ///   ① own avatar spawns and the InitialOnly name arrives (client-side: DisplayName is never set locally)
    ///   ② the owned client fires (TryFire) → the authoritative ammo check → the OwnerOnly decrease round-trips
    ///   ③ the server applies damage (ServerApplyDamage) → death RepNotify + killfeed (ClientRpc) + FX (MulticastRpc)
    /// Both sides end by logging "[ARENA-BUILD-2PROC] PASS" on success; a timeout or a broken marker logs FAIL.
    /// </summary>
    internal static class ArenaBuildSmoke
    {
        public const string Marker = "[ARENA-BUILD-2PROC]";
        private const float TimeoutSeconds = 90f;   // generous: covers slow first-frame player startup on CI-class hardware

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Arm()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!string.Equals(arg, "--arena-smoke", StringComparison.Ordinal)) continue;
                new GameObject("ArenaBuildSmokeDriver").AddComponent<SmokeDriver>();
                return;   // smoke mode armed exactly once per process
            }
        }

        /// <summary>Per-frame state machine; the phase advances only when the marker for the current step is observed.</summary>
        private sealed class SmokeDriver : MonoBehaviour
        {
            private float _deadline;
            private int _phase;
            private bool _fired;
            private ArenaPlayer _mine;
            private bool _finished;

            private void Start() => _deadline = Time.unscaledTime + TimeoutSeconds;

            private void Update()
            {
                if (_finished) return;
                if (Time.unscaledTime > _deadline)
                {
                    Debug.LogError($"{Marker} FAIL — 시간 초과 (phase={_phase} role={(UniNetEnvironment.Server != null ? "server" : "client")})");
                    _finished = true;
                    return;
                }

                // The scene bootstrap owns startup — the driver only observes once UniNet is actually running.
                var server = UniNetEnvironment.Server;
                if (server != null) ServerDrive();
                else if (UniNetEnvironment.Client != null) ClientDrive();
            }

            // ---------------------------------------------------------------- Server side

            private void ServerDrive()
            {
                var player = FirstPlayer();
                if (player == null) return;

                if (_phase == 0)
                {
                    // ① spawn (the bootstrap spawns on connection). InitialOnly propagation is judged on the client —
                    // the server sets the name locally, so there is nothing to observe here.
                    Debug.Log($"{Marker} SERVER-AVATAR-SPAWNED name={player.DisplayName} netId={player.NetId}");
                    _phase = 1;
                }

                if (_phase == 1)
                {
                    // ② the client's fire request processed by the authoritative ammo check (cooldown/ammo validated server-side)
                    if (player.HudAmmo >= ArenaConfig.MaxAmmo) return;
                    Debug.Log($"{Marker} SERVER-AMMO-DECREASED ammo={player.HudAmmo}");
                    _phase = 2;
                }

                if (_phase == 2)
                {
                    // ③ authority-path damage + killfeed broadcast. MulticastRpc death FX also runs locally on the
                    // server, so the local FxExplosion is the server-side round-trip proof; the killfeed (ClientRpc)
                    // is observable on the client only.
                    player.ServerApplyDamage(ArenaConfig.MaxHp, crit: false);
                    player.BroadcastKillFeed(player.DisplayName, player.DisplayName);
                    Debug.Log($"{Marker} SERVER-DAMAGE-APPLIED hp={player.HudHp}");
                    _phase = 3;
                }

                if (_phase == 3)
                {
                    if (FindNamed("FxExplosion") == null) return;   // local MulticastRpc death FX
                    Debug.Log($"{Marker} PASS (server) — 스폰·발사 처리·사망 관측 완료");
                    _finished = true;
                }
            }

            // ---------------------------------------------------------------- Client side

            private void ClientDrive()
            {
                if (_phase == 0)
                {
                    // ① own avatar + InitialOnly name — the client never writes DisplayName locally, so a real
                    // (non-default) name on the owned replica proves the spawn baseline carried it.
                    _mine = OwnedPlayer();
                    if (_mine == null) return;
                    if (string.IsNullOrEmpty(_mine.DisplayName) || _mine.DisplayName == "Player")
                    {
                        Fail("InitialOnly 이름 미도달 (owned avatar name이 기본값)");
                        return;
                    }
                    Debug.Log($"{Marker} CLIENT-AVATAR-SPAWNED name={_mine.DisplayName}");
                    _phase = 1;
                }

                if (_mine == null) { _mine = OwnedPlayer(); if (_mine == null) return; }

                if (_phase == 1)
                {
                    // ② fire once → the OwnerOnly ammo decrease must round-trip back from the authority
                    if (!_fired)
                    {
                        _mine.TryFire(1f, 0f, UniNetTime.Now);
                        _fired = true;
                    }
                    if (_mine.HudAmmo >= ArenaConfig.MaxAmmo) return;
                    Debug.Log($"{Marker} CLIENT-AMMO-DECREASED ammo={_mine.HudAmmo}");
                    _phase = 2;
                }

                if (_phase == 2)
                {
                    // ③ server-applied death observed via RepNotify (HP drop / hit text), then the ClientRpc
                    // killfeed and the MulticastRpc death FX complete the round trip.
                    bool dead = _mine.HudHp <= 0 || ArenaHud.LastHitText.Length > 0;   // LastHitText persists — survives a fast respawn between polls
                    if (!dead) return;
                    if (string.IsNullOrEmpty(ArenaHud.LastKillFeed)) return;
                    if (FindNamed("FxExplosion") == null) return;
                    Debug.Log($"{Marker} PASS — 아바타+이름·탄약 왕복·피해→킬피드/FX 왕복 (killfeed={ArenaHud.LastKillFeed})");
                    _finished = true;
                }
            }

            /// <summary>Terminal failure — a broken marker will not repair itself by waiting longer.</summary>
            private void Fail(string why)
            {
                _finished = true;
                Debug.LogError($"{Marker} FAIL — {why}");
            }

            private static ArenaPlayer FirstPlayer()
            {
                var players = FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                return players.Length > 0 ? players[0] : null;
            }

            private static ArenaPlayer OwnedPlayer()
            {
                foreach (var p in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (p.IsOwner)
                        return p;
                return null;
            }

            private static GameObject FindNamed(string name)
            {
                foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (t.name == name)
                        return t.gameObject;
                return null;
            }
        }
    }
}
