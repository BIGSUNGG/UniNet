using System;
using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    // ── GEN-003 fixtures — partials in the Fixtures assembly (nested NB is supported since GEN-002 — the generator
    //    wraps the emitted partial in the containing class chain — these stay top-level by choice). Living here
    //    (not in the EditMode assembly) keeps the EditMode assembly free of NetworkBehaviour types, so the
    //    CS0436-ambiguous `UniNet.Generated.__UniNetRegistration.Register()` calls in EditMode tests bind to the
    //    Fixtures assembly's registration and every fixture type gets registered. ──

    /// <summary>int-backed enum (default underlying) — the common case.</summary>
    public enum WireStance { Idle = 0, Guard = 1, Chase = 2 }

    /// <summary>byte-backed enum — proves the codec picks the declared backing width, not always int.</summary>
    public enum WirePhase : byte { Ready = 0, Running = 1, Halted = 200 }

    /// <summary>ulong-backed [Flags] enum — bit 33 must survive the wire (an int-backed codec would silently truncate it).</summary>
    [Flags]
    public enum WireAccess : ulong { None = 0, Move = 1UL << 0, Build = 1UL << 33 }

    /// <summary>GEN-003 RPC fixture — enum parameters on every supported backing integer.</summary>
    public sealed partial class Gen003EnumRpcPlayer : NetworkBehaviour
    {
        public int StanceCalls;
        public WireStance LastStance = WireStance.Idle;
        public WirePhase LastPhase = WirePhase.Ready;
        public WireAccess LastAccess = WireAccess.None;

        [ServerRpc]
        internal partial void RpcStance(WireStance stance);

        private void RpcStance_Implementation(WireStance stance)
        {
            StanceCalls++;
            LastStance = stance;
        }

        /// <summary>Primitive twin of RpcStance — byte-for-byte payload equality with the enum version proves wire invariance.</summary>
        [ServerRpc]
        internal partial void RpcStanceRaw(int stance);

        private void RpcStanceRaw_Implementation(int stance) { }

        [ServerRpc]
        internal partial void RpcPhase(WirePhase phase);

        private void RpcPhase_Implementation(WirePhase phase) => LastPhase = phase;

        [ServerRpc]
        internal partial void RpcAccess(WireAccess access);

        private void RpcAccess_Implementation(WireAccess access) => LastAccess = access;
    }

    /// <summary>GEN-003 replication fixture — enum scalar + enum-element array ([Replicated]).</summary>
    public sealed partial class Gen003EnumReplicator : NetworkBehaviour
    {
        [Replicated]
        public WireStance Stance;

        [Replicated]
        public WireStance[] Stances = new WireStance[0];
    }
}
