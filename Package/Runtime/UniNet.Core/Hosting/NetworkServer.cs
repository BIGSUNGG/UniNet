using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Server-side runtime — registers scene/dynamic objects (one netId per object plus a NetworkBehaviour sub
    /// table), keeps per-connection hubs, applies the minimal ownership policy, runs the replication tick,
    /// propagates spawns/destroys, and catches late joiners up.
    /// Calls can arrive on network threads, so state access is lock-protected. Broadcasts and catch-up run on
    /// the main thread.
    /// </summary>
    public sealed class NetworkServer
    {
        private readonly object _gate = new();
        private readonly Dictionary<ulong, ServerObjectEntry> _objects = new();
        private readonly List<ServerConnection> _connections = new();
        private long _nextConnId = 1;
        private ulong _nextDynamicNetId = 1;
        private int _rrCursor;

        // P3 — visibility, priority, and budget scheduling state. Tick, broadcast, catch-up, and viewer setup
        // all touch it on the main thread only (lock-free).
        private readonly Dictionary<long, ViewerPosition> _viewerPositions = new();
        private readonly Dictionary<long, HashSet<ulong>> _visible = new();
        private readonly Dictionary<long, HashSet<ulong>> _everVisible = new();
        private readonly Dictionary<Type, int> _channelBudgets = new();
        private readonly Dictionary<Type, int> _typeBudgetLeft = new();
        private readonly List<DeferredDelta> _queue = new();
        private readonly List<DeferredDelta> _pending = new();
        private bool[] _relevanceScratch = Array.Empty<bool>();
        private long _seq;

        // P4 — grid spatial-partition visibility (RepGraph style) state. Main thread only.
        private bool _gridEnabled;
        private float _gridCellSize = 32f;
        private float _gridRadius = 48f;
        private readonly Dictionary<(int X, int Z), List<ulong>> _gridBuckets = new();
        private readonly List<ulong> _gridAlwaysRelevant = new();
        private readonly Dictionary<long, HashSet<ulong>> _gridRelevant = new();
        private readonly List<List<ulong>> _bucketFree = new();   // pooled empty cell buckets available for reuse
        private readonly List<List<ulong>> _bucketUsed = new();   // cell buckets assigned during this tick

        /// <summary>Global replication byte budget per tick — 0 = unlimited (default). Exceeding deltas are deferred to a queue and sent on the next tick.</summary>
        public int ReplicationBudgetPerTickBytes { get; set; }

        /// <summary>
        /// Client connected event — fired on the main thread after Welcome, ownership assignment, and catch-up
        /// have completed. Spawn the player's avatar, update HUD, etc. here (connection lifecycle gateway).
        /// </summary>
        public event Action<long> ClientConnected;

        /// <summary>
        /// Client disconnected event — fired on the main thread BEFORE ownership is reassigned (objects owned by
        /// the leaving connection can still be identified by owner). Destroy the leaving player's objects here
        /// (otherwise they remain with the reassigned owner).
        /// </summary>
        public event Action<long> ClientDisconnected;

        /// <summary>Server-side state of one network object — per GameObject. The unit of ownership, destruction, and visibility.</summary>
        public sealed class ServerObjectEntry
        {
            /// <summary>Representative instance (the first NetworkBehaviour — used for ghost sweeps and transform lookup).</summary>
            public object Instance { get; }

            /// <summary>Owning client connection ID (0 = unassigned).</summary>
            public long OwnerConnId;

            /// <summary>Whether the object was dynamically spawned (resent as a spawn message during late-join catch-up).</summary>
            public bool IsDynamic;

            /// <summary>Sub-object table in slot (SubId) order — holds each NetworkBehaviour's replication state.</summary>
            public SubObjectEntry[] Subs = Array.Empty<SubObjectEntry>();

            public ServerObjectEntry(object instance) => Instance = instance;
        }

        /// <summary>Server-side replication state of one sub-object (a NetworkBehaviour).</summary>
        public sealed class SubObjectEntry
        {
            /// <summary>The instance in this slot (a NetworkBehaviour).</summary>
            public object Instance { get; }

            /// <summary>This type's replication handler (only when it has Replicated fields).</summary>
            public UniNetReplicationHandler Replication;

            /// <summary>Type identifier for client-side creation (UniNetSpawnRegistry).</summary>
            public ulong TypeKey;

            /// <summary>Server-side last-sent snapshot (baseline for dirty comparison).</summary>
            public object[] Snapshot = Array.Empty<object>();

            /// <summary>Earliest time the next send is allowed (NetUpdateFrequency scheduling — on TickReplication's time axis).</summary>
            public double NextReplicateTime;

            public SubObjectEntry(object instance) => Instance = instance;
        }

        /// <summary>Per-connection server state — holds the hub together with its system send channel (implemented by the generated hub).</summary>
        public sealed class ServerConnection
        {
            /// <summary>Connection ID assigned by the server.</summary>
            public long ConnId { get; }

            /// <summary>Whether the connection has ended.</summary>
            public bool Disconnected { get; internal set; }

            /// <summary>The connection's send channel (the generated server hub — generated code type-casts it for sends).</summary>
            public IUniNetSystemChannel Channel { get; }

            internal ServerConnection(long connId, IUniNetSystemChannel channel)
            {
                ConnId = connId;
                Channel = channel;
            }
        }

        /// <summary>Registers a scene object — once per object, with all components in slot order. Both sides compute the netId with the same rule (scene path hash).</summary>
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
        /// Registers a dynamic object and returns its server-assigned netId. Accepts all components in slot
        /// order. The owning connection is assigned immediately by round-robin. Afterwards call
        /// BroadcastSpawn(netId) to propagate it to all clients (main thread).
        /// </summary>
        public ulong RegisterDynamicObject(IReadOnlyList<object> components)
        {
            lock (_gate)
            {
                ulong netId = _nextDynamicNetId;
                while (_objects.ContainsKey(netId)) netId++;   // skip collisions with scene hashes — keeps the registration invariant
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

        /// <summary>Removes a registered object and propagates the destroy to all connections. Returns false if it was not registered (main thread).</summary>
        public bool DestroyObject(ulong netId)
        {
            lock (_gate)
            {
                if (!_objects.Remove(netId)) return false;
            }

            // P3 visibility cleanup — prevents destroyed netIds from lingering (a slow leak under spawn/destroy churn).
            // Also guarantees a fresh baseline on scene-reload re-registration (first evaluation silently seeds). Same main thread as P3 state.
            foreach (var set in _visible.Values) set.Remove(netId);
            foreach (var set in _everVisible.Values) set.Remove(netId);

            foreach (var conn in SnapshotConnections())
                if (!conn.Disconnected)
                    conn.Channel.SendDestroy(netId);
            return true;
        }

        /// <summary>Propagates a dynamic spawn to all connections — the spawn message (transform, sub types, full state) plus the ownership notice (main thread).</summary>
        public void BroadcastSpawn(ulong netId)
        {
            var entry = GetEntry(netId);
            if (entry == null) return;

            GetSpawnTransform(entry, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
            foreach (var conn in SnapshotConnections())
            {
                if (conn.Disconnected) continue;
                if (!IsRelevant(entry, conn, netId)) continue;   // irrelevant connection — skip the spawn (sent when it becomes relevant)
                GetVisibleSet(conn.ConnId).Add(netId);
                GetEverVisibleSet(conn.ConnId).Add(netId);
                SendSpawnState(conn.Channel, netId, entry, px, py, pz, qx, qy, qz, qw, conn.ConnId == entry.OwnerConnId);
            }
            if (entry.OwnerConnId != 0)
                foreach (var conn in SnapshotConnections())
                    if (!conn.Disconnected && IsRelevant(entry, conn, netId))
                        conn.Channel.SendOwnerUpdate(netId, entry.OwnerConnId);
        }

        /// <summary>Returns the representative instance of a registered object by netId (object-level lookup, e.g. for ghost sweeps).</summary>
        public object Get(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var e) ? e.Instance : null;
        }

        /// <summary>Returns an object's owner (0 = unassigned). Used by the generated ServerRpc dispatch to enforce ownership (ADR-0016).</summary>
        public long GetOwner(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var e) ? e.OwnerConnId : 0;
        }

        private static readonly HashSet<long> _rejectionLoggedConns = new();
        private static DateTime _lastRejectionLogUtc;
        private static readonly object _rejectionGate = new();   // lock for the static throttle state only — separate from the instance _gate

        /// <summary>
        /// Should a ServerRpc rejection be logged (ADR-0016 log throttling — defends against log-flooding DoS).
        /// First rejection per sender, plus at most one global log per 5 seconds. Core keeps its no-logging
        /// contract — this only decides.
        /// A sender first rejected inside an open 5-second window is recorded but not logged (and not retried —
        /// churn cap takes priority).
        /// </summary>
        public static bool ReportServerRpcRejection(long senderConnId)
        {
            lock (_rejectionGate)
            {
                if (!_rejectionLoggedConns.Add(senderConnId)) return false;   // already logged this sender
                var now = DateTime.UtcNow;
                if (_lastRejectionLogUtc != default && (now - _lastRejectionLogUtc).TotalMilliseconds < 5000) return false;
                _lastRejectionLogUtc = now;
                return true;
            }
        }

        /// <summary>Returns the instance in a sub slot (RPC routing).</summary>
        public object Get(ulong netId, byte subId)
        {
            lock (_gate)
            {
                return _objects.TryGetValue(netId, out var e)
                    && subId < e.Subs.Length ? e.Subs[subId].Instance : null;
            }
        }

        /// <summary>Returns the server-side object entry.</summary>
        public ServerObjectEntry GetEntry(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var e) ? e : null;
        }

        /// <summary>Enumerates all registered server-side objects (for the driver tick, copied inside the lock).</summary>
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

        /// <summary>Attaches a new connection — schedules connection ID assignment, ownership reassignment, and catch-up of existing state onto the main queue.</summary>
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
                    RaiseLifecycle(ClientConnected, connId);   // after catch-up — spawns raised here never race the catch-up
                });
                return conn.ConnId;
            }
        }

        /// <summary>Detaches a connection (observed when the hub disconnects).</summary>
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
                            _viewerPositions.Remove(conn.ConnId);   // P3/P4 cleanup — same main thread as the tick
                            _visible.Remove(conn.ConnId);
                            _everVisible.Remove(conn.ConnId);   // prevents HashSets lingering across reconnects (connIds are monotonically increasing)
                            _gridRelevant.Remove(conn.ConnId);   // clears the P4 grid sets too
                            try
                            {
                                RaiseLifecycle(ClientDisconnected, conn.ConnId);   // before reassignment — the game can identify and handle owned objects
                            }
                            finally
                            {
                                // Always runs regardless of subscriber exceptions — server invariant that keeps dead connections from
                                // staying owners (letting a rethrown exception skip the rest of this lambda would lose OwnerOnly delta receivers)
                                ReassignOwnership();
                            }
                        });
                        return;
                    }
                }
            }
        }

        /// <summary>List of live connections (a copy).</summary>
        public IReadOnlyList<ServerConnection> SnapshotConnections()
        {
            lock (_gate) return _connections.ToArray();
        }

        /// <summary>
        /// Minimal ownership policy — assigns objects (ascending netId) to connections in order, one lap per
        /// pass, and notifies every client.
        /// ponytail: round-robin minimal policy; replace with an explicit ownership-transfer API if production demands it.
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

        /// <summary>Late-join catch-up — dynamic objects arrive as spawn messages; scene objects as per-sub full-state replication.</summary>
        private void SendCatchup(ServerConnection conn)
        {
            bool isOwner;
            foreach (var (netId, entry) in SnapshotObjects())
            {
                if (!IsRelevant(entry, conn, netId)) continue;   // irrelevant object — excluded from catch-up (sent on relevance transition)
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
                        byte[] state = TryWriteFull(handler, entry.Subs[subId].Instance, isOwner);
                        if (state != null && state.Length > 0)   // null when every field is filtered out by conditions — skip catch-up (also null on a serializer fault — that sub-object is skipped)
                            conn.Channel.SendReplicate(netId, subId, handler.MethodId, state);
                    }
                }
            }
        }

        /// <summary>
        /// Replication tick (call from the driver Update) — picks only changed [Replicated] fields per sub-object
        /// and sends deltas per receive group (OwnerOnly/SkipOwner).
        /// The driver can share ghost sweeps and snapshots to limit copying to once per frame.
        /// Without a time argument it runs in immediate mode (no rate, starvation, or budget policy) — the P2-compatible path.
        /// </summary>
        public void TickReplication()
            => TickReplication(SnapshotObjects());

        /// <summary>Tick overload accepting a snapshot — immediate mode (no policy applied).</summary>
        public void TickReplication(IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> objects)
            => TickReplication(objects, double.NaN);

        /// <summary>
        /// P3 tick — refreshes visibility (cull distance, relevancy hook), then sends delta candidates filtered by
        /// dormancy and update rate, ordered by priority (with starvation boost), within the global and per-type
        /// budgets; anything over budget is deferred to the next tick. time is a monotonic clock in seconds.
        /// </summary>
        public void TickReplication(IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> objects, double time)
        {
            var connections = SnapshotConnections();   // capture once per tick (avoids per-object copies)
            if (connections.Count == 0) return;

            bool timed = !double.IsNaN(time);
            int budget = timed ? ReplicationBudgetPerTickBytes : 0;   // immediate mode skips the budget policy too (P2-compatible contract)
            int budgetLeft = budget > 0 ? budget : int.MaxValue;
            if (_gridEnabled) RefreshGrid(objects, connections);   // P4 — refresh grid visibility once per tick

            _pending.Clear();
            foreach (var (netId, entry) in objects)
            {
                if (!UpdateVisibility(netId, entry, connections)) continue;   // irrelevant to every connection — skip compare and send (restores the baseline on re-entry)

                // Snapshot per-connection relevancy as a bitmask at enqueue time — removes per-item × per-connection
                // re-evaluation (hook + transform native calls) from the send loop.
                // Falls back to re-evaluation at send time with more than 64 connections (rare large topology).
                ulong relevantMask = 0;
                int maskCount = Math.Min(connections.Count, 64);
                for (int i = 0; i < maskCount; i++)
                    if (_relevanceScratch[i]) relevantMask |= 1ul << i;

                var policy = entry.Instance as IUniNetReplicationPolicy;
                if (policy != null && policy.IsNetworkDormant) continue;   // dormant — skip delta compare (flushing dormancy sends the accumulated changes)

                float hz = policy?.NetworkUpdateFrequencyHz ?? 0f;
                for (byte subId = 0; subId < entry.Subs.Length; subId++)
                {
                    var sub = entry.Subs[subId];
                    var handler = sub.Replication;
                    if (handler == null || !handler.HasFields) continue;
                    if (timed && hz > 0f && time < sub.NextReplicateTime) continue;   // rate not due yet — snapshot kept, changes go on the due tick

                    byte[] toOwner, toOthers;
                    try
                    {
                        (toOwner, toOthers) = handler.CompareAndWriteDelta(sub);
                    }
                    catch (Exception ex)
                    {
                        // A faulty user serializer (custom Write/Read/Equals, see ADR-0020) must not kill the whole
                        // replication tick — skip this sub-object; the snapshot was not updated, so it retries next tick.
                        // The rate deadline still advances: a deterministic fault (e.g. an over-limit collection kept as-is)
                        // would otherwise re-throw at the full send rate and flood the console with stack traces.
                        UniNetEnvironment.LogFault(ex);
                        if (timed && hz > 0f) sub.NextReplicateTime = time + 1f / hz;   // fault backoff — one period
                        continue;
                    }
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

            // Merge candidates with the previous tick's deferrals → send in starvation-boosted priority order (UE GetNetPriority: priority × (1 + waitSec / 0.1))
            _pending.AddRange(_queue);
            _pending.Sort((a, b) =>
            {
                double effA = a.Priority * (1 + (timed ? time - a.EnqueuedAt : 0) / 0.1);
                double effB = b.Priority * (1 + (timed ? time - b.EnqueuedAt : 0) / 0.1);
                int cmp = effB.CompareTo(effA);
                return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq);
            });

            _queue.Clear();
            _typeBudgetLeft.Clear();   // refill per-type channel budgets — left empty in immediate mode (no budget policy)
            if (timed)
                foreach (var kv in _channelBudgets) _typeBudgetLeft[kv.Key] = kv.Value;

            foreach (var item in _pending)
            {
                var entry = GetEntry(item.NetId);
                if (entry == null) continue;   // destroyed mid-tick — drop the deferral

                var entryPolicy = entry.Instance as IUniNetReplicationPolicy;
                if (entryPolicy != null && entryPolicy.IsNetworkDormant)
                {
                    // Went dormant — hold the deferrals too. Dropping them would lose changes permanently (the snapshot
                    // was already updated), so they are sent once dormancy is flushed.
                    _queue.Add(item);
                    continue;
                }

                int size = Math.Max(item.Owner?.Length ?? 0, item.Others?.Length ?? 0);
                bool globalOk = budgetLeft >= size || (budget > 0 && size > budget);   // force-send a single delta larger than the budget (anti-starvation)
                bool typeOk = true;
                if (_typeBudgetLeft.TryGetValue(item.SenderType, out int typeLeft))
                {
                    _channelBudgets.TryGetValue(item.SenderType, out int typeBudget);
                    typeOk = typeLeft >= size || (typeBudget > 0 && size > typeBudget);
                }
                if (!globalOk || !typeOk)
                {
                    _queue.Add(item);   // defer — EnqueuedAt is preserved so the starvation boost keeps growing
                    continue;
                }

                int spent = 0;
                for (int i = 0; i < connections.Count; i++)
                {
                    var conn = connections[i];
                    if (conn.Disconnected) continue;
                    bool relevant = i < 64
                        ? (item.RelevantMask & (1ul << i)) != 0
                        : IsRelevant(entry, conn, item.NetId);   // >64 connections — re-evaluate at send time (rare large topology)
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

        // ── P3 — visibility (relevancy), channel budget, viewers ────────────────────────────

        /// <summary>Sets a connection's viewer position — the reference point for cull-distance tests (main thread). Connections without one are never distance-culled (always relevant).</summary>
        public void SetViewerPosition(long connId, float x, float y, float z)
            => _viewerPositions[connId] = new ViewerPosition(x, y, z);

        /// <summary>Clears a connection's viewer position — the connection is no longer distance-culled (main thread).</summary>
        public void ClearViewerPosition(long connId) => _viewerPositions.Remove(connId);

        /// <summary>Sets a per-type (channel) send budget per tick — stops one type's flood from starving others (bytesPerTick ≤ 0 disables; main thread).</summary>
        public void SetReplicationChannelBudget(Type behaviourType, int bytesPerTick)
        {
            if (behaviourType == null) throw new ArgumentNullException(nameof(behaviourType));
            if (bytesPerTick > 0) _channelBudgets[behaviourType] = bytesPerTick;
            else _channelBudgets.Remove(behaviourType);
        }

        /// <summary>
        /// P4 grid spatial-partition visibility (RepGraph style) — divides the world into a cellSize grid (XZ
        /// plane) and treats only objects in cells within visibleRadius of a viewer as relevant. This is a
        /// quantized test replacing exact distance (boundary error ≤ cellSize); per-object NetworkCullDistance > 0
        /// still applies, ANDed with the grid. Connections without a viewer position fail open per the P3 contract (main thread).
        /// </summary>
        public void SetVisibilityGrid(float cellSize, float visibleRadius)
        {
            if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (visibleRadius <= 0f) throw new ArgumentOutOfRangeException(nameof(visibleRadius));
            _gridCellSize = cellSize;
            _gridRadius = visibleRadius;
            _gridEnabled = true;
        }

        /// <summary>Disables grid visibility — falls back to P3 distance culling (main thread).</summary>
        public void ClearVisibilityGrid() => _gridEnabled = false;

        /// <summary>P4 grid refresh — assigns objects to cells and computes each connection's (viewer-radius) relevant set, once per tick.
        /// Bucket lists and connection sets are reused to eliminate per-tick GC pressure. ponytail: extend pooling if object/connection counts grow very large.</summary>
        private void RefreshGrid(IReadOnlyList<(ulong NetId, ServerObjectEntry Entry)> objects, IReadOnlyList<ServerConnection> connections)
        {
            // Recycle buckets — clear the previous tick's lists for reuse (the dictionary is rebuilt because keys change)
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
                else _gridAlwaysRelevant.Add(netId);   // object without position — always relevant (fail-open)
            }

            // Reuse per-connection sets — keep the dictionary, clear and refill each set (no GC pressure). Keys of dropped connections are removed in DetachConnection
            int range = (int)Math.Ceiling(_gridRadius / _gridCellSize);
            foreach (var conn in connections)
            {
                if (conn.Disconnected) continue;
                if (!_viewerPositions.TryGetValue(conn.ConnId, out var viewer)) continue;   // no set created — IsRelevant's fail-open handles it
                if (!_gridRelevant.TryGetValue(conn.ConnId, out var set)) _gridRelevant[conn.ConnId] = set = new HashSet<ulong>();
                set.Clear();   // reuse the existing instance — keeps capacity
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

        /// <summary>Whether an object is relevant to a connection — relevancy hook AND (grid membership OR cull distance against the viewer position). Always relevant without a policy.</summary>
        private bool IsRelevant(ServerObjectEntry entry, ServerConnection conn, ulong netId)
        {
            if (entry.Instance is not IUniNetReplicationPolicy policy) return true;
            if (!policy.IsNetworkRelevant(conn.ConnId)) return false;

            float cull = policy.NetworkCullDistance;
            if (_gridEnabled && _viewerPositions.ContainsKey(conn.ConnId))
            {
                if (cull <= 0f) return GridContains(conn.ConnId, netId);   // grid membership replaces the distance test (quantization error ≤ cellSize)
                if (!GridContains(conn.ConnId, netId)) return false;   // after the global radius passes, still check the object's own cull (AND)
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
        /// Per-object visibility refresh — tracks per-connection relevancy changes and returns whether anything
        /// is relevant. A scene object's first evaluation silently seeds only (P2 contract — after registration,
        /// only subsequent change deltas are sent). Re-entry (irrelevant → relevant again) and a dynamic
        /// object's first evaluation (the client doesn't have it) restore the baseline with a spawn/full state —
        /// preventing loss of deltas missed while irrelevant.
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
                        SendVisibilityState(conn, netId, entry);   // re-entry baseline restore · dynamic object creation
                }
                else
                {
                    _relevanceScratch[i] = false;
                    seen.Remove(netId);   // relevant → irrelevant — stop deltas (client keeps the last state; propagation destroy is out of contract)
                }
            }
            return anyRelevant;
        }

        /// <summary>Sends a relevancy transition — dynamic objects arrive as spawns (the client lacks them); scene objects as full-state replication (same path as catch-up, outside the budget).</summary>
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
                byte[] state = TryWriteFull(handler, entry.Subs[subId].Instance, isOwner);
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

        /// <summary>
        /// Raises a lifecycle event — isolates subscribers (the first exception never silences the rest) and
        /// rethrows the last exception afterwards.
        /// The rethrown exception is logged by the main-pump guard (driver/generated code catch), and remaining
        /// pump work resumes next frame.
        /// </summary>
        private static void RaiseLifecycle(Action<long> handlers, long connId)
        {
            if (handlers == null) return;
            Exception last = null;
            foreach (Action<long> handler in handlers.GetInvocationList())
            {
                try { handler(connId); }
                catch (Exception e) { last = e; }
            }
            if (last != null) ExceptionDispatchInfo.Capture(last).Throw();   // preserves the original stack trace — logging belongs to the Unity layer; core stays log-free
        }

        /// <summary>Viewer position (the reference point for cull-distance tests).</summary>
        private readonly struct ViewerPosition
        {
            public readonly float X, Y, Z;
            public ViewerPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
        }

        /// <summary>One delta deferred for exceeding a budget — EnqueuedAt anchors the starvation boost; RelevantMask holds the target connection bits (low 64).</summary>
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

        /// <summary>Builds the sub table — slot order = component order, including attaching replication handlers and initializing snapshots.</summary>
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

        /// <summary>
        /// WriteFull with per-sub-object fault isolation — a throwing user serializer (custom Write, see ADR-0020)
        /// skips that sub-object's state instead of aborting the spawn/catch-up/visibility pass. Returns null on fault
        /// (the wire treats it as a zero-length state).
        /// </summary>
        private static byte[] TryWriteFull(UniNetReplicationHandler handler, object instance, bool isOwner)
        {
            try
            {
                return handler.WriteFull(instance, isOwner);
            }
            catch (Exception ex)
            {
                UniNetEnvironment.LogFault(ex);
                return null;
            }
        }

        /// <summary>Assembles the spawn message for a receiver — [7 transform values][sub count][per-sub typeKey + full state] (sub slots implied by order).</summary>
        private static void SendSpawnState(IUniNetSystemChannel channel, ulong netId, ServerObjectEntry entry,
            float px, float py, float pz, float qx, float qy, float qz, float qw, bool isOwner)
        {
            var typeKeys = new ulong[entry.Subs.Length];
            var states = new byte[entry.Subs.Length][];
            for (int i = 0; i < entry.Subs.Length; i++)
            {
                typeKeys[i] = entry.Subs[i].TypeKey;
                states[i] = entry.Subs[i].Replication != null && entry.Subs[i].Replication.HasFields
                    ? TryWriteFull(entry.Subs[i].Replication, entry.Subs[i].Instance, isOwner)
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
