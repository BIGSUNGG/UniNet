using System;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// The verified sender of a ServerRpc call — injected by the generated server dispatch into
    /// <c>&lt;Rpc&gt;_Implementation</c>/<c>&lt;Rpc&gt;_Validate</c> when their signature declares a trailing
    /// <c>RpcSender</c> parameter (ADR-0056). The value comes from the transport-assigned connection ID —
    /// a client cannot forge it, unlike a connId passed as a regular RPC argument.
    /// Declare it on RPCs with <c>RequireOwnership = false</c> (chat, world interaction, shop): the ownership
    /// check is skipped there, so this is the only way the server learns who called.
    /// The trailing parameter is excluded from the wire — callers keep the original RPC signature.
    /// </summary>
    public readonly struct RpcSender
    {
        /// <summary>Connection ID of the calling client — assigned by the transport, never client-supplied.</summary>
        public long ConnId { get; }

        /// <summary>Server/host local invocation (the RPC was called directly on the authority, ConnId 0).
        /// A dedicated-server local call and the host's own player call both report true.</summary>
        public bool IsLocal => ConnId == 0;

        /// <summary>Whether this is a real remote client (not a local authority call).</summary>
        public bool IsValid => ConnId != 0;

        /// <summary>Convenience — the player object this sender declared via SetPlayerObject (0 = none).
        /// Reads the server registry: valid on the main thread (where _Implementation/_Validate run).</summary>
        public ulong PlayerNetId => ConnId == 0 ? 0 : UniNetEnvironment.Server?.GetPlayerObject(ConnId) ?? 0;

        /// <summary>Runtime-constructed by the generated dispatch. Public so generated code in game
        /// assemblies can construct it; a game-constructed value has no wire effect.</summary>
        public RpcSender(long connId) => ConnId = connId;
    }
}
