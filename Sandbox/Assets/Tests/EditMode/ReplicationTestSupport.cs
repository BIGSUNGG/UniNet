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
    internal sealed class RecordingChannel : IUniNetSystemChannel
    {
        public long UniNetConnId { get; set; }
        internal readonly List<(ulong netId, byte subId, int methodId, byte[] payload)> Replicates = new();
        internal readonly List<ulong> Spawns = new();
        internal readonly List<ulong> Destroys = new();
        internal readonly List<(ulong netId, long owner)> Owners = new();
        internal readonly List<double> TimeSyncs = new();

        public void Clear()
        {
            Replicates.Clear();
            Spawns.Clear();
            Destroys.Clear();
            Owners.Clear();
        }

        public void SendWelcome(long connId) { }
        public void SendOwnerUpdate(ulong netId, long ownerConnId) => Owners.Add((netId, ownerConnId));
        public void SendReplicate(ulong netId, byte subId, int methodId, byte[] payload)
        {
            if (payload != null && payload.Length > 0)
                Replicates.Add((netId, subId, methodId, payload));
        }

        public void SendSpawn(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
            byte subCount, ulong[] typeKeys, byte[][] states) => Spawns.Add(netId);
        public void SendDestroy(ulong netId) => Destroys.Add(netId);
        public void SendTimeSync(double serverTime) => TimeSyncs.Add(serverTime);
        public void UniNetSend(int methodId, byte[] payload, RpcDeliveryMode mode) { }
    }
}
