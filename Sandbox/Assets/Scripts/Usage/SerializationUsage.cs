using System.Collections.Generic;
using MessageProtocol.Serialize;
using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Usage
{
    /// <summary>
    /// Usage example 1 — custom field serializers (ADR-0020). A quantized aim vector crosses the wire as
    /// 3×int16 (6 bytes) instead of 3×float (12 bytes), and the optional Equals suppresses resends while
    /// both values quantize into the same 0.01 bucket. Declaring a Serializer also unlocks field types that
    /// are neither primitives nor [Message] — UnityEngine.Vector3 here.
    /// Run the live demo: menu "UniNet ▸ Usage ▸ 직렬화 사용 예시 (호스트 루프백)".
    /// </summary>
    public sealed partial class QuantizedWeapon : NetworkBehaviour
    {
        [Replicated(Serializer = typeof(AimQuantized), Notify = nameof(OnAimMoved))]
        private Vector3 _aimPoint;   // user wire format — 0.01 precision per axis

        [Replicated(Serializer = typeof(ChargeQuantized))]
        private float _charge01;   // quantized to a byte (0..255) — 1 byte instead of 4

        /// <summary>RepNotify — fires on clients when a delta lands (receives the pre-quantization previous value).</summary>
        public int AimMoveNotified;

        private void OnAimMoved(Vector3 prev)
        {
            AimMoveNotified++;
            Debug.Log($"[Usage] aim synced → {_aimPoint} (prev {prev})");
        }

        /// <summary>Server-side mutator — call from server/host code only (server authority).</summary>
        public void ServerAim(Vector3 worldPoint) => _aimPoint = worldPoint;

        /// <summary>Server-side mutator — 0..1 charge, wire-quantized to a byte.</summary>
        public void ServerCharge(float charge01) => _charge01 = Mathf.Clamp01(charge01);

        public Vector3 AimPoint => _aimPoint;
    }

    /// <summary>16-bit-per-axis quantized Vector3 — the optional Equals makes same-bucket resends free.</summary>
    public static class AimQuantized
    {
        public const float Scale = 100f;

        public static void Write(ref MessageBufferWriter w, in Vector3 v)
        {
            w.WriteInt16(Quantize(v.x));
            w.WriteInt16(Quantize(v.y));
            w.WriteInt16(Quantize(v.z));
        }

        public static Vector3 Read(ref MessageBufferReader r)
            => new(Dequantize(r.ReadInt16()), Dequantize(r.ReadInt16()), Dequantize(r.ReadInt16()));

        public static bool Equals(in Vector3 a, in Vector3 b)
            => Quantize(a.x) == Quantize(b.x) && Quantize(a.y) == Quantize(b.y) && Quantize(a.z) == Quantize(b.z);

        private static short Quantize(float v) => (short)Mathf.Clamp(v * Scale, short.MinValue, short.MaxValue);
        private static float Dequantize(short v) => v / Scale;
    }

    /// <summary>float→byte wire format for a 0..1 value (no Equals — object.Equals comparison is fine for a scalar).</summary>
    public static class ChargeQuantized
    {
        public static void Write(ref MessageBufferWriter w, in float v) => w.WriteByte((byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f));
        public static float Read(ref MessageBufferReader r) => r.ReadByte() / 255f;
    }

    /// <summary>
    /// Usage example 2 — FastArray element deltas (ADR-0020). Collection fields ([Replicated] List&lt;T&gt; / T[])
    /// sync only the changed elements: Set / Insert / RemoveAt / Clear ops, not the whole collection.
    /// On a collection field, Serializer applies to the ELEMENT wire format. Mutate freely on the server —
    /// the diff (common prefix/suffix, then Set for equal-length middles / Remove+Insert otherwise) is automatic.
    /// </summary>
    public sealed partial class InventoryRig : NetworkBehaviour
    {
        [Replicated(Notify = nameof(OnBackpackChanged))]
        private List<int> _backpack = new();   // element deltas — append/remove/set sync only the touched elements (NOT readonly: the generated null-guard assigns)

        [Replicated(Serializer = typeof(SlotFlagsSerializer))]
        private uint[] _slots = new uint[0];   // T[] + a custom element serializer — same rules as scalar fields

        public int BackpackNotified;

        /// <summary>RepNotify — receives a shallow copy of the pre-change collection.</summary>
        private void OnBackpackChanged(List<int> prev)
        {
            BackpackNotified++;
            Debug.Log($"[Usage] backpack synced → [{string.Join(",", _backpack)}] (prev [{string.Join(",", prev)}])");
        }

        // Server-side mutators — every mutation below becomes element ops on the wire (server authority)
        public void ServerLoot(int itemId) => _backpack.Add(itemId);           // → Insert op
        public void ServerDrop(int index) => _backpack.RemoveAt(index);        // → RemoveAt op
        public void ServerSwap(int index, int itemId) => _backpack[index] = itemId;   // → Set op
        public void ServerDropAll() => _backpack.Clear();                      // → single Clear op
        public void ServerSetSlots(uint[] slots) => _slots = slots;            // full array replace — element diffs

        public IReadOnlyList<int> Backpack => _backpack;
        public uint[] Slots => _slots;
    }

    /// <summary>
    /// Element serializer for InventoryRig._slots — on a collection field, Serializer applies per ELEMENT.
    /// This one passes the uint through as-is; replace the body with your packed bitfield format
    /// (e.g. drop unused flag bits into 2 bytes) for the same wiring.
    /// </summary>
    public static class SlotFlagsSerializer
    {
        public static void Write(ref MessageBufferWriter w, in uint v) => w.WriteUInt32(v);
        public static uint Read(ref MessageBufferReader r) => r.ReadUInt32();
    }
}
