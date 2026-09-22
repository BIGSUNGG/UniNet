using System;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// P4 interpolation buffer — a client-side store for received state snapshots on a time axis, sampled with
    /// interpolation at a past timestamp (renderTime). This is the client-side counterpart of the tick-scheduling
    /// hook that lets the game control "when" remote objects are applied (UE client interpolation buffer
    /// equivalent — render InterpolationDelay seconds behind server time).
    /// Values live in a finite ring buffer; queries outside the range hold the nearest end value (a freeze under
    /// starvation).
    /// </summary>
    public sealed class SnapshotBuffer<T>
    {
        private readonly (double Time, T Value)[] _ring;
        private readonly Func<T, T, double, T> _lerp;
        private int _head;    // next write slot
        private int _count;

        /// <summary>capacity is the snapshot retention limit (≥ 2); lerp interpolates for t in [0,1].</summary>
        public SnapshotBuffer(int capacity, Func<T, T, double, T> lerp)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "인터폴레이션 버퍼는 최소 2 샘플이 필요하다");
            _ring = new (double, T)[capacity];
            _lerp = lerp ?? throw new ArgumentNullException(nameof(lerp));
        }

        /// <summary>Adds a received snapshot — discards time reversals (late-arriving older snapshots) to keep the time axis monotonic.</summary>
        public void Add(double time, T value)
        {
            if (_count > 0)
            {
                ref readonly var last = ref Peek(-1);
                if (time <= last.Time) return;
            }
            _ring[_head] = (time, value);
            _head = (_head + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
        }

        /// <summary>Timestamp of the most recent snapshot — returns false when the buffer is empty.</summary>
        public bool TryGetLatestTime(out double time)
        {
            if (_count == 0) { time = 0; return false; }
            time = Peek(-1).Time;
            return true;
        }

        /// <summary>
        /// Interpolated value at renderTime — lerp between two snapshots, end-value hold outside the range.
        /// Returns false when the buffer is empty.
        /// </summary>
        public bool TrySample(double renderTime, out T value)
        {
            if (_count == 0) { value = default; return false; }

            int oldest = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 1; i < _count; i++)
            {
                var (t0, v0) = _ring[(oldest + i - 1) % _ring.Length];
                var (t1, v1) = _ring[(oldest + i) % _ring.Length];
                if (renderTime <= t1)
                {
                    double span = t1 - t0;
                    double alpha = span > 0 ? Math.Min(1.0, Math.Max(0.0, (renderTime - t0) / span)) : 1.0;
                    value = _lerp(v0, v1, alpha);
                    return true;
                }
            }
            value = Peek(-1).Value;   // past the latest — hold (freeze under starvation)
            return true;
        }

        /// <summary>Empties the buffer (reconnect, object replacement, etc.).</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>Number of samples currently held (for diagnostics).</summary>
        public int SampleCount => _count;

        private ref readonly (double Time, T Value) Peek(int offsetFromHead)
        {
            int index = ((_head + offsetFromHead) % _ring.Length + _ring.Length) % _ring.Length;
            return ref _ring[index];
        }
    }
}
