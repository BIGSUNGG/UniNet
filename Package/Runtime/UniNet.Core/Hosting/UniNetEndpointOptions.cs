using System;
using System.Collections.Generic;
using DRPC.Shared.Network;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// UniNet connection settings — wraps DRPC's RpcEndpointOptions so game code never needs to know DRPC or
    /// MessageProtocol types directly. Supports the same security (connection key, CRC32c, DTLS 1.2
    /// certificate/pinning) and tuning (timeouts, connection cap) features as DRPC's delivery modes.
    /// </summary>
    public sealed class UniNetEndpointOptions
    {
        /// <summary>Connection authentication key (null = transport stack default).</summary>
        public string ConnectionKey { get; set; }

        /// <summary>Connect timeout in ms (0 = default, about 5 seconds).</summary>
        public int ConnectTimeoutMs { get; set; }

        /// <summary>Maximum simultaneous connections (0 = unlimited, server only).</summary>
        public int MaxConnections { get; set; }

        /// <summary>CRC32c integrity check — must match on both sides.</summary>
        public bool EnableCrc32c { get; set; }

        /// <summary>DTLS 1.2 server certificate (server only — enabling it turns on encryption).</summary>
        public System.Security.Cryptography.X509Certificates.X509Certificate2 ServerCertificate { get; set; }

        /// <summary>DTLS target host name (client only).</summary>
        public string TlsTargetHost { get; set; }

        /// <summary>Allow matching by certificate name only (client only).</summary>
        public bool TlsAllowNameOnlyCertificateMatch { get; set; }

        /// <summary>Certificate pinning callback — receives the DER bytes and returns whether to accept (client only).</summary>
        public Communication.Network.RUDP.RudpRemoteCertificateValidation TlsCertificateValidation { get; set; }

        /// <summary>Converts these options to DRPC endpoint options.</summary>
        public RpcEndpointOptions ToRpcEndpointOptions() => new()
        {
            ConnectionKey = ConnectionKey,
            ConnectTimeoutMs = ConnectTimeoutMs,
            MaxConnections = MaxConnections,
            EnableCrc32c = EnableCrc32c,
            ServerCertificate = ServerCertificate,
            TlsTargetHost = TlsTargetHost,
            TlsAllowNameOnlyCertificateMatch = TlsAllowNameOnlyCertificateMatch,
            TlsCertificateValidation = TlsCertificateValidation,
        };
    }
}
