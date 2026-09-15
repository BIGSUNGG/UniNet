using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace Usage
{
    /// <summary>
    /// UniNet 사용법 예제 — RPC partial 선언 + _Implementation(구현) + _Validate(선택 검증) 패턴.
    /// (사용법 우선 설계: 이 클래스가 곧 UniNet 공개 API의 설계안이다)
    /// </summary>
    public sealed partial class Player : NetworkBehaviour
    {
        /// <summary>서버 권위 변수 — 변경 시 클라이언트에 자동 동기화 + RepNotify 콜백.</summary>
        [Replicated(Notify = nameof(OnHpChanged))]
        private int _hp = 100;

        /// <summary>RepNotify — 클라에서 _hp가 네트워크로 변경될 때마다 실행. 이전값만 인자로 받고 현재값은 필드에서 읽는다.</summary>
        private void OnHpChanged(int prevHp)
        {
            // _hp(현재값)로 UI 갱신, prevHp로 변화량 계산 (데미지 팝업 등)
        }

        /// <summary>클라 → 서버 요청. 선언만 하고 구현은 _Implementation에 쓴다 (서버 권위).</summary>
        [ServerRpc]
        private partial void RpcRequestHit(int damage);

        /// <summary>메시지 타입 매개변수 + 다형성 — 부모(DamageMsg) 선언에 자식(CriticalHitMsg) 인스턴스 전달.</summary>
        [ServerRpc]
        private partial void RpcApplyDamage(DamageMsg damage);

        /// <summary>선택 검증 후크 — false면 서버가 _Implementation를 실행하지 않는다 (DRPC Validation 패리티).</summary>
        private Task<bool> RpcRequestHit_Validate(int damage)
            => Task.FromResult(damage > 0);

        private Task<bool> RpcApplyDamage_Validate(DamageMsg damage)
            => Task.FromResult(damage != null && damage.Amount > 0);

        /// <summary>서버에서만 실행되는 구현.</summary>
        private void RpcRequestHit_Implementation(int damage)
        {
            _hp -= damage;

            if (_hp <= 0)
                RpcPlayDeathFx();   // 서버에서 호출 → 서버+전 클라에서 실행
        }

        /// <summary>메시지 구현 — 수신측에서 자식 타입으로 캐스팅해 확장 필드 사용 (다형성 공식 지원).</summary>
        private void RpcApplyDamage_Implementation(DamageMsg damage)
        {
            _hp -= damage.Amount;

            if (damage is CriticalHitMsg crit)
                _hp -= (int)(damage.Amount * (crit.Multiplier - 1f));   // 치명타 추가분
        }

        /// <summary>서버 → 클라(들) 브로드캐스트. 비신뢰 전달(유실 허용) 예시.</summary>
        [ClientRpc(Delivery.Unreliable)]
        private partial void RpcPlayHitFx(int damage);

        /// <summary>클라이언트에서 실행되는 구현.</summary>
        private void RpcPlayHitFx_Implementation(int damage) { /* FX 재생 */ }

        /// <summary>서버 → 서버+전 클라 Multicast. 사망 FX처럼 모두가 봐야 하는 것 (서버에서도 실행, 클라 호출 시 로컬 전용).</summary>
        [MulticastRpc]
        private partial void RpcPlayDeathFx();

        /// <summary>서버·클라 모두에서 실행되는 구현.</summary>
        private void RpcPlayDeathFx_Implementation() { /* 사망 FX */ }

        private void Update()
        {
            if (!IsOwner) return;   // 소유자만 입력 처리

            if (Input.GetKeyDown(KeyCode.Space))
                RpcRequestHit(10);

            if (Input.GetKeyDown(KeyCode.K))
                RpcApplyDamage(new CriticalHitMsg { Amount = 20, Multiplier = 2f });   // 자식 인스턴스 전달
        }
    }
}
