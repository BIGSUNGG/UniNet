using System.Collections.Generic;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Base class for networked objects — the MonoBehaviour that RPCs and replication target.
    /// A networked entity is a whole GameObject (one netId — scene objects hash their scene path, dynamic spawns get a server-assigned id).
    /// If a GameObject has multiple NetworkBehaviours, each gets its own SubId (slot) and replicates/RPCs independently (See ADR-0010).
    /// Slot order follows GetComponents order — never add or remove components at runtime (both ends must agree on slots).
    /// </summary>
    public abstract partial class NetworkBehaviour : MonoBehaviour, IUniNetSpawnTransform, IUniNetReplicationPolicy
    {
        private ulong _netId;
        private bool _netIdComputed;
        private byte _subId;
        private bool _registeredServer;
        private bool _registeredClient;

        /// <summary>Networked object ID — one per GameObject. Scene objects use the FNV-1a 64-bit hash of the scene name + hierarchy path (including sibling indices); dynamic spawns use a server-assigned value.</summary>
        public ulong NetId
        {
            get
            {
                if (!_netIdComputed)
                {
                    _netId = Fnv1a.Hash64(BuildHierarchyPath());
                    _netIdComputed = true;
                }
                return _netId;
            }
        }

        /// <summary>This component's sub-slot within its object — GetComponents order (assigned on registration).</summary>
        public byte SubId => _subId;

        /// <summary>True when running with server authority (on server or host).</summary>
        public bool IsServer => UniNetEnvironment.Server != null && _registeredServer;

        /// <summary>True when connected as a client (on client or host).</summary>
        public bool IsClient => UniNetEnvironment.Client != null && _registeredClient;

        /// <summary>True when the local player's connection owns this object (gate input and authority decisions on this — object-wide).</summary>
        public bool IsOwner
        {
            get
            {
                var client = UniNetEnvironment.Client;
                return client != null && client.GetOwner(NetId) == client.LocalConnId && client.LocalConnId != 0;
            }
        }

        // ── P3 replication policy (relevancy, priority, dormancy, update rate) ──

        /// <summary>Replication priority (UE NetPriority equivalent, default 1). When the bandwidth budget runs out, higher values are sent first.</summary>
        public float NetworkPriority { get; set; } = 1f;

        /// <summary>Per-object send rate in Hz (UE NetUpdateFrequency equivalent). 0 = unlimited, every tick (default).</summary>
        public float NetworkUpdateFrequencyHz { get; set; }

        /// <summary>Relevancy cull distance (world-unit radius, UE NetCullDistance equivalent). 0 = disabled (default). Requires viewer positions supplied via the server's SetViewerPosition.</summary>
        public float NetworkCullDistance { get; set; }

        /// <summary>Dormancy — while true, the server stops delta-checking and sending this object (simplified UE NetDormancy). Call FlushNetworkDormancy to wake it.</summary>
        public bool NetworkDormant { get; set; }

        /// <summary>Wakes the object — changes accumulated while dormant are sent on subsequent ticks (UE FlushNetDormancy equivalent).</summary>
        public void FlushNetworkDormancy() => NetworkDormant = false;

        /// <summary>Per-connection custom relevancy (UE IsNetRelevantFor equivalent) — default: always relevant. ANDed with the distance cull.</summary>
        public virtual bool IsNetworkRelevant(long viewerConnId) => true;

        float IUniNetReplicationPolicy.NetworkPriority => NetworkPriority;
        float IUniNetReplicationPolicy.NetworkUpdateFrequencyHz => NetworkUpdateFrequencyHz;
        float IUniNetReplicationPolicy.NetworkCullDistance => NetworkCullDistance;
        bool IUniNetReplicationPolicy.IsNetworkDormant => NetworkDormant;
        bool IUniNetReplicationPolicy.IsNetworkRelevant(long viewerConnId) => IsNetworkRelevant(viewerConnId);

        // ── P4 lag compensation hooks (position history, rewind) ──

        /// <summary>Have the server record this object's position history (enables lag-compensation rewind — default false, server-side only).</summary>
        public bool NetworkRewindHistory { get; set; }

        private PositionHistory _rewindHistory;

        internal PositionHistory RewindHistory => _rewindHistory ??= new PositionHistory(128, 1.0 / 60.0);   // 60 Hz sampling — 128 samples ≈ 2.1 s window (frame-rate independent)

        /// <summary>Test observer — number of position samples recorded so far.</summary>
        internal int RewindSampleCount => _rewindHistory?.SampleCount ?? 0;

        /// <summary>Called from the driver's server tick — records a position sample in server-domain time (only when NetworkRewindHistory is true).</summary>
        internal void RecordRewindSample(double serverTime)
        {
            var p = transform.position;
            RewindHistory.Record(serverTime, p.x, p.y, p.z);
        }

        /// <summary>
        /// Server-only — queries the position at a past serverTime (UniNetTime domain) for lag-compensation rewind.
        /// Objects without recorded history return the current transform position (not rewind-tracked — caller's contract).
        /// </summary>
        public bool GetHistoryPosition(double serverTime, out float x, out float y, out float z)
        {
            if (IsServer && _rewindHistory != null && _rewindHistory.Sample(serverTime, out x, out y, out z))
                return true;
            var p = transform.position;
            x = p.x;
            y = p.y;
            z = p.z;
            return true;
        }

        /// <summary>Dynamic spawn — injects the server-assigned netId (preempts path-hash computation).</summary>
        internal void AssignNetId(ulong netId)
        {
            _netId = netId;
            _netIdComputed = true;
        }

        /// <summary>Injects the sub-slot within the object at registration time.</summary>
        internal void AssignSubId(byte subId) => _subId = subId;

        /// <summary>Marks server registration complete (activates IsServer).</summary>
        internal void MarkServerRegistered() => _registeredServer = true;

        /// <summary>Marks client registration complete (activates IsClient).</summary>
        internal void MarkClientRegistered() => _registeredClient = true;

        /// <summary>Writes the current transform in spawn wire format (the server attaches this to spawn messages).</summary>
        void IUniNetSpawnTransform.GetSpawnTransform(
            out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw)
        {
            var p = transform.position;
            var r = transform.rotation;
            px = p.x; py = p.y; pz = p.z;
            qx = r.x; qy = r.y; qz = r.z; qw = r.w;
        }

        /// <summary>Registers all scene objects at startup (called once by UniNetManager) — one entry per object, all of its components become slots.</summary>
        internal static void RegisterAllToServer()
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return;

            foreach (var comps in CollectSceneObjects())
            {
                if (comps.Length > byte.MaxValue)
                {
                    Debug.LogError($"[UniNet] 오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한) — 등록 건너뜀: {comps[0].gameObject.name}");
                    continue;
                }
                server.RegisterSceneObject(comps[0].NetId, comps);
                for (byte i = 0; i < comps.Length; i++)
                {
                    comps[i].AssignSubId(i);
                    comps[i]._registeredServer = true;
                }
            }
        }

        /// <summary>Registers all objects on the client — same composition (slot order) as the server.</summary>
        internal static void RegisterAllToClient()
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;

            foreach (var comps in CollectSceneObjects())
            {
                if (comps.Length > byte.MaxValue)
                {
                    Debug.LogError($"[UniNet] 오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한) — 등록 건너뜀: {comps[0].gameObject.name}");
                    continue;
                }
                client.Register(comps[0].NetId, comps);
                for (byte i = 0; i < comps.Length; i++)
                {
                    comps[i].AssignSubId(i);
                    comps[i]._registeredClient = true;
                    UniNetTypeRegistry.Find(comps[i].GetType())?.InitClientSnapshot(comps[i]);
                }
            }
        }

        /// <summary>Groups the scene's NetworkBehaviours by GameObject (GetComponents order = slot order).</summary>
        private static List<NetworkBehaviour[]> CollectSceneObjects()
        {
            var result = new List<NetworkBehaviour[]>();
            var seen = new HashSet<GameObject>();
            foreach (var nb in FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (seen.Add(nb.gameObject))
                    result.Add(nb.gameObject.GetComponents<NetworkBehaviour>());
            return result;
        }

        private string BuildHierarchyPath()
        {
            var parts = new List<string>();
            var current = transform;
            while (current != null)
            {
                parts.Add(current.name + ":" + current.GetSiblingIndex());
                current = current.parent;
            }
            parts.Reverse();
            return gameObject.scene.name + "/" + string.Join("/", parts);
        }
    }
}
