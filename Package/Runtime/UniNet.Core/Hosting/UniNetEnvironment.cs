using System;
using System.Collections.Concurrent;

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


        /// <summary>This process's server runtime (set when the server/host starts).</summary>
        public static NetworkServer Server { get; private set; }

        /// <summary>This process's client runtime (set when the client/host starts).</summary>
        public static NetworkClient Client { get; private set; }

        /// <summary>Hub factory produced by the code generator (set by the user assembly's registration hook).</summary>
        public static UniNetHubFactory HubFactory { get; private set; }

        /// <summary>Client hub send slot — the generated client hub registers itself here on connect (assembly-agnostic).</summary>
        public static IUniNetClientSender ClientSender { get; private set; }

        /// <summary>Called by generated code to register its hub factory.</summary>
        public static void RegisterHubFactory(UniNetHubFactory factory)
        {
            HubFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>Sets the server runtime (UniNetManager only).</summary>
        public static void SetServer(NetworkServer server) => Server = server;

        /// <summary>Sets the client runtime (UniNetManager only).</summary>
        public static void SetClient(NetworkClient client) => Client = client;

        /// <summary>Sets the client hub send slot (generated client hub only).</summary>
        public static void SetClientSender(IUniNetClientSender sender) => ClientSender = sender;

        /// <summary>Schedules work for the main thread to run (callable from network threads).</summary>
        public static void QueueOnMain(Action action)
        {
            if (action != null) MainQueue.Enqueue(action);
        }

        /// <summary>Runs all scheduled work — call from the main thread (the Unity driver Update).</summary>
        public static void PumpMain()
        {
            while (MainQueue.TryDequeue(out var action))
                action();
        }
    }
}
