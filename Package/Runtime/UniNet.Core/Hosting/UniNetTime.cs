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
    /// <remarks>
    /// <para>Time-sync contract (TIM-001 — documented, not changed; audit verdict: the "clock stall" hypothesis was
    /// rejected mathematically, the real (bounded) behaviors are):</para>
    /// <list type="number">
    /// <item><description><b>Progress rate is exactly 1.</b> The offset is a piecewise constant between
    /// <see cref="ApplyServerTime"/> calls, so <see cref="Now"/> always advances at the local clock's rate between
    /// syncs — the clock never slows down, only the monotonic clamp (below) can hold it momentarily.</description></item>
    /// <item><description><b>Offset decreases are bounded stalls, not freezes.</b> A decreasing adjustment is absorbed
    /// by the monotonic clamp: each EMA step moves the offset by ≤ 0.25·|Δoffset|, so a single correction stalls
    /// <see cref="Now"/> for at most 0.25·|Δoffset| seconds, and repeated decreases accumulate at most Σ(EMA steps) —
    /// a permanent stall is mathematically impossible.</description></item>
    /// <item><description><b>Permanent client bias ≤ s2c one-way delay.</b> There is no RTT/2 correction (deliberate
    /// — YAGNI, Phase B gate): the received TimeSync stamp is one-way delayed, so a client's <see cref="Now"/>
    /// carries a constant bias bounded by the server→client one-way delay. Server-side rewind queries are unaffected
    /// (the server queries its own local clock); client-supplied hit timestamps land within that bias.</description></item>
    /// <item><description><b>Monotonicity guarantee.</b> <see cref="Now"/> is clamped to <c>_lastNow</c> — it never
    /// goes backwards across TimeSync smoothing, so rewind queries and interpolation buffers cannot miss samples
    /// due to a backwards timestamp.</description></item>
    /// <item><description><b>Thread-safety limit.</b> This type is not thread-safe: <see cref="ApplyServerTime"/> must
    /// run on the main thread (the TimeSync path funnels through QueueOnMain), and concurrent <see cref="Now"/>
    /// reads from other threads are not synchronized against an in-flight application.</description></item>
    /// </list>
    /// </remarks>
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
        /// Monotonically non-decreasing (clamp to <c>_lastNow</c>) — prevents rewind queries and interpolation buffers
        /// from missing samples due to a backwards timestamp. Progress rate is exactly 1 between syncs (the offset is a
        /// piecewise constant); a decreasing TimeSync adjustment holds <see cref="Now"/> for at most one EMA step
        /// (≤ 0.25·|Δoffset| seconds, see the type-level contract). On clients the value carries the one-way-delay bias
        /// described in the type contract (TIM-001, bounded by s2c one-way delay).</summary>
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
        /// Each call moves the offset by at most 0.25·|Δoffset| — a decrease therefore stalls <see cref="Now"/>
        /// for at most that long (monotonic clamp), never longer; repeated decreases accumulate at most Σ(EMA steps).
        /// Main thread only (QueueOnMain path — not thread-safe, see the type-level contract).
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
