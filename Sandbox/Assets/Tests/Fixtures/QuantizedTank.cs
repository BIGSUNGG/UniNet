using MessageProtocol.Serialize;
using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// Custom-serializer test fixture (see ADR-0020) — TWO custom-Equals fields coexist (generated comparison code
    /// must use per-field variables), plus a Write/Read-only field on a shared overload hub with an inherited base
    /// member (validation must scan all overloads and base declarations, not one arbitrary candidate).
    /// </summary>
    public sealed partial class QuantizedTank : NetworkBehaviour
    {
        [Replicated(Serializer = typeof(PositionQuantized), Notify = nameof(OnPosition))]
        public UnityEngine.Vector3 Position;

        [Replicated(Serializer = typeof(AimQuantized), Notify = nameof(OnAim))]
        public UnityEngine.Vector3 AimVector;

        [Replicated(Serializer = typeof(PackedFlagsSerializer))]
        public uint Packed;

        [Replicated(Serializer = typeof(PolarSerializer), Notify = nameof(OnPolarAim))]
        public PolarAim PolarAim;   // plain struct WITHOUT operator== — proves the emitted null guard skips CS0019 paths

        /// <summary>RepNotify call count for PolarAim.</summary>
        public int PolarNotifyCalls;

        /// <summary>RepNotify call count for Position (delta batches received).</summary>
        public int NotifyCalls;

        /// <summary>Previous value delivered to the last Position RepNotify call.</summary>
        public UnityEngine.Vector3 LastPrev;

        /// <summary>RepNotify call count for AimVector.</summary>
        public int AimNotifyCalls;

        private void OnPosition(UnityEngine.Vector3 prev)
        {
            NotifyCalls++;
            LastPrev = prev;
        }

        private void OnAim(UnityEngine.Vector3 prev)
            => AimNotifyCalls++;

        private void OnPolarAim(PolarAim prev)
            => PolarNotifyCalls++;
    }

    /// <summary>Plain value type with NO user-defined operator== — regression fixture for the generated null guard (CS0019).</summary>
    public struct PolarAim
    {
        public float Radius;
        public float Angle;
    }

    /// <summary>Custom serializer + Equals for the operator==-less struct — quantizes to 0.1 buckets.</summary>
    public static class PolarSerializer
    {
        public const float Scale = 10f;

        public static void Write(ref MessageBufferWriter w, in PolarAim v)
        {
            w.WriteInt16((short)(v.Radius * Scale));
            w.WriteInt16((short)(v.Angle * Scale));
        }

        public static PolarAim Read(ref MessageBufferReader r)
            => new PolarAim { Radius = r.ReadInt16() / Scale, Angle = r.ReadInt16() / Scale };

        public static bool Equals(in PolarAim a, in PolarAim b)
            => (short)(a.Radius * Scale) == (short)(b.Radius * Scale) && (short)(a.Angle * Scale) == (short)(b.Angle * Scale);
    }

    /// <summary>Quantized Vector3 wire format — 16 bits per axis. The optional Equals suppresses resends when both values quantize identically.</summary>
    public static class PositionQuantized
    {
        public const float Scale = 100f;   // 2 decimal digits of precision

        public static void Write(ref MessageBufferWriter w, in UnityEngine.Vector3 v)
        {
            w.WriteInt16(QuantizeAxis(v.x));
            w.WriteInt16(QuantizeAxis(v.y));
            w.WriteInt16(QuantizeAxis(v.z));
        }

        public static UnityEngine.Vector3 Read(ref MessageBufferReader r)
            => new UnityEngine.Vector3(DequantizeAxis(r.ReadInt16()), DequantizeAxis(r.ReadInt16()), DequantizeAxis(r.ReadInt16()));

        public static bool Equals(in UnityEngine.Vector3 a, in UnityEngine.Vector3 b)
            => QuantizeAxis(a.x) == QuantizeAxis(b.x) && QuantizeAxis(a.y) == QuantizeAxis(b.y) && QuantizeAxis(a.z) == QuantizeAxis(b.z);

        internal static short QuantizeAxis(float value)
        {
            float scaled = value * Scale;
            if (scaled > 32767f) scaled = 32767f;
            if (scaled < -32768f) scaled = -32768f;
            return (short)scaled;
        }

        internal static float DequantizeAxis(short value) => value / Scale;
    }

    /// <summary>Second custom-Equals serializer — coarser 0.1 buckets, proving two custom comparison fields generate non-colliding code.</summary>
    public static class AimQuantized
    {
        public const float Scale = 10f;   // 1 decimal digit of precision

        public static void Write(ref MessageBufferWriter w, in UnityEngine.Vector3 v)
        {
            w.WriteInt16((short)(v.x * Scale));
            w.WriteInt16((short)(v.y * Scale));
            w.WriteInt16((short)(v.z * Scale));
        }

        public static UnityEngine.Vector3 Read(ref MessageBufferReader r)
            => new UnityEngine.Vector3(r.ReadInt16() / Scale, r.ReadInt16() / Scale, r.ReadInt16() / Scale);

        public static bool Equals(in UnityEngine.Vector3 a, in UnityEngine.Vector3 b)
            => (short)(a.x * Scale) == (short)(b.x * Scale)
                && (short)(a.y * Scale) == (short)(b.y * Scale)
                && (short)(a.z * Scale) == (short)(b.z * Scale);
    }

    /// <summary>
    /// Shared overload hub (Write/Read-only contract — default object.Equals comparison). The decoy long overload and
    /// the inherited base Read exercise overload-tolerant validation: the uint pair must be found among all candidates.
    /// Non-static classes — static classes cannot inherit (CS0713) and the base-chain walk must still find members.
    /// </summary>
    public class PackedFlagsSerializer : SharedSerializerBase
    {
        // decoy overload — declared FIRST on purpose; a naive first-candidate check would false-positive UNINET013 here
        public static void Write(ref MessageBufferWriter w, in long v) => w.WriteInt64(v);

        public static void Write(ref MessageBufferWriter w, in uint v) => w.WriteUInt32(v);

        public static new uint Read(ref MessageBufferReader r) => r.ReadUInt32();   // 'new' — intentionally hides the base long-overload (decoy inheritance test)
    }

    /// <summary>Base declaration — the inherited Read(long) must not shadow the derived uint Read during validation.</summary>
    public class SharedSerializerBase
    {
        public static long Read(ref MessageBufferReader r) => r.ReadInt64();
    }
}
