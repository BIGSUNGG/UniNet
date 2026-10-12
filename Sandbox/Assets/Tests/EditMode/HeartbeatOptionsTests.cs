using NUnit.Framework;
using UniNet.Core.Hosting;

namespace UniNet.Tests
{
    /// <summary>
    /// Heartbeat/keep-alive tuning exposure (ADR-0033) — DisconnectTimeoutMs flows from UniNetEndpointOptions
    /// through DRPC's RpcEndpointOptions into the RUDP transport options (the actual enforcement is the
    /// transport's disconnect detector; this pins the passthrough contract).
    /// </summary>
    public sealed class HeartbeatOptionsTests
    {
        [Test]
        public void DisconnectTimeoutMs는_상위_전달_계약을_지킨다()
        {
            var options = new UniNetEndpointOptions { DisconnectTimeoutMs = 12345 };

            var rpc = options.ToRpcEndpointOptions();
            Assert.AreEqual(12345, rpc.DisconnectTimeoutMs, "DRPC 엔드포인트 옵션에 전달");

            var transport = rpc.ToTransportOptions();
            Assert.AreEqual(12345, transport.DisconnectTimeout, "RUDP 전송 옵션에 전달 (ADR-0033)");
        }

        [Test]
        public void 기본값_0은_전송_기본값을_유지한다()
        {
            var options = new UniNetEndpointOptions();

            var transport = options.ToRpcEndpointOptions().ToTransportOptions();

            Assert.AreEqual(0, options.DisconnectTimeoutMs, "UniNet 기본 = 미설정");
            Assert.AreEqual(Communication.Network.RUDP.RudpTransportOptions.DefaultDisconnectTimeoutMs,
                transport.DisconnectTimeout, "0 → 전송 기본값 5000ms 유지");
        }
    }
}
