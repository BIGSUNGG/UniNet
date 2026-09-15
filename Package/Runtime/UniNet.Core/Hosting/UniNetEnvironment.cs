using System;
using System.Collections.Concurrent;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// UniNet 프로세스 전역 환경 — 서버·클라 런타임(호스트 모드에선 둘 다)과 생성 코드가 등록하는 허브 팩토리를 보관한다.
    /// 네트워크 스레드에서 받은 디스패치는 메인 스레드 큐로 넘겨 Unity 객체를 안전하게 만진다.
    /// </summary>
    public static class UniNetEnvironment
    {
        private static readonly ConcurrentQueue<Action> MainQueue = new();


        /// <summary>현 프로세스의 서버 런타임 (서버/호스트 시작 시 설정).</summary>
        public static NetworkServer Server { get; private set; }

        /// <summary>현 프로세스의 클라이언트 런타임 (클라/호스트 시작 시 설정).</summary>
        public static NetworkClient Client { get; private set; }

        /// <summary>코드 생성기가 내놓은 허브 팩토리 (사용자 어셈블리의 등록 훅이 설정).</summary>
        public static UniNetHubFactory HubFactory { get; private set; }

        /// <summary>클라 허브 송신 슬롯 — 생성 클라 허브가 접속 시 자신을 등록한다 (어셈블리 무관).</summary>
        public static IUniNetClientSender ClientSender { get; private set; }

        /// <summary>생성 코드가 자신의 허브 팩토리를 등록한다.</summary>
        public static void RegisterHubFactory(UniNetHubFactory factory)
        {
            HubFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>서버 런타임을 설정한다 (UniNetManager 전용).</summary>
        public static void SetServer(NetworkServer server) => Server = server;

        /// <summary>클라 런타임을 설정한다 (UniNetManager 전용).</summary>
        public static void SetClient(NetworkClient client) => Client = client;

        /// <summary>클라 허브 송신 슬롯을 설정한다 (생성 클라 허브 전용).</summary>
        public static void SetClientSender(IUniNetClientSender sender) => ClientSender = sender;

        /// <summary>네트워크 스레드가 메인 스레드에서 실행할 작업을 예약한다.</summary>
        public static void QueueOnMain(Action action)
        {
            if (action != null) MainQueue.Enqueue(action);
        }

        /// <summary>메인 스레드(Unity 드라이버 Update)가 예약된 작업을 전부 실행한다.</summary>
        public static void PumpMain()
        {
            while (MainQueue.TryDequeue(out var action))
                action();
        }
    }
}
