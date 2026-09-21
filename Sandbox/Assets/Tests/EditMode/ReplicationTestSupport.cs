using System.Collections.Generic;
using DRPC;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>P3 리플리케이션 테스트 공용 — 시스템 메시지 기록 채널 + 씬 등록 헬퍼.</summary>
    internal static class ReplicationTestSupport
    {
        /// <summary>씬 오브젝트 등록 (실제 경로와 동일 — 컴포넌트 NetId 키). DestroyImmediate는 호출자 책임.</summary>
        internal static T RegisterScene<T>(NetworkServer server, string name) where T : NetworkBehaviour
        {
            var go = new GameObject(name);
            var comp = go.AddComponent<T>();
            server.RegisterSceneObject(comp.NetId, new object[] { comp });
            return comp;
        }
    }

    /// <summary>시스템 메시지를 전부 기록하는 테스트 채널.</summary>
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
