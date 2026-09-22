namespace UniNet.Core
{
    /// <summary>RPC delivery mode — reliability and ordering level.</summary>
    public enum Delivery
    {
        /// <summary>Reliable + ordered (default). For state changes and important events.</summary>
        ReliableOrdered,

        /// <summary>Unreliable. For frequent, loss-tolerant data such as movement or FX.</summary>
        Unreliable,
    }
}
