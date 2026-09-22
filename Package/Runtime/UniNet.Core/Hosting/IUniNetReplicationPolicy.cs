namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Per-object replication policy contract (P3: visibility, priority, dormancy, update rate).
    /// The server tick reads it to decide per-object whether, in what order, and how often to send deltas.
    /// Implemented by <see cref="NetworkBehaviour"/>; override its virtual members in your subclass to change policy.
    /// Objects that don't override use the default policy (every tick, unlimited).
    /// </summary>
    public interface IUniNetReplicationPolicy
    {
        /// <summary>Replication priority (UE NetPriority equivalent, default 1). When the bandwidth budget runs short, higher values are sent first and starvation boost raises the value the longer an object waits.</summary>
        float NetworkPriority { get; }

        /// <summary>Per-object update rate in Hz (UE NetUpdateFrequency equivalent). 0 = every tick, unlimited (default). Delta comparison is skipped until 1/Hz has elapsed since the last send.</summary>
        float NetworkUpdateFrequencyHz { get; }

        /// <summary>Distance-cull radius in world units (UE NetCullDistance equivalent). 0 = no distance culling (default). Viewer positions are provided via the server's SetViewerPosition.</summary>
        float NetworkCullDistance { get; }

        /// <summary>Dormancy flag (simplified UE NetDormancy — Awake/DormantAll two states). While true, delta comparison and sending stop. Wake it with FlushNetworkDormancy to flush accumulated changes.</summary>
        bool IsNetworkDormant { get; }

        /// <summary>Per-connection custom relevancy test (UE IsNetRelevantFor equivalent). Returning false withholds deltas, catch-up state, and spawns from that connection.</summary>
        bool IsNetworkRelevant(long viewerConnId);
    }
}
