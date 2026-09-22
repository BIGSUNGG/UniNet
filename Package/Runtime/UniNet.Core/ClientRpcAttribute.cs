using System;

namespace UniNet.Core
{
    /// <summary>Server-to-client(s) RPC. Runs on the client.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ClientRpcAttribute : Attribute
    {
        /// <summary>Delivery mode (default: reliable + ordered).</summary>
        public Delivery Delivery { get; }

        /// <summary>Declares a Client RPC with the specified delivery mode.</summary>
        public ClientRpcAttribute(Delivery delivery = Delivery.ReliableOrdered)
        {
            Delivery = delivery;
        }
    }
}
