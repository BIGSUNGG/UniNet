using System;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// P4 position history — the server records object positions on the server time axis and queries them at
    /// past timestamps (lag compensation hook; UE FLagCompensation position history equivalent). Recording runs
    /// in the UniNetTime.Now domain on the authoritative server tick; sampling uses linear interpolation between
    /// adjacent samples with end-value hold (clamp) outside the range.
    /// </summary>
    public sealed class PositionHistory
    {
        private readonly (double Time, float X, float Y, float Z)[] _ring;
        private readonly double _minInterval;
        private int _head;    // next write slot
        private int _count;

        /// <summary>
        /// capacity is the sample retention limit (≥ 2); minSampleInterval is the minimum gap between records in
        /// seconds (0 = accept every record). With a gap, the window (capacity × interval) stays guaranteed
        /// regardless of frame rate (UE server tick-rate equivalent).
        /// </summary>
        public PositionHistory(int capacity = 128, double minSampleInterval = 0.0)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "위치 히스토리는 최소 2 샘플이 필요하다");
            _ring = new (double, float, float, float)[capacity];
            _minInterval = Math.Max(0.0, minSampleInterval);
        }

        /// <summary>Records a position on the server tick — discards time reversals, duplicates, and samples under the minimum gap, preserving the monotonic time axis and the window guarantee (same contract as SnapshotBuffer.Add).</summary>
        public void Record(double time, float x, float y, float z)
        {
            if (_count > 0)
            {
                ref var last = ref _ring[(_head - 1 + _ring.Length) % _ring.Length];
                if (time <= last.Time) return;   // reversed or duplicate — discard
                if (time - last.Time < _minInterval) return;   // under minimum gap — discard (window guarantee)
            }
            _ring[_head] = (time, x, y, z);
            _head = (_head + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
        }

        /// <summary>
        /// Queries a past position — linear interpolation between adjacent samples, nearest end-value hold
        /// outside the history range. Returns false when empty (rewind unavailable — the caller falls back to
        /// the current position).
        /// </summary>
        public bool Sample(double time, out float x, out float y, out float z)
        {
            if (_count == 0) { x = y = z = 0; return false; }

            int oldest = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 1; i < _count; i++)
            {
                var a = _ring[(oldest + i - 1) % _ring.Length];
                var b = _ring[(oldest + i) % _ring.Length];
                if (time <= b.Time)
                {
                    double span = b.Time - a.Time;
                    double alpha = span > 0 ? Math.Min(1.0, Math.Max(0.0, (time - a.Time) / span)) : 1.0;
                    x = (float)(a.X + (b.X - a.X) * alpha);
                    y = (float)(a.Y + (b.Y - a.Y) * alpha);
                    z = (float)(a.Z + (b.Z - a.Z) * alpha);
                    return true;
                }
            }
            ref var latest = ref _ring[(_head - 1 + _ring.Length) % _ring.Length];
            x = latest.X;
            y = latest.Y;
            z = latest.Z;
            return true;
        }

        /// <summary>Oldest recorded timestamp — for clamping the rewind lower bound. Returns false when empty.</summary>
        public bool TryGetOldestTime(out double time)
        {
            if (_count == 0) { time = 0; return false; }
            time = _ring[(_head - _count + _ring.Length) % _ring.Length].Time;
            return true;
        }

        /// <summary>Most recent recorded timestamp. Returns false when empty.</summary>
        public bool TryGetLatestTime(out double time)
        {
            if (_count == 0) { time = 0; return false; }
            time = _ring[(_head - 1 + _ring.Length) % _ring.Length].Time;
            return true;
        }

        /// <summary>Empties the buffer.</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>Number of samples currently held (for diagnostics).</summary>
        public int SampleCount => _count;
    }
}
