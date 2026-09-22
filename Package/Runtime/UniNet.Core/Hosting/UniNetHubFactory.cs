using Communication.Shared.Channels;
using DRPC.Shared.Network;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Factory contract for the generated hub pair — implemented and registered by generated code in user
    /// assemblies. The generated hubs assemble directly on the DRPC hub base + HubSessionFactory.
    /// </summary>
    public abstract class UniNetHubFactory
    {
        /// <summary>Creates a per-connection server hub (includes assembling the RUDP session on the channel).</summary>
        public abstract HubBase CreateServerHub(IMessageChannel channel);

        /// <summary>Creates a client hub.</summary>
        public abstract HubBase CreateClientHub(IMessageChannel channel);
    }
}
