using System;

namespace UniNet.Core
{
    /// <summary>
    /// 서버 → 서버+전체 클라이언트 Multicast RPC. 서버에서 호출하면 서버에서도 실행된다.
    /// 클라이언트에서 호출하면 로컬에서만 실행되고 서버·타 클라에 전파되지 않는다 (UE NetMulticast 패리티).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MulticastRpcAttribute : Attribute
    {
        /// <summary>전달 모드 (기본: 신뢰 + 순서 보장).</summary>
        public Delivery Delivery { get; }

        /// <summary>전달 모드를 지정해 Multicast RPC를 선언한다.</summary>
        public MulticastRpcAttribute(Delivery delivery = Delivery.ReliableOrdered)
        {
            Delivery = delivery;
        }
    }
}
