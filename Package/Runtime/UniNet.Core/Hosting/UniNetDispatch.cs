using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DRPC;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 전역 디스패치 테이블 — 각 어셈블리의 생성 코드가 자기 메서드를 등록하면, 어떤 어셈블리의 허브가
    /// 리슨하든 전체 메서드를 수신 디스패치한다 (다중 어셈블리 지원).
    /// 재등록은 같은 내용의 멱등 갱신으로 간주해 조용히 덮어쓴다(도메인 재로드 안전) — 서로 다른 핸들이 같은 ID를
    /// 요구하는 충돌은 컴파일 타임 진단(UNINET007)이 어셈블리 내부에서 1차 방어한다.
    /// </summary>
    public static class UniNetDispatch
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<int, Entry> ServerTable = new();
        private static readonly Dictionary<int, Entry> ClientTable = new();

        /// <summary>수신 핸들 한 건 (핸들러 + 전달 모드).</summary>
        public readonly struct Entry
        {
            /// <summary>발신 연결 ID와 페이로드를 받아 디스패치하는 핸들러 (응답은 사용하지 않음).</summary>
            public Func<long, byte[], Task<byte[]>> Handler { get; }

            /// <summary>전달 모드.</summary>
            public RpcDeliveryMode Mode { get; }

            public Entry(Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            {
                Handler = handler;
                Mode = mode;
            }
        }

        /// <summary>클라→서버 수신 핸들 등록 (멱등 — 같은 ID 재등록은 덮어쓰기).</summary>
        public static void RegisterServer(int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            => Add(ServerTable, methodId, handler, mode);

        /// <summary>서버→클라 수신 핸들 등록 (멱등 — 같은 ID 재등록은 덮어쓰기).</summary>
        public static void RegisterClient(int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            => Add(ClientTable, methodId, handler, mode);

        /// <summary>서버 수신 테이블 스냅샷 (허브 생성자가 복사).</summary>
        public static IReadOnlyDictionary<int, Entry> ServerHandlers()
        {
            lock (Gate) return new Dictionary<int, Entry>(ServerTable);
        }

        /// <summary>클라 수신 테이블 스냅샷.</summary>
        public static IReadOnlyDictionary<int, Entry> ClientHandlers()
        {
            lock (Gate) return new Dictionary<int, Entry>(ClientTable);
        }

        private static void Add(Dictionary<int, Entry> table, int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
        {
            lock (Gate)
            {
                // 멱등 재등록 — 기존 핸들과 다른 델리게이트가 같은 ID에 오면 추적 가능하게 표시 (조용한 충돌 방지)
                if (table.TryGetValue(methodId, out var existing) && !ReferenceEquals(existing.Handler, handler))
                    System.Diagnostics.Debug.WriteLine($"[UniNet] 메서드 ID {methodId} 재등록이 기존 핸들을 덮어씁니다 (어셈블리 충돌 가능성)");
                table[methodId] = new Entry(handler, mode);
            }
        }
    }
}
