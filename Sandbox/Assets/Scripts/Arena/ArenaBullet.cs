using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// 총알 — 서버 권위 발사체. 동적 스폰/파괴 + 조건부 리플리케이션 + P3 고급 정책 시연:
    /// - _x, _y      : 조건 없음 — 궤적을 전 클라가 추종
    /// - _seed       : InitialOnly — 발사 시 1회 전송 (치명타 판정·색에 재사용, 이후 변경 무전파)
    /// - P3-④ NetworkUpdateFrequencyHz — 궤적 전송을 30Hz로 제한 (매 틱 불필요)
    /// - P3-① NetworkCullDistance — 멀리 있는 연결에는 궤적을 보내지 않고, 접근하면 그때 스폰한다
    ///
    /// 충돌 판정은 서버만 수행한다 (거리 기반). 히트 시 피해자의 ServerApplyDamage를 호출하고
    /// 사망이면 발사자에게 킬을 부여한 뒤 스스로 NetworkDestroy (파괴가 전 클라에 동기화된다).
    /// </summary>
    public sealed partial class ArenaBullet : NetworkBehaviour
    {
        [Replicated] private float _x;
        [Replicated] private float _y;

        [Replicated(ReplicateCondition.InitialOnly)] private int _seed;

        private Vector2 _dir;
        private ArenaPlayer _owner;   // 서버 전용 참조
        private float _age;

        /// <summary>서버가 스폰 직후 호출 — InitialOnly 시드는 Spawn 전에 세팅해야 스폰에 실린다.</summary>
        public void Init(ArenaPlayer owner, Vector2 dir, int seed)
        {
            _owner = owner;
            _dir = dir.normalized;
            _seed = seed;
            _x = transform.position.x;
            _y = transform.position.z;

            // P3 리플리케이션 정책 — 서버 틱이 읽는다 (Spawn 전 설정)
            NetworkUpdateFrequencyHz = ArenaConfig.BulletUpdateFrequencyHz;   // P3-④ 30Hz 스로틀 — 미도달 변경분은 최신값으로 합쳐진다
            NetworkCullDistance = ArenaConfig.BulletCullDistance;             // P3-① 거리 컬 — 접근 시 관련 전환 틱에 스폰된다
        }

        private void Start()
        {
            // 에셋 없이 기본 도형 — 총알 구. 색은 발사자와 같은 팔레트(시드 기반)로만 구분
            var mesh = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(mesh.GetComponent<Collider>());
            mesh.transform.SetParent(transform, false);
            mesh.transform.localScale = Vector3.one * 0.35f;
            var renderer = mesh.GetComponent<Renderer>();
            renderer.sharedMaterial = new Material(renderer.sharedMaterial)
            {
                color = ArenaFx.SeedToColor(_seed),
            };
        }

        private void OnDestroy()
        {
            UnityEngine.Debug.Log($"[ArenaDBG] bullet destroyed age={_age:0.00} pos=({_x:0.00},{_y:0.00}) fc={Time.frameCount}");
        }

        private void Update()
        {
            if (!IsServer)
            {
                // 클라 — 복제 좌표 추종 (총알은 빠르므로 스냅)
                transform.position = new Vector3(_x, 0.5f, _y);
                return;
            }

            float dt = Time.deltaTime;
            _age += dt;
            float fromX = _x;
            float fromY = _y;
            float nx = _x + _dir.x * ArenaConfig.BulletSpeed * dt;
            float ny = _y + _dir.y * ArenaConfig.BulletSpeed * dt;

            // 수명·경계·기둥 종료 — 파괴가 전 클라에 동기화된다
            if (_age >= ArenaConfig.BulletLifetime
                || Mathf.Abs(nx) > ArenaConfig.Half || Mathf.Abs(ny) > ArenaConfig.Half
                || Blocked(nx, ny))
            {
                UniNetManager.NetworkDestroy(gameObject);
                return;
            }

            _x = nx;
            _y = ny;
            transform.position = new Vector3(_x, 0.5f, _y);

            TryHit(fromX, fromY, nx, ny);
        }

        private static bool Blocked(float x, float z)
        {
            foreach (var (px, pz, half) in ArenaConfig.Pillars)
            {
                if (x > px - half && x < px + half && z > pz - half && z < pz + half)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 서버 히트 판정 — 이동 선분 스윕. 프레임 이동량이 커도(느린 프레임·dt 스파이크)
        /// 대상 통과(터널링)로 명중을 놓치지 않는다. 발사자와 이미 죽은 대상은 제외.
        /// </summary>
        private void TryHit(float fromX, float fromY, float toX, float toY)
        {
            float moveX = toX - fromX;
            float moveZ = toY - fromY;
            float moveLenSq = moveX * moveX + moveZ * moveZ;

            foreach (var player in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (player == _owner || player.IsDead) continue;

                float dx = player.transform.position.x - fromX;
                float dz = player.transform.position.z - fromY;

                float distSq;
                if (moveLenSq < 0.000001f)
                {
                    distSq = dx * dx + dz * dz;   // 정지 프레임 — 점 거리
                }
                else
                {
                    // 선분에서 대상까지 최근접점 투영 — 프레임 속도 무관 명중
                    float t = Mathf.Clamp01((dx * moveX + dz * moveZ) / moveLenSq);
                    float cx = dx - moveX * t;
                    float cz = dz - moveZ * t;
                    distSq = cx * cx + cz * cz;
                }

                if (distSq > ArenaConfig.HitDistance * ArenaConfig.HitDistance) continue;

                bool crit = _seed % ArenaConfig.CritEvery == 0;
                UnityEngine.Debug.Log($"[ArenaDBG] HIT {player.DisplayName} crit={crit} hpBefore={player.HudHp} isDead={player.IsDead}");
                player.ServerApplyDamage(ArenaConfig.Damage, crit);   // 서버 권위 — 권한 있는 직접 호출
                UnityEngine.Debug.Log($"[ArenaDBG] after-damage {player.DisplayName} hpAfter={player.HudHp} isDead={player.IsDead}");

                if (player.IsDead && _owner != null)
                {
                    _owner.CreditKill();
                    _owner.BroadcastKillFeed(_owner.DisplayName, player.DisplayName);   // ClientRpc — 전 클라 킬피드
                }

                UniNetManager.NetworkDestroy(gameObject);   // 히트 시 소멸
                return;
            }
        }
    }
}
