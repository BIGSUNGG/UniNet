using System;

namespace UniNet.Core
{
    /// <summary>
    /// Server-to-everyone Multicast RPC (server + all clients). Calling it on the server also runs it locally on the server.
    /// Calling it on a client runs it locally only and is not propagated to the server or other clients (UE NetMulticast parity).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MulticastRpcAttribute : Attribute
    {
        /// <summary>Delivery mode (default: reliable + ordered).</summary>
        public Delivery Delivery { get; }

        /// <summary>Declares a Multicast RPC with the specified delivery mode.</summary>
        public MulticastRpcAttribute(Delivery delivery = Delivery.ReliableOrdered)
        {
            Delivery = delivery;
        }
    }
}
