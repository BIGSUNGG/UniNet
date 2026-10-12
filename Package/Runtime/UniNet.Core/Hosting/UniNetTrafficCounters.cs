namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Thread-safe UniNet traffic counters (ADR-0029) — bytes and packets counted at the UniNet layer: payload
    /// bytes handed to RPC sends and received RPC payloads. Wire framing (method headers, transport overhead,
    /// ACKs, DTLS) is NOT included — this measures application-level bandwidth ("how much state am I shipping"),
    /// not socket bytes. Written from network threads, read from anywhere (Interlocked).
    /// </summary>
    public sealed class UniNetTrafficCounters
    {
        private long _bytesSent;
        private long _bytesReceived;
        private long _packetsSent;
        private long _packetsReceived;

        /// <summary>UniNet payload bytes sent (RPC parameter payloads handed to the transport).</summary>
        public long BytesSent => System.Threading.Interlocked.Read(ref _bytesSent);

        /// <summary>UniNet payload bytes received (dispatched RPC parameter payloads).</summary>
        public long BytesReceived => System.Threading.Interlocked.Read(ref _bytesReceived);

        /// <summary>UniNet messages sent (one per RPC/system send call).</summary>
        public long PacketsSent => System.Threading.Interlocked.Read(ref _packetsSent);

        /// <summary>UniNet messages received (one per dispatched handler call).</summary>
        public long PacketsReceived => System.Threading.Interlocked.Read(ref _packetsReceived);

        /// <summary>Records one outgoing payload (called by the generated hubs).</summary>
        public void OnSent(int byteCount)
        {
            if (byteCount <= 0) byteCount = 0;
            System.Threading.Interlocked.Add(ref _bytesSent, byteCount);
            System.Threading.Interlocked.Add(ref _packetsSent, 1);
        }

        /// <summary>Records one incoming payload (called by the generated hubs).</summary>
        public void OnReceived(int byteCount)
        {
            if (byteCount <= 0) byteCount = 0;
            System.Threading.Interlocked.Add(ref _bytesReceived, byteCount);
            System.Threading.Interlocked.Add(ref _packetsReceived, 1);
        }
    }
}
