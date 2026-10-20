using System.Collections.Generic;
using DRPC;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>Shared P3 replication test support — recording system channel plus scene registration helper.</summary>
    internal static class ReplicationTestSupport
    {
        /// <summary>Registers a scene object exactly as the live path does (keyed by the component's NetId). Caller owns DestroyImmediate.</summary>
        internal static T RegisterScene<T>(NetworkServer server, string name) where T : NetworkBehaviour
        {
            var go = new GameObject(name);
            var comp = go.AddComponent<T>();
            server.RegisterSceneObject(comp.NetId, new object[] { comp });
            return comp;
        }
    }

    /// <summary>Test channel that records every system message instead of sending it.</summary>
    internal class RecordingChannel : IUniNetSystemChannel
    {
        public long UniNetConnId { get; set; }
        internal readonly List<string> Order = new();   // cross-message send order — OWN-001 baseline assertions (owner before state/spawn)
        internal readonly List<(ulong netId, byte subId, int methodId, byte[] payload)> Replicates = new();
        internal readonly List<(RpcDeliveryMode mode, List<(ulong netId, byte subId, int methodId, byte[] payload)> entries)> Batches = new();
        internal readonly List<ulong> Spawns = new();
        internal readonly List<ulong> Destroys = new();
        internal readonly List<(ulong netId, long owner)> Owners = new();
        internal readonly List<double> TimeSyncs = new();
        internal readonly List<UniNet.Core.UniNetDisconnectReason> DisconnectNotices = new();
        internal readonly List<string> LoadScenes = new();

        public void Clear()
        {
            Order.Clear();
            Replicates.Clear();
            Batches.Clear();
            Spawns.Clear();
            Destroys.Clear();
            Owners.Clear();
            TimeSyncs.Clear();
            DisconnectNotices.Clear();
            LoadScenes.Clear();
        }

        public void SendWelcome(long connId, string serverVersion, string sessionToken) { }
        public void SendOwnerUpdate(ulong netId, long ownerConnId)
        {
            Owners.Add((netId, ownerConnId));
            Order.Add($"owner:{netId}:{ownerConnId}");
        }
        public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload)
        {
            if (payload != null && payload.Length > 0)
            {
                Replicates.Add((netId, subId, methodId, payload));
                Order.Add($"replicate:{netId}:{subId}");
            }
        }

        public void SendReplicateBatch(byte[] blob, RpcDeliveryMode mode)
        {
            var r = new global::MessageProtocol.Serialize.MessageBufferReader(blob);
            int count = r.ReadUInt16();
            var entries = new List<(ulong netId, byte subId, int methodId, byte[] payload)>(count);
            for (int i = 0; i < count; i++)
            {
                ulong netId = r.ReadUInt64();
                byte subId = r.ReadByte();
                int methodId = r.ReadInt32();
                int len = r.ReadInt32();
                byte[] payload = len > 0 ? r.ReadBytes(len).ToArray() : System.Array.Empty<byte>();
                entries.Add((netId, subId, methodId, payload));
                if (payload.Length > 0)
                    Replicates.Add((netId, subId, methodId, payload));   // merged view — pre-batching assertions keep passing
            }
            Batches.Add((mode, entries));
        }

        public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
            ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states)
        {
            Spawns.Add(netId);
            Order.Add($"spawn:{netId}");
        }
        public void SendDestroy(ulong netId) => Destroys.Add(netId);
        public void SendTimeSync(double serverTime) => TimeSyncs.Add(serverTime);
        public System.Threading.Tasks.Task SendDisconnectNoticeAsync(UniNet.Core.UniNetDisconnectReason reason)
        {
            DisconnectNotices.Add(reason);
            return System.Threading.Tasks.Task.CompletedTask;
        }
        public void SendLoadScene(string sceneName) => LoadScenes.Add(sceneName);
        public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) { }
    }
}
