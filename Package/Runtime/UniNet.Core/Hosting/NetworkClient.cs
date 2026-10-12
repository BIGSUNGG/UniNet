using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Client-side runtime — keeps scene and dynamically spawned objects registered (one netId per object plus its
    /// component array), and stores the connection ID and ownership map the server announced.
    /// </summary>
    public sealed class NetworkClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<ulong, object[]> _objects = new();
        private readonly Dictionary<ulong, long> _owners = new();

        /// <summary>This client's connection ID assigned by the server (set when the Welcome message arrives).</summary>
        public long LocalConnId { get; private set; }

        /// <summary>
        /// The server's protocol version as declared in the Welcome (ADR-0028). Null before the Welcome arrives. Informational —
        /// enforcement is server-side (a mismatched client is kicked with VersionMismatch).
        /// </summary>
        public string ServerProtocolVersion { get; set; }

        /// <summary>Session token issued by the server in the Welcome (ADR-0034) — presented back on reconnect for grace-session reattach.</summary>
        public ulong SessionToken { get; set; }

        /// <summary>Client-side UniNet traffic counters (ADR-0029) — payload bytes/packets received by this client and sent by its RPCs.</summary>
        public UniNetTrafficCounters TrafficStats { get; } = new();

        private int _roundTripUs = -1000;   // microseconds backing store — the -1 ms sentinel encodes as -1000

        /// <summary>
        /// Round-trip time in ms measured by the client's ping probe (ADR-0029 wire 8/9 — send/echo, EMA-smoothed).
        /// -1 until the first round trip completes. Volatile-backed: the echo handler runs on the dispatch thread.
        /// </summary>
        public double RoundTripTimeMs
        {
            get => System.Threading.Volatile.Read(ref _roundTripUs) / 1000.0;
            set => System.Threading.Volatile.Write(ref _roundTripUs, (int)(value * 1000.0));   // runtime-assigned (generated echo handler, ADR-0029)
        }

        /// <summary>
        /// Why the last session ended — assigned by the generated client hub when a disconnect notice (ADR-0027)
        /// arrives, read by UniNetManager when raising ClientDisconnected. None when no notice was received
        /// (voluntary stop or an unexplained transport loss).
        /// Written on the DRPC dispatch ThreadPool and read on the transport thread — volatile-backed so a weakly
        /// ordered core cannot observe a stale None after the notice dispatch (reason would degrade to ConnectionLost).
        /// </summary>
        public UniNetDisconnectReason LastDisconnectReason
        {
            get => (UniNetDisconnectReason)System.Threading.Volatile.Read(ref _lastDisconnectReason);
            set => System.Threading.Volatile.Write(ref _lastDisconnectReason, (int)value);
        }
        private int _lastDisconnectReason = (int)UniNetDisconnectReason.None;

        /// <summary>Registers an object — binds a NetworkBehaviour component array (in slot order) to a netId. Shared by scene objects and dynamic spawns.</summary>
        public void Register(ulong netId, object[] components)
        {
            lock (_gate) _objects[netId] = components;
        }

        /// <summary>Removes a registration (destroy sync). Returns true if it was registered.</summary>
        public bool Unregister(ulong netId)
        {
            lock (_gate) return _objects.Remove(netId);
        }

        /// <summary>Returns the object's component array (null if unknown) — for object-level checks.</summary>
        public object[] Get(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var o) ? o : null;
        }

        /// <summary>Returns the instance in a sub slot (used for RPC routing and applying replication).</summary>
        public object Get(ulong netId, byte subId)
        {
            lock (_gate)
            {
                return _objects.TryGetValue(netId, out var o)
                    && subId < o.Length ? o[subId] : null;
            }
        }

        /// <summary>Applies the Welcome message — sets this client's connection ID (called by the generated client hub).</summary>
        public void SetLocalConnId(long connId) => LocalConnId = connId;

        /// <summary>Applies an ownership update (OwnerUpdate) — called by the generated client hub.</summary>
        public void ApplyOwner(ulong netId, long ownerConnId)
        {
            lock (_gate) _owners[netId] = ownerConnId;
        }

        /// <summary>Returns the owner connection of an object (0 = unassigned).</summary>
        public long GetOwner(ulong netId)
        {
            lock (_gate) return _owners.TryGetValue(netId, out var o) ? o : 0;
        }
    }
}
