using System;
using System.Diagnostics;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// P4 time authority — server-authoritative monotonic clock. On the server (authority) the local clock IS
    /// server time; a client adds the offset computed from received TimeSync messages.
    /// Prediction, interpolation, and rewind hooks all share this clock (UE ReplicatedWorldTimeSeconds equivalent).
    /// Because there is a single time domain, rewind queries and buffer sampling need no conversion when given a timestamp.
    /// </summary>
    public static class UniNetTime
    {
        private static readonly Stopwatch Fallback = Stopwatch.StartNew();

        /// <summary>Local monotonic clock in seconds. UniNet.Unity swaps in Unity's unscaled clock — core tests use the default stopwatch.</summary>
        public static Func<double> LocalClock { get; set; } = () => Fallback.Elapsed.TotalSeconds;

        private static double _serverOffset;   // serverTime = local + offset (clients only — servers keep 0)
        private static double _lastNow;        // monotonicity guard — keeps Now from going backwards across TimeSync smoothing
        private static bool _synced;
        private static int _receiveCount;

        /// <summary>Whether the server time domain is synced. Server/host are always true (the local clock is the
        /// authority); a pure client becomes true once it has received any TimeSync.</summary>
        public static bool IsSynced => _synced || UniNetEnvironment.Server != null;

        /// <summary>Number of TimeSync messages received (wire-reachability observer — includes ones servers/hosts ignore; for diagnostics and tests).</summary>
        public static int ReceiveCount => _receiveCount;

        /// <summary>Current server-domain time in seconds. Servers use the local clock; clients use local + sync offset.
        /// Monotonically non-decreasing — prevents rewind queries and interpolation buffers from missing samples due to a backwards timestamp.</summary>
        public static double Now
        {
            get
            {
                double now = LocalClock() + _serverOffset;
                if (now < _lastNow) now = _lastNow;
                _lastNow = now;
                return now;
            }
        }

        /// <summary>
        /// Client — updates the offset when a TimeSync arrives. The received value includes one-way delay, so
        /// exponential smoothing (α = 0.25) absorbs sudden jumps; the first sample is adopted immediately.
        /// Server/host ignore this — the local clock is already the authority, and applying an offset would
        /// desync the recording clock (raw) from the querying clock (corrected), making latest-sample queries land in the future.
        /// </summary>
        public static void ApplyServerTime(double serverTime)
        {
            _receiveCount++;   // wire-reachability observer — counts messages servers/hosts ignore too
            if (UniNetEnvironment.Server != null) return;   // server/host — no offset (recording and querying share one clock)
            double offset = serverTime - LocalClock();
            if (!_synced)
            {
                _serverOffset = offset;
                _synced = true;
                return;
            }
            _serverOffset += (offset - _serverOffset) * 0.25;
        }

        /// <summary>Resets sync state (for tests and restarts).</summary>
        public static void Reset()
        {
            _serverOffset = 0;
            _lastNow = 0;
            _synced = false;
            _receiveCount = 0;
        }
    }
}
