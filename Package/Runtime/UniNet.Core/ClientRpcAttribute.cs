using System;

namespace UniNet.Core
{
    /// <summary>서버 → 클라이언트(들) RPC. 클라이언트에서 실행된다.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ClientRpcAttribute : Attribute
    {
        /// <summary>전달 모드 (기본: 신뢰 + 순서 보장).</summary>
        public Delivery Delivery { get; }

        /// <summary>전달 모드를 지정해 Client RPC를 선언한다.</summary>
        public ClientRpcAttribute(Delivery delivery = Delivery.ReliableOrdered)
        {
            Delivery = delivery;
        }
    }
}
