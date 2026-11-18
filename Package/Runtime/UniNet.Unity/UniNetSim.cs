using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Communication.Shared.Channels;
using UniNet.Core.Hosting;

namespace UniNet.Unity
{
    /// <summary>
    /// Optional network-condition simulation for multi-process gate runs. Today: one-way outbound latency.
    /// The consuming game sets <see cref="DelayMs"/> at boot — the engine never parses command-line arguments.
    /// </summary>
    public static class UniNetSim
    {
        /// <summary>
        /// One-way outbound delay in milliseconds (0 = off). Every payload the library hands to the transport is
        /// held this long before it is actually sent; inbound traffic is untouched (delaying the outbound side of
        /// each process already yields one delay per hop). No drops, no duplication, no reordering. Set it once at
        /// boot, before Server/Client start — changing it while messages are in flight can reorder them.
        /// </summary>
        public static int DelayMs { get; set; }

        private readonly struct PendingSend
        {
            public readonly double Due;
            public readonly IMessageChannel Channel;
            public readonly byte[] Payload;
            public readonly SendOptions Options;

            public PendingSend(double due, IMessageChannel channel, byte[] payload, SendOptions options)
            {
                Due = due;
                Channel = channel;
                Payload = payload;
                Options = options;
            }
        }

        private static readonly object Gate = new object();
        private static readonly Queue<PendingSend> Pending = new Queue<PendingSend>();

        /// <summary>Wraps a connection's transport channel — the single place every outbound payload passes through
        /// (RPCs, replication/state stream, system messages). Always wrapped so DelayMs can be set at any point.</summary>
        internal static IMessageChannel Wrap(IMessageChannel channel) => new DelayedMessageChannel(channel);

        /// <summary>Schedules one payload — the caller's buffer is copied, since DRPC recycles pooled payload buffers
        /// as soon as SendAsync returns.</summary>
        internal static void Schedule(IMessageChannel channel, ReadOnlyMemory<byte> payload, SendOptions options, int delayMs)
        {
            byte[] copy = payload.ToArray();
            lock (Gate)
            {
                // equal delays keep enqueue order == due order, so the pump can only ever release the head
                Pending.Enqueue(new PendingSend(UniNetTime.Now + delayMs / 1000.0, channel, copy, options));
            }
        }

        /// <summary>Releases every payload whose delay has elapsed, oldest first (from the driver's Update).</summary>
        internal static void Pump()
        {
            while (true)
            {
                PendingSend next;
                lock (Gate)
                {
                    if (Pending.Count == 0) return;
                    next = Pending.Peek();
                    if (next.Due > UniNetTime.Now) return;
                    Pending.Dequeue();
                }
                _ = SendAsync(next);   // outside the lock — network threads enqueue here too
            }
        }

        private static async Task SendAsync(PendingSend pending)
        {
            try
            {
                await pending.Channel.SendAsync(pending.Payload, pending.Options);
            }
            catch (Exception error)
            {
                // the channel closed while the payload was held — the same message would have failed in-flight
                UniNetEnvironment.LogFault(error);
            }
        }
    }

    /// <summary>Outbound latency decorator over a transport channel; inbound traffic passes through untouched.</summary>
    internal sealed class DelayedMessageChannel : IMessageChannel
    {
        private readonly IMessageChannel _inner;

        public DelayedMessageChannel(IMessageChannel inner) => _inner = inner;

        public bool IsConnected => _inner.IsConnected;

        public event Action<ReadOnlyMemory<byte>> MessageReceived
        {
            add => _inner.MessageReceived += value;
            remove => _inner.MessageReceived -= value;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, SendOptions options = null,
            CancellationToken cancellationToken = default)
        {
            int delayMs = UniNetSim.DelayMs;
            if (delayMs <= 0) return _inner.SendAsync(payload, options, cancellationToken);

            // shortcut: a cancellation that fires while the payload is held is ignored — no UniNet send path cancels
            UniNetSim.Schedule(_inner, payload, options, delayMs);
            return default;
        }

        public void Dispose() => _inner.Dispose();
    }
}
