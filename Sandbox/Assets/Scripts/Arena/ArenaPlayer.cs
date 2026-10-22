using System;
using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace Arena
{
    /// <summary>Combat stance demo enum (GEN-003 example) — int-backed; on the wire it is byte-identical to its underlying int.</summary>
    internal enum ArenaStance { Idle, Guard, Chase }

    /// <summary>
    /// Arena player — demonstrates all implemented UniNet features (P1 RPC + P2 replication + P3 policies + P4 hooks) in one class.
    ///
    /// [Replicated] condition coverage:
    /// - Position      : handled by the NetworkTransform component (same object, separate slot — ADR-0010); no condition, synced to all clients
    /// - _hp           : RepNotify — all clients observe HP changes via callback (hit flash)
    /// - _score        : RepNotify — score popup
    /// - _ammo         : OwnerOnly + RepNotify — my ammo is sent only to me
    /// - _aimYaw       : SkipOwner — I render my own aim locally, so it is sent only to others
    /// - _displayName,
    ///   _colorSeed    : InitialOnly — sent once at spawn; later changes are not replicated
    ///
    /// RPC coverage:
    /// - Two ServerRpcs (aim, fire) + _Validate trust-boundary checks — move input streams through the NetworkTransform simulation (ADR-0038)
    /// - MulticastRpc — tracer and hit FX (unreliable), death (reliable) — runs locally on everyone including the server
    /// - ClientRpc — kill feed (only server calls are broadcast)
    ///
    /// P3 replication policy coverage:
    /// - NetworkPriority (P3-②) — player state is sent before lower-priority types
    /// - NetworkDormant + FlushNetworkDormancy (P3-③) — dormant from after the death delta until respawn;
    ///   the full respawn state (HP, ammo, position) then replicates in one burst
    ///
    /// P4 hook coverage:
    /// - Movement is handled by the NetworkTransform component (library) — server-authoritative replication + owner-client prediction/reconciliation + remote interpolation.
    ///   The game only injects the movement rule (ArenaMovement.StepNet) into the component (ADR-0013).
    /// - Position history and rewind (P4-②) — NetworkRewindHistory target; fire checks rewind the target to the sender's hit time
    /// </summary>
    [RequireComponent(typeof(NetworkTransform))]
    public sealed partial class ArenaPlayer : NetworkBehaviour
    {
        [Replicated(Notify = nameof(OnHpChanged))] private int _hp = ArenaConfig.MaxHp;

        [Replicated(Notify = nameof(OnScoreChanged))] private int _score;

        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnAmmoChanged))]
        private int _ammo = ArenaConfig.MaxAmmo;

        [Replicated(ReplicateCondition.SkipOwner)] private float _aimYaw;

        [Replicated(ReplicateCondition.InitialOnly)] private string _displayName = "Player";

        [Replicated(ReplicateCondition.InitialOnly)] private int _colorSeed;

        // ---- Server-only state (never replicated) ----
        private NetworkTransform _nt;   // P4 — movement prediction component (position replication, prediction, reconciliation, interpolation)
        private MoveSimulation<ArenaMovement.MoveInput, ArenaMovement.MoveState> _sim;   // ADR-0038 — game input/state + fixed-step simulation
        private float _nextFireAllowed;
        private float _nextAmmoRegen;
        private float _respawnAt;
        private ArenaStance _stance;   // GEN-003 demo — enum RPC parameter, server-side copy (read via ServerStance)
        private bool _dormancyArmed;   // P3-③ — enter dormancy on the next server tick, after the death delta has gone out

        // ---- Local (client) state ----
        private Transform _body;
        private float _lastSentAim;
        private bool _aimSent;

        /// <summary>
        /// Whether local keyboard/mouse input is processed. Turn off when tests or the two-process verifier
        /// drive players with programmatic input (so IsOwner local input does not override it).
        /// </summary>
        internal bool LocalInputEnabled { get; set; } = true;

        private void Awake()
        {
            // P4 — inject the game's movement simulation (input/state structs + pure step rule) into the prediction
            // component (ADR-0038). Server authority and client prediction share the same rule; jump/dash state rides
            // inside the simulation, so rollback restores it exactly.
            _nt = GetComponent<NetworkTransform>();
            _nt.NetworkUpdateFrequencyHz = ArenaConfig.PlayerReplicationHz;   // 30 Hz — two 60 Hz sim steps merge into one position delta
            _sim = new MoveSimulation<ArenaMovement.MoveInput, ArenaMovement.MoveState>(
                ArenaMovement.Step, ArenaMovement.WriteInput, ArenaMovement.ReadInput,
                ArenaMovement.WriteState, ArenaMovement.ReadState, ArenaConfig.MoveSimHz);
            _nt.Simulation = _sim;
            BuildVisuals();
        }

        /// <summary>Called by the server while spawning — set InitialOnly values before the spawn so they ride on it.
        /// The spawn transform is absorbed by NetworkTransform (it replicates once with the spawn; later movement is the component's job).</summary>
        public void InitServerState(string displayName, int colorSeed)
        {
            _displayName = displayName;
            _colorSeed = colorSeed;
            gameObject.name = "ArenaPlayer_" + displayName;

            // P3-② priority — under bandwidth pressure, player state is sent before lower-priority types (default 1)
            NetworkPriority = ArenaConfig.PlayerNetworkPriority;
            NetworkUpdateFrequencyHz = ArenaConfig.PlayerReplicationHz;   // 30 Hz — game state (HP·ammo·score) does not need per-tick delivery either

            // P4-② lag compensation — the server records this object's position history (rewind target for fire checks)
            NetworkRewindHistory = true;
        }

        /// <summary>Avatar display name (for HUD and kill feed — replicated as InitialOnly).</summary>
        public string DisplayName => _displayName;

        /// <summary>Replicated-value getter for the HUD — reads the authoritative value on the server, the replicated value on clients.</summary>
        public int HudHp => _hp;

        /// <summary>Replicated-value getter for the HUD.</summary>
        public int HudAmmo => _ammo;

        /// <summary>Last stance the server accepted (GEN-003 enum RPC example — keeps the demo state observable).</summary>
        internal ArenaStance ServerStance => _stance;

        /// <summary>Replicated-value getter for the HUD.</summary>
        public int HudScore => _score;

        /// <summary>Aim angle observer (verifies SkipOwner propagation — read by the two-process verifier).</summary>
        internal float HudAim => _aimYaw;

        // ---- Input submission wrappers (tests and the two-process verifier submit input on behalf of the local player — RPCs stay private) ----

        /// <summary>Sets the local move input (client → server stream) — the simulation keeps applying it every fixed step until changed (tests and the two-process verifier drive players this way).</summary>
        internal void SubmitMoveInput(sbyte dx, sbyte dz) => SubmitMoveInput(new ArenaMovement.MoveInput { Dx = dx, Dz = dz });

        /// <summary>Sets the full move input — jump/dash buttons included (ADR-0038 input struct).</summary>
        internal void SubmitMoveInput(in ArenaMovement.MoveInput input) => _sim.SetInput(input);

        /// <summary>Requests a teleport to a random spawn point (demonstrates the ADR-0038 teleport re-anchor — state resets, prediction re-anchors).</summary>
        internal void TryTeleport() => RpcTeleport();

        /// <summary>Submits the local aim angle (client → server).</summary>
        internal void SubmitAimInput(float yaw) => RpcSubmitAim(yaw);

        /// <summary>Submits a fire request (client → server) — P4-② the sender also sends the server time it aimed at (lag compensation).</summary>
        internal void TryFire(float dirX, float dirY, double hitTime) => RpcFire(dirX, dirY, hitTime);

        /// <summary>Assembles the avatar from primitive shapes with no assets (body + gun). Identical on client and server.</summary>
        private void BuildVisuals()
        {
            var root = new GameObject("Body");
            root.transform.SetParent(transform, false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "BodyMesh";
            UnityEngine.Object.Destroy(body.GetComponent<Collider>());   // hit checks use server-side distance math only
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.9f, 0.6f, 0.9f);
            _body = body.transform;

            var gun = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gun.name = "Gun";
            UnityEngine.Object.Destroy(gun.GetComponent<Collider>());
            gun.transform.SetParent(root.transform, false);
            gun.transform.localPosition = new Vector3(0f, 0f, 0.55f);
            gun.transform.localScale = new Vector3(0.18f, 0.18f, 0.7f);
        }

        private void Update()
        {
            if (IsServer)
                ServerTick();   // game logic (respawn timer, dormancy, ammo) — movement runs on the NetworkTransform component's tick

            ApplyVisualState();
            if (IsOwner && IsClient && LocalInputEnabled)
                HandleLocalInput();
        }

        // ---------------------------------------------------------------- Server simulation (authority)

        private void ServerTick()
        {
            if (_respawnAt > 0f)
            {
                if (Time.time >= _respawnAt)
                    Respawn();
                else if (_dormancyArmed)
                {
                    // P3-③ dormancy — after the death delta goes out on the first tick, stop delta comparison and sending until respawn
                    // (switching on the next server tick guarantees the death HP propagates regardless of ordering with the driver tick)
                    _dormancyArmed = false;
                    NetworkDormant = true;
                }
                return;
            }

            // movement runs on the NetworkTransform component's fixed server step (ArenaMovement.Step injected via Simulation)

            // ammo regen (OwnerOnly delta — only the owning client receives it)
            if (_ammo < ArenaConfig.MaxAmmo && Time.time >= _nextAmmoRegen)
            {
                _ammo++;
                _nextAmmoRegen = Time.time + ArenaConfig.AmmoRegenSeconds;
            }
        }

        /// <summary>Server-authoritative damage — called directly by the server-side hitscan check. Clamps the amount as trust-boundary defense.</summary>
        internal void ServerApplyDamage(int amount, bool crit)
        {
            if (_respawnAt > 0f) return;
            int clamped = Mathf.Clamp(amount, 0, ArenaConfig.MaxHp);
            if (crit) clamped *= 2;
            _hp = Mathf.Max(0, _hp - clamped);
            RpcPlayHitFx(transform.position.x, transform.position.z, crit);

            if (_hp <= 0)
            {
                _respawnAt = Time.time + ArenaConfig.RespawnDelay;
                _dormancyArmed = true;          // P3-③ — enter dormancy on the next server tick
                _nt.SimulationEnabled = false;  // P4-③ — death: stop both server and predicted movement
                RpcPlayDeathFx(transform.position.x, transform.position.z, _displayName);
            }
        }

        /// <summary>Kill credit — the hitscan check grants this to the owner after confirming the kill (server only).</summary>
        internal void CreditKill()
        {
            _score++;
        }

        /// <summary>Broadcasts the kill feed — RPCs stay private to the class and are exposed as intent-revealing game APIs.</summary>
        internal void BroadcastKillFeed(string killerName, string victimName)
        {
            RpcShowKillFeed(killerName, victimName);
        }

        /// <summary>Death check — the hitscan uses this for immediate kill-credit decisions.</summary>
        internal bool IsDead => _respawnAt > 0f || _hp <= 0;

        private void Respawn()
        {
            _respawnAt = 0f;
            _dormancyArmed = false;
            _hp = ArenaConfig.MaxHp;
            _ammo = ArenaConfig.MaxAmmo;
            var (x, z) = ArenaConfig.SpawnPoints[UnityEngine.Random.Range(0, ArenaConfig.SpawnPoints.Length)];
            _nt.SetNetworkPosition(new Vector3(x, 0.5f, z));   // P4 — set the network position (replicates to all clients next tick)
            _nt.SimulationEnabled = true;   // P4 — server-side gate re-enable (OnHpChanged only fires on clients — review round 1)
            FlushNetworkDormancy();   // P3-③ — wake from dormancy; the full respawn state (HP, ammo, position) propagates in one burst next tick
        }

        // ---------------------------------------------------------------- Local input (owning client)

        private void HandleLocalInput()
        {
            // Movement (ADR-0038) — set the typed input every frame; the component streams it to the server at the fixed
            // simulation rate. Jump/dash buttons ride the same input struct (edges are detected inside the rule via state).
            _sim.SetInput(new ArenaMovement.MoveInput
            {
                Dx = (sbyte)((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0)),
                Dz = (sbyte)((Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0)),
                Jump = Input.GetKey(KeyCode.Space),
                Dash = Input.GetKey(KeyCode.LeftShift),
            });

            // Teleport — T snaps to a random spawn point (state reset + prediction re-anchor demo)
            if (Input.GetKeyDown(KeyCode.T))
                TryTeleport();

            // Aim — mouse direction. Send only on ≥3° change (SkipOwner: sent to others only)
            var mouseWorld = MouseWorld();
            if (mouseWorld.HasValue)
            {
                float yaw = Mathf.Atan2(mouseWorld.Value.x - transform.position.x, mouseWorld.Value.z - transform.position.z) * Mathf.Rad2Deg;
                if (!_aimSent || Mathf.Abs(Mathf.DeltaAngle(yaw, _lastSentAim)) > 3f)
                {
                    _aimSent = true;
                    _lastSentAim = yaw;
                    RpcSubmitAim(yaw);
                    ApplyAimVisual(yaw);
                }
            }

            // Fire — left mouse button. The server judges cooldown authoritatively, but spam is also suppressed locally
            if (Input.GetMouseButton(0) && Time.time >= _nextFireAllowed)
            {
                if (mouseWorld.HasValue)
                {
                    var dir = mouseWorld.Value - transform.position;
                    float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
                    if (len > 0.01f)
                    {
                        _nextFireAllowed = Time.time + ArenaConfig.FireCooldown;
                        RpcFire(dir.x / len, dir.z / len, UniNetTime.Now);   // P4-② attach the sender's aim time — the server performs the rewind check
                    }
                }
            }
        }

        /// <summary>Mouse world position from the top-down camera (y=0 plane).</summary>
        private static Vector3? MouseWorld()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(Vector3.up, Vector3.zero);
            return plane.Raycast(ray, out float enter) ? ray.GetPoint(enter) : (Vector3?)null;
        }

        // ---------------------------------------------------------------- Visuals (everyone)

        private void ApplyVisualState()
        {
            if (_body == null) return;

            // InitialOnly color — valid on clients from the moment the spawn state is applied
            float hue = (_colorSeed % 1000) / 1000f;
            var renderer = _body.GetComponent<Renderer>();
            if (renderer != null && !_body.name.EndsWith(hue.ToString("0.000")))
            {
                renderer.sharedMaterial = new Material(renderer.sharedMaterial)
                {
                    color = Color.HSVToRGB(hue, 0.7f, 0.95f),
                };
                _body.name = "BodyMesh_" + hue.ToString("0.000");
            }

            if (!IsOwner)
                ApplyAimVisual(_aimYaw);   // others' aim lines use the value received via SkipOwner
        }

        private void ApplyAimVisual(float yaw)
        {
            if (_body == null) return;
            _body.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // ---------------------------------------------------------------- RepNotify (client callbacks)

        private void OnHpChanged(int prevHp)
        {
            if (_hp < prevHp)
                ArenaHud.NotifyHit(_displayName, prevHp - _hp);   // flash only on decrease (heals stay silent)

            // P4-③ — sync the owner-client prediction gate (movement prediction also stops on death, resumes on respawn)
            if (IsOwner)
                _nt.SimulationEnabled = _hp > 0;
        }

        private void OnScoreChanged(int prevScore)
        {
            if (_score > prevScore)
                ArenaHud.NotifyScore(_displayName, _score - prevScore);
        }

        private void OnAmmoChanged(int prevAmmo)
        {
            // my ammo changed — the HUD reads fields every frame, so this only notes the shot (demo)
            if (_ammo < prevAmmo)
                ArenaHud.NotifyShot();
        }

        // ---------------------------------------------------------------- RPC declarations (UniNet source generator targets)

        /// <summary>Client → server aim angle (degrees) — propagated to others only via SkipOwner.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcSubmitAim(float yaw);

        /// <summary>Client → server fire request (unit direction vector + the server time the sender aimed at). The server authoritatively judges cooldown, ammo, and rewind.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcFire(float dirX, float dirY, double hitTime);

        /// <summary>Client → server teleport request (ADR-0038 teleport demo) — the server judges everything; the requester only triggers it.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcTeleport();

        /// <summary>Client → server stance request — GEN-003 example: an enum parameter rides its underlying integer's codec (no extra validation needed; range checks would belong in a _Validate).</summary>
        [ServerRpc]
        private partial void RpcSubmitStance(ArenaStance stance);

        /// <summary>Server → everyone hitscan tracer. Loss-tolerant (unreliable) — the next shot's tracer naturally replaces it.</summary>
        [MulticastRpc(Delivery.Unreliable)]
        private partial void RpcPlayTracer(float fromX, float fromZ, float toX, float toZ, int colorSeed);

        /// <summary>Server → everyone hit FX. Unreliable.</summary>
        [MulticastRpc(Delivery.Unreliable)]
        private partial void RpcPlayHitFx(float x, float y, bool crit);

        /// <summary>Server → everyone death explosion. Reliable (default) — must be seen.</summary>
        [MulticastRpc]
        private partial void RpcPlayDeathFx(float x, float y, string name);

        /// <summary>Server → all clients kill feed (not run on the server — same as UE ClientRpc).</summary>
        [ClientRpc]
        private partial void RpcShowKillFeed(string killerName, string victimName);

        // ---------------------------------------------------------------- RPC implementations

        private Task<bool> RpcSubmitAim_Validate(float yaw)
            => Task.FromResult(!float.IsNaN(yaw) && !float.IsInfinity(yaw) && Mathf.Abs(yaw) <= 3600f);

        private void RpcSubmitAim_Implementation(float yaw)
        {
            _aimYaw = yaw;   // SkipOwner — not sent to the owning client (it already renders its own aim locally)
        }

        private Task<bool> RpcFire_Validate(float dirX, float dirY, double hitTime)
            => Task.FromResult(IsFinite01(dirX) && IsFinite01(dirY)
                               && !double.IsNaN(hitTime) && !double.IsInfinity(hitTime)
                               && hitTime <= UniNetTime.Now + 1.0);   // reject future aim times (trust boundary — the server clamps the past)

        private Task<bool> RpcTeleport_Validate() => Task.FromResult(true);

        private void RpcSubmitStance_Implementation(ArenaStance stance)
        {
            _stance = stance;   // demo-only state — no gameplay consumer yet
        }

        private void RpcTeleport_Implementation()
        {
            var (x, z) = ArenaConfig.SpawnPoints[UnityEngine.Random.Range(0, ArenaConfig.SpawnPoints.Length)];
            _nt.SetNetworkPosition(new Vector3(x, ArenaConfig.GroundY, z));   // ADR-0038 — teleport: state reset + owner re-anchor
        }

        private void RpcFire_Implementation(float dirX, float dirY, double hitTime)
        {
            if (_respawnAt > 0f) return;
            if (Time.time < _nextFireAllowed) return;          // fire-rate cooldown (server authority)
            if (_ammo <= 0) return;                            // ammo check (server authority)

            _ammo--;
            _nextFireAllowed = Time.time + ArenaConfig.FireCooldown;
            _nextAmmoRegen = Time.time + ArenaConfig.AmmoRegenSeconds;

            // P4-② lag compensation — rewind the target to when the sender aimed and check against that position.
            // Trust boundary: hitTime is clamped to the rewind window (rejects excessive past/future rewinds)
            double rewindTime = Math.Clamp(hitTime, UniNetTime.Now - ArenaConfig.RewindWindowSeconds, UniNetTime.Now);

            var origin = new Vector2(transform.position.x, transform.position.z);   // authoritative coordinates synced by NetworkTransform
            var dir = new Vector2(dirX, dirY);
            ArenaPlayer hitTarget = null;
            float bestT = float.MaxValue;
            var end = origin + dir * ArenaConfig.HitscanRange;

            foreach (var player in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (player == this || player.IsDead) continue;

                // rewind — the target's past position (targets without history return their current position)
                player.GetHistoryPosition(rewindTime, out float hx, out float hy, out float hz);

                // segment-circle check (2D XZ) — closest-point projection (dir is a unit vector)
                float ox = hx - origin.x;
                float oz = hz - origin.y;
                float t = Mathf.Clamp(ox * dir.x + oz * dir.y, 0f, ArenaConfig.HitscanRange);
                float cx = origin.x + dir.x * t - hx;
                float cz = origin.y + dir.y * t - hz;
                if (cx * cx + cz * cz > ArenaConfig.HitDistance * ArenaConfig.HitDistance) continue;
                if (t < bestT)
                {
                    bestT = t;
                    hitTarget = player;
                    end = origin + dir * t;
                }
            }

            int shotSeed = UnityEngine.Random.Range(1, int.MaxValue);
            if (hitTarget != null)
            {
                bool crit = shotSeed % ArenaConfig.CritEvery == 0;
                hitTarget.ServerApplyDamage(ArenaConfig.Damage, crit);
                if (hitTarget.IsDead)
                {
                    CreditKill();
                    BroadcastKillFeed(DisplayName, hitTarget.DisplayName);
                }
            }

            RpcPlayTracer(origin.x, origin.y, end.x, end.y, shotSeed);   // Multicast (unreliable) — local FX on everyone
        }

        private static bool IsFinite01(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Mathf.Abs(v) <= 1.01f;

        private void RpcPlayTracer_Implementation(float fromX, float fromZ, float toX, float toZ, int colorSeed)
        {
            ArenaFx.Tracer(new Vector3(fromX, 0.5f, fromZ), new Vector3(toX, 0.5f, toZ), ArenaFx.SeedToColor(colorSeed));
        }

        private void RpcPlayHitFx_Implementation(float x, float y, bool crit)
        {
            ArenaFx.Flash(new Vector3(x, 0.6f, y), crit ? Color.yellow : Color.white, crit ? 0.8f : 0.5f);
        }

        private void RpcPlayDeathFx_Implementation(float x, float y, string name)
        {
            ArenaFx.Explosion(new Vector3(x, 0.6f, y));
            Debug.Log($"[Arena] {name} 사망");
        }

        private void RpcShowKillFeed_Implementation(string killerName, string victimName)
        {
            ArenaHud.AddKillFeed(killerName, victimName);
        }
    }
}
