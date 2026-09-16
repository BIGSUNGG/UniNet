using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace Usage
{
    /// <summary>
    /// 동적 스폰 예제 — 서버에서 발사 요청(ServerRpc)을 받아 프로젝타일을 스폰한다.
    /// Instantiate → UniNetManager.Spawn 순서가 전부다. 파괴는 Projectile 스스로 NetworkDestroy.
    /// </summary>
    public sealed partial class Weapon : NetworkBehaviour
    {
        [SerializeField] private Projectile _projectilePrefab;

        /// <summary>프로젝타일 발사 속도 (예제용 상수).</summary>
        private const float Speed = 8f;

        private void Start()
        {
            // 클라이언트 생성용 프리팹 카탈로그 — 양단(서버·클라)에서 같은 프리팹으로 호출하면 클라가 이 프리팹으로 생성한다.
            // 미등록 시 기본 팩토리(빈 GameObject + AddComponent)로 생성된다.
            if (_projectilePrefab != null)
                UniNetManager.RegisterPrefab<Projectile>(_projectilePrefab.gameObject);
        }

        private void Update()
        {
            if (!IsOwner) return;   // 소유자만 입력

            if (Input.GetKeyDown(KeyCode.B))
                RpcFire(transform.position.x, transform.position.y, transform.position.z);
        }

        /// <summary>클라 → 서버 발사 요청 (서버 권위 — 스폰은 서버에서만).</summary>
        [ServerRpc]
        private partial void RpcFire(float x, float y, float z);

        private void RpcFire_Implementation(float x, float y, float z)
        {
            // 일반 Instantiate 그대로 + Spawn 한 줄 — netId 할당·소유권·전 클라 스폰 전파가 일어난다
            var projectile = Instantiate(_projectilePrefab, new Vector3(x, y, z), Quaternion.identity);
            projectile.Launch(Speed);
            UniNetManager.Spawn(projectile.gameObject);
        }
    }
}
