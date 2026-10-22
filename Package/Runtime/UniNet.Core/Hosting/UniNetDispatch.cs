using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DRPC;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Global dispatch table — generated code in each assembly registers its methods here, so any assembly's
    /// hub can dispatch the full method set on receive (multi-assembly support).
    /// Registration policy (GEN-001): reserved wire IDs (&lt;64) overwrite silently — each assembly registers its own
    /// internal handler under the same ID, so cross-assembly overwrite there is by design. User IDs (>=64, the FNV-1a
    /// range) are idempotent for the same handler method and mode; a different handler (or a different mode) claiming
    /// a taken user ID is rejected via LogFault and the first registration wins. Same-assembly conflicts are still
    /// caught earlier by compile-time diagnostics (UNINET007).
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

        /// <summary>Registers a client→server receive handle. Re-registering the same handler method and mode is an
        /// idempotent no-op; a different handler (or mode) on a taken user ID is rejected via LogFault — first-wins (GEN-001).</summary>
        public static void RegisterServer(int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            => Add("ServerTable", ServerTable, methodId, handler, mode);

        /// <summary>Registers a server→client receive handle. Re-registering the same handler method and mode is an
        /// idempotent no-op; a different handler (or mode) on a taken user ID is rejected via LogFault — first-wins (GEN-001).</summary>
        public static void RegisterClient(int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
            => Add("ClientTable", ClientTable, methodId, handler, mode);

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

        private static void Add(string tableName, Dictionary<int, Entry> table, int methodId, Func<long, byte[], Task<byte[]>> handler, RpcDeliveryMode mode)
        {
            lock (Gate)
            {
                if (!table.TryGetValue(methodId, out var existing))
                {
                    table[methodId] = new Entry(handler, mode);
                    return;
                }

                // Reserved wire IDs (<64): every assembly registers its own internal handler under the same ID
                // (e.g. wires 13/14), so the silent overwrite is by design — multi-assembly wiring depends on it.
                if (methodId < 64)
                {
                    table[methodId] = new Entry(handler, mode);
                    return;
                }

                // User IDs (>=64, the FNV-1a output range). Delegate identity must be method identity, not instance
                // identity: a method-group argument produces a fresh delegate on every call (registrations rerun after
                // domain reloads), so comparing delegate references would misreport an idempotent re-registration.
                if (ReferenceEquals(existing.Handler.Target, handler.Target) && Equals(existing.Handler.Method, handler.Method))
                {
                    if (existing.Mode == mode)
                        return;   // idempotent re-registration — entry values are identical, keep the original

                    // Same handler method with a different delivery mode is still a conflict (strict): the delivery mode
                    // is part of the registration contract and must not silently diverge from what registered first.
                    UniNetEnvironment.LogFault(new InvalidOperationException(
                        $"[UniNet] {tableName}: user method ID {methodId} re-registration with a different delivery mode ({existing.Mode} -> {mode}) — keeping the first registration."));
                    return;   // first-wins — existing entry stays
                }

                // Genuine collision: a different handler claims an ID already taken (e.g. a cross-assembly FNV-1a clash,
                // which compile-time UNINET007 cannot see). Reject loudly and keep the first registration so the losing
                // handler's absence is observable instead of the dispatch being silently hijacked.
                UniNetEnvironment.LogFault(new InvalidOperationException(
                    $"[UniNet] {tableName}: user method ID {methodId} collision — existing {Describe(existing.Handler)}, new {Describe(handler)} — keeping the first registration."));
            }

            static string Describe(Func<long, byte[], Task<byte[]>> handler)
            {
                var method = handler.Method;
                return $"{method.DeclaringType?.FullName}.{method.Name}";
            }
        }
    }
}
