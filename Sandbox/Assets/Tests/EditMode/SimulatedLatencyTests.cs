using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Communication.Shared.Channels;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// UniNetSim outbound latency (gate runs under a 100 ms one-way delay) — a payload scheduled with a nonzero
    /// delay is held until the delay elapses and delivered after; with DelayMs 0 the payload goes straight through.
    /// Drives the decorator + scheduler directly against a recording channel, with a controllable UniNetTime clock.
    /// </summary>
    public sealed class SimulatedLatencyTests
    {
        private double _clock;
        private Func<double> _originalClock;
        private int _originalDelayMs;
        private readonly List<Exception> _faults = new List<Exception>();
        private Action<Exception> _originalLogFault;

        [SetUp]
        public void SetUp()
        {
            _originalClock = UniNetTime.LocalClock;
            _originalDelayMs = UniNetSim.DelayMs;
            _clock = 1000.0;   // arbitrary non-zero start — far from the monotonicity guard's initial state
            UniNetTime.Reset();
            UniNetTime.LocalClock = () => _clock;
            _originalLogFault = UniNetEnvironment.LogFault;
            _faults.Clear();
            UniNetEnvironment.LogFault = e => _faults.Add(e);
        }

        [TearDown]
        public void TearDown()
        {
            UniNetTime.LocalClock = _originalClock;
            UniNetTime.Reset();
            UniNetSim.DelayMs = _originalDelayMs;
            UniNetEnvironment.LogFault = _originalLogFault;
        }

        [Test]
        public void DelayedSend_IsHeldUntilTheDelayElapses()
        {
            var transport = new RecordingChannel();
            var channel = UniNetSim.Wrap(transport);
            UniNetSim.DelayMs = 100;

            channel.SendAsync(new byte[] { 1, 2, 3 }).AsTask().GetAwaiter().GetResult();
            UniNetSim.Pump();
            Assert.AreEqual(0, transport.Sent.Count, "held: the delay has not even started elapsing");

            _clock += 0.05;   // half the delay
            UniNetSim.Pump();
            Assert.AreEqual(0, transport.Sent.Count, "not delivered before the delay elapses");

            _clock += 0.06;   // past 100 ms
            UniNetSim.Pump();
            Assert.AreEqual(1, transport.Sent.Count, "delivered once the delay elapsed");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, transport.Sent[0], "payload delivered unchanged");
        }

        [Test]
        public void ZeroDelay_SendsImmediately()
        {
            var transport = new RecordingChannel();
            var channel = UniNetSim.Wrap(transport);
            UniNetSim.DelayMs = 0;

            channel.SendAsync(new byte[] { 9 }).AsTask().GetAwaiter().GetResult();
            Assert.AreEqual(1, transport.Sent.Count, "off means straight through — no pump needed");
        }

        [Test]
        public void HeldSend_ToAPeerThatLeft_IsDroppedWithoutAFault()
        {
            var transport = new RecordingChannel();
            var channel = UniNetSim.Wrap(transport);
            UniNetSim.DelayMs = 100;

            channel.SendAsync(new byte[] { 1 }).AsTask().GetAwaiter().GetResult();
            transport.Connected = false;
            _clock += 0.2;
            UniNetSim.Pump();

            Assert.AreEqual(0, transport.Sent.Count, "an undeliverable payload is not handed to a closed transport");
            Assert.AreEqual(0, _faults.Count, "an in-flight loss at teardown is not a fault");
        }

        [Test]
        public void HeldSend_PeerLeavesDuringTheSend_IsNotAFault()
        {
            var transport = new RecordingChannel { RefuseAndDisconnect = true };
            var channel = UniNetSim.Wrap(transport);
            UniNetSim.DelayMs = 100;

            channel.SendAsync(new byte[] { 1 }).AsTask().GetAwaiter().GetResult();
            _clock += 0.2;
            UniNetSim.Pump();

            Assert.AreEqual(0, _faults.Count, "the peer left mid-send — an in-flight loss, not a fault");
        }

        [Test]
        public void HeldSend_RefusedWhileConnected_IsStillReported()
        {
            var transport = new RecordingChannel { RefuseWhileConnected = true };
            var channel = UniNetSim.Wrap(transport);
            UniNetSim.DelayMs = 100;

            channel.SendAsync(new byte[] { 1 }).AsTask().GetAwaiter().GetResult();
            _clock += 0.2;
            UniNetSim.Pump();

            Assert.AreEqual(1, _faults.Count, "a refusal on a live connection stays visible");
        }

        private sealed class RecordingChannel : IMessageChannel
        {
            public readonly List<byte[]> Sent = new List<byte[]>();
            public bool Connected = true;
            public bool RefuseWhileConnected;
            public bool RefuseAndDisconnect;

            public bool IsConnected => Connected;

            public event Action<ReadOnlyMemory<byte>> MessageReceived
            {
                add { }
                remove { }
            }

            public ValueTask SendAsync(ReadOnlyMemory<byte> payload, SendOptions options = null,
                CancellationToken cancellationToken = default)
            {
                if (RefuseAndDisconnect)
                {
                    Connected = false;
                    return Refused();
                }

                if (RefuseWhileConnected) return Refused();

                Sent.Add(payload.ToArray());
                return default;
            }

            private static ValueTask Refused() =>
                new ValueTask(Task.FromException(new InvalidOperationException("Cannot send: the peer is not connected.")));

            public void Dispose() { }
        }
    }
}
