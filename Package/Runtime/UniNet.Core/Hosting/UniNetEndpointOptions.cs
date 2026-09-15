using System;
using System.Collections.Generic;
using DRPC.Shared.Network;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// UniNet 연결 설정 — DRPC RpcEndpointOptions를 감싸 노출한다 (게임 코드는 DRPC·MP 타입을 직접 몰라도 된다).
    /// 보안(연결 키·CRC32c·DTLS 1.2 인증서/핀닝)·속도(타임아웃·연결 상한) 기능은 DRPC 전달 모드와 동등하게 지원한다.
    /// </summary>
    public sealed class UniNetEndpointOptions
    {
        /// <summary>연결 인증 키 (null이면 전송 스택 기본값).</summary>
        public string ConnectionKey { get; set; }

        /// <summary>접속 타임아웃 ms (0 = 기본값 약 5초).</summary>
        public int ConnectTimeoutMs { get; set; }

        /// <summary>동시 접속 상한 (0 = 무제한, 서버 전용).</summary>
        public int MaxConnections { get; set; }

        /// <summary>CRC32c 무결성 검사 — 양단 같게 설정해야 한다.</summary>
        public bool EnableCrc32c { get; set; }

        /// <summary>DTLS 1.2 서버 인증서 (서버 전용 — 설정 시 암호화 활성화).</summary>
        public System.Security.Cryptography.X509Certificates.X509Certificate2 ServerCertificate { get; set; }

        /// <summary>DTLS 대상 호스트명 (클라 전용).</summary>
        public string TlsTargetHost { get; set; }

        /// <summary>인증서 이름만 매칭 허용 (클라 전용).</summary>
        public bool TlsAllowNameOnlyCertificateMatch { get; set; }

        /// <summary>인증서 핀닝 콜백 — DER 바이트를 받아 수락 여부를 돌려준다 (클라 전용).</summary>
        public Communication.Network.RUDP.RudpRemoteCertificateValidation TlsCertificateValidation { get; set; }

        /// <summary>DRPC 엔드포인트 옵션으로 변환한다.</summary>
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
