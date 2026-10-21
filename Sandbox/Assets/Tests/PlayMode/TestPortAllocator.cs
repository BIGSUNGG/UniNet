using System.Threading;

namespace UniNet.Tests
{
    /// <summary>
    /// Hands out a fresh UDP port per call and never repeats within a domain:
    /// RUDP listener sockets linger in the editor process for minutes after play
    /// mode ends, so any reused port collides with the leftover listener.
    /// Stride 8 keeps legacy port+1 / port+2 sub-uses inside one allocated slot.
    /// </summary>
    internal static class TestPortAllocator
    {
        private const int Base = 31000;
        private const int Span = 20000; // 2500 stride-8 slots (~80 full-suite runs before wrap)
        private const int Stride = 8;

        // Process-wide seed: a domain reload re-seeds from editor uptime, so a fresh
        // domain starts far from slots whose sockets may still be lingering.
        private static int _cursor = System.Environment.TickCount;

        public static int Next()
        {
            int v = Interlocked.Add(ref _cursor, Stride);
            return Base + (int)((uint)v % Span); // uint cast keeps the port positive across int wrap
        }
    }
}
