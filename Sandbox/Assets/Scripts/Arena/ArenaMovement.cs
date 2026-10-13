using System;
using MessageProtocol.Serialize;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// Single source of truth for movement rules (ADR-0038 state-based prediction) — server-authoritative
    /// simulation and client prediction share the same pure Step over game-defined input/state structs.
    /// Step depends on (state, position, input, fixed dt) only: the vertical velocity that carries a jump arc
    /// and the dash window/cooldown live in the state struct, so rollback restores them and re-simulation
    /// reproduces the exact trajectory. XZ collision stays plane-projected (jumping does not clear pillars — demo simplification).
    /// </summary>
    internal static class ArenaMovement
    {
        /// <summary>Per-step movement input — 8-direction axes plus edge-triggered buttons (edges are detected inside Step via the state).</summary>
        internal struct MoveInput
        {
            public sbyte Dx;      // -1..1 (A/D)
            public sbyte Dz;      // -1..1 (S/W)
            public bool Jump;     // held — launches when grounded
            public bool Dash;     // held — starts when off cooldown and a direction is held
        }

        /// <summary>Carried movement state — everything the next step needs that is not position or input.</summary>
        internal struct MoveState
        {
            public float Vy;          // vertical velocity (jump arc)
            public float DashTimer;   // remaining dash window (0 = none)
            public float DashCooldown;
            public sbyte DashDx;      // locked dash direction
            public sbyte DashDz;
        }

        /// <summary>One movement step — gravity/jump, dash window, then XZ motion with boundary and pillar collision. Applied identically by the server and the predicting client.</summary>
        public static void Step(ref MoveState st, ref float x, ref float y, ref float z, in MoveInput i, float dt)
        {
            bool grounded = y <= ArenaConfig.GroundY + 0.001f && st.Vy <= 0f;

            if (i.Jump && grounded)
                st.Vy = ArenaConfig.JumpSpeed;
            if (i.Dash && st.DashTimer <= 0f && st.DashCooldown <= 0f && (i.Dx != 0 || i.Dz != 0))
            {
                st.DashTimer = ArenaConfig.DashSeconds;
                st.DashCooldown = ArenaConfig.DashCooldownSeconds;
                st.DashDx = i.Dx;
                st.DashDz = i.Dz;
            }

            st.DashTimer = Math.Max(0f, st.DashTimer - dt);
            st.DashCooldown = Math.Max(0f, st.DashCooldown - dt);

            float speed = ArenaConfig.MoveSpeed;
            float dx = i.Dx, dz = i.Dz;
            if (st.DashTimer > 0f)
            {
                speed *= ArenaConfig.DashSpeedMult;
                dx = st.DashDx;
                dz = st.DashDz;
            }

            st.Vy = grounded && !i.Jump && st.Vy <= 0f ? 0f : st.Vy - ArenaConfig.Gravity * dt;
            y += st.Vy * dt;
            if (y < ArenaConfig.GroundY)
            {
                y = ArenaConfig.GroundY;
                st.Vy = 0f;
            }

            float nx = Mathf.Clamp(x + dx * speed * dt, -ArenaConfig.Half, ArenaConfig.Half);
            float nz = Mathf.Clamp(z + dz * speed * dt, -ArenaConfig.Half, ArenaConfig.Half);
            if (!Blocked(nx, z)) x = nx;
            if (!Blocked(x, nz)) z = nz;
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

        // ---- ADR-0020-style static codecs (custom serializer contract) ----

        /// <summary>Serializes one input — 5 bytes on the wire.</summary>
        public static void WriteInput(ref MessageBufferWriter w, in MoveInput i)
        {
            w.WriteSByte(i.Dx);
            w.WriteSByte(i.Dz);
            w.WriteBoolean(i.Jump);
            w.WriteBoolean(i.Dash);
        }

        /// <summary>Deserializes one input.</summary>
        public static MoveInput ReadInput(ref MessageBufferReader r)
            => new MoveInput { Dx = r.ReadSByte(), Dz = r.ReadSByte(), Jump = r.ReadBoolean(), Dash = r.ReadBoolean() };

        /// <summary>Serializes the movement state — 18 bytes on the wire (echoed to the owner per acknowledged input, ADR-0038).</summary>
        public static void WriteState(ref MessageBufferWriter w, in MoveState s)
        {
            w.WriteSingle(s.Vy);
            w.WriteSingle(s.DashTimer);
            w.WriteSingle(s.DashCooldown);
            w.WriteSByte(s.DashDx);
            w.WriteSByte(s.DashDz);
        }

        /// <summary>Deserializes the movement state.</summary>
        public static MoveState ReadState(ref MessageBufferReader r)
            => new MoveState
            {
                Vy = r.ReadSingle(),
                DashTimer = r.ReadSingle(),
                DashCooldown = r.ReadSingle(),
                DashDx = r.ReadSByte(),
                DashDz = r.ReadSByte(),
            };
    }
}
