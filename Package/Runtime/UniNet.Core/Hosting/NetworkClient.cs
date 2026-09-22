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
