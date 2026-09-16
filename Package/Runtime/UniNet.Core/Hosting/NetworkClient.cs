using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 클라이언트 런타임 — 씬·동적 스폰 오브젝트 등록, 서버가 알려준 연결 ID·소유권 맵 보관.
    /// </summary>
    public sealed class NetworkClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<ulong, object> _objects = new();
        private readonly Dictionary<ulong, long> _owners = new();

        /// <summary>서버가 부여한 내 연결 ID (Welcome 수신 시 설정).</summary>
        public long LocalConnId { get; private set; }

        /// <summary>오브젝트를 등록한다 — 씬 오브젝트(netId=씬 경로 해시)와 동적 스폰 오브젝트(netId=서버 할당) 모두.</summary>
        public void Register(ulong netId, object instance)
        {
            lock (_gate) _objects[netId] = instance;
        }

        /// <summary>등록을 해제한다 (파괴 동기화). 등록이 있었으면 true.</summary>
        public bool Unregister(ulong netId)
        {
            lock (_gate) return _objects.Remove(netId);
        }

        /// <summary>netId로 등록된 인스턴스를 조회한다.</summary>
        public object Get(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var o) ? o : null;
        }

        /// <summary>Welcome 수신 — 내 연결 ID를 설정한다 (생성 클라 허브가 호출).</summary>
        public void SetLocalConnId(long connId) => LocalConnId = connId;

        /// <summary>소유권 갱신 수신 (OwnerUpdate) — 생성 클라 허브가 호출.</summary>
        public void ApplyOwner(ulong netId, long ownerConnId)
        {
            lock (_gate) _owners[netId] = ownerConnId;
        }

        /// <summary>오브젝트 소유자 조회 (0 = 미할당).</summary>
        public long GetOwner(ulong netId)
        {
            lock (_gate) return _owners.TryGetValue(netId, out var o) ? o : 0;
        }
    }
}
