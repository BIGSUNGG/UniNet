using System;
using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 서버 런타임 — 씬/동적 오브젝트 등록(오브젝트당 1 netId + NetworkBehaviour 서브 테이블), 연결별 허브 보관,
    /// 소유권 최소 정책, 리플리케이션 틱, 스폰/파괴 전파, 후발 접속 캐치업.
    /// 네트워크 스레드에서 호출될 수 있어 상태 접근은 락으로 보호한다. 브로드캐스트·캐치업은 메인 스레드에서 호출한다.
    /// </summary>
    public sealed class NetworkServer
    {
        private readonly object _gate = new();
        private readonly Dictionary<ulong, ServerObjectEntry> _objects = new();
        private readonly List<ServerConnection> _connections = new();
        private long _nextConnId = 1;
        private ulong _nextDynamicNetId = 1;
        private int _rrCursor;

        /// <summary>네트워크 오브젝트 1개의 서버 측 상태 — 오브젝트(게임오브젝트) 단위. 소유권·파괴·가시성의 단위다.</summary>
        public sealed class ServerObjectEntry
        {
            /// <summary>대표 인스턴스 (첫 NetworkBehaviour — 고스트 스윕·변환 조회용).</summary>
            public object Instance { get; }

            /// <summary>소유 클라이언트 연결 ID (0 = 미할당).</summary>
            public long OwnerConnId;

            /// <summary>동적 스폰 오브젝트인가 (후발 접속 캐치업에서 스폰 메시지로 재전달된다).</summary>
            public bool IsDynamic;

            /// <summary>서브오브젝트 테이블 — 슬롯(SubId)순. 각 NetworkBehaviour의 리플리케이션 상태를 가진다.</summary>
            public SubObjectEntry[] Subs = Array.Empty<SubObjectEntry>();

            public ServerObjectEntry(object instance) => Instance = instance;
        }

        /// <summary>서브오브젝트 1개(NetworkBehaviour)의 서버 측 리플리케이션 상태.</summary>
        public sealed class SubObjectEntry
        {
            /// <summary>이 슬롯의 인스턴스 (NetworkBehaviour).</summary>
            public object Instance { get; }

            /// <summary>이 타입의 리플리케이션 핸들러 (Replicated 필드가 있을 때만).</summary>
            public UniNetReplicationHandler Replication;

            /// <summary>클라 생성용 타입 식별자 (UniNetSpawnRegistry).</summary>
            public ulong TypeKey;

            /// <summary>서버 측 마지막 전송 스냅샷 (dirty 비교 기준).</summary>
            public object[] Snapshot = Array.Empty<object>();

            public SubObjectEntry(object instance) => Instance = instance;
        }

        /// <summary>연결별 서버 상태 — 허브와 시스템 전송 채널(생성 허브가 구현)을 함께 보관.</summary>
        public sealed class ServerConnection
        {
            /// <summary>서버가 부여한 연결 ID.</summary>
            public long ConnId { get; }

            /// <summary>연결 종료 여부.</summary>
            public bool Disconnected { get; internal set; }

            /// <summary>연결의 전송 채널 (생성 서버 허브 — 생성 코드가 타입 캐스팅해 송신에 쓴다).</summary>
            public IUniNetSystemChannel Channel { get; }

            internal ServerConnection(long connId, IUniNetSystemChannel channel)
            {
                ConnId = connId;
                Channel = channel;
            }
        }

        /// <summary>씬 오브젝트를 등록한다 — 오브젝트당 1회, 컴포넌트 전체를 슬롯 순서로. netId는 양단이 같은 규칙(씬 경로 해시)으로 계산한다.</summary>
        public void RegisterSceneObject(ulong netId, IReadOnlyList<object> components)
        {
            lock (_gate)
            {
                var entry = new ServerObjectEntry(components[0]);
                AttachSubs(entry, components);
                _objects[netId] = entry;
            }
        }

        /// <summary>
        /// 동적 오브젝트를 등록하고 서버가 할당한 netId를 반환한다. 컴포넌트 전체를 슬롯 순서로 받는다.
        /// 소유 연결은 라운드로빈으로 즉시 배정한다. 이후 BroadcastSpawn(netId)로 클라 전체에 전파한다 (메인 스레드).
        /// </summary>
        public ulong RegisterDynamicObject(IReadOnlyList<object> components)
        {
            lock (_gate)
            {
                ulong netId = _nextDynamicNetId;
                while (_objects.ContainsKey(netId)) netId++;   // 씬 해시와의 충돌 회피 — 등록 불변식 보장
                _nextDynamicNetId = netId + 1;

                var entry = new ServerObjectEntry(components[0]) { IsDynamic = true };
                AttachSubs(entry, components);
                if (_connections.Count > 0)
                {
                    entry.OwnerConnId = _connections[_rrCursor % _connections.Count].ConnId;
                    _rrCursor++;
                }
                _objects[netId] = entry;
                return netId;
            }
        }

        /// <summary>등록된 오브젝트를 제거하고 전 연결에 파괴를 전파한다. 등록이 없으면 false (메인 스레드).</summary>
        public bool DestroyObject(ulong netId)
        {
            lock (_gate)
            {
                if (!_objects.Remove(netId)) return false;
            }

            foreach (var conn in SnapshotConnections())
                if (!conn.Disconnected)
                    conn.Channel.SendDestroy(netId);
            return true;
        }

        /// <summary>동적 스폰을 전 연결에 전파한다 — 스폰 메시지(변환·서브 타입·전체 상태) + 소유권 알림 (메인 스레드).</summary>
        public void BroadcastSpawn(ulong netId)
        {
            var entry = GetEntry(netId);
            if (entry == null) return;

            GetSpawnTransform(entry, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
            foreach (var conn in SnapshotConnections())
            {
                if (conn.Disconnected) continue;
                SendSpawnState(conn.Channel, netId, entry, px, py, pz, qx, qy, qz, qw, conn.ConnId == entry.OwnerConnId);
            }
            if (entry.OwnerConnId != 0)
                foreach (var conn in SnapshotConnections())
                    if (!conn.Disconnected)
                        conn.Channel.SendOwnerUpdate(netId, entry.OwnerConnId);
        }

        /// <summary>netId로 등록된 오브젝트의 대표 인스턴스를 조회한다 (고스트 스윕 등 오브젝트 단위 조회).</summary>
        public object Get(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var e) ? e.Instance : null;
        }

        /// <summary>서브슬롯의 인스턴스를 조회한다 (RPC 라우팅).</summary>
        public object Get(ulong netId, byte subId)
        {
            lock (_gate)
            {
                return _objects.TryGetValue(netId, out var e)
                    && subId < e.Subs.Length ? e.Subs[subId].Instance : null;
            }
        }

        /// <summary>서버 측 오브젝트 엔트리를 조회한다.</summary>
        public ServerObjectEntry GetEntry(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var e) ? e : null;
        }

        /// <summary>서버 측 등록 오브젝트 전체를 순회한다 (드라이버 틱용, 락 내 복사본).</summary>
        public IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> SnapshotObjects()
        {
            lock (_gate)
            {
                var list = new (ulong, ServerObjectEntry)[_objects.Count];
                int i = 0;
                foreach (var kv in _objects) list[i++] = (kv.Key, kv.Value);
                return list;
            }
        }

        /// <summary>새 연결을 붙인다 — 연결 ID 부여·소유권 재배정·기존 상태 캐치업을 메인 큐로 예약한다.</summary>
        public long AttachConnection(IUniNetSystemChannel channel)
        {
            lock (_gate)
            {
                var conn = new ServerConnection(_nextConnId++, channel);
                channel.UniNetConnId = conn.ConnId;
                _connections.Add(conn);
                var connId = conn.ConnId;
                UniNetEnvironment.QueueOnMain(() =>
                {
                    channel.SendWelcome(connId);
                    ReassignOwnership();
                    SendCatchup(conn);
                });
                return conn.ConnId;
            }
        }

        /// <summary>연결을 뗀다 (허브 Disconnect 관측 시).</summary>
        public void DetachConnection(IUniNetSystemChannel channel)
        {
            lock (_gate)
            {
                for (int i = 0; i < _connections.Count; i++)
                {
                    if (ReferenceEquals(_connections[i].Channel, channel))
                    {
                        _connections[i].Disconnected = true;
                        _connections.RemoveAt(i);
                        UniNetEnvironment.QueueOnMain(ReassignOwnership);
                        return;
                    }
                }
            }
        }

        /// <summary>살아있는 연결 목록 (복사본).</summary>
        public IReadOnlyList<ServerConnection> SnapshotConnections()
        {
            lock (_gate) return _connections.ToArray();
        }

        /// <summary>
        /// 소유권 최소 정책 — 연결 순서대로 오브젝트(netId 오름차순)를 한 바퀴씩 배정하고 전 클라에 알린다.
        /// ponytail: 라운드로빈 최소 정책, 실서비스 요구 시 명시적 소유권 이전 API로 대체.
        /// </summary>
        private void ReassignOwnership()
        {
            var objects = SnapshotObjects();
            var connections = SnapshotConnections();
            if (connections.Count == 0 || objects.Count == 0) return;

            var sorted = new List<(ulong NetId, ServerObjectEntry Entry)>(objects);
            sorted.Sort((a, b) => a.NetId.CompareTo(b.NetId));

            for (int i = 0; i < sorted.Count; i++)
            {
                long owner = connections[i % connections.Count].ConnId;
                sorted[i].Entry.OwnerConnId = owner;
                foreach (var conn in connections)
                    conn.Channel.SendOwnerUpdate(sorted[i].NetId, owner);
            }
        }

        /// <summary>후발 접속 캐치업 — 동적 오브젝트는 스폰으로, 씬 오브젝트는 서브별 전체 상태 리플리케이션으로 뒤늦게 합류시킨다.</summary>
        private void SendCatchup(ServerConnection conn)
        {
            bool isOwner;
            foreach (var (netId, entry) in SnapshotObjects())
            {
                isOwner = entry.OwnerConnId == conn.ConnId;
                if (entry.IsDynamic)
                {
                    GetSpawnTransform(entry, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
                    SendSpawnState(conn.Channel, netId, entry, px, py, pz, qx, qy, qz, qw, isOwner);
                }
                else
                {
                    for (byte subId = 0; subId < entry.Subs.Length; subId++)
                    {
                        var handler = entry.Subs[subId].Replication;
                        if (handler == null || !handler.HasFields) continue;
                        byte[] state = handler.WriteFull(entry.Subs[subId].Instance, isOwner);
                        if (state != null && state.Length > 0)   // 전 필드가 조건으로 제외되면 null — 캐치업 생략
                            conn.Channel.SendReplicate(netId, subId, handler.MethodId, state);
                    }
                }
            }
        }

        /// <summary>
        /// 리플리케이션 틱(드라이버 Update에서 호출) — 서브오브젝트별로 변경된 [Replicated] 필드만 골라 수신 그룹별(OwnerOnly/SkipOwner) 델타를 전송한다.
        /// 드라이버는 고스트 스윕과 스냅샷을 공유해 프레임당 복사를 1회로 제한할 수 있다.
        /// </summary>
        public void TickReplication()
            => TickReplication(SnapshotObjects());

        /// <summary>스냅샷을 받는 틱 오버로드 — 드라이버가 같은 프레임의 스윕과 공유한다.</summary>
        public void TickReplication(IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> objects)
        {
            var connections = SnapshotConnections();   // 틱당 1회 캡처 (오브젝트마다 복사 방지)
            if (connections.Count == 0) return;

            foreach (var (netId, entry) in objects)
            {
                for (byte subId = 0; subId < entry.Subs.Length; subId++)
                {
                    var sub = entry.Subs[subId];
                    var handler = sub.Replication;
                    if (handler == null || !handler.HasFields) continue;

                    var (toOwner, toOthers) = handler.CompareAndWriteDelta(sub);
                    if (IsEmpty(toOwner) && IsEmpty(toOthers)) continue;

                    foreach (var conn in connections)
                    {
                        if (conn.Disconnected) continue;
                        var payload = conn.ConnId == entry.OwnerConnId ? toOwner : toOthers;
                        if (!IsEmpty(payload))
                            conn.Channel.SendReplicate(netId, subId, handler.MethodId, payload);
                    }
                }
            }
        }

        private static bool IsEmpty(byte[] payload) => payload == null || payload.Length == 0;

        /// <summary>서브 테이블을 구성한다 — 슬롯 순서 = 컴포넌트 순서, 리플리케이션 핸들 부착·스냅샷 초기화 포함.</summary>
        private static void AttachSubs(ServerObjectEntry entry, IReadOnlyList<object> components)
        {
            if (components.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(components), "오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한)");
            entry.Subs = new SubObjectEntry[components.Count];
            for (int i = 0; i < components.Count; i++)
            {
                var sub = new SubObjectEntry(components[i])
                {
                    TypeKey = UniNetSpawnRegistry.TypeKeyOf(components[i].GetType()),
                };
                sub.Replication = UniNetTypeRegistry.Find(components[i].GetType());
                sub.Replication?.InitSnapshot(sub);
                entry.Subs[i] = sub;
            }
        }

        /// <summary>수신자용 스폰 메시지 조립 — [변환 7값][서브 수][서브별 typeKey+전체 상태] (서브슬롯은 순서 암시).</summary>
        private static void SendSpawnState(IUniNetSystemChannel channel, ulong netId, ServerObjectEntry entry,
            float px, float py, float pz, float qx, float qy, float qz, float qw, bool isOwner)
        {
            var typeKeys = new ulong[entry.Subs.Length];
            var states = new byte[entry.Subs.Length][];
            for (int i = 0; i < entry.Subs.Length; i++)
            {
                typeKeys[i] = entry.Subs[i].TypeKey;
                states[i] = entry.Subs[i].Replication != null && entry.Subs[i].Replication.HasFields
                    ? entry.Subs[i].Replication.WriteFull(entry.Subs[i].Instance, isOwner)
                    : Array.Empty<byte>();
            }
            channel.SendSpawn(netId, px, py, pz, qx, qy, qz, qw, (byte)entry.Subs.Length, typeKeys, states);
        }

        private static void GetSpawnTransform(ServerObjectEntry entry,
            out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw)
        {
            px = py = pz = 0f;
            qx = qy = qz = 0f;
            qw = 1f;
            if (entry.Instance is IUniNetSpawnTransform provider)
                provider.GetSpawnTransform(out px, out py, out pz, out qx, out qy, out qz, out qw);
        }
    }
}
