using System;
using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// 아레나 플레이어 — UniNet 구현 기능(P1 RPC + P2 리플리케이션 + P3 정책 + P4 훅)을 한 클래스에서 시연한다.
    ///
    /// [Replicated] 조건 커버리지:
    /// - 위치            : NetworkTransform 컴포넌트(같은 오브젝트, 별도 슬롯 — ADR-0010)가 담당 — 조건 없음·전 클라 동기화
    /// - _hp             : RepNotify — HP 변화를 전 클라가 콜백으로 감지 (피격 플래시)
    /// - _score          : RepNotify — 점수 팝업
    /// - _ammo           : OwnerOnly + RepNotify — 내 탄약은 나에게만 전송
    /// - _aimYaw         : SkipOwner — 나는 내 조준을 로컬로 렌더하므로 남에게만 전송
    /// - _displayName,
    ///   _colorSeed      : InitialOnly — 스폰 시 1회만 전송, 이후 변경 무전파
    ///
    /// RPC 커버리지:
    /// - ServerRpc 2종(조준·발사) + _Validate 신뢰 경계 검증 — 이동 입력은 NetworkTransform 컴포넌트의 SubmitMove가 담당
    /// - MulticastRpc — 트레이서·피격(비신뢰), 사망(신뢰) — 서버 포함 전원 로컬 실행
    /// - ClientRpc — 킬피드 (서버 호출만 전파)
    ///
    /// P3 리플리케이션 정책 커버리지:
    /// - NetworkPriority (P3-②) — 플레이어 상태는 총알보다 먼저 전송된다
    /// - NetworkDormant + FlushNetworkDormancy (P3-③) — 사망 확정 델타가 나간 뒤 리스폰까지 휴면,
    ///   리스폰 시 상태 전체(HP·탄약·좌표)가 일괄 전파된다
    ///
    /// P4 훅 커버리지:
    /// - 이동은 NetworkTransform 컴포넌트(라이브러리)가 담당 — 서버 권위 복제 + 소유 클라 예측·조정 + 리모트 인터폴레이션.
    ///   게임은 이동 규칙(ArenaMovement.StepNet)만 컴포넌트에 주입한다 (ADR-0013).
    /// - 위치 히스토리·리와인드 (P4-②) — NetworkRewindHistory 대상, 발사 판정 시 발신자 시점(hitTime)으로 대상 리와인드
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

        // ---- 서버 전용 상태 (복제되지 않는다) ----
        private NetworkTransform _nt;   // P4 — 이동 예측 컴포넌트 (위치 복제·예측·조정·보간 담당)
        private float _nextFireAllowed;
        private float _nextAmmoRegen;
        private float _respawnAt;
        private bool _dormancyArmed;   // P3-③ — 사망 델타 전송이 지나간 뒤 다음 서버 틱에 휴면 전환

        // ---- 로컬(클라) 상태 ----
        private Transform _body;
        private sbyte _lastDx;   // 전송 중복 방지용 마지막 입력 (예측 입력은 컴포넌트가 관리)
        private sbyte _lastDy;
        private float _lastSentAim;
        private bool _aimSent;
        private float _lastSentDirTick = -1;

        /// <summary>
        /// 로컬 키보드·마우스 입력 처리 여부. 테스트·2프로세스 검증기가 프로그램 입력으로
        /// 플레이어를 구동할 때 끈다 (IsOwner 로컬 입력이 프로그램 입력을 덮어쓰지 않게).
        /// </summary>
        internal bool LocalInputEnabled { get; set; } = true;

        private void Awake()
        {
            // P4 — 이동 예측 컴포넌트에 게임 이동 규칙을 주입한다 (서버 권위·예측이 같은 규칙 공유)
            _nt = GetComponent<NetworkTransform>();
            _nt.MovementRule = ArenaMovement.StepNet;
            BuildVisuals();
        }

        /// <summary>서버가 스폰 직후 호출 — InitialOnly 값은 Spawn 전에 세팅해야 스폰에 실린다.
        /// 스폰 트랜스폽은 NetworkTransform이 흡수한다 (Awake — 스폰 변환은 1회 전파, 이후 이동은 컴포넌트가 담당).</summary>
        public void InitServerState(string displayName, int colorSeed)
        {
            _displayName = displayName;
            _colorSeed = colorSeed;
            gameObject.name = "ArenaPlayer_" + displayName;

            // P3-② 우선순위 — 대역폭 예산 부족 시 총알(기본 1)보다 플레이어 상태가 먼저 전송된다
            NetworkPriority = ArenaConfig.PlayerNetworkPriority;

            // P4-② 래그 컴펜세이션 — 서버가 이 오브젝트의 위치 히스토리를 기록한다 (발사 판정 시 리와인드 대상)
            NetworkRewindHistory = true;
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

        /// <summary>로컬 이동 입력 제출 (클라 → 서버) — P4 NetworkTransform 컴포넌트로 위임 (예측·전송 내장).</summary>
        internal void SubmitMoveInput(sbyte dx, sbyte dy) => _nt.SubmitMove(dx, dy, 0);

        /// <summary>로컬 조준각 제출 (클라 → 서버).</summary>
        internal void SubmitAimInput(float yaw) => RpcSubmitAim(yaw);

        /// <summary>발사 요청 제출 (클라 → 서버) — P4-② 발신자가 조준한 서버 시각을 함께 보낸다 (래그 컴펜세이션).</summary>
        internal void TryFire(float dirX, float dirY, double hitTime) => RpcFire(dirX, dirY, hitTime);

        /// <summary>에셋 없이 기본 도형으로 아바타를 조립한다 (몸통 + 총구). 클라·서버 동일.</summary>
        private void BuildVisuals()
        {
            var root = new GameObject("Body");
            root.transform.SetParent(transform, false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "BodyMesh";
            UnityEngine.Object.Destroy(body.GetComponent<Collider>());   // 판정은 서버 거리 계산만 사용
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
                ServerTick();   // 게임 로직 (리스폰 타이머·휴면·탄약) — 이동은 NetworkTransform 컴포넌트 틱이 담당

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
                else if (_dormancyArmed)
                {
                    // P3-③ 휴면 — 사망 확정 델타가 첫 틱에 나간 뒤 리스폰까지 델타 비교·전송을 중단한다
                    // (다음 서버 틱에 전환하므로 드라이버 틱과의 실행 순서와 무관하게 사망 HP 전파가 보장된다)
                    _dormancyArmed = false;
                    NetworkDormant = true;
                }
                return;
            }

            // 이동은 NetworkTransform 컴포넌트의 서버 틱이 수행한다 (MovementRule로 ArenaMovement.Step 주입)

            // 탄약 재생 (OwnerOnly 델타 — 소유 클라만 수신)
            if (_ammo < ArenaConfig.MaxAmmo && Time.time >= _nextAmmoRegen)
            {
                _ammo++;
                _nextAmmoRegen = Time.time + ArenaConfig.AmmoRegenSeconds;
            }
        }

        /// <summary>서버 권위 피해 — 히트스캔 판정(서버)이 직접 호출한다. 신뢰 경계 방어로 범위를 클램프한다.</summary>
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
                _dormancyArmed = true;          // P3-③ — 다음 서버 틱에 휴면 진입
                _nt.SimulationEnabled = false;  // P4-③ — 사망: 서버·예측 이동 모두 정지
                RpcPlayDeathFx(transform.position.x, transform.position.z, _displayName);
            }
        }

        /// <summary>킬 크레딧 — 히트스캔 판정이 사망 확정 후 소유자에게 부여한다 (서버 전용).</summary>
        internal void CreditKill()
        {
            _score++;
        }

        /// <summary>킬피드 전파 — RPC는 클래스 내부에 은닉하고 의도를 드러내는 게임 API로 노출한다.</summary>
        internal void BroadcastKillFeed(string killerName, string victimName)
        {
            RpcShowKillFeed(killerName, victimName);
        }

        /// <summary>사망 판정 — 히트스캔이 즉시 크레딧 판단에 쓴다.</summary>
        internal bool IsDead => _respawnAt > 0f || _hp <= 0;

        private void Respawn()
        {
            _respawnAt = 0f;
            _dormancyArmed = false;
            _hp = ArenaConfig.MaxHp;
            _ammo = ArenaConfig.MaxAmmo;
            var (x, z) = ArenaConfig.SpawnPoints[UnityEngine.Random.Range(0, ArenaConfig.SpawnPoints.Length)];
            _nt.SetNetworkPosition(new Vector3(x, 0.5f, z));   // P4 — 네트워크 위치 설정 (다음 틱에 전 클라 복제)
            FlushNetworkDormancy();   // P3-③ — 휴면 해제, 리스폰 상태(HP·탄약·좌표)가 다음 틱에 일괄 전파된다
        }

        // ---------------------------------------------------------------- 로컬 입력 (소유 클라)

        private void HandleLocalInput()
        {
            // 이동 — 8방향. 값이 바뀔 때 + 유지 중 0.25초마다 전송 (서버가 마지막 입력을 유지).
            // P4 — SubmitMove가 예측 즉시 반영과 서버 전송을 함께 처리한다 (컴포넌트)
            sbyte dx = (sbyte)((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0));
            sbyte dy = (sbyte)((Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0));
            bool changed = dx != _lastDx || dy != _lastDy;
            if (changed)
                _lastSentDirTick = Time.time;
            if (changed || ((dx != 0 || dy != 0) && Time.time - _lastSentDirTick > 0.25f))
            {
                _lastDx = dx;
                _lastDy = dy;
                _lastSentDirTick = Time.time;
                _nt.SubmitMove(dx, dy, 0);
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
                        RpcFire(dir.x / len, dir.z / len, UniNetTime.Now);   // P4-② 발신자 조준 시각 첨부 — 서버가 리와인드 판정
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

            // P4-③ — 소유 클라 예측 게이트 동기 (사망 시 이동 예측도 정지, 리스폰 시 재개)
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
            // 내 탄약 감지 — HUD는 매 프레임 필드를 읽으므로 여기서는 잔탄 변화 로그만 (데모)
            if (_ammo < prevAmmo)
                ArenaHud.NotifyShot();
        }

        // ---------------------------------------------------------------- RPC 선언 (UniNet 소스 제너레이터 대상)

        /// <summary>클라 → 서버 조준각 (도 단위) — SkipOwner로 남에게만 전파된다.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcSubmitAim(float yaw);

        /// <summary>클라 → 서버 발사 요청 (단위 방향 벡터 + 발신자 조준 서버 시각). 서버가 쿨다운·탄약·리와인드 판정을 권위 수행한다.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcFire(float dirX, float dirY, double hitTime);

        /// <summary>서버 → 전원 히트스캔 트레이서. 유실 허용(비신뢰) — 다음 발사 트레이서가 자연스럽게 대체한다.</summary>
        [MulticastRpc(Delivery.Unreliable)]
        private partial void RpcPlayTracer(float fromX, float fromZ, float toX, float toZ, int colorSeed);

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

        private Task<bool> RpcSubmitAim_Validate(float yaw)
            => Task.FromResult(!float.IsNaN(yaw) && !float.IsInfinity(yaw) && Mathf.Abs(yaw) <= 3600f);

        private void RpcSubmitAim_Implementation(float yaw)
        {
            _aimYaw = yaw;   // SkipOwner — 소유 클라에는 전송되지 않는다 (나는 이미 로컬로 그림)
        }

        private Task<bool> RpcFire_Validate(float dirX, float dirY, double hitTime)
            => Task.FromResult(IsFinite01(dirX) && IsFinite01(dirY)
                               && !double.IsNaN(hitTime) && !double.IsInfinity(hitTime)
                               && hitTime <= UniNetTime.Now + 1.0);   // 미래 조준 거부 (신뢰 경계 — 과거는 서버가 클램프)

        private void RpcFire_Implementation(float dirX, float dirY, double hitTime)
        {
            if (_respawnAt > 0f) return;
            if (Time.time < _nextFireAllowed) return;          // 연사 쿨다운 (서버 권위)
            if (_ammo <= 0) return;                            // 탄약 검사 (서버 권위)

            _ammo--;
            _nextFireAllowed = Time.time + ArenaConfig.FireCooldown;
            _nextAmmoRegen = Time.time + ArenaConfig.AmmoRegenSeconds;

            // P4-② 래그 컴펜세이션 — 발신자가 조준했던 시점으로 대상을 리와인드해 판정한다.
            // 신뢰 경계: hitTime은 히스토리 창+[1초 허용]으로 클램프 (과도한 과거/미래 리와인드 거부)
            double rewindTime = Math.Clamp(hitTime, UniNetTime.Now - ArenaConfig.RewindWindowSeconds, UniNetTime.Now);

            var origin = new Vector2(transform.position.x, transform.position.z);   // NetworkTransform이 동기화하는 권위 좌표
            var dir = new Vector2(dirX, dirY);
            ArenaPlayer hitTarget = null;
            float bestT = float.MaxValue;
            var end = origin + dir * ArenaConfig.HitscanRange;

            foreach (var player in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (player == this || player.IsDead) continue;

                // 리와인드 — 대상의 과거 위치 (히스토리 미기록 대상은 현재 위치를 반환한다)
                player.GetHistoryPosition(rewindTime, out float hx, out float hy, out float hz);

                // 선분-원 판정 (2D XZ) — 최근접점 투영 (dir는 단위 벡터)
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

            RpcPlayTracer(origin.x, origin.y, end.x, end.y, shotSeed);   // Multicast(비신뢰) — 전원 로컬 FX
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
