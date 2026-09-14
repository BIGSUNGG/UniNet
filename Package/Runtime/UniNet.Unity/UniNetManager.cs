using System;
using System.Threading.Tasks;

namespace UniNet.Unity
{
    /// <summary>연결 수명주기 진입점 — 서버·클라이언트·호스트 시작을 담당한다.</summary>
    public static class UniNetManager
    {
        /// <summary>전용 서버를 시작하고 클라이언트 접속을 대기한다 (서버 권위).</summary>
        public static Task ServerAsync(int port)
            => throw new NotImplementedException("사용법 확정용 스텁 — P1(NetworkManager)에서 구현");

        /// <summary>클라이언트로 지정 주소의 서버에 접속한다.</summary>
        public static Task ClientAsync(string address, int port)
            => throw new NotImplementedException("사용법 확정용 스텁 — P1(NetworkManager)에서 구현");

        /// <summary>서버 + 클라이언트를 한 프로세스에서 시작한다 (개발·테스트용).</summary>
        public static Task HostAsync(int port)
            => throw new NotImplementedException("사용법 확정용 스텁 — P1(NetworkManager)에서 구현");
    }
}
