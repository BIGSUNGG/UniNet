using UnityEngine;

namespace Arena
{
    /// <summary>
    /// Single source of truth for P4 movement rules — server-authoritative simulation and client prediction share the same Step
    /// (diverging rules would turn prediction error into a constant offset; never duplicate the rule).
    /// Reconcile corrects the predicted coordinates when authoritative server coordinates arrive.
    /// </summary>
    internal static class ArenaMovement
    {
        /// <summary>One movement step — speed, boundary, and pillar collision rules (axis-separated). Applied identically by the server and the predicting client.</summary>
        public static (float X, float Z) Step(float x, float z, sbyte dx, sbyte dy, float dt)
        {
            float nx = Mathf.Clamp(x + dx * ArenaConfig.MoveSpeed * dt, -ArenaConfig.Half, ArenaConfig.Half);
            float nz = Mathf.Clamp(z + dy * ArenaConfig.MoveSpeed * dt, -ArenaConfig.Half, ArenaConfig.Half);
            if (!Blocked(nx, z)) x = nx;
            if (!Blocked(x, nz)) z = nz;
            return (x, z);
        }

        /// <summary>Pillar collision — expanded AABB point containment check (includes player radius).</summary>
        public static bool Blocked(float x, float z)
        {
            foreach (var (px, pz, half) in ArenaConfig.Pillars)
            {
                float ex = half + ArenaConfig.PlayerRadius;
                if (x > px - ex && x < px + ex && z > pz - ex && z < pz + ex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// P4 NetworkTransform.MovementRule adapter — called by the component tick (server authority and prediction).
        /// The Y axis is unused in this flat arena. Converts float input into the 8-direction sbyte rule and delegates to Step.
        /// </summary>
        public static void StepNet(ref float x, ref float y, ref float z, float inX, float inY, float inZ, float dt)
        {
            sbyte dx = (sbyte)Mathf.Clamp(Mathf.Round(inX), -1f, 1f);
            sbyte dy = (sbyte)Mathf.Clamp(Mathf.Round(inY), -1f, 1f);
            var (nx, nz) = Step(x, z, dx, dy, dt);
            x = nx;
            z = nz;   // y stays as the flat plane height — not managed by the component
        }

        /// <summary>
        /// P4 prediction reconciliation — corrects the predicted coordinate when the authoritative server coordinate arrives (per axis).
        /// Errors beyond snapThreshold snap (hard correction); otherwise softRate of the residual error is absorbed immediately (soft correction)
        /// — a simplified UE ClientAdjustPosition.
        /// ponytail: input-sequence ack and a replay queue of unacknowledged inputs would be the UE-grade version — add if prediction error becomes visible.
        /// </summary>
        public static float ReconcileAxis(float predicted, float server,
            float snapThreshold = 0.5f, float softRate = 0.15f)
        {
            float error = server - predicted;
            if (error * error > snapThreshold * snapThreshold)
                return server;
            return predicted + error * softRate;
        }
    }
}
