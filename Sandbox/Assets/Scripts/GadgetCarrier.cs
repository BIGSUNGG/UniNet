using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace Usage
{
    /// <summary>
    /// 다중 컴포넌트 네트워킹 예제 — Weapon(발사)과 **같은 게임오브젝트**에 붙는 장비 서브오브젝트.
    /// 한 오브젝트에 여러 NetworkBehaviour가 있어도 각자 SubId 슬롯을 받아 독립적으로 RPC·리플리케이션된다 (ADR-0010).
    /// 슬롯 순서는 GetComponents 순서 — 런타임 AddComponent/Destroy로 구성을 바꾸면 안 된다.
    /// 다중 컴포넌트 오브젝트의 동적 스폰은 RegisterPrefab 필수 (기본 팩토리는 단일 컴포넌트만 생성).
    /// </summary>
    public sealed partial class GadgetCarrier : NetworkBehaviour
    {
        /// <summary>장비 수 — 소유 클라에만 동기화 (예: 내 장비 잔량은 나만 본다).</summary>
        [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnGadgetsChanged))]
        private int _gadgets = 3;

        private void Update()
        {
            if (!IsOwner) return;

            if (Input.GetKeyDown(KeyCode.G))
                RpcUseGadget();
        }

        /// <summary>클라 → 서버 장비 사용 요청.</summary>
        [ServerRpc]
        private partial void RpcUseGadget();

        private void RpcUseGadget_Implementation()
        {
            if (_gadgets <= 0) return;
            _gadgets--;
        }

        private void OnGadgetsChanged(int prev)
        {
            // 소유 클라에서만 호출 — prev로 잔량 감소 표시 가능
        }
    }
}
