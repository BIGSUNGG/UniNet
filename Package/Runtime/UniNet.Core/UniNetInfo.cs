// Wiring-check placeholder — verifies that the pure C# core (no engine references)
// compiles in the Unity pipeline. Replaced with the real core types when P1 implementation begins.
namespace UniNet.Core
{
    /// <summary>UniNet core assembly identification info.</summary>
    public static class UniNetInfo
    {
        /// <summary>Package version (kept in sync with package.json). Also the default protocol-handshake version (ADR-0028) — same-package ends always match.</summary>
        public const string Version = "0.13.0";   // 0.13.0 — death-gate pending-history cleanup (MOV-002): INetworkMoveSimulation.DropPendingHistory DIM; NetworkTransform drops the owner's unacknowledged prediction history on the SimulationEnabled true→false edge (MoveSimulation keeps state/input), so death-window acks hard-snap and respawn re-anchors via the existing pending==0 path
    }
}
