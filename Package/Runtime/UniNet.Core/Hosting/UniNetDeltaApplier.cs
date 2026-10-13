using MessageProtocol.Serialize;

namespace UniNet.Core.Hosting
{
    /// <summary>Applies one replication payload to an instance — the generated batch receiver routes a wire methodId to this (batched replication).</summary>
    public delegate void UniNetDeltaApplier(object instance, ref MessageBufferReader reader);
}
