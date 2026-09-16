using System.Collections.Generic;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// 네트워크 오브젝트 기반 클래스 — RPC·리플리케이션의 대상이 되는 MonoBehaviour.
    /// 씬 오브젝트의 네트워크 ID는 씬 경로 해시로 양단이 같은 규칙으로 계산한다 (무합의 일치).
    /// 동적 스폰 오브젝트는 서버가 할당한 ID를 받는다 (UniNetManager.Spawn).
    /// </summary>
    public abstract partial class NetworkBehaviour : MonoBehaviour, IUniNetSpawnTransform
    {
        private ulong _netId;
        private bool _netIdComputed;
        private bool _registeredServer;
        private bool _registeredClient;

        /// <summary>네트워크 오브젝트 ID — 씬 오브젝트는 씬 이름+계층 경로(형제 인덱스 포함)의 FNV-1a 64bit, 동적 스폰은 서버 할당값.</summary>
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

        /// <summary>동적 스폰 — 서버가 할당한 netId를 주입한다 (경로 해시 계산을 선제한다).</summary>
        internal void AssignNetId(ulong netId)
        {
            _netId = netId;
            _netIdComputed = true;
        }

        /// <summary>서버 등록 완료 표시 (IsServer 활성화).</summary>
        internal void MarkServerRegistered() => _registeredServer = true;

        /// <summary>클라 등록 완료 표시 (IsClient 활성화).</summary>
        internal void MarkClientRegistered() => _registeredClient = true;

        /// <summary>스폰 와이어 포맷으로 현재 변환을 출력한다 (서버가 스폰 메시지에 실음).</summary>
        void IUniNetSpawnTransform.GetSpawnTransform(
            out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw)
        {
            var p = transform.position;
            var r = transform.rotation;
            px = p.x; py = p.y; pz = p.z;
            qx = r.x; qy = r.y; qz = r.z; qw = r.w;
        }

        /// <summary>씬 로드 시 전체 등록 (UniNetManager 시작 시 1회 호출). 리플리케이션 핸들 부착은 서버 등록이 처리한다.</summary>
        internal static void RegisterAllToServer()
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return;

            foreach (var nb in FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                server.RegisterSceneObject(nb.NetId, nb);
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
                client.Register(nb.NetId, nb);
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
