using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace Usage
{
    /// <summary>
    /// 동적 스폰되는 프로젝타일 예제 — 조건부 리플리케이션 3종 시연.
    /// - [Replicated] 위치(x) — 전 클라 항상 동기화 (서버 Update에서 전진)
    /// - [Replicated(OwnerOnly, Notify)] 데미지 — 소유 클라에만 동기화
    /// - [Replicated(InitialOnly)] 발사 시드 — 스폰 시 1회 전송, 이후 변경은 전파 안 됨
    /// 수명이 다하면 스스로 NetworkDestroy (전 클라에서 파괴 동기화).
    /// </summary>
    public sealed partial class Projectile : NetworkBehaviour
    {
        /// <summary>전 클라에 항상 동기화되는 위치(X축).</summary>
        [Replicated]
        private float _x;

        /// <summary>소유 클라에만 동기화 — 예: 내 발사체의 데미지 배율은 나만 본다.</summary>
        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnDamageChanged))]
        private int _damage = 10;

        /// <summary>스폰 시 1회 전송 — 이후 서버에서 바꿔도 클라에 전파되지 않는다.</summary>
        [Replicated(ReplicateCondition.InitialOnly)]
        private int _seed;

        [SerializeField] private float _lifetime = 3f;

        private float _speed;
        private float _age;

        /// <summary>서버 — 스폰 직후 초기화 (스폰 전에 호출해야 InitialOnly 값이 스폰에 실린다).</summary>
        public void Launch(float speed)
        {
            _speed = speed;
            _x = transform.position.x;
            _seed = Random.Range(0, 1000);
        }

        private void Update()
        {
            if (!IsServer) return;   // 서버 권위 — 시뮬레이션은 서버만

            _age += Time.deltaTime;
            _x += _speed * Time.deltaTime;

            if (_age >= _lifetime)
                UniNetManager.NetworkDestroy(gameObject);   // 파괴 동기화 + 로컬 파괴
        }

        private void OnDamageChanged(int prevDamage)
        {
            // 소유 클라에서만 호출 — prevDamage로 변화량 계산 가능
        }
    }
}
