using System;
using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// 서버 런타임 — 씬 오브젝트 등록, 연결별 허브 보관, 소유권 최소 정책, 리플리케이션 틱.
    /// 네트워크 스레드에서 호출될 수 있어 상태 접근은 락으로 보호한다.
    /// </summary>
    public sealed class NetworkServer
    {
        private readonly object _gate = new();
        private readonly Dictionary<ulong, ServerObjectEntry> _objects = new();
        private readonly List<ServerConnection> _connections = new();
        private long _nextConnId = 1;

        /// <summary>네트워크 오브젝트 1개의 서버 측 상태 — 인스턴스와 소유 연결·리플리케이션 스냅샷.</summary>
        public sealed class ServerObjectEntry
        {
            /// <summary>네트워크 ID로 조회된 실제 인스턴스 (NetworkBehaviour).</summary>
            public object Instance { get; }

            /// <summary>소유 클라이언트 연결 ID (0 = 미할당).</summary>
            public long OwnerConnId;

            /// <summary>이 타입의 리플리케이션 핸들러 (Replicated 필드가 있을 때만).</summary>
            public UniNetReplicationHandler Replication;

            /// <summary>서버 측 마지막 전송 스냅샷 (dirty 비교 기준).</summary>
            public object[] Snapshot = Array.Empty<object>();

            public ServerObjectEntry(object instance) => Instance = instance;
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

        /// <summary>씬 오브젝트를 등록한다. netId는 양단이 같은 규칙(씬 경로 해시)으로 계산한다.</summary>
        public void RegisterSceneObject(ulong netId, object instance)
        {
            lock (_gate)
            {
                _objects[netId] = new ServerObjectEntry(instance);
            }
        }

        /// <summary>netId로 등록된 인스턴스를 조회한다.</summary>
        public object Get(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var e) ? e.Instance : null;
        }

        /// <summary>서버 측 오브젝트 엔트리를 조회한다 (등록 직후 핸들 부착용).</summary>
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

        /// <summary>새 연결을 붙인다 — 연결 ID 부여·소유권 재배정·알림을 메인 큐로 예약한다.</summary>
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
        /// 소유권 최소 정책 — 연결 순서대로 씬 오브젝트(netId 오름차순)를 한 바퀴씩 배정하고 전 클라에 알린다.
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

        /// <summary>
        /// 리플리케이션 틱(드라이버 Update에서 호출) — 변경된 [Replicated] 필드만 골라 델타를 전 클라에 전송한다.
        /// </summary>
        public void TickReplication()
        {
            var connections = SnapshotConnections();   // 틱당 1회 캡처 (오브젝트마다 복사 방지)
            if (connections.Count == 0) return;

            foreach (var (netId, entry) in SnapshotObjects())
            {
                var handler = entry.Replication;
                if (handler == null || !handler.HasFields) continue;

                byte[] payload = handler.CompareAndWriteDelta(entry);
                if (payload.Length == 0) continue;

                foreach (var conn in connections)
                    if (!conn.Disconnected)
                        conn.Channel.SendReplicate(netId, handler.MethodId, payload);
            }
        }
    }
}
