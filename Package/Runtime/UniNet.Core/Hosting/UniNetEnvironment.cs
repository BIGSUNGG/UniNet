using System;
using System.Collections.Concurrent;
using System.Threading;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Process-wide UniNet environment — holds the server and client runtimes (both in host mode) and the hub
    /// factory registered by generated code. Dispatches received on network threads are queued to the main
    /// thread so Unity objects are touched safely.
    /// </summary>
    public static class UniNetEnvironment
    {
        private static readonly ConcurrentQueue<Action> MainQueue = new();

        // FR-06 — finite main-queue bound. _mainQueueDepth is incremented before enqueue and decremented at dequeue,
        // so under enqueue races the cap is approximate (a safety bound, not an exact invariant); the drain path is exact.
        private static long _mainQueueDepth;
        private static long _droppedMainActions;
        private static long _droppedReplicateEntries;

        /// <summary>FR-06 — default cap on queued main-thread actions (see <see cref="MainQueueCapacity"/>).</summary>
        public const int DefaultMainQueueCapacity = 65536;

        /// <summary>
        /// FR-06 — hard cap on actions waiting for <see cref="PumpMain"/>. A healthy pump drains every frame, so real
        /// depth stays in the tens; the cap exists to keep a blocked or starved main thread from growing the queue without
        /// bound. An action enqueued past the cap is DROPPED but never silently: <see cref="LogFault"/> fires and
        /// <see cref="DroppedMainActions"/> counts it (throwing instead would kill network dispatch threads — the least
        /// destructive explicit failure). Default <see cref="DefaultMainQueueCapacity"/> (≈ a few MB of delegates worst case).
        /// </summary>
        public static int MainQueueCapacity { get; set; } = DefaultMainQueueCapacity;

        /// <summary>FR-06 — cumulative actions dropped because the main queue was at capacity (explicit overflow observability).</summary>
        public static long DroppedMainActions => Interlocked.Read(ref _droppedMainActions);

        /// <summary>
        /// FR-06 — cumulative replication/RPC entries dropped at the client apply step (no live object at the
        /// (netId, subId) slot, or an unregistered methodId — version-skew or spawn/destroy races). Counted by the
        /// generated batch/single dispatch paths so the drop is observed instead of silently skipped.
        /// </summary>
        public static long DroppedReplicateEntries => Interlocked.Read(ref _droppedReplicateEntries);


        /// <summary>This process's server runtime (set when the server/host starts).</summary>
        public static NetworkServer Server { get; private set; }

        /// <summary>Raised the moment a server instance is set — on the main thread, BEFORE the server can accept
        /// any connection. Lets scene components (NetSession) hook lifecycle events without a polling race
        /// (a polling hook can lose a fast loopback ClientConnected that pumps before the first Update).</summary>
        public static event Action<NetworkServer> ServerChanged;

        /// <summary>This process's client runtime (set when the client/host starts).</summary>
        public static NetworkClient Client { get; private set; }

        /// <summary>Hub factory produced by the code generator (set by the user assembly's registration hook).</summary>
        public static UniNetHubFactory HubFactory { get; private set; }

        /// <summary>Client hub send slot — the generated client hub registers itself here on connect (assembly-agnostic).</summary>
        public static IUniNetClientSender ClientSender { get; private set; }

        /// <summary>
        /// Protocol version this endpoint declares in the handshake (ADR-0028). Set from UniNetEndpointOptions.ProtocolVersion
        /// by UniNetManager.ClientAsync/HostAsync; the generated client hub sends it on connect. Defaults to the package version.
        /// </summary>
        public static string LocalProtocolVersion { get; set; } = UniNetInfo.Version;

        /// <summary>
        /// Game credential this endpoint sends with its wire-7 version declaration (CON-002). Set from
        /// UniNetEndpointOptions.ConnectionCredential by UniNetManager.ClientAsync; the generated client hub appends it to
        /// the declaration payload. Empty (default) sends an empty credential — the server's approval callback decides what
        /// that means (games that gate on a token reject it; open servers pass it).
        /// </summary>
        public static string LocalCredential { get; set; } = string.Empty;

        /// <summary>
        /// Engine-level fault logger — the Unity layer wires UnityEngine.Debug.LogException here at startup so Core
        /// stays engine-agnostic. Core's replication tick routes user-serializer exceptions (custom Write/Read/Equals,
        /// see ADR-0020) through this hook: a faulty serializer skips its sub-object instead of killing the tick.
        /// Defaults to a no-op so engine-less hosts keep compiling.
        /// </summary>
        public static Action<Exception> LogFault { get; set; } = static _ => { };

        /// <summary>Called by generated code to register its hub factory.</summary>
        public static void RegisterHubFactory(UniNetHubFactory factory)
        {
            HubFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>Sets the server runtime (UniNetManager only). Raises ServerChanged synchronously.</summary>
        public static void SetServer(NetworkServer server)
        {
            Server = server;
            ServerChanged?.Invoke(server);
        }

        /// <summary>Sets the client runtime (UniNetManager only).</summary>
        public static void SetClient(NetworkClient client) => Client = client;

        /// <summary>Sets the client hub send slot (generated client hub only).</summary>
        public static void SetClientSender(IUniNetClientSender sender) => ClientSender = sender;

        /// <summary>
        /// Schedules work for the main thread to run (callable from network threads). FR-06 — past
        /// <see cref="MainQueueCapacity"/> the action is dropped with an explicit fault (never a silent loss, never a
        /// throw that would kill the dispatch thread).
        /// </summary>
        public static void QueueOnMain(Action action)
        {
            if (action == null) return;
            if (Interlocked.Increment(ref _mainQueueDepth) > MainQueueCapacity)
            {
                Interlocked.Decrement(ref _mainQueueDepth);
                Interlocked.Increment(ref _droppedMainActions);
                LogFault(new InvalidOperationException(
                    $"[UniNet] main-thread queue over capacity ({MainQueueCapacity}) — action dropped. PumpMain is not keeping up (blocked or starved main thread); UniNet keeps the queue finite instead of growing without bound."));
                return;
            }
            MainQueue.Enqueue(action);
        }

        /// <summary>FR-06 — counts a dropped apply-time entry (generated dispatch paths; see <see cref="DroppedReplicateEntries"/>).</summary>
        public static void CountDroppedReplicateEntry() => Interlocked.Increment(ref _droppedReplicateEntries);

        /// <summary>Number of main-queue actions waiting for the next pump drain (ADR-0029 queue-depth observability).</summary>
        public static int PendingMainActions => MainQueue.Count;

        /// <summary>
        /// Runs ALL scheduled work — call from the main thread (the Unity driver Update). The full-drain contract is
        /// unchanged (ordering and completion); the FR-06 capacity bounds growth at enqueue time, not here.
        /// </summary>
        public static void PumpMain()
        {
            while (MainQueue.TryDequeue(out var action))
            {
                Interlocked.Decrement(ref _mainQueueDepth);
                action();
            }
        }
    }
}
