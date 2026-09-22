using System;

namespace UniNet.Core
{
    /// <summary>Client-to-server RPC. Runs on the server only (server authority).
    /// By default only the object's owner may call it; calls from non-owners are rejected at server dispatch (See ADR-0016).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ServerRpcAttribute : Attribute
    {
        /// <summary>true (default) — executes only when the sender owns the object (non-owner calls are rejected with a warning log).
        /// false — accepts calls from any client (e.g. an RPC every client reports state through — the server remains responsible for validating it).</summary>
        public bool RequireOwnership { get; set; } = true;

        /// <summary>true — runs the validation hook &lt;RPC&gt;_Validate (same parameters, returns Task&lt;bool&gt;) right before server dispatch;
        /// returning false skips the RPC implementation (opt-in via [ServerRpc(Validate = true)]).
        /// There is no auto-detection — a _Validate method without the flag simply never runs (the generator emits a warning).</summary>
        public bool Validate { get; set; } = false;
    }
}
