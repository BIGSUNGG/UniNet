using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 클라이언트 런타임 — 씬·동적 스폰 오브젝트 등록(오브젝트당 1 netId + 컴포넌트 배열), 서버가 알려준 연결 ID·소유권 맵 보관.
    /// </summary>
    public sealed class NetworkClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<ulong, object[]> _objects = new();
        private readonly Dictionary<ulong, long> _owners = new();

        /// <summary>서버가 부여한 내 연결 ID (Welcome 수신 시 설정).</summary>
        public long LocalConnId { get; private set; }

        /// <summary>오브젝트를 등록한다 — 오브젝트(netId)에 NetworkBehaviour 컴포넌트 배열을 슬롯 순서로. 씬·동적 스폰 공용.</summary>
        public void Register(ulong netId, object[] components)
        {
            lock (_gate) _objects[netId] = components;
        }

        /// <summary>등록을 해제한다 (파괴 동기화). 등록이 있었으면 true.</summary>
        public bool Unregister(ulong netId)
        {
            lock (_gate) return _objects.Remove(netId);
        }

        /// <summary>오브젝트의 컴포넌트 배열을 조회한다 (없으면 null) — 오브젝트 단위 확인용.</summary>
        public object[] Get(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var o) ? o : null;
        }

        /// <summary>서브슬롯의 인스턴스를 조회한다 (RPC 라우팅·리플리케이션 적용).</summary>
        public object Get(ulong netId, byte subId)
        {
            lock (_gate)
            {
                return _objects.TryGetValue(netId, out var o)
                    && subId < o.Length ? o[subId] : null;
            }
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
