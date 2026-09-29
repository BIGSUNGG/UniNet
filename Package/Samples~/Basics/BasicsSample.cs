using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Samples.Basics
{
    /// <summary>최소 예제 — 서버 권위 HP + 클라→서버 RPC + 서버→클라 FX RPC.
    /// 빈 GameObject에 부착하고 BasicsBootstrap도 같은 씬에 두면 동작한다.</summary>
    public sealed partial class BasicsSample : NetworkBehaviour
    {
        [Replicated(Notify = nameof(OnHpChanged))]
        private int _hp = 100;

        private void OnHpChanged(int prevHp) => Debug.Log($"[Basics] HP {prevHp} -> {_hp}");

        [ServerRpc(Validate = true)]
        private partial void RpcHit(int damage);

        private Task<bool> RpcHit_Validate(int damage) => Task.FromResult(damage > 0);

        private void RpcHit_Implementation(int damage)
        {
            _hp -= damage;
            RpcHitFx(damage);
        }

        [ClientRpc(Delivery.Unreliable)]
        private partial void RpcHitFx(int damage);

        private void RpcHitFx_Implementation(int damage) => Debug.Log($"[Basics] hit fx -{damage}");

        private void Update()
        {
            if (!IsOwner) return;
            if (Input.GetKeyDown(KeyCode.Space)) RpcHit(10);
        }
    }

    /// <summary>씬 부트스트랩 — 서버+클라 한 프로세스(호스트)로 기동한다.</summary>
    public sealed class BasicsBootstrap : MonoBehaviour
    {
        private async void Start()
        {
            await UniNetManager.HostAsync(7777);
            Debug.Log("[Basics] host ready — Space 키로 RpcHit(10)");
        }
    }
}
