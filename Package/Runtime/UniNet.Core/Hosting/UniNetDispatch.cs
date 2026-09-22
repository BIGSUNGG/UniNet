using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DRPC;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Global dispatch table — generated code in each assembly registers its methods here, so any assembly's
    /// hub can dispatch the full method set on receive (multi-assembly support).
    /// Re-registration with the same content is treated as an idempotent update and silently overwrites
    /// (safe across domain reloads). Conflicting handles demanding the same ID are first caught within an
    /// assembly by compile-time diagnostics (UNINET007).
    /// </summary>
    public static class UniNetDispatch
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<int, Entry> ServerTable = new();
        private static readonly Dictionary<int, Entry> ClientTable = new();

        /// <summary>One registered receive handle (handler + delivery mode).</summary>
        public readonly struct Entry
        {
            /// <summary>Handler dispatched with the sender connection ID and payload (the response is unused).</summary>
            public Func<long, byte[], Task<byte[]>> Handler { get; }

            /// <summary>Delivery mode.</summary>
            public RpcDeliveryMode Mode { get; }

            public Entry(Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            {
                Handler = handler;
                Mode = mode;
            }
        }

        /// <summary>Registers a client→server receive handle (idempotent — re-registering the same ID overwrites).</summary>
        public static void RegisterServer(int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            => Add(ServerTable, methodId, handler, mode);

        /// <summary>Registers a server→client receive handle (idempotent — re-registering the same ID overwrites).</summary>
        public static void RegisterClient(int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            => Add(ClientTable, methodId, handler, mode);

        /// <summary>Snapshot of the server receive table (hub constructors copy it).</summary>
        public static IReadOnlyDictionary<int, Entry> ServerHandlers()
        {
            lock (Gate) return new Dictionary<int, Entry>(ServerTable);
        }

        /// <summary>Snapshot of the client receive table.</summary>
        public static IReadOnlyDictionary<int, Entry> ClientHandlers()
        {
            lock (Gate) return new Dictionary<int, Entry>(ClientTable);
        }

        private static void Add(Dictionary<int, Entry> table, int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
        {
            lock (Gate)
            {
                // Idempotent re-registration — flag a different delegate claiming the same ID so it stays traceable (no silent collisions)
                if (table.TryGetValue(methodId, out var existing) && !ReferenceEquals(existing.Handler, handler))
                    System.Diagnostics.Debug.WriteLine($"[UniNet] 메서드 ID {methodId} 재등록이 기존 핸들을 덮어씁니다 (어셈블리 충돌 가능성)");
                table[methodId] = new Entry(handler, mode);
            }
        }
    }
}
