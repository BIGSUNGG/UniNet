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

        /// <summary>
        /// Protocol version declared during the handshake (ADR-0028). Defaults to the UniNet package version — same-package ends
        /// always match. Set a game build version to force clients and servers from different builds apart; a mismatch is rejected
        /// server-side with a VersionMismatch kick.
        /// </summary>
        public string ProtocolVersion { get; set; }

        /// <summary>
        /// Server-only — how long an undeclared connection may stay attached before a VersionMismatch kick (CON-001), in ms.
        /// Default 10000; 0 disables the sweep (an undeclared connection stays attached but receives no game data —
        /// ownership reassignment, catch-up, ClientConnected, replication, spawns, and game ServerRpcs are all held
        /// until its wire-7 version declaration is accepted). Applies only while ProtocolVersion checking is active.
        /// </summary>
        public int VersionDeclareTimeoutMs { get; set; } = 10000;

        /// <summary>Connect timeout in ms (0 = default, about 5 seconds).</summary>
        public int ConnectTimeoutMs { get; set; }

        /// <summary>
        /// Disconnect-detection timeout in ms (ADR-0033) — with no packets from the peer for this long, the peer is
        /// considered disconnected (the RUDP transport's only half-open detection signal). 0 = transport default
        /// (5000 ms). Raise for lossy/mobile links (10000–15000), lower for faster dead-peer detection. Note the
        /// built-in 1 Hz RTT probe (ADR-0029) keeps traffic flowing, so this window measures genuine silence.
        /// </summary>
        public int DisconnectTimeoutMs { get; set; }

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
            DisconnectTimeoutMs = DisconnectTimeoutMs,
            MaxConnections = MaxConnections,
            EnableCrc32c = EnableCrc32c,
            ServerCertificate = ServerCertificate,
            TlsTargetHost = TlsTargetHost,
            TlsAllowNameOnlyCertificateMatch = TlsAllowNameOnlyCertificateMatch,
            TlsCertificateValidation = TlsCertificateValidation,
        };
    }
}
