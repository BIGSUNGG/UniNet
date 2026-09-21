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

        // P3 — 가시성·우선순위·예산 스케줄링 상태. 틱·브로드캐스트·캐치업·뷰어 설정이 모두 메인 스레드에서만 접근한다 (무락).
        private readonly Dictionary<long, ViewerPosition> _viewerPositions = new();
        private readonly Dictionary<long, HashSet<ulong>> _visible = new();
        private readonly Dictionary<long, HashSet<ulong>> _everVisible = new();
        private readonly Dictionary<Type, int> _channelBudgets = new();
        private readonly Dictionary<Type, int> _typeBudgetLeft = new();
        private readonly List<DeferredDelta> _queue = new();
        private readonly List<DeferredDelta> _pending = new();
        private bool[] _relevanceScratch = Array.Empty<bool>();
        private long _seq;

        // P4 — 그리드 공간 분할 가시성 (RepGraph 스타일) 상태. 메인 스레드 전용.
        private bool _gridEnabled;
        private float _gridCellSize = 32f;
        private float _gridRadius = 48f;
        private readonly Dictionary<(int X, int Z), List<ulong>> _gridBuckets = new();
        private readonly List<ulong> _gridAlwaysRelevant = new();
        private readonly Dictionary<long, HashSet<ulong>> _gridRelevant = new();
        private readonly List<List<ulong>> _bucketFree = new();   // 재사용 가능한 빈 셀 버킷
        private readonly List<List<ulong>> _bucketUsed = new();   // 이번 틱에 배정된 셀 버킷

        /// <summary>틱당 리플리케이션 전역 바이트 예산 — 0 = 무제한(기본). 초과 델타는 대기열로 연기됐다가 다음 틱에 전송된다.</summary>
        public int ReplicationBudgetPerTickBytes { get; set; }

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

            /// <summary>다음 전송 허용 시각 (NetUpdateFrequency 스케줄링 — TickReplication의 시간 축).</summary>
            public double NextReplicateTime;

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

            // P3 가시성 상태 정리 — 파괴 netId 잔존 방지 (동적 스폰/파괴 churn의 완만한 누수).
            // 씬 리로드 재등록 시에도 신선한 기준선(첫 평가 조용한 시드)을 보장한다. P3 상태와 같은 메인 스레드다.
            foreach (var set in _visible.Values) set.Remove(netId);
            foreach (var set in _everVisible.Values) set.Remove(netId);

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
                if (!IsRelevant(entry, conn, netId)) continue;   // 비관련 연결 — 스폰 생략 (관련 전환 시 전송된다)
                GetVisibleSet(conn.ConnId).Add(netId);
                GetEverVisibleSet(conn.ConnId).Add(netId);
                SendSpawnState(conn.Channel, netId, entry, px, py, pz, qx, qy, qz, qw, conn.ConnId == entry.OwnerConnId);
            }
            if (entry.OwnerConnId != 0)
                foreach (var conn in SnapshotConnections())
                    if (!conn.Disconnected && IsRelevant(entry, conn, netId))
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
                        var conn = _connections[i];
                        conn.Disconnected = true;
                        _connections.RemoveAt(i);
                        UniNetEnvironment.QueueOnMain(() =>
                        {
                            _viewerPositions.Remove(conn.ConnId);   // P3/P4 상태 정리 — 틱과 같은 메인 스레드에서
                            _visible.Remove(conn.ConnId);
                            _everVisible.Remove(conn.ConnId);   // 재접속마다 잔존하는 HashSet 누수 방지 (connId 단조 증가)
                            _gridRelevant.Remove(conn.ConnId);   // P4 그리드 집합도 정리
                            ReassignOwnership();
                        });
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
                if (!IsRelevant(entry, conn, netId)) continue;   // 비관련 오브젝트 — 캐치업 제외 (관련 전환 시 전송)
                GetVisibleSet(conn.ConnId).Add(netId);
                GetEverVisibleSet(conn.ConnId).Add(netId);
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
        /// 시간을 주지 않으면 즉시 모드(주기·기아·예산 정책 미적용)로 동작한다 — P2 호환 경로.
        /// </summary>
        public void TickReplication()
            => TickReplication(SnapshotObjects());

        /// <summary>스냅샷을 받는 틱 오버로드 — 즉시 모드(정책 미적용).</summary>
        public void TickReplication(IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> objects)
            => TickReplication(objects, double.NaN);

        /// <summary>
        /// P3 틱 — 가시성 갱신(컬 거리·관련성 훅) → 휴면·주기 필터를 거친 델타 후보를 우선순위(기아 보정)순으로
        /// 전역·유형별 예산 안에서 전송하고, 예산 초과분은 다음 틱으로 연기한다. time은 초 단위 단조 시계.
        /// </summary>
        public void TickReplication(IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> objects, double time)
        {
            var connections = SnapshotConnections();   // 틱당 1회 캡처 (오브젝트마다 복사 방지)
            if (connections.Count == 0) return;

            bool timed = !double.IsNaN(time);
            int budget = timed ? ReplicationBudgetPerTickBytes : 0;   // 즉시 모드는 예산 정책도 미적용 (P2 호환 계약)
            int budgetLeft = budget > 0 ? budget : int.MaxValue;
            if (_gridEnabled) RefreshGrid(objects, connections);   // P4 — 그리드 가시성 갱신 (틱당 1회)

            _pending.Clear();
            foreach (var (netId, entry) in objects)
            {
                if (!UpdateVisibility(netId, entry, connections)) continue;   // 어느 연결에도 비관련 — 비교·전송 모두 생략 (재진입 시 기준선 복구)

                // 연결별 관련 여부를 인큐 시점에 비트마스크로 스냅샷 — 전송 루프의 항목×연결 재평가(훅·transform 네이티브 호출) 제거.
                // 64 초과 연결은 전송 시점 재평가로 폴백한다 (드문 대규모 토폴로지).
                ulong relevantMask = 0;
                int maskCount = Math.Min(connections.Count, 64);
                for (int i = 0; i < maskCount; i++)
                    if (_relevanceScratch[i]) relevantMask |= 1ul << i;

                var policy = entry.Instance as IUniNetReplicationPolicy;
                if (policy != null && policy.IsNetworkDormant) continue;   // 휴면 — 델타 비교 중단 (깨우면 누적 변경분이 나간다)

                float hz = policy?.NetworkUpdateFrequencyHz ?? 0f;
                for (byte subId = 0; subId < entry.Subs.Length; subId++)
                {
                    var sub = entry.Subs[subId];
                    var handler = sub.Replication;
                    if (handler == null || !handler.HasFields) continue;
                    if (timed && hz > 0f && time < sub.NextReplicateTime) continue;   // 주기 미도달 — 스냅샷 유지, 도달 틱에 변경분 전송

                    var (toOwner, toOthers) = handler.CompareAndWriteDelta(sub);
                    if (IsEmpty(toOwner) && IsEmpty(toOthers)) continue;
                    if (timed && hz > 0f) sub.NextReplicateTime = time + 1f / hz;

                    _pending.Add(new DeferredDelta
                    {
                        NetId = netId,
                        SubId = subId,
                        MethodId = handler.MethodId,
                        Owner = toOwner,
                        Others = toOthers,
                        SenderType = sub.Instance.GetType(),
                        Priority = policy?.NetworkPriority ?? 1f,
                        EnqueuedAt = timed ? time : 0,
                        RelevantMask = relevantMask,
                        Seq = _seq++,
                    });
                }
            }

            // 후보 + 이전 틱 연기분 병합 → 기아 보정 우선순위 순 전송 (UE GetNetPriority: priority × (1 + 대기초/0.1))
            _pending.AddRange(_queue);
            _pending.Sort((a, b) =>
            {
                double effA = a.Priority * (1 + (timed ? time - a.EnqueuedAt : 0) / 0.1);
                double effB = b.Priority * (1 + (timed ? time - b.EnqueuedAt : 0) / 0.1);
                int cmp = effB.CompareTo(effA);
                return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq);
            });

            _queue.Clear();
            _typeBudgetLeft.Clear();   // 유형별 채널 예산 리필 — 즉시 모드는 비워 둔다 (예산 정책 미적용)
            if (timed)
                foreach (var kv in _channelBudgets) _typeBudgetLeft[kv.Key] = kv.Value;

            foreach (var item in _pending)
            {
                var entry = GetEntry(item.NetId);
                if (entry == null) continue;   // 틱 도중 파괴 — 연기분 폐기

                var entryPolicy = entry.Instance as IUniNetReplicationPolicy;
                if (entryPolicy != null && entryPolicy.IsNetworkDormant)
                {
                    // 휴면 진입 — 연기분도 보류한다. 드롭하면 스냅샷이 이미 갱신돼 변경분이 영구 유실되므로, 깨우면 전송된다.
                    _queue.Add(item);
                    continue;
                }

                int size = Math.Max(item.Owner?.Length ?? 0, item.Others?.Length ?? 0);
                bool globalOk = budgetLeft >= size || (budget > 0 && size > budget);   // 예산보다 큰 단일 델타는 기아 방지 강제 전송
                bool typeOk = true;
                if (_typeBudgetLeft.TryGetValue(item.SenderType, out int typeLeft))
                {
                    _channelBudgets.TryGetValue(item.SenderType, out int typeBudget);
                    typeOk = typeLeft >= size || (typeBudget > 0 && size > typeBudget);
                }
                if (!globalOk || !typeOk)
                {
                    _queue.Add(item);   // 연기 — EnqueuedAt 보존으로 기아 보정이 계속 성장한다
                    continue;
                }

                int spent = 0;
                for (int i = 0; i < connections.Count; i++)
                {
                    var conn = connections[i];
                    if (conn.Disconnected) continue;
                    bool relevant = i < 64
                        ? (item.RelevantMask & (1ul << i)) != 0
                        : IsRelevant(entry, conn, item.NetId);   // 64 초과 연결 — 전송 시점 재평가 (드문 대규모 토폴로지)
                    if (!relevant) continue;
                    var payload = conn.ConnId == entry.OwnerConnId ? item.Owner : item.Others;
                    if (IsEmpty(payload)) continue;
                    conn.Channel.SendReplicate(item.NetId, item.SubId, item.MethodId, payload);
                    spent += payload.Length;
                }
                budgetLeft = Math.Max(0, budgetLeft - spent);
                if (_typeBudgetLeft.ContainsKey(item.SenderType))
                    _typeBudgetLeft[item.SenderType] = Math.Max(0, _typeBudgetLeft[item.SenderType] - spent);
            }
        }

        private static bool IsEmpty(byte[] payload) => payload == null || payload.Length == 0;

        // ── P3 — 가시성(Relevancy)·채널 예산 ·뷰어 ────────────────────────────

        /// <summary>연결의 뷰어 위치를 설정한다 — 컬 거리 판정 기준점 (메인 스레드). 설정하지 않은 연결은 거리 컬을 받지 않는다(항상 관련).</summary>
        public void SetViewerPosition(long connId, float x, float y, float z)
            => _viewerPositions[connId] = new ViewerPosition(x, y, z);

        /// <summary>연결의 뷰어 위치 설정을 해제한다 — 이후 해당 연결은 거리 컬을 받지 않는다 (메인 스레드).</summary>
        public void ClearViewerPosition(long connId) => _viewerPositions.Remove(connId);

        /// <summary>네트워크 유형(채널)별 틱당 전송 예산을 설정한다 — 한 유형의 과다 전송이 다른 유형을 굶기지 않게 한다 (bytesPerTick ≤ 0 = 해제, 메인 스레드).</summary>
        public void SetReplicationChannelBudget(Type behaviourType, int bytesPerTick)
        {
            if (behaviourType == null) throw new ArgumentNullException(nameof(behaviourType));
            if (bytesPerTick > 0) _channelBudgets[behaviourType] = bytesPerTick;
            else _channelBudgets.Remove(behaviourType);
        }

        /// <summary>
        /// P4 그리드 공간 분할 가시성 (RepGraph 스타일) — 월드를 cellSize 격자(XZ 평면)로 나눠 뷰어 주변 visibleRadius
        /// 반경 셀의 오브젝트만 관련으로 판정한다. 거리 판정을 셀 멤버십으로 대체하는 양자화 판정(경계 오차 cellSize 이하)이며,
        /// 오브젝트별 NetworkCullDistance>0는 그리드와 AND로 유지된다. 뷰어 위치 미설정 연결은 P3 계약대로 페일오픈 (메인 스레드).
        /// </summary>
        public void SetVisibilityGrid(float cellSize, float visibleRadius)
        {
            if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (visibleRadius <= 0f) throw new ArgumentOutOfRangeException(nameof(visibleRadius));
            _gridCellSize = cellSize;
            _gridRadius = visibleRadius;
            _gridEnabled = true;
        }

        /// <summary>그리드 가시성을 해제한다 — P3 거리 컬로 복귀한다 (메인 스레드).</summary>
        public void ClearVisibilityGrid() => _gridEnabled = false;

        /// <summary>P4 그리드 갱신 — 오브젝트를 셀에 분배하고 연결별(뷰어 반경 내) 관련 집합을 계산한다 (틱당 1회).
        /// 버킷 리스트·연결 집합은 재사용해 틱당 GC 압력을 없앤다. ponytail: 오브젝트·연결 수가 매우 크면 풀링으로 확장.</summary>
        private void RefreshGrid(IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> objects, IReadOnlyList<ServerConnection> connections)
        {
            // 버킷 회수 — 이전 틱 리스트를 비워 재사용 가능하게 만든다 (딕셔너리는 키가 바뀌므로 재구성)
            foreach (var bucket in _bucketUsed) bucket.Clear();
            _bucketFree.AddRange(_bucketUsed);
            _bucketUsed.Clear();
            _gridBuckets.Clear();
            _gridAlwaysRelevant.Clear();

            foreach (var (netId, entry) in objects)
            {
                if (entry.Instance is IUniNetSpawnTransform transform)
                {
                    transform.GetSpawnTransform(out float px, out _, out float pz, out _, out _, out _, out _);
                    var key = ((int)Math.Floor(px / _gridCellSize), (int)Math.Floor(pz / _gridCellSize));
                    if (!_gridBuckets.TryGetValue(key, out var bucket))
                    {
                        bucket = _bucketFree.Count > 0 ? PopBucket() : new List<ulong>();
                        _gridBuckets[key] = bucket;
                    }
                    bucket.Add(netId);
                }
                else _gridAlwaysRelevant.Add(netId);   // 위치 없는 오브젝트 — 항상 관련 (페일오픈)
            }

            // 연결별 집합 재사용 — 딕셔너리를 유지한 채 Clear 후 재기입 (GC 압력 제거). 끊긴 연결 키는 DetachConnection에서 제거
            int range = (int)Math.Ceiling(_gridRadius / _gridCellSize);
            foreach (var conn in connections)
            {
                if (conn.Disconnected) continue;
                if (!_viewerPositions.TryGetValue(conn.ConnId, out var viewer)) continue;   // 집합 미생성 — IsRelevant의 페일오픈이 처리
                if (!_gridRelevant.TryGetValue(conn.ConnId, out var set)) _gridRelevant[conn.ConnId] = set = new HashSet<ulong>();
                set.Clear();   // 기존 인스턴스 재사용 — 용량 유지
                int cx = (int)Math.Floor(viewer.X / _gridCellSize);
                int cz = (int)Math.Floor(viewer.Z / _gridCellSize);
                for (int dx = -range; dx <= range; dx++)
                    for (int dz = -range; dz <= range; dz++)
                        if (_gridBuckets.TryGetValue((cx + dx, cz + dz), out var bucket))
                            set.UnionWith(bucket);
                set.UnionWith(_gridAlwaysRelevant);
            }
        }

        private List<ulong> PopBucket()
        {
            var last = _bucketFree[_bucketFree.Count - 1];
            _bucketFree.RemoveAt(_bucketFree.Count - 1);
            _bucketUsed.Add(last);
            return last;
        }

        /// <summary>오브젝트가 연결에 관련되는가 — 관련성 훅 + (그리드 멤버십 또는 뷰어 위치 대비 컬 거리). 정책이 없으면 항상 관련.</summary>
        private bool IsRelevant(ServerObjectEntry entry, ServerConnection conn, ulong netId)
        {
            if (entry.Instance is not IUniNetReplicationPolicy policy) return true;
            if (!policy.IsNetworkRelevant(conn.ConnId)) return false;

            float cull = policy.NetworkCullDistance;
            if (_gridEnabled && _viewerPositions.ContainsKey(conn.ConnId))
            {
                if (cull <= 0f) return GridContains(conn.ConnId, netId);   // 그리드 멤버십으로 거리 판정 대체 (양자화 오차 cellSize 이하)
                if (!GridContains(conn.ConnId, netId)) return false;   // 전역 반경 통과 후 오브젝트 컬도 검사 (AND)
            }
            if (cull > 0f
                && _viewerPositions.TryGetValue(conn.ConnId, out var viewer)
                && entry.Instance is IUniNetSpawnTransform transform)
            {
                transform.GetSpawnTransform(out float px, out float py, out float pz, out _, out _, out _, out _);
                float dx = px - viewer.X, dy = py - viewer.Y, dz = pz - viewer.Z;
                if (dx * dx + dy * dy + dz * dz > cull * cull) return false;
            }
            return true;
        }

        private bool GridContains(long connId, ulong netId)
            => _gridRelevant.TryGetValue(connId, out var set) && set.Contains(netId);

        /// <summary>
        /// 오브젝트 단위 가시성 갱신 — 연결별 관련 변화를 추적하고 관련 여부를 되돌린다. 씬 오브젝트의 첫 평가는
        /// 조용히 시드만 한다 (P2 계약 — 등록 이후 변경분 델타만 전송). 재진입(비관련→관련 복귀)과 동적 오브젝트
        /// 첫 평가(클라에 없음)는 스폰/전체 상태로 기준선을 복구한다 — 비관련 기간 중 놓친 델타의 유실을 막는다.
        /// </summary>
        private bool UpdateVisibility(ulong netId, ServerObjectEntry entry, IReadOnlyList<ServerConnection> conns)
        {
            if (_relevanceScratch.Length < conns.Count) _relevanceScratch = new bool[conns.Count];
            bool anyRelevant = false;
            for (int i = 0; i < conns.Count; i++)
            {
                var conn = conns[i];
                var seen = GetVisibleSet(conn.ConnId);
                if (IsRelevant(entry, conn, netId))
                {
                    _relevanceScratch[i] = anyRelevant = true;
                    bool everNew = GetEverVisibleSet(conn.ConnId).Add(netId);
                    if (seen.Add(netId) && (!everNew || entry.IsDynamic))
                        SendVisibilityState(conn, netId, entry);   // 재진입 기준선 복구 · 동적 오브젝트 생성
                }
                else
                {
                    _relevanceScratch[i] = false;
                    seen.Remove(netId);   // 관련→비관련 — 델타 중단 (클라는 마지막 상태 유지 — 전파 파괴 미지원 계약)
                }
            }
            return anyRelevant;
        }

        /// <summary>관련 전이 전송 — 동적 오브젝트는 클라에 없으므로 스폰으로, 씬 오브젝트는 전체 상태 리플리케이션으로 기준선을 복구한다 (캐치업과 같은 경로, 예산 외).</summary>
        private void SendVisibilityState(ServerConnection conn, ulong netId, ServerObjectEntry entry)
        {
            bool isOwner = conn.ConnId == entry.OwnerConnId;
            if (entry.IsDynamic)
            {
                GetSpawnTransform(entry, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
                SendSpawnState(conn.Channel, netId, entry, px, py, pz, qx, qy, qz, qw, isOwner);
                if (entry.OwnerConnId != 0)
                    conn.Channel.SendOwnerUpdate(netId, entry.OwnerConnId);
                return;
            }
            for (byte subId = 0; subId < entry.Subs.Length; subId++)
            {
                var handler = entry.Subs[subId].Replication;
                if (handler == null || !handler.HasFields) continue;
                byte[] state = handler.WriteFull(entry.Subs[subId].Instance, isOwner);
                if (state != null && state.Length > 0)
                    conn.Channel.SendReplicate(netId, subId, handler.MethodId, state);
            }
        }

        private HashSet<ulong> GetVisibleSet(long connId)
        {
            if (!_visible.TryGetValue(connId, out var set))
                _visible[connId] = set = new HashSet<ulong>();
            return set;
        }

        private HashSet<ulong> GetEverVisibleSet(long connId)
        {
            if (!_everVisible.TryGetValue(connId, out var set))
                _everVisible[connId] = set = new HashSet<ulong>();
            return set;
        }

        /// <summary>뷰어 위치 (컬 거리 판정 기준점).</summary>
        private readonly struct ViewerPosition
        {
            public readonly float X, Y, Z;
            public ViewerPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
        }

        /// <summary>예산 초과로 연기된 델타 1건 — EnqueuedAt이 기아 보정의 기준, RelevantMask가 전송 대상 연결 비트(하위 64개)다.</summary>
        private sealed class DeferredDelta
        {
            public ulong NetId;
            public byte SubId;
            public int MethodId;
            public byte[] Owner;
            public byte[] Others;
            public Type SenderType;
            public float Priority;
            public double EnqueuedAt;
            public ulong RelevantMask;
            public long Seq;
        }

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
