using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// 아레나 플레이어 — UniNet 구현 기능(P1 RPC + P2 리플리케이션) 전부를 한 클래스에서 시연한다.
    ///
    /// [Replicated] 조건 커버리지:
    /// - _x, _y          : 조건 없음 — 전 클라 항상 동기화 (서버 권위 이동)
    /// - _hp             : RepNotify — HP 변화를 전 클라가 콜백으로 감지 (피격 플래시)
    /// - _score          : RepNotify — 점수 팝업
    /// - _ammo           : OwnerOnly + RepNotify — 내 탄약은 나에게만 전송
    /// - _aimYaw         : SkipOwner — 나는 내 조준을 로컬로 렌더하므로 남에게만 전송
    /// - _displayName,
    ///   _colorSeed      : InitialOnly — 스폰 시 1회만 전송, 이후 변경 무전파
    ///
    /// RPC 커버리지:
    /// - ServerRpc 3종(입력·조준·발사) + _Validate 신뢰 경계 검증
    /// - MulticastRpc — 발사·피격(비신뢰), 사망(신뢰) — 서버 포함 전원 로컬 실행
    /// - ClientRpc — 킬피드 (서버 호출만 전파)
    /// </summary>
    public sealed partial class ArenaPlayer : NetworkBehaviour
    {
        [Replicated] private float _x;
        [Replicated] private float _y;

        [Replicated(Notify = nameof(OnHpChanged))] private int _hp = ArenaConfig.MaxHp;

        [Replicated(Notify = nameof(OnScoreChanged))] private int _score;

        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnAmmoChanged))]
        private int _ammo = ArenaConfig.MaxAmmo;

        [Replicated(ReplicateCondition.SkipOwner)] private float _aimYaw;

        [Replicated(ReplicateCondition.InitialOnly)] private string _displayName = "Player";

        [Replicated(ReplicateCondition.InitialOnly)] private int _colorSeed;

        // ---- 서버 전용 상태 (복제되지 않는다) ----
        private sbyte _inDx;
        private sbyte _inDy;
        private float _nextFireAllowed;
        private float _nextAmmoRegen;
        private float _respawnAt;

        // ---- 로컬(클라) 상태 ----
        private Transform _body;
        private float _lastSentMove;
        private float _lastSentAim;
        private bool _aimSent;
        private float _lastSentDirTick = -1;

        /// <summary>
        /// 로컬 키보드·마우스 입력 처리 여부. 테스트·2프로세스 검증기가 프로그램 입력으로
        /// 플레이어를 구동할 때 끈다 (IsOwner 로컬 입력이 프로그램 입력을 덮어쓰지 않게).
        /// </summary>
        internal bool LocalInputEnabled { get; set; } = true;

        /// <summary>서버가 스폰 직후 호출 — InitialOnly 값은 Spawn 전에 세팅해야 스폰에 실린다.
        /// 복제 좌표(_x·_y)도 스폰 트랜스폽에서 흡수한다 — 스폰 변환은 1회 전파, 이후 이동은 필드가 담당한다.</summary>
        public void InitServerState(string displayName, int colorSeed)
        {
            _displayName = displayName;
            _colorSeed = colorSeed;
            _x = transform.position.x;
            _y = transform.position.z;
            gameObject.name = "ArenaPlayer_" + displayName;
        }

        /// <summary>아바타 표시 이름 (HUD·킬피드용 — InitialOnly로 전파됨).</summary>
        public string DisplayName => _displayName;

        /// <summary>HUD 표시용 복제값 게터 — 서버는 권위값, 클라는 복제값을 그대로 읽는다.</summary>
        public int HudHp => _hp;

        /// <summary>HUD 표시용 복제값 게터.</summary>
        public int HudAmmo => _ammo;

        /// <summary>HUD 표시용 복제값 게터.</summary>
        public int HudScore => _score;

        /// <summary>조준각 관찰용 (SkipOwner 전파 검증 — 2프로세스 검증기가 읽는다).</summary>
        internal float HudAim => _aimYaw;

        // ---- 입력 제출 래퍼 (테스트·2프로세스 검증기가 로컬 플레이어 입력을 대신 보낸다 — RPC는 private 유지) ----

        /// <summary>로컬 이동 입력 제출 (클라 → 서버).</summary>
        internal void SubmitMoveInput(sbyte dx, sbyte dy) => RpcSubmitMove(dx, dy);

        /// <summary>로컬 조준각 제출 (클라 → 서버).</summary>
        internal void SubmitAimInput(float yaw) => RpcSubmitAim(yaw);

        /// <summary>발사 요청 제출 (클라 → 서버).</summary>
        internal void TryFire(float dirX, float dirY) => RpcFire(dirX, dirY);

        private void Start()
        {
            BuildVisuals();
        }

        /// <summary>에셋 없이 기본 도형으로 아바타를 조립한다 (몸통 + 총구). 클라·서버 동일.</summary>
        private void BuildVisuals()
        {
            var root = new GameObject("Body");
            root.transform.SetParent(transform, false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "BodyMesh";
            Object.Destroy(body.GetComponent<Collider>());   // 판정은 서버 거리 계산만 사용
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.9f, 0.6f, 0.9f);
            _body = body.transform;

            var gun = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gun.name = "Gun";
            Object.Destroy(gun.GetComponent<Collider>());
            gun.transform.SetParent(root.transform, false);
            gun.transform.localPosition = new Vector3(0f, 0f, 0.55f);
            gun.transform.localScale = new Vector3(0.18f, 0.18f, 0.7f);
        }

        private void Update()
        {
            if (IsServer)
            {
                ServerTick();
                transform.position = new Vector3(_x, 0.5f, _y);   // 서버는 권위 좌표를 즉시 반영
            }
            else
            {
                // 클라 — 복제된 좌표를 부드럽게 추종 (보간은 데모 수준: Lerp 고정 계수)
                var target = new Vector3(_x, 0.5f, _y);
                transform.position = Vector3.Lerp(transform.position, target, 12f * Time.deltaTime);
                if ((transform.position - target).sqrMagnitude < 0.0001f)
                    transform.position = target;
            }

            ApplyVisualState();
            if (IsOwner && IsClient && LocalInputEnabled)
                HandleLocalInput();
        }

        // ---------------------------------------------------------------- 서버 시뮬레이션 (권위)

        private void ServerTick()
        {
            if (_respawnAt > 0f)
            {
                if (Time.time >= _respawnAt)
                    Respawn();
                return;
            }

            // 입력 적용 — 기둥 회피는 축 분리 시도로 처리 (막히면 그 축 이동만 취소)
            float dt = Time.deltaTime;
            float nx = Mathf.Clamp(_x + _inDx * ArenaConfig.MoveSpeed * dt, -ArenaConfig.Half, ArenaConfig.Half);
            float ny = Mathf.Clamp(_y + _inDy * ArenaConfig.MoveSpeed * dt, -ArenaConfig.Half, ArenaConfig.Half);

            if (!Blocked(nx, _y)) _x = nx;
            if (!Blocked(_x, ny)) _y = ny;

            // 탄약 재생 (OwnerOnly 델타 — 소유 클라만 수신)
            if (_ammo < ArenaConfig.MaxAmmo && Time.time >= _nextAmmoRegen)
            {
                _ammo++;
                _nextAmmoRegen = Time.time + ArenaConfig.AmmoRegenSeconds;
            }
        }

        /// <summary>기둥 4개와의 충돌 — 확장 AABB 점 포함 판정 (서버 전용).</summary>
        private static bool Blocked(float x, float z)
        {
            foreach (var (px, pz, half) in ArenaConfig.Pillars)
            {
                float ex = half + ArenaConfig.PlayerRadius;
                if (x > px - ex && x < px + ex && z > pz - ex && z < pz + ex)
                    return true;
            }
            return false;
        }

        /// <summary>서버 권위 피해 — 총알(서버)이 직접 호출한다. 신뢰 경계 방어로 범위를 클램프한다.</summary>
        internal void ServerApplyDamage(int amount, bool crit)
        {
            if (_respawnAt > 0f) return;
            int clamped = Mathf.Clamp(amount, 0, ArenaConfig.MaxHp);
            if (crit) clamped *= 2;
            _hp = Mathf.Max(0, _hp - clamped);
            RpcPlayHitFx(_x, _y, crit);

            if (_hp <= 0)
            {
                _respawnAt = Time.time + ArenaConfig.RespawnDelay;
                RpcPlayDeathFx(_x, _y, _displayName);
            }
        }

        /// <summary>킬 크레딧 — 총알이 사망 확정 후 소유자에게 부여한다 (서버 전용).</summary>
        internal void CreditKill()
        {
            _score++;
        }

        /// <summary>킬피드 전파 — RPC는 클래스 내부에 은닉하고 의도를 드러내는 게임 API로 노출한다.</summary>
        internal void BroadcastKillFeed(string killerName, string victimName)
        {
            RpcShowKillFeed(killerName, victimName);
        }

        /// <summary>사망 판정 — 총알이 즉시 크레딧 판단에 쓴다.</summary>
        internal bool IsDead => _respawnAt > 0f || _hp <= 0;

        private void Respawn()
        {
            _respawnAt = 0f;
            _hp = ArenaConfig.MaxHp;
            _ammo = ArenaConfig.MaxAmmo;
            var (x, z) = ArenaConfig.SpawnPoints[Random.Range(0, ArenaConfig.SpawnPoints.Length)];
            _x = x;
            _y = z;
        }

        // ---------------------------------------------------------------- 로컬 입력 (소유 클라)

        private void HandleLocalInput()
        {
            // 이동 — 8방향. 값이 바뀔 때 + 유지 중 0.25초마다 전송 (서버가 마지막 입력을 유지)
            sbyte dx = (sbyte)((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0));
            sbyte dy = (sbyte)((Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0));
            bool changed = dx != _inDx || dy != _inDy;
            if (changed)
                _lastSentDirTick = Time.time;
            if (changed || ((dx != 0 || dy != 0) && Time.time - _lastSentDirTick > 0.25f))
            {
                _inDx = dx;
                _inDy = dy;
                _lastSentDirTick = Time.time;
                RpcSubmitMove(dx, dy);
            }

            // 조준 — 마우스 방향. 3도 이상 바뀔 때만 (SkipOwner: 남에게만 전송된다)
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

            // 발사 — 마우스 좌클릭. 쿨다운은 서버가 권위로 판정하지만 스팸 전송은 로컬에서도 억제
            if (Input.GetMouseButton(0) && Time.time >= _nextFireAllowed)
            {
                if (mouseWorld.HasValue)
                {
                    var dir = mouseWorld.Value - transform.position;
                    float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
                    if (len > 0.01f)
                    {
                        _nextFireAllowed = Time.time + ArenaConfig.FireCooldown;
                        RpcFire(dir.x / len, dir.z / len);
                    }
                }
            }
        }

        /// <summary>탑다운 카메라 기준 마우스 월드 좌표 (y=0 평면).</summary>
        private static Vector3? MouseWorld()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(Vector3.up, Vector3.zero);
            return plane.Raycast(ray, out float enter) ? ray.GetPoint(enter) : (Vector3?)null;
        }

        // ---------------------------------------------------------------- 비주얼 (전원)

        private void ApplyVisualState()
        {
            if (_body == null) return;

            // InitialOnly 색 — 클라는 스폰 상태 적용 시점부터 유효하다
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
                ApplyAimVisual(_aimYaw);   // 타인의 조준선은 SkipOwner로 받은 값
        }

        private void ApplyAimVisual(float yaw)
        {
            if (_body == null) return;
            _body.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // ---------------------------------------------------------------- RepNotify (클라 콜백)

        private void OnHpChanged(int prevHp)
        {
            if (_hp < prevHp)
                ArenaHud.NotifyHit(_displayName, prevHp - _hp);   // 감소만 플래시 (회복은 무음)
        }

        private void OnScoreChanged(int prevScore)
        {
            if (_score > prevScore)
                ArenaHud.NotifyScore(_displayName, _score - prevScore);
        }

        private void OnAmmoChanged(int prevAmmo)
        {
            // 내 탄약 감지 — HUD는 매 프레임 필드를 읽으므로 여기서는 잔탄 변화 로그만 (데모)
            if (_ammo < prevAmmo)
                ArenaHud.NotifyShot();
        }

        // ---------------------------------------------------------------- RPC 선언 (UniNet 소스 제너레이터 대상)

        /// <summary>클라 → 서버 이동 입력 (마지막 값을 서버가 유지한다).</summary>
        [ServerRpc]
        private partial void RpcSubmitMove(sbyte dx, sbyte dy);

        /// <summary>클라 → 서버 조준각 (도 단위) — SkipOwner로 남에게만 전파된다.</summary>
        [ServerRpc]
        private partial void RpcSubmitAim(float yaw);

        /// <summary>클라 → 서버 발사 요청 (단위 방향 벡터). 서버가 쿨다운·탄약을 권위 판정한다.</summary>
        [ServerRpc]
        private partial void RpcFire(float dirX, float dirY);

        /// <summary>서버 → 전원 발사 섬광. 유실 허용(비신뢰) — 다음 발사 FX가 자연스럽게 대체한다.</summary>
        [MulticastRpc(Delivery.Unreliable)]
        private partial void RpcPlayFireFx(float x, float y, int colorSeed);

        /// <summary>서버 → 전원 피격 FX. 비신뢰.</summary>
        [MulticastRpc(Delivery.Unreliable)]
        private partial void RpcPlayHitFx(float x, float y, bool crit);

        /// <summary>서버 → 전원 사망 폭발. 반드시 보여야 하므로 신뢰(기본값).</summary>
        [MulticastRpc]
        private partial void RpcPlayDeathFx(float x, float y, string name);

        /// <summary>서버 → 클라 전체 킬피드 (서버는 실행하지 않는다 — UE ClientRpc 동일).</summary>
        [ClientRpc]
        private partial void RpcShowKillFeed(string killerName, string victimName);

        // ---------------------------------------------------------------- RPC 구현

        private Task<bool> RpcSubmitMove_Validate(sbyte dx, sbyte dy)
            => Task.FromResult(dx is >= -1 and <= 1 && dy is >= -1 and <= 1);

        private void RpcSubmitMove_Implementation(sbyte dx, sbyte dy)
        {
            _inDx = dx;
            _inDy = dy;
        }

        private Task<bool> RpcSubmitAim_Validate(float yaw)
            => Task.FromResult(!float.IsNaN(yaw) && !float.IsInfinity(yaw) && Mathf.Abs(yaw) <= 3600f);

        private void RpcSubmitAim_Implementation(float yaw)
        {
            _aimYaw = yaw;   // SkipOwner — 소유 클라에는 전송되지 않는다 (나는 이미 로컬로 그림)
        }

        private Task<bool> RpcFire_Validate(float dirX, float dirY)
            => Task.FromResult(IsFinite01(dirX) && IsFinite01(dirY));

        private void RpcFire_Implementation(float dirX, float dirY)
        {
            if (_respawnAt > 0f) return;
            if (Time.time < _nextFireAllowed) return;          // 연사 쿨다운 (서버 권위)
            if (_ammo <= 0) return;                            // 탄약 검사 (서버 권위)

            _ammo--;
            _nextFireAllowed = Time.time + ArenaConfig.FireCooldown;
            _nextAmmoRegen = Time.time + ArenaConfig.AmmoRegenSeconds;

            // 동적 스폰 — 서버에서 생성 + Spawn (전 클라에 전파). 총알 스스로 이동·충돌·파괴를 수행한다
            var go = new GameObject("ArenaBullet");
            go.transform.position = new Vector3(_x, 0.5f, _y) + new Vector3(dirX, 0f, dirY) * 0.8f;
            var bullet = go.AddComponent<ArenaBullet>();
            bullet.Init(this, new Vector2(dirX, dirY), Random.Range(1, int.MaxValue));
            UniNetManager.Spawn(go);

            RpcPlayFireFx(_x, _y, _colorSeed);                 // Multicast(비신뢰) — 전원 로컬 FX
        }

        private static bool IsFinite01(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Mathf.Abs(v) <= 1.01f;

        private void RpcPlayFireFx_Implementation(float x, float y, int colorSeed)
        {
            ArenaFx.Flash(new Vector3(x, 0.6f, y), ArenaFx.SeedToColor(colorSeed), 0.35f);
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
