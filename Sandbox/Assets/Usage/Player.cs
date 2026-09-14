using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace Usage
{
    /// <summary>
    /// UniNet 사용법 예제 — MonoBehaviour RPC + 변수 리플리케이션의 최소 형태.
    /// (사용법 우선 설계: 이 클래스가 곧 UniNet 공개 API의 설계안이다)
    /// </summary>
    public sealed class Player : NetworkBehaviour
    {
        /// <summary>서버 권위 변수 — 변경 시 클라이언트에 자동 동기화 + RepNotify 콜백.</summary>
        [Replicated(Notify = nameof(OnHpChanged))]
        private int _hp = 100;

        /// <summary>RepNotify — 클라에서 _hp가 네트워크로 변경될 때마다 실행. 이전값만 인자로 받고 현재값은 필드에서 읽는다.</summary>
        private void OnHpChanged(int prevHp)
        {
            // _hp(현재값)로 UI 갱신, prevHp로 변화량 계산 (데미지 팝업 등)
        }

        /// <summary>클라 → 서버 요청. 서버에서만 실행된다 (서버 권위).</summary>
        [ServerRpc]
        private void RpcRequestHit(int damage)
        {
            _hp -= damage;

            if (_hp <= 0)
                RpcPlayDeathFx();   // 서버에서 호출 → 서버+전 클라에서 실행
        }

        /// <summary>서버 → 클라(들) 브로드캐스트. 비신뢰 전달(유실 허용) 예시.</summary>
        [ClientRpc(Delivery.Unreliable)]
        private void RpcPlayHitFx(int damage) { /* FX 재생 */ }

        /// <summary>서버 → 서버+전 클라 Multicast. 사망 FX처럼 모두가 봐야 하는 것 (서버에서도 실행, 클라 호출 시 로컬 전용).</summary>
        [MulticastRpc]
        private void RpcPlayDeathFx() { /* 사망 FX */ }

        private void Update()
        {
            if (!IsOwner) return;   // 소유자만 입력 처리

            if (Input.GetKeyDown(KeyCode.Space))
                RpcRequestHit(10);
        }
    }
}
