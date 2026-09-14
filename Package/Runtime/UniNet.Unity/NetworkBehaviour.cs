using System;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>네트워크 오브젝트 기반 클래스 — RPC와 리플리케이션의 대상이 되는 MonoBehaviour.</summary>
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        /// <summary>이 오브젝트를 로컬 플레이어가 소유하는지 여부 (입력·권위 판단용).</summary>
        public bool IsOwner => throw new NotImplementedException("사용법 확정용 스텁 — P1(오브젝트 소유권)에서 구현");
    }
}
