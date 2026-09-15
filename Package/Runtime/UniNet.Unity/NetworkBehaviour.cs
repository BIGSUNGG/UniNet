using System.Collections.Generic;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// 네트워크 오브젝트 기반 클래스 — RPC·리플리케이션의 대상이 되는 MonoBehaviour.
    /// 네트워크 ID는 씬 경로 해시로 양단이 같은 규칙으로 계산한다 (무합의 일치).
    /// </summary>
    public abstract partial class NetworkBehaviour : MonoBehaviour
    {
        private ulong _netId;
        private bool _netIdComputed;
        private bool _registeredServer;
        private bool _registeredClient;

        /// <summary>네트워크 오브젝트 ID — 씬 이름 + 계층 경로(형제 인덱스 포함)의 FNV-1a 64bit.</summary>
        public ulong NetId
        {
            get
            {
                if (!_netIdComputed)
                {
                    _netId = Fnv1a.Hash64(BuildHierarchyPath());
                    _netIdComputed = true;
                }
                return _netId;
            }
        }

        /// <summary>서버 권위로 동작 중인가 (서버/호스트에서 true).</summary>
        public bool IsServer => UniNetEnvironment.Server != null && _registeredServer;

        /// <summary>클라이언트로 접속 중인가 (클라/호스트에서 true).</summary>
        public bool IsClient => UniNetEnvironment.Client != null && _registeredClient;

        /// <summary>이 오브젝트를 로컬 플레이어의 연결이 소유하는가 (입력·권위 판단용).</summary>
        public bool IsOwner
        {
            get
            {
                var client = UniNetEnvironment.Client;
                return client != null && client.GetOwner(NetId) == client.LocalConnId && client.LocalConnId != 0;
            }
        }

        /// <summary>씬 로드 시 전체 등록 (UniNetManager 시작 시 1회 호출).</summary>
        internal static void RegisterAllToServer()
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return;

            foreach (var nb in FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                server.RegisterSceneObject(nb.NetId, nb);
                var entry = server.GetEntry(nb.NetId);
                if (entry != null)
                {
                    entry.Replication = UniNetTypeRegistry.Find(nb.GetType());
                    entry.Replication?.InitSnapshot(entry);
                }
                nb._registeredServer = true;
            }
        }

        /// <summary>클라 측 전체 등록.</summary>
        internal static void RegisterAllToClient()
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;

            foreach (var nb in FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                client.RegisterSceneObject(nb.NetId, nb);
                UniNetTypeRegistry.Find(nb.GetType())?.InitClientSnapshot(nb);
                nb._registeredClient = true;
            }
        }

        private string BuildHierarchyPath()
        {
            var parts = new List<string>();
            var current = transform;
            while (current != null)
            {
                parts.Add(current.name + ":" + current.GetSiblingIndex());
                current = current.parent;
            }
            parts.Reverse();
            return gameObject.scene.name + "/" + string.Join("/", parts);
        }
    }
}
