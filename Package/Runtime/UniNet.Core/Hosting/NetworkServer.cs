using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using MessageProtocol.Serialize;

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
        private ulong[] _relevanceOverflowScratch = Array.Empty<ulong>();
        private long _seq;

        // Batched replication — per-connection accumulation buffers (parallel to the tick's connection snapshot;
        // index-stable for the duration of one TickReplication call). Cleared and reused every tick.
        private BatchBuffer[] _batchRel = Array.Empty<BatchBuffer>();
        private BatchBuffer[] _batchSeq = Array.Empty<BatchBuffer>();

        // P4 — grid spatial-partition visibility (RepGraph style) state. Main thread only.
        private bool _gridEnabled;
        private float _gridCellSize = 32f;
        private float _gridRadius = 48f;
        private readonly Dictionary<(int X, int Z), List<ulong>> _gridBuckets = new();
        private readonly List<ulong> _gridAlwaysRelevant = new();
        private readonly Dictionary<long, HashSet<ulong>> _gridRelevant = new();
        private readonly List<List<ulong>> _bucketFree = new();   // pooled empty cell buckets available for reuse
        private readonly List<List<ulong>> _bucketUsed = new();   // cell buckets assigned during this tick

        // ADR-0031 — declared connection↔player-object mapping (game-owned; ownership transfers do not move it). Main thread.
        private readonly Dictionary<long, ulong> _playerObjects = new();

        // ADR-0032 — per-connection ServerRpc fixed-window rate state. Keyed separately from _connections so the
        // dispatch-thread check never contends with the connection registry lock; per-window lock is per-connection.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, RpcRateWindow> _rpcWindows = new();
        private long _rateLimitedDrops;

        // CON-001 — O(1) approval index for the game-ServerRpc dispatch hot path (no lock/copy per call, unlike
        // SnapshotConnections). Added on approval (inactive-checking attach, accepted declaration), removed on
        // detach — detached/unknown connIds report unapproved, matching the old snapshot-based lookup.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _approvedConns = new();

        // ADR-0034 — grace sessions: disconnected connections whose objects+ownership are preserved for reattach.
        // Keyed by opaque session token (issued in Welcome, ADR-0041). Guarded by _gate: issuance runs on the transport
        // accept thread (AttachConnection) while save/resume/sweep run on the main queue — the registry lock arbitrates both
        // (Monitor is reentrant, so nested Snapshot* calls under the same gate stay safe).
        private readonly Dictionary<string, GraceSession> _graceSessions = new();
        private double _nextGraceSweep;

        // REC-001 — closed-token tombstones (token → (reason, expiry)): consumed and swept-expired tokens report
        // UnknownToken / Expired exactly instead of masquerading as SessionNotFound (a retryable detach race), so a
        // double-consume loser stops retrying immediately. Pruned at the original expiry in the grace sweep — bounded
        // by the grace window, never grows unboundedly. Guarded by _gate.
        private readonly Dictionary<string, (byte Reason, double ExpiresAt)> _closedResumeTokens = new();

        /// <summary>Global replication byte budget per tick — 0 = unlimited (default). Exceeding deltas are deferred to a queue and sent on the next tick.</summary>
        public int ReplicationBudgetPerTickBytes { get; set; }

        /// <summary>Protocol version this server expects from clients (ADR-0028 handshake). Set from UniNetEndpointOptions.ProtocolVersion;
        /// defaults to the package version, so same-package ends always match. A client declaring a different version is rejected
        /// with a VersionMismatch kick. Empty string disables the check.</summary>
        public string ExpectedProtocolVersion { get; set; } = UniNetInfo.Version;

        /// <summary>Server-side UniNet traffic counters (ADR-0029) — payload bytes/packets sent by this server. Per-connection view: <see cref="ServerConnection.RttMs"/>.</summary>
        public UniNetTrafficCounters TrafficStats { get; } = new();

        /// <summary>Session token entropy in bytes (ADR-0041) — 256-bit: guessing a live token is computationally infeasible.</summary>
        public const int SessionTokenBytes = 32;

        /// <summary>
        /// Session token length in characters (ADR-0041) — Base64 of <see cref="SessionTokenBytes"/> bytes. The generated resume
        /// handler bounds the wire-10 payload BEFORE parsing (see <see cref="MaxResumeRequestPayloadBytes"/>), so a forged length
        /// prefix can never drive a large string allocation (MessageProtocol strings are int32-length-prefixed UTF8 — the reader
        /// also validates the declared length against the buffer before allocating, so truncation/forgery throws, not allocates).
        /// </summary>
        public const int MaxSessionTokenChars = 44;

        /// <summary>
        /// Upper bound of a well-formed wire-10 (ResumeRequest) payload (ADR-0041) — int32 UTF8 length prefix (4 bytes) + token
        /// characters. The generated server handler rejects anything larger before parsing.
        /// </summary>
        public const int MaxResumeRequestPayloadBytes = sizeof(int) + MaxSessionTokenChars;   // 48

        /// <summary>
        /// Issues a new session token (ADR-0034/0041) — <see cref="SessionTokenBytes"/> bytes of cryptographic randomness as an
        /// opaque Base64 string, sent in the Welcome and presented back on reconnect. Never empty and never equal to another
        /// live connection's token or a pending grace-session token (collision odds are ~2^-256; the check is belt and braces).
        /// Uses <see cref="System.Security.Cryptography.RandomNumberGenerator"/> — never time, Random, Guid, or counters,
        /// because predictable sequences let an attacker guess a neighbor's token and steal their grace session.
        /// Thread-safe: callable from the transport accept thread (takes the registry gate).
        /// </summary>
        public string IssueSessionToken()
        {
            lock (_gate)
            {
                while (true)
                {
                    var bytes = new byte[SessionTokenBytes];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
                    string token = Convert.ToBase64String(bytes);
                    if (_graceSessions.ContainsKey(token)) continue;
                    bool clash = false;
                    for (int i = 0; i < _connections.Count; i++)
                        if (_connections[i].SessionToken == token) { clash = true; break; }
                    if (!clash) return token;
                }
            }
        }

        /// <summary>
        /// Per-connection ServerRpc rate limit in calls/second (ADR-0032) — 0 = unlimited (default, opt-in).
        /// Enforced in the generated dispatch before the handler runs; over-limit calls are dropped (empty response)
        /// and counted. Set from game bootstrap; a generous normal-gameplay ceiling keeps floods (macros/cheats) out.
        /// </summary>
        public int ServerRpcRateLimitPerSecond { get; set; }

        /// <summary>
        /// Grace period for session preservation (ADR-0034) in seconds — how long a disconnected connection's
        /// objects and ownership survive before the reassignment policy adopts them. 0 disables (immediate).
        /// </summary>
        public double SessionGraceSeconds { get; set; }

        // ADR-0035 — scene transition tracking
        private string? _pendingScene;
        private string? _currentScene;   // SCN-001 — the last COMPLETED transition's scene: post-completion joiners receive it (previously unrecoverable)
        private readonly HashSet<long> _sceneReadyConnections = new();
        private const int MaxPendingSceneDestroys = 4096;

        /// <summary>Fires on the main thread when every live connection has reported the pending scene loaded (ADR-0035). Parameter: the scene identifier.</summary>
        public event Action<string>? AllClientsSceneLoaded;

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

            /// <summary>Spawnable prefab identity (stamped NetworkSpawnable GUID hash — ADR-0024) — carried on spawn messages so clients instantiate the same prefab.</summary>
            public ulong PrefabId;

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

            /// <summary>Server time of the last tick this sub sent a delta (-1 = never). Quiescence resend measures quiet time from this.</summary>
            public double LastChangeTime = -1;

            /// <summary>Whether the one-shot quiescence resend already went out for the current quiet stretch (unreliable subs only).</summary>
            public bool QuiescenceResent;

            /// <summary>Forces the next delta onto the reliable batch (dormancy flush — a lost unreliable flush would strand stale state).</summary>
            public bool NextSendReliable;

            public SubObjectEntry(object instance) => Instance = instance;
        }

        /// <summary>Per-connection server state — holds the hub together with its system send channel (implemented by the generated hub).</summary>
        public sealed class ServerConnection
        {
            /// <summary>Connection ID assigned by the server.</summary>
            public long ConnId { get; }

            /// <summary>Whether the connection has ended.</summary>
            public bool Disconnected { get; internal set; }

            /// <summary>
            /// REC-002 — the client announced a voluntary leave (wire 15) before its transport close: the detach path
            /// then discards the grace save for this departure (the leaving client burns its resume token — nobody
            /// can ever resume) and orphans are adopted immediately. Written on the dispatch thread, read by the
            /// detach lambda on the main queue — volatile like <see cref="RttMs"/>. Losing the race (detach first)
            /// downgrades to the legacy grace behavior — the notice is best effort by contract, no new failure mode.
            /// </summary>
            internal volatile bool IntentionalLeave;

            /// <summary>
            /// This connection's round-trip time in ms, as reported by the client's own probe (ADR-0029 ping wire 8 —
            /// the client measures client↔server RTT and piggybacks the EMA on each ping). -1 until the first report.
            /// Written on the DRPC dispatch thread, read on the main thread — volatile int-µs backing (same pattern as
            /// NetworkClient.RoundTripTimeMs: doubles can tear on 32-bit runtimes and an unfenced write may read stale).
            /// </summary>
            public double RttMs
            {
                get => System.Threading.Volatile.Read(ref _rttUs) / 1000.0;
                internal set => System.Threading.Volatile.Write(ref _rttUs, (int)(value * 1000.0));
            }
            private int _rttUs = -1000;   // microseconds backing store — the -1 ms sentinel encodes as -1000

            /// <summary>Whether the Welcome has been sent (guards LoadScene broadcast from racing attach).</summary>
            public bool WelcomeSent { get; internal set; }

            /// <summary>
            /// Whether this connection's protocol version was accepted (CON-001) — true immediately when version
            /// checking is inactive (empty ExpectedProtocolVersion), otherwise set when the client's wire-7 declaration
            /// matches. Until true the server holds ownership reassignment, catch-up, the ClientConnected event, and
            /// all game-data sends; system wires (7/8/10/12/15) stay live so the handshake itself can complete.
            /// Volatile-backed: written on the dispatch thread, read by the tick and dispatch filters.
            /// </summary>
            public bool VersionApproved
            {
                get => System.Threading.Volatile.Read(ref _versionApproved) != 0;
                internal set => System.Threading.Volatile.Write(ref _versionApproved, value ? 1 : 0);
            }
            private int _versionApproved;

            /// <summary>Clock reading stamped on the main queue when the Welcome is sent (UniNetTime.LocalClock domain — same as the grace sweep) — NaN until then; drives the undeclared-connection timeout (CON-001). Stamped off the attach thread on purpose: Unity's clock throws off the main thread.</summary>
            public double AttachedAtClock { get; internal set; } = double.NaN;

            /// <summary>The post-approval attach work already ran — makes approval idempotent under any main-queue ordering (CON-001).</summary>
            internal bool PostApprovalRun;

            /// <summary>SCN-002 — wire 11 was sent to this connection and its ready report has not arrived: game data
            /// (catch-up, replication, spawns, ownership notices) is held. Reports land after client registration
            /// (UniNetManager registers scene objects BEFORE reporting), so full-state baselines are never dropped.</summary>
            internal bool AwaitingSceneReady;

            // Main-thread only, including detach cleanup: absent objects have no catch-up baseline to retire client survivors.
            internal HashSet<ulong> PendingSceneDestroys;

            /// <summary>SCN-003 — wire 11 was sent to this connection for the CURRENT transition (reset at LoadScene start):
            /// only reports arriving after this connection's wire 11 count — stale reports that predate the send are rejected.</summary>
            internal bool SceneLoadSent;

            // Invalidated at transition start and each wire-11 send. Dispatch reads atomically BEFORE queueing.
            internal long SceneLoadGeneration;

            /// <summary>SCN-002 — the attach tail (reassignment, catch-up, ClientConnected) is held behind the scene-ready report
            /// (set when RunPostApproval sends wire 11). CON-001 legacy behavior when no scene was ever loaded.</summary>
            internal bool CatchupHeld;

            /// <summary>Session token issued in the Welcome (ADR-0034/0041) — opaque Base64 string identifying the connection for
            /// grace-session reattach. Empty = not issued (inactive; grace saving treats it as no session).</summary>
            public string SessionToken { get; internal set; } = string.Empty;

            /// <summary>The connection's send channel (the generated server hub — generated code type-casts it for sends).</summary>
            public IUniNetSystemChannel Channel { get; }

            internal ServerConnection(long connId, IUniNetSystemChannel channel)
            {
                ConnId = connId;
                Channel = channel;
            }
        }

        /// <summary>Registers a scene object — once per object, with all components in slot order. Both ends read the same edit-time-stamped serialized id (GlobalObjectId hash — ADR-0023); unstamped objects fall back to the hierarchy-path hash.</summary>
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
        /// order plus the spawnable prefab identity (stamped NetworkSpawnable GUID hash — ADR-0024).
        /// The owning connection is assigned immediately: ownerConnId when given (explicit ownership —
        /// NetSession spawns each joiner's token for that joiner), otherwise round-robin. Afterwards call
        /// BroadcastSpawn(netId) to propagate it to all clients (main thread).
        /// </summary>
        public ulong RegisterDynamicObject(IReadOnlyList<object> components, long ownerConnId = 0, ulong prefabId = 0)
        {
            lock (_gate)
            {
                ulong netId = _nextDynamicNetId;
                while (_objects.ContainsKey(netId)) netId++;   // skip collisions with scene hashes — keeps the registration invariant
                _nextDynamicNetId = netId + 1;

                var entry = new ServerObjectEntry(components[0]) { IsDynamic = true, PrefabId = prefabId };
                AttachSubs(entry, components);
                if (ownerConnId != 0)
                    entry.OwnerConnId = ownerConnId;
                else if (_connections.Count > 0)
                {
                    entry.OwnerConnId = _connections[_rrCursor % _connections.Count].ConnId;
                    _rrCursor++;
                }
                _objects[netId] = entry;
                return netId;
            }
        }

        /// <summary>Removes a registered object and propagates its destroy. Approved connections waiting for scene readiness receive the destroy after their valid ready report; other ready connections receive it immediately. Returns false if the object was not registered (main thread).</summary>
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
            {
                if (conn.Disconnected || !conn.VersionApproved) continue;
                if (conn.AwaitingSceneReady)
                {
                    var pending = conn.PendingSceneDestroys ??= new HashSet<ulong>();
                    if (pending.Contains(netId)) continue;
                    if (pending.Count == MaxPendingSceneDestroys)
                    {
                        conn.PendingSceneDestroys = null;
                        DisconnectClient(conn.ConnId, UniNetDisconnectReason.Kicked);
                        // Revoke acceptance immediately; the notice/transport close still uses the normal kick path.
                        DetachConnection(conn.Channel);
                        continue;
                    }
                    pending.Add(netId);
                }
                else
                    conn.Channel.SendDestroy(netId);
            }
            return true;
        }

        /// <summary>Propagates a dynamic spawn to all connections — the ownership notice (owner 0 included) BEFORE the spawn message (transform, sub types, full state), the same baseline order as catch-up/re-entry (OWN-001).</summary>
        public void BroadcastSpawn(ulong netId)
        {
            var entry = GetEntry(netId);
            if (entry == null) return;

            GetSpawnTransform(entry, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
            foreach (var conn in SnapshotConnections())
            {
                if (conn.Disconnected || !conn.VersionApproved || conn.AwaitingSceneReady) continue;   // CON-001 — undeclared connections receive no game data; SCN-002 — scene-held connections wait for the ready report
                if (!IsRelevant(entry, conn, netId)) continue;   // irrelevant connection — skip the spawn (sent when it becomes relevant)
                GetVisibleSet(conn.ConnId).Add(netId);
                GetEverVisibleSet(conn.ConnId).Add(netId);
                InvalidateDeferredRecipient(netId, conn.ConnId);
                conn.Channel.SendOwnerUpdate(netId, entry.OwnerConnId);   // owner baseline before the state — a frame between state and notice would twist the initial spawn's Notify owner view (OWN-001)
                SendSpawnState(conn.Channel, netId, entry, px, py, pz, qx, qy, qz, qw, conn.ConnId == entry.OwnerConnId);
            }
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

        /// <summary>
        /// Resets the rejection-log throttle. Test hook — the editor can run the EditMode and PlayMode suites in one
        /// domain (Enter Play Mode domain reload disabled), leaking static throttle state across suites and silently
        /// suppressing the expected rejection log. Batch runs (separate processes) never need this.
        /// </summary>
        internal static void ResetRejectionLogThrottleForTests()
        {
            lock (_rejectionGate)
            {
                _rejectionLoggedConns.Clear();
                _lastRejectionLogUtc = default;
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

        /// <summary>Attaches a new connection — schedules the Welcome; the remaining attach work (ownership reassignment, catch-up, the ClientConnected event) runs at approval (CON-001): immediately when version checking is inactive (empty <see cref="ExpectedProtocolVersion"/>), otherwise once the client's wire-7 declaration matches. An undeclared connection receives no game data and is swept by <see cref="SweepUnapprovedConnections"/>.</summary>
        public long AttachConnection(IUniNetSystemChannel channel)
        {
            lock (_gate)
            {
                var conn = new ServerConnection(_nextConnId++, channel);
                channel.UniNetConnId = conn.ConnId;
                conn.SessionToken = IssueSessionToken();   // ADR-0034 — grace-session reattach identity
                // CON-001 — AttachedAtClock stays NaN here: this section runs on the DRPC ACCEPT thread and
                // UniNetTime.LocalClock is Unity's Time.unscaledTimeAsDouble, which throws off the main thread —
                // stamping here would abort the attach entirely. The queued main lambda stamps it instead.
                if (string.IsNullOrEmpty(ExpectedProtocolVersion))
                {
                    conn.VersionApproved = true;   // CON-001 — checking inactive: immediate synchronous approval, exact legacy behavior
                    _approvedConns[conn.ConnId] = 1;   // hot-path index — kept in sync with the approval flag
                }
                _connections.Add(conn);
                var connId = conn.ConnId;
                var sessionToken = conn.SessionToken;
                UniNetEnvironment.QueueOnMain(() =>
                {
                    conn.AttachedAtClock = UniNetTime.LocalClock();   // CON-001 — main-thread stamp: the undeclared-timeout window starts once the Welcome is dispatchable (the client can declare from then on)
                    channel.SendWelcome(connId, ExpectedProtocolVersion, sessionToken);   // always — the client needs the server version + session token to declare
                    conn.WelcomeSent = true;
                    RunPostApproval(conn);
                });
                return conn.ConnId;
            }
        }

        /// <summary>
        /// Runs the post-approval attach work exactly once (CON-001) in the legacy order: pending scene join,
        /// ownership reassignment, catch-up, then the ClientConnected event. Held while the connection is
        /// unapproved — the wire-7 acceptance re-enters here via the main queue. Queue-order agnostic (a declaration
        /// can land before the attach lambda pumps): the per-connection flag makes re-entry a no-op.
        /// </summary>
        private void RunPostApproval(ServerConnection conn)
        {
            if (conn.Disconnected || conn.PostApprovalRun) return;
            if (!conn.VersionApproved) return;   // held — no game data, no reassignment, no event yet
            conn.PostApprovalRun = true;
            string? scene = _pendingScene ?? _currentScene;   // SCN-001 — completed transitions reach post-completion joiners too
            if (scene != null)
            {
                // SCN-002 — hold game data until the ready report: the report is sent AFTER client registration
                // (UniNetManager registers scene objects, then reports), so the catch-up full state always lands on
                // registered objects instead of being silently dropped. Timeout/retry is game responsibility (ADR-0035).
                conn.AwaitingSceneReady = true;
                System.Threading.Interlocked.Increment(ref conn.SceneLoadGeneration);
                conn.SceneLoadSent = true;
                conn.CatchupHeld = true;   // SCN-002 — the attach tail waits for the ready report
                conn.Channel.SendLoadScene(scene);
                return;   // the reassignment/catch-up/ClientConnected tail runs on the ready report (RunPostSceneReady)
            }
            RunPostSceneReady(conn, true);
        }

        /// <summary>Reconciles ownership and full state after approval or scene readiness. Only the initial attach
        /// raises ClientConnected; existing participants still need baselines for changes missed during the hold.</summary>
        private void RunPostSceneReady(ServerConnection conn, bool notifyConnected)
        {
            if (conn.Disconnected) return;
            // The current full state supersedes this recipient's queued deltas, not other clients' changes.
            InvalidateDeferredRecipient(null, conn.ConnId);
            ReassignOwnership();
            SendCatchup(conn);
            if (notifyConnected)
                RaiseLifecycle(ClientConnected, conn.ConnId);   // after catch-up — spawns raised here never race the catch-up
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
                        _approvedConns.TryRemove(conn.ConnId, out _);   // CON-001 — the approval index dies with the connection (unknown → unapproved)
                        _connections.RemoveAt(i);
                        bool wasApproved = conn.VersionApproved;   // CON-001 — a never-approved connection never surfaced to the game (no event, no grace session); it owns nothing
                        UniNetEnvironment.QueueOnMain(() =>
                        {
                            conn.PendingSceneDestroys = null;
                            _viewerPositions.Remove(conn.ConnId);   // P3/P4 cleanup — same main thread as the tick
                            _visible.Remove(conn.ConnId);
                            _everVisible.Remove(conn.ConnId);   // prevents HashSets lingering across reconnects (connIds are monotonically increasing)
                            _gridRelevant.Remove(conn.ConnId);   // clears the P4 grid sets too
                            InvalidateDeferredRecipient(null, conn.ConnId);
                            try
                            {
                                if (wasApproved)
                                    RaiseLifecycle(ClientDisconnected, conn.ConnId);   // before reassignment — the game can identify and handle owned objects
                            }
                            finally
                            {
                                // Always runs regardless of subscriber exceptions — server invariant that keeps dead connections from
                                // staying owners (letting a rethrown exception skip the rest of this lambda would lose OwnerOnly delta receivers)
                                if (wasApproved && conn.IntentionalLeave)
                                {
                                    // REC-002 — a notified voluntary leave discards the grace save (the leaving client burned its
                                    // resume token in ClientStop — a preserved session could never be resumed and would only block
                                    // orphan adoption for the whole grace window); orphans are adopted immediately instead.
                                    // Tombstoning the token (same one-grace-window bound as the sweep) makes a stale wire-10 resume
                                    // report UnknownToken (terminal — no retry armed) instead of a misleading retryable SessionNotFound.
                                    // Empty token = grace identity never issued — nothing to tombstone.
                                    if (!string.IsNullOrEmpty(conn.SessionToken) && SessionGraceSeconds > 0)
                                    {
                                        lock (_gate)   // issuance reads the registry from the transport accept thread (ADR-0041) — same gate arbitrates
                                            _closedResumeTokens[conn.SessionToken] = ((byte)GraceResumeResult.UnknownToken, UniNetTime.LocalClock() + SessionGraceSeconds);
                                    }
                                    ReassignOwnership();
                                }
                                else if (wasApproved && SessionGraceSeconds > 0)
                                    SaveGraceSession(conn.ConnId, conn.SessionToken);
                                else
                                    ReassignOwnership();
                                _playerObjects.Remove(conn.ConnId);   // ADR-0031 — the mapping dies with the connection, AFTER the event (ClientDisconnected handlers can still read GetPlayerObject for custom cleanup — loot drops etc.)
                                _rpcWindows.TryRemove(conn.ConnId, out _);   // ADR-0032 — rate state dies with the connection
                                _sceneReadyConnections.Remove(conn.ConnId);   // ADR-0035 — scene gate readiness dies too
                                CheckSceneGate();   // departure may complete the gate
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

        /// <summary>Applies a client's version declaration (ADR-0028 wire 7 — generated server hub). Returns whether the version was
        /// accepted; a mismatch kicks the connection with the VersionMismatch reason (the generated hub logs the rejection).
        /// CON-001: an accepted declaration approves the connection and releases the held attach work (main queue); a
        /// redeclaration by an already-approved connection is idempotent (accepted, nothing re-runs).</summary>
        public bool OnClientVersionDeclared(long connId, string version)
        {
            if (string.IsNullOrEmpty(ExpectedProtocolVersion) || version == ExpectedProtocolVersion)
            {
                if (!string.IsNullOrEmpty(ExpectedProtocolVersion))
                {
                    foreach (var conn in SnapshotConnections())
                        if (conn.ConnId == connId && !conn.Disconnected && !conn.VersionApproved)
                        {
                            conn.VersionApproved = true;
                            _approvedConns[conn.ConnId] = 1;   // hot-path index — kept in sync with the approval flag
                            UniNetEnvironment.QueueOnMain(() => RunPostApproval(conn));   // dispatch thread — held work runs in order on the main queue
                        }
                }
                return true;
            }
            bool kicked = DisconnectClient(connId, UniNetDisconnectReason.VersionMismatch);
            if (!kicked)
                UniNetEnvironment.LogFault(new InvalidOperationException(
                    $"Version-mismatch kick found no connection {connId} — declaration raced attachment (ADR-0028). The mismatched client may survive this check."));   // surfaced instead of a silent false — the accept callback attaches synchronously so this window is theoretical, but never silent
            return false;
        }

        /// <summary>Whether the connection has declared an accepted protocol version (CON-001) — the generated dispatch gate for game ServerRpcs. O(1) index lookup; unknown and detached connections report unapproved (safe default).</summary>
        public bool IsVersionApproved(long connId) => _approvedConns.ContainsKey(connId);

        /// <summary>Records one game ServerRpc dropped at the approval gate (CON-001) — called by the generated dispatch; <see cref="UnapprovedServerRpcDrops"/> is the only observable (quiet by design, no log flood).</summary>
        public void ReportUnapprovedServerRpc(long connId) => System.Threading.Interlocked.Increment(ref _unapprovedRpcDrops);

        private long _unapprovedRpcDrops;

        /// <summary>Total game ServerRpc calls dropped because the sender never declared an accepted protocol version (CON-001; all connections, since server start).</summary>
        public long UnapprovedServerRpcDrops => System.Threading.Interlocked.Read(ref _unapprovedRpcDrops);

        // REC-002 — voluntary-leave notices (wire 15) applied to live connections. Quiet observability like
        // _unapprovedRpcDrops: a notice that loses the detach race is counted only by its absence here.
        private long _intentionalLeaves;

        /// <summary>Records a voluntary-leave notice for one connection (REC-002 wire 15 — called by the generated
        /// dispatch on the network thread). Sets <see cref="ServerConnection.IntentionalLeave"/>, so the detach path
        /// skips the grace save for this departure — the client burned its resume token in ClientStop, so a preserved
        /// session could never be resumed and would only block orphan adoption for the whole grace window. Best
        /// effort by contract: a notice for an unknown/already-detached connection is ignored (the departure keeps
        /// the legacy grace behavior). Runtime dispatch point — not intended for direct game calls.</summary>
        public void OnIntentionalLeave(long connId)
        {
            foreach (var conn in SnapshotConnections())
                if (conn.ConnId == connId && !conn.Disconnected)
                {
                    conn.IntentionalLeave = true;
                    System.Threading.Interlocked.Increment(ref _intentionalLeaves);
                    return;
                }
        }

        /// <summary>Total wire-15 intentional-leave notices applied to live connections (REC-002; all connections, since server start).</summary>
        public long IntentionalLeaves => System.Threading.Interlocked.Read(ref _intentionalLeaves);

        /// <summary>
        /// How long an undeclared connection may stay attached before it is kicked with VersionMismatch (CON-001), in
        /// milliseconds. Default 10000; 0 disables the sweep (undeclared connections stay held — still no game data).
        /// Only applies while version checking is active (non-empty ExpectedProtocolVersion). Set from
        /// UniNetEndpointOptions.VersionDeclareTimeoutMs by UniNetManager.ServerAsync.
        /// </summary>
        public int VersionDeclareTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// Kicks connections that never declared an accepted protocol version within VersionDeclareTimeoutMs (CON-001) —
        /// VersionMismatch notice then close, reusing the existing kick path. Call from the driver tick alongside
        /// SweepGraceSessions; the time argument shares UniNetTime.LocalClock's domain (the attach reading).
        /// </summary>
        public void SweepUnapprovedConnections(double time)
        {
            if (VersionDeclareTimeoutMs <= 0 || string.IsNullOrEmpty(ExpectedProtocolVersion)) return;
            double timeout = VersionDeclareTimeoutMs / 1000.0;
            foreach (var conn in SnapshotConnections())
            {
                if (conn.Disconnected || conn.VersionApproved) continue;
                if (double.IsNaN(conn.AttachedAtClock)) continue;   // Welcome not yet dispatched — the timeout window has not started (thread-safe stamping, CON-001)
                if (time - conn.AttachedAtClock > timeout)
                    DisconnectClient(conn.ConnId, UniNetDisconnectReason.VersionMismatch);
            }
        }

        /// <summary>Applies a client's RTT report from its ping probe (ADR-0029 wire 8 — client-measured EMA, piggybacked on each ping).</summary>
        public void OnClientRttReported(long connId, double rttMs)
        {
            if (!(rttMs >= 0 && rttMs < 1e6)) return;   // trust-boundary clamp — drop NaN/negative/absurd client reports (observational data, keep it clean)
            foreach (var conn in SnapshotConnections())
                if (conn.ConnId == connId)
                {
                    conn.RttMs = rttMs;
                    return;
                }
        }

        /// <summary>
        /// Rate-limit gate for one ServerRpc call (ADR-0032) — called by the generated dispatch on the dispatch thread.
        /// Fixed 1-second window per connection; over-limit calls are refused and counted (RateLimitedServerRpcDrops).
        /// Runtime enforcement point — not intended for direct game calls.
        /// </summary>
        public bool AllowsServerRpc(long connId)
        {
            int limit = ServerRpcRateLimitPerSecond;
            if (limit <= 0) return true;
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;   // thread-safe clock — dispatch thread, Unity clocks are main-thread only
            var w = _rpcWindows.GetOrAdd(connId, static _ => new RpcRateWindow());
            lock (w)
            {
                if (now - w.WindowStart >= 1.0)
                {
                    w.WindowStart = now;
                    w.Count = 0;
                }
                if (w.Count >= limit)
                {
                    w.Drops++;
                    System.Threading.Interlocked.Increment(ref _rateLimitedDrops);
                    return false;
                }
                w.Count++;
                return true;
            }
        }

        /// <summary>Total ServerRpc calls dropped by the rate limit (ADR-0032; all connections, since server start).</summary>
        public long RateLimitedServerRpcDrops => System.Threading.Interlocked.Read(ref _rateLimitedDrops);

        /// <summary>Rate-limited ServerRpc drops for one connection (ADR-0032; 0 for unknown/never-limited connections).</summary>
        public long GetRateLimitedServerRpcDrops(long connId)
        {
            if (!_rpcWindows.TryGetValue(connId, out var w)) return 0;
            lock (w) return w.Drops;   // consistent with the write side — no torn 64-bit read on 32-bit runtimes
        }

        /// <summary>Fixed-window rate state for one connection (ADR-0032).</summary>
        private sealed class RpcRateWindow
        {
            public int Count;
            public double WindowStart;
            public long Drops;
        }

        /// <summary>
        /// Declares a connection's player object (ADR-0031, main thread) — the game-owned connId↔netId mapping behind
        /// GetPlayerObject/GetPlayerConnection. Register at avatar spawn; the mapping clears automatically when the
        /// connection detaches. Declarative: a SetOwner ownership transfer does NOT move the mapping.
        /// </summary>
        public void SetPlayerObject(long connId, ulong netId)
        {
            if (netId == 0)
                _playerObjects.Remove(connId);
            else
                _playerObjects[connId] = netId;
        }

        /// <summary>Removes a connection's player-object declaration (ADR-0031, main thread).</summary>
        public void ClearPlayerObject(long connId) => _playerObjects.Remove(connId);

        /// <summary>The connection's declared player object — 0 when none (ADR-0031).</summary>
        public ulong GetPlayerObject(long connId)
            => _playerObjects.TryGetValue(connId, out var netId) ? netId : 0;

        /// <summary>The connection that declared this object as its player object — 0 when none (ADR-0031; linear scan).</summary>
        public long GetPlayerConnection(ulong netId)
        {
            foreach (var kv in _playerObjects)
                if (kv.Value == netId) return kv.Key;
            return 0;
        }

        /// <summary>
        /// Tells every client to load a scene (ADR-0035, wire 11) — the server must load its own copy itself (game responsibility — this method only broadcasts and gates). Ready tracking
        /// begins immediately: <see cref="AllClientsSceneLoaded"/> fires once every live connection reports loaded (wire 12).
        /// The Unity layer loads via SceneManager; this Core method broadcasts and tracks (main thread).
        /// </summary>
        public void LoadScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) throw new ArgumentNullException(nameof(sceneName));
            _pendingScene = sceneName;
            _sceneReadyConnections.Clear();
            foreach (var conn in SnapshotConnections())
            {
                System.Threading.Interlocked.Increment(ref conn.SceneLoadGeneration);
                conn.SceneLoadSent = false;   // SCN-003 — reports predating THIS transition's wire 11 are stale (same-name re-transition guard)
                if (conn.Disconnected || !conn.WelcomeSent || !conn.VersionApproved || !conn.PostApprovalRun) continue;   // the pending approval tail delivers this transition exactly once
                conn.AwaitingSceneReady = true;   // SCN-002 — hold game data until the ready report
                System.Threading.Interlocked.Increment(ref conn.SceneLoadGeneration);
                conn.SceneLoadSent = true;
                conn.Channel.SendLoadScene(sceneName);   // wire 11
            }
        }

        /// <summary>Applies a client's scene-loaded report (ADR-0035, wire 12). Convenience overload for legacy generated
        /// handlers and manual reports — it captures the generation at call time; the current generated handler instead
        /// captures at dispatch receipt and calls the three-argument overload directly. SCN-002: the report releases the
        /// game-data hold (the client registers scene objects BEFORE reporting, so catch-up full state lands on registered
        /// objects). SCN-003 rejects reports arriving before this connection's current wire 11 send. An already-sent old
        /// same-scene report arriving after that send is indistinguishable without wire tokens.</summary>
        public void OnClientSceneLoaded(long connId, string sceneName)
            => OnClientSceneLoaded(connId, sceneName, CaptureSceneLoadGeneration(connId));

        /// <summary>Captures the connection's local scene-load generation at wire-12 dispatch receipt, before parsing or
        /// main-thread queueing. Pass it to the three-argument OnClientSceneLoaded overload. Thread-safe; no wire token.</summary>
        public long CaptureSceneLoadGeneration(long connId)
        {
            lock (_gate)
            {
                foreach (var conn in _connections)
                    if (conn.ConnId == connId && !conn.Disconnected)
                        return System.Threading.Interlocked.Read(ref conn.SceneLoadGeneration);
                return -1;
            }
        }

        /// <summary>Processes a queued scene-ready report on the main thread using its dispatch-receipt generation.
        /// Reports received before a newer LoadScene or wire-11 send cannot release that newer transition.</summary>
        public void OnClientSceneLoaded(long connId, string sceneName, long receivedGeneration)
        {
            var conn = FindConnection(connId);
            if (conn == null || conn.Disconnected || !conn.VersionApproved) return;
            if (!conn.SceneLoadSent || receivedGeneration != System.Threading.Interlocked.Read(ref conn.SceneLoadGeneration)) return;
            string? expectedScene = _pendingScene ?? _currentScene;
            if (expectedScene == null || sceneName != expectedScene) return;
            bool destroysDrained = false;
            try
            {
                DrainPendingSceneDestroys(conn);   // keep the hold until every pending destroy has succeeded
                destroysDrained = true;
                conn.AwaitingSceneReady = false;
                conn.SceneLoadSent = false;
                if (_pendingScene != null) _sceneReadyConnections.Add(connId);
                bool notifyConnected = conn.CatchupHeld;
                conn.CatchupHeld = false;
                RunPostSceneReady(conn, notifyConnected);   // existing participants also missed ownership notices and state while held
            }
            finally
            {
                if (!destroysDrained)
                {
                    conn.AwaitingSceneReady = true;
                    conn.SceneLoadSent = true;
                    _sceneReadyConnections.Remove(connId);
                }
                CheckSceneGate();   // a post-ready subscriber fault must not strand the last valid report
            }
        }

        private void DrainPendingSceneDestroys(ServerConnection conn)
        {
            var destroys = conn.PendingSceneDestroys;
            if (destroys == null) return;
            var ids = new ulong[destroys.Count];
            destroys.CopyTo(ids);
            foreach (ulong netId in ids)
            {
                // Re-registration replaces the old lifetime; catch-up owns its current baseline.
                if (GetEntry(netId) == null) conn.Channel.SendDestroy(netId);
                destroys.Remove(netId);   // a throwing send retains this and all remaining IDs for the next valid report
            }
            conn.PendingSceneDestroys = null;
        }

        /// <summary>The pending scene identifier (null when no transition is in progress — ADR-0035).</summary>
        public string? PendingScene => _pendingScene;

        /// <summary>Whether a specific connection has reported the pending scene loaded (ADR-0035).</summary>
        public bool IsClientSceneReady(long connId) => _sceneReadyConnections.Contains(connId);

        /// <summary>Fires AllClientsSceneLoaded when every live connection has reported (ADR-0035, main thread).</summary>
        private void CheckSceneGate()
        {
            if (_pendingScene == null) return;
            foreach (var conn in SnapshotConnections())
                if (!conn.Disconnected && conn.VersionApproved && (conn.AwaitingSceneReady || !_sceneReadyConnections.Contains(conn.ConnId))) return;   // someone not ready — undeclared connections cannot report (CON-001); scene-held connections report after loading (SCN-002)
            var scene = _pendingScene;
            _pendingScene = null;
            _currentScene = scene;   // SCN-001 — post-completion joiners receive this scene
            AllClientsSceneLoaded?.Invoke(scene);
        }

        /// <summary>Saves a grace session for a disconnecting connection (ADR-0034, main thread). An empty token (never issued —
        /// inactive grace) skips the session and reassigns ownership immediately instead of preserving a session nothing can resume.</summary>
        private void SaveGraceSession(long connId, string sessionToken)
        {
            var token = sessionToken;
            if (string.IsNullOrEmpty(token)) { ReassignOwnership(); return; }

            var owned = new List<ulong>();
            foreach (var (netId, entry) in SnapshotObjects())
                if (entry.OwnerConnId == connId)
                    owned.Add(netId);
            var playerObj = _playerObjects.TryGetValue(connId, out var po) ? po : 0;

            lock (_gate)   // issuance reads the registry from the transport accept thread (ADR-0041) — same gate arbitrates
            {
                _graceSessions[token] = new GraceSession
                {
                    Token = token,
                    OldConnId = connId,
                    ExpiresAt = UniNetTime.LocalClock() + SessionGraceSeconds,
                    OwnedNetIds = owned,
                    PlayerObjectNetId = playerObj,
                };
            }
        }

        /// <summary>
        /// Reattaches a grace session to a new connection (ADR-0034, main thread — called by the generated resume handler).
        /// Returns true only when the token matched a live, unexpired session AND newConnId is a current live connection;
        /// ownership is then transferred. A wrong, guessed, expired, or unissued token — or an unknown/dead newConnId —
        /// returns false WITHOUT consuming the session, so a probing attacker can never burn a victim's grace window
        /// (ADR-0041). Expired sessions are left for SweepGraceSessions, which also reassigns their orphans.
        /// </summary>
        public GraceResumeResult ResumeSession(string token, long newConnId)
        {
            GraceResumeResult result;
            double retryableMs = 0;
            GraceSession session = null;
            if (string.IsNullOrEmpty(token))
            {
                result = GraceResumeResult.UnknownToken;   // nothing presented — cannot match any issued token (defensive; the handler never sends an empty token)
            }
            else
            {
                lock (_gate)
                {
                    if (!_graceSessions.TryGetValue(token, out var found))
                    {
                        // tombstones only exist for tokens REMOVED from _graceSessions — check them HERE, first:
                        // consumed (UnknownToken) and swept-expired (Expired) report exactly, never as a retryable NotFound
                        if (_closedResumeTokens.TryGetValue(token, out var closed))
                            result = (GraceResumeResult)closed.Reason;   // exact terminal outcome — NOT retryable
                        else
                        {
                            result = GraceResumeResult.SessionNotFound;   // detach race (not saved yet) or a bogus token — indistinguishable server-side
                            retryableMs = SessionGraceSeconds > 0 ? SessionGraceSeconds * 1000.0 : 0;   // the grace window may still save it — retry until then (REC-001); with grace off nothing can appear
                        }
                    }
                    else if (UniNetTime.LocalClock() > found.ExpiresAt)
                    {
                        result = GraceResumeResult.Expired;   // expired — sweep owns removal+reassignment (terminal)
                    }
                    else if (!IsLiveConnection(newConnId))
                    {
                        result = GraceResumeResult.UnknownToken;   // invalid/dead new connId — session survives for the genuine reconnect (terminal for this attempt)
                    }
                    else
                    {
                        _graceSessions.Remove(token);   // consumed only on the verified success path
                        _closedResumeTokens[token] = ((byte)GraceResumeResult.UnknownToken, found.ExpiresAt);   // the double-consume loser learns its outcome exactly
                        session = found;
                        result = GraceResumeResult.Reattached;
                    }
                }
            }
            if (session != null)
            {
                foreach (var netId in session.OwnedNetIds)
                {
                    var entry = GetEntry(netId);
                    if (entry == null) continue;
                    entry.OwnerConnId = newConnId;
                    foreach (var conn in SnapshotConnections())
                        if (!conn.Disconnected && conn.VersionApproved && !conn.AwaitingSceneReady)
                            conn.Channel.SendOwnerUpdate(netId, newConnId);   // CON-001 — undeclared connections receive the baseline at approval instead; SCN-002 — scene-held at ready report
                }
                if (session.PlayerObjectNetId != 0)
                    _playerObjects[newConnId] = session.PlayerObjectNetId;
            }
            // REC-001 wire 14 — every terminal outcome reports to the requesting connection (default no-op on test channels;
            // failures consumed nothing, SEC-001 intact)
            ServerConnection requester = FindConnection(newConnId);
            requester?.Channel.SendResumeResult(result == GraceResumeResult.Reattached, (byte)result, retryableMs);
            return result;
        }

        private ServerConnection FindConnection(long connId)
        {
            foreach (var conn in SnapshotConnections())
                if (conn.ConnId == connId) return conn;
            return null;
        }

        private bool IsLiveConnection(long connId)
        {
            lock (_gate)
            {
                for (int i = 0; i < _connections.Count; i++)
                    if (_connections[i].ConnId == connId && !_connections[i].Disconnected) return true;
                return false;
            }
        }

        /// <summary>Sweeps expired grace sessions (ADR-0034 — call from the tick; expiry releases objects to reassignment).</summary>
        public void SweepGraceSessions(double time)
        {
            if (time < _nextGraceSweep) return;
            _nextGraceSweep = time + 0.5;
            lock (_gate)
            {
                if (_graceSessions.Count == 0) return;
                List<string>? expired = null;
                foreach (var kv in _graceSessions)
                    if (time > kv.Value.ExpiresAt)
                        (expired ??= new List<string>()).Add(kv.Key);
                if (expired != null)
                {
                    foreach (var tk in expired)
                    {
                        _graceSessions.Remove(tk);
                        // swept tombstones use a sweep-time-based window — the ORIGINAL expiry is already in the past,
                        // so the same sweep's prune step would delete the tombstone instantly (zero lifetime).
                        // time + SessionGraceSeconds keeps the one-grace-window bound from the sweep moment.
                        double tombstoneUntil = time + SessionGraceSeconds;
                        _closedResumeTokens[tk] = ((byte)GraceResumeResult.Expired, tombstoneUntil);   // REC-001 — a late resume for a swept session reports Expired exactly, not a retryable NotFound
                    }
                    ReassignOwnership();
                }
                // REC-001 — prune tombstones past their original expiry (bounded memory: at most one grace window of closed tokens)
                List<string>? spent = null;
                foreach (var kv in _closedResumeTokens)
                    if (time > kv.Value.ExpiresAt)
                        (spent ??= new List<string>()).Add(kv.Key);
                if (spent != null)
                    foreach (var tk in spent) _closedResumeTokens.Remove(tk);
            }
        }

        /// <summary>Whether a connId is held by an active grace session (ADR-0034 — ReassignOwnership skips these).</summary>
        private bool IsInGraceSession(long connId)
        {
            lock (_gate)
            {
                foreach (var kv in _graceSessions)
                    if (kv.Value.OldConnId == connId) return true;
                return false;
            }
        }

        /// <summary>Grace session state (ADR-0034). The token is the opaque Base64 session key (ADR-0041) — never logged.</summary>
        private sealed class GraceSession
        {
            public string Token = string.Empty;
            public long OldConnId;
            public double ExpiresAt;
            public List<ulong> OwnedNetIds = new();
            public ulong PlayerObjectNetId;
        }

        /// <summary>
        /// NETWORK-side destroy of every DYNAMIC object owned by the connection (ADR-0031, main thread) — server
        /// deregistration + destroy wire + visibility cleanup, precise from server entries (no scene scans). Does NOT
        /// destroy the local GameObject (Core is engine-free) — in Unity use UniNetManager.DestroyObjectsOwnedBy,
        /// which delegates here AND destroys the local instances. Scene objects are untouched (level furniture; their
        /// ownership falls to the reassignment policy). Returns the number destroyed on the wire.
        /// </summary>
        public int DestroyObjectsOwnedBy(long connId)
        {
            int destroyed = 0;
            foreach (var (netId, entry) in SnapshotObjects())
            {
                if (entry.IsDynamic && entry.OwnerConnId == connId && DestroyObject(netId))
                    destroyed++;
            }
            return destroyed;
        }

        /// <summary>
        /// Transfers an object's ownership to a specific connection (ADR-0030, main thread) — pickup, vehicles, capture points.
        /// Propagates to every client via the existing OwnerUpdate wire. connId 0 releases the object (the next
        /// reassignment pass adopts it round-robin); a non-live connId behaves like a release once reassignment runs.
        /// Explicit ownership survives joins/leaves (the policy only adopts orphans). Returns false for unknown netIds.
        /// </summary>
        public bool SetOwner(ulong netId, long ownerConnId)
        {
            var entry = GetEntry(netId);
            if (entry == null) return false;
            entry.OwnerConnId = ownerConnId;
            foreach (var conn in SnapshotConnections())   // all connections — same propagation as ReassignOwnership (clients without the object ignore the notice harmlessly)
                if (!conn.Disconnected && conn.VersionApproved && !conn.AwaitingSceneReady)   // CON-001 — undeclared connections receive the notice at approval instead; SCN-002 — scene-held at ready report
                    conn.Channel.SendOwnerUpdate(netId, ownerConnId);
            return true;
        }

        /// <summary>Number of replication deltas deferred by the budget policy this tick (ADR-0029 queue-depth observability; main thread).</summary>
        public int DeferredSendCount => _queue.Count;

        /// <summary>
        /// Disconnects a client explicitly (ADR-0027) — flushes a disconnect notice to the wire (the reason rides
        /// the wire, best effort) and then closes the transport connection; DetachConnection follows via the hub's
        /// Disconnected event, so ownership reassignment and lifecycle events run the normal path. Returns false when
        /// the connection id is unknown. The close is asynchronous (it waits for the notice flush) — the connection
        /// stays live until the flush completes or faults. Test channels (not HubBase) receive the notice but are not
        /// transport-closed.
        /// </summary>
        public bool DisconnectClient(long connId, UniNetDisconnectReason reason = UniNetDisconnectReason.Kicked)
        {
            foreach (var conn in SnapshotConnections())
            {
                if (conn.ConnId != connId || conn.Disconnected) continue;
                _ = CloseAfterNoticeAsync(conn.Channel, reason);
                return true;
            }
            return false;
        }

        /// <summary>Notice-then-close — the flush must reach the wire BEFORE the transport closes, and the close waits a short grace so the client's async dispatch of the notice (DRPC fire-and-forget ThreadPool) settles first.</summary>
        private static async System.Threading.Tasks.Task CloseAfterNoticeAsync(IUniNetSystemChannel channel, UniNetDisconnectReason reason)
        {
            try
            {
                await channel.SendDisconnectNoticeAsync(reason).ConfigureAwait(false);
            }
            catch
            {
                // best effort — a broken connection cannot carry the notice; the client degrades to ConnectionLost
            }
            await System.Threading.Tasks.Task.Delay(150).ConfigureAwait(false);   // grace — let the client dispatch the notice before the close datagram arrives
            if (channel is global::DRPC.Shared.Network.HubBase hub)
            {
                try { hub.Disconnect(); }
                catch { /* disconnect cleanup noise must not propagate */ }
            }
        }

        /// <summary>
        /// Sends a disconnect notice to every live connection and waits until every flush reaches the wire (ADR-0027) —
        /// UniNetManager.ServerStopAsync uses this before disposing the listener. Best effort: notices lost with
        /// dying connections degrade to ConnectionLost on those clients.
        /// </summary>
        public async System.Threading.Tasks.Task BroadcastDisconnectNoticeAsync(UniNetDisconnectReason reason)
        {
            var tasks = new List<System.Threading.Tasks.Task>();
            foreach (var conn in SnapshotConnections())
                if (!conn.Disconnected)
                    tasks.Add(conn.Channel.SendDisconnectNoticeAsync(reason));
            try
            {
                await System.Threading.Tasks.Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
            }
            catch
            {
                // best effort — see CloseAfterNoticeAsync
            }
        }

        /// <summary>Fire-and-forget variant for synchronous stop paths (quit) — the flushes race the teardown; best effort by design.</summary>
        public void BroadcastDisconnectNotice(UniNetDisconnectReason reason)
        {
            foreach (var conn in SnapshotConnections())
                if (!conn.Disconnected)
                    _ = conn.Channel.SendDisconnectNoticeAsync(reason);
        }

        /// <summary>
        /// Minimal ownership policy — objects whose owner is still a live connection KEEP it (explicit
        /// ownership survives joins/leaves/rejoins); only orphaned objects (owner 0 or a gone connection)
        /// are (re)assigned round-robin by ascending netId. Notifies clients on every change.
        /// ponytail: round-robin for orphans; replace with an explicit ownership-transfer API if production demands it.
        /// </summary>
        private void ReassignOwnership()
        {
            var objects = SnapshotObjects();
            var connections = SnapshotConnections();
            if (connections.Count == 0 || objects.Count == 0) return;

            var live = new HashSet<long>();
            var candidates = new List<ServerConnection>();
            foreach (var conn in connections)
            {
                if (conn.Disconnected || !conn.VersionApproved) continue;   // CON-001 — undeclared connections neither take nor receive ownership
                live.Add(conn.ConnId);   // an approved owner KEEPS its objects even while scene-held — a re-transition must not steal ownership (SCN-002)
                if (!conn.AwaitingSceneReady) candidates.Add(conn);   // scene-held connections cannot TAKE orphans yet — the ready report re-runs this pass
            }
            if (candidates.Count == 0) return;   // nobody approved yet — orphans stay ownerless until an approval re-runs this pass

            var sorted = new List<(ulong NetId, ServerObjectEntry Entry)>(objects);
            sorted.Sort((a, b) => a.NetId.CompareTo(b.NetId));

            int rr = 0;
            foreach (var (netId, entry) in sorted)
            {
                if (entry.OwnerConnId != 0 && live.Contains(entry.OwnerConnId)) continue;
                if (entry.OwnerConnId != 0 && IsInGraceSession(entry.OwnerConnId)) continue;   // ADR-0034 — grace-held   // 살아 있는 소유자는 그대로

                long owner = candidates[rr++ % candidates.Count].ConnId;
                if (owner == entry.OwnerConnId) continue;
                entry.OwnerConnId = owner;
                foreach (var conn in candidates)
                    conn.Channel.SendOwnerUpdate(netId, owner);
            }
        }

        /// <summary>Late-join catch-up — dynamic objects arrive as spawn messages; scene objects as per-sub full-state replication.</summary>
        private void SendCatchup(ServerConnection conn)
        {
            foreach (var (netId, entry) in SnapshotObjects())
            {
                if (!IsRelevant(entry, conn, netId)) continue;   // irrelevant object — excluded from catch-up (sent on relevance transition)
                GetVisibleSet(conn.ConnId).Add(netId);
                GetEverVisibleSet(conn.ConnId).Add(netId);
                SendBaseline(conn, netId, entry, invalidateDeferred: false);   // RunPostSceneReady already pruned the entire recipient once
            }
        }

        /// <summary>
        /// Object entry baseline for a connection (late-join catch-up and relevancy re-entry share this exact sequence).
        /// The ownership notice goes FIRST — including owner 0, which is an explicit baseline that overwrites stale client
        /// metadata — so the client's owner cache is correct before any full state applies and Notify callbacks observe
        /// the real owner (OWN-001). Reusing the existing OwnerUpdate wire: the receiver updates the cache synchronously
        /// while replication/spawn payloads apply on the main queue, so a pre-state notice is already visible to them.
        /// </summary>
        private void SendBaseline(ServerConnection conn, ulong netId, ServerObjectEntry entry, bool invalidateDeferred)
        {
            // Single-object re-entry must prune here; catch-up prunes all objects before its first baseline.
            if (invalidateDeferred) InvalidateDeferredRecipient(netId, conn.ConnId);
            conn.Channel.SendOwnerUpdate(netId, entry.OwnerConnId);
            bool isOwner = entry.OwnerConnId == conn.ConnId;
            if (entry.IsDynamic)
            {
                GetSpawnTransform(entry, out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
                SendSpawnState(conn.Channel, netId, entry, px, py, pz, qx, qy, qz, qw, isOwner);
                return;
            }
            for (byte subId = 0; subId < entry.Subs.Length; subId++)
            {
                var handler = entry.Subs[subId].Replication;
                if (handler == null || !handler.HasFields) continue;
                byte[] state = TryWriteFull(handler, entry.Subs[subId].Instance, isOwner);
                if (state != null && state.Length > 0)   // null when every field is filtered out by conditions — skip catch-up (also null on a serializer fault — that sub-object is skipped)
                    conn.Channel.SendReplicate(netId, subId, handler.MethodId, state);
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

            EnsureBatchLists(connections.Count);

            bool timed = !double.IsNaN(time);
            int budget = timed ? ReplicationBudgetPerTickBytes : 0;   // immediate mode skips the budget policy too (P2-compatible contract)
            int budgetLeft = budget > 0 ? budget : int.MaxValue;
            if (_gridEnabled) RefreshGrid(objects, connections);   // P4 — refresh grid visibility once per tick

            long[] recipientIds = null;   // one immutable, shared ID snapshot per tick, only if something is deferred
            _pending.Clear();
            try
            {
                foreach (var (netId, entry) in objects)
                {
                    if (!UpdateVisibility(netId, entry, connections)) continue;   // irrelevant to every connection — skip compare and send (restores the baseline on re-entry)

                    // Fresh candidates reuse this tick's visibility bits; deferred candidates also check current relevancy.
                    // Higher indexes use pooled words copied from the same visibility pass (no per-candidate connection scan).
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
                        if (IsEmpty(toOwner) && IsEmpty(toOthers))
                        {
                            // Unreliable streams heal an in-motion loss with the next delta — but a lost FINAL delta strands
                            // the remote (nothing follows it). Once an unreliable sub has been quiet past the resend delay,
                            // re-send its full state once on the reliable batch. Reliable subs never need this.
                            bool unreliableSub = (policy?.NetworkReplicationDelivery ?? Delivery.ReliableOrdered) == Delivery.Unreliable;
                            if (timed && unreliableSub && !sub.QuiescenceResent && sub.LastChangeTime >= 0
                                && time - sub.LastChangeTime >= QuiescenceResendDelaySeconds)
                            {
                                sub.QuiescenceResent = true;
                                _pending.Add(new DeferredDelta
                                {
                                    NetId = netId,
                                    SubId = subId,
                                    MethodId = handler.MethodId,
                                    Owner = TryWriteFull(handler, sub.Instance, true),
                                    Others = TryWriteFull(handler, sub.Instance, false),
                                    SenderType = sub.Instance.GetType(),
                                    Priority = policy?.NetworkPriority ?? 1f,
                                    EnqueuedAt = time,
                                    RelevantMask = relevantMask,
                                    RelevantOverflow = CaptureRelevanceOverflow(connections.Count),
                                    Seq = _seq++,
                                    ForceReliable = true,
                                });
                            }
                            continue;
                        }
                        if (timed && hz > 0f) sub.NextReplicateTime = time + 1f / hz;

                        if (timed) sub.LastChangeTime = time;
                        sub.QuiescenceResent = false;
                        bool flushReliable = sub.NextSendReliable;
                        sub.NextSendReliable = false;

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
                            RelevantOverflow = CaptureRelevanceOverflow(connections.Count),
                            Seq = _seq++,
                            ForceReliable = flushReliable,
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

                foreach (var item in _queue) item.Queued = false;
                _queue.Clear();
                _typeBudgetLeft.Clear();   // refill per-type channel budgets — left empty in immediate mode (no budget policy)
                if (timed)
                    foreach (var kv in _channelBudgets) _typeBudgetLeft[kv.Key] = kv.Value;

                foreach (var item in _pending)
                {
                    var entry = GetEntry(item.NetId);
                    if (entry == null) { item.ReleaseRecipients(); continue; }   // destroyed mid-tick — drop the deferral

                    var entryPolicy = entry.Instance as IUniNetReplicationPolicy;
                    if (entryPolicy != null && entryPolicy.IsNetworkDormant)
                    {
                        // Went dormant — hold the deferrals too. Dropping them would lose changes permanently (the snapshot
                        // was already updated), so they are sent once dormancy is flushed.
                        DeferDelta(item, connections, ref recipientIds);
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
                        DeferDelta(item, connections, ref recipientIds);   // EnqueuedAt is preserved so the starvation boost keeps growing
                        continue;
                    }

                    int spent = 0;
                    bool unreliable = !item.ForceReliable
                        && (entryPolicy?.NetworkReplicationDelivery ?? Delivery.ReliableOrdered) == Delivery.Unreliable;
                    int recipientCursor = 0;
                    for (int i = 0; i < connections.Count; i++)
                    {
                        var conn = connections[i];
                        if (conn.Disconnected || !conn.VersionApproved || conn.AwaitingSceneReady) continue;   // CON-001 — undeclared connections receive no replication until approval; SCN-002 — scene-held connections wait for their ready report
                        bool relevant;
                        if (item.Recipients != null)
                        {
                            // Both snapshots are ascending: attach appends monotonic IDs; removal never reorders.
                            while (recipientCursor < item.Recipients.Length && item.Recipients[recipientCursor] < conn.ConnId)
                                recipientCursor++;
                            relevant = recipientCursor < item.Recipients.Length && item.Recipients[recipientCursor] == conn.ConnId
                                && item.IsRecipientIndex(recipientCursor) && IsRelevant(entry, conn, item.NetId);
                        }
                        else relevant = item.IsRecipientIndex(i);   // fresh candidates use this tick's index-stable visibility bits
                        if (!relevant) continue;
                        var payload = conn.ConnId == entry.OwnerConnId ? item.Owner : item.Others;
                        if (IsEmpty(payload)) continue;

                        var batch = unreliable ? _batchSeq[i] : _batchRel[i];
                        var candidate = new ReplicateEntry(item.NetId, item.SubId, item.MethodId, payload);
                        if (batch.Count > 0 && batch.Bytes + candidate.WireBytes > MaxReplicateBatchBytes)
                            FlushBatch(batch, conn.Channel, unreliable);   // ponytail: fixed ~MTU cap (Sequenced cannot fragment); per-connection splitting if deltas ever grow
                        batch.Add(candidate);
                        spent += payload.Length;
                    }
                    item.ReleaseRecipients();
                    budgetLeft = Math.Max(0, budgetLeft - spent);
                    if (_typeBudgetLeft.ContainsKey(item.SenderType))
                        _typeBudgetLeft[item.SenderType] = Math.Max(0, _typeBudgetLeft[item.SenderType] - spent);
                }

                // Tail flush — at most two batch messages per connection this tick (one reliable, one unreliable)
                for (int i = 0; i < connections.Count; i++)
                {
                    FlushBatch(_batchRel[i], connections[i].Channel, unreliable: false);
                    FlushBatch(_batchSeq[i], connections[i].Channel, unreliable: true);
                }
            }
            finally
            {
                // A hook/channel fault may abort anywhere; only retained queue entries keep pool ownership.
                foreach (var item in _pending)
                    if (!item.Queued) item.ReleaseRecipients();
                _pending.Clear();
            }
        }

        /// <summary>Captures identity once per deferred tick; each candidate keeps only its own compact visibility bits.</summary>
        private void DeferDelta(DeferredDelta item, IReadOnlyList<ServerConnection> connections, ref long[] recipientIds)
        {
            if (item.Recipients == null)
            {
                if (recipientIds == null)
                {
                    recipientIds = new long[connections.Count];
                    // Attach appends monotonically assigned IDs under _gate; detach preserves order.
                    for (int i = 0; i < connections.Count; i++) recipientIds[i] = connections[i].ConnId;
                }
                item.Recipients = recipientIds;
            }
            _queue.Add(item);
            item.Queued = true;
        }

        private void InvalidateDeferredRecipient(ulong? netId, long connId)
        {
            // ponytail: scan on baseline/relevance changes; index by netId if transition churn makes this hot.
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var item = _queue[i];
                if (netId.HasValue && item.NetId != netId.Value) continue;
                item.RemoveRecipient(connId);
                if (item.HasRecipients()) continue;
                _queue.RemoveAt(i);
                item.Queued = false;
                item.ReleaseRecipients();
            }
        }

        private ulong[] CaptureRelevanceOverflow(int connectionCount)
        {
            if (connectionCount <= 64) return null;
            int words = (connectionCount - 1) / 64;
            var bits = ArrayPool<ulong>.Shared.Rent(words);
            Array.Copy(_relevanceOverflowScratch, bits, words);
            return bits;
        }

        private void EnsureBatchLists(int count)
        {
            if (_batchRel.Length < count)
            {
                _batchRel = new BatchBuffer[count];
                _batchSeq = new BatchBuffer[count];
            }
            for (int i = 0; i < count; i++)
            {
                _batchRel[i] ??= new BatchBuffer();
                _batchSeq[i] ??= new BatchBuffer();
                _batchRel[i].Clear();
                _batchSeq[i].Clear();
            }
        }

        /// <summary>Wire size cap per batch message — stays well under a single UDP datagram (Sequenced delivery cannot fragment; batched deltas are tiny, so this bound is never hit in practice).</summary>
        internal const int MaxReplicateBatchBytes = 1000;

        /// <summary>How long an unreliable sub must stay quiet after its last delta before the one-shot full-state resend fires (seconds).</summary>
        internal const double QuiescenceResendDelaySeconds = 0.25;

        private void FlushBatch(BatchBuffer batch, IUniNetSystemChannel channel, bool unreliable)
        {
            if (batch.Count == 0) return;
            var w = MessageBufferWriter.Create();
            w.WriteUInt16((ushort)batch.Count);
            foreach (var e in batch.Entries)
            {
                w.WriteUInt64(e.NetId);
                w.WriteByte(e.SubId);
                w.WriteInt32(e.MethodId);
                w.WriteInt32(e.Payload.Length);
                w.WriteBytes(e.Payload);
            }
            batch.Clear();
            channel.SendReplicateBatch(w.ToArray(), unreliable ? DRPC.RpcDeliveryMode.Sequenced : DRPC.RpcDeliveryMode.ReliableOrdered);
        }

        /// <summary>Forces the next send of every sub of one object onto the reliable batch — dormancy flush rides reliable (a lost unreliable flush would strand stale state until the quiescence resend).</summary>
        public void MarkNextSendReliable(ulong netId)
        {
            var entry = GetEntry(netId);
            if (entry == null) return;
            foreach (var sub in entry.Subs)
                sub.NextSendReliable = true;
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
        /// P4 grid spatial-partition visibility (RepGraph style) — divides the world into a cellSize grid (XZ plane) and
        /// spatially pre-filters objects in cells within visibleRadius of a viewer (quantized test, boundary error ≤ cellSize).
        /// Applies only to objects with NetworkCullDistance > 0 (ANDed with their own cull); cull=0 objects stay always
        /// relevant per the P3 contract (ADR-0026). Connections without a viewer position fail open per the P3 contract (main thread).
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
            // Grid membership gates only objects with their own cull distance — a cull=0 object keeps the P3 contract
            // ("never distance-culled") and is not grid-culled either (ADR-0026). For cull>0 objects the grid is a spatial
            // pre-filter (quantization error ≤ cellSize); their own cull distance still ANDs below.
            if (_gridEnabled && cull > 0f && _viewerPositions.ContainsKey(conn.ConnId)
                && !GridContains(conn.ConnId, netId))
                return false;
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
            int overflowWords = (conns.Count - 1) / 64;
            if (_relevanceOverflowScratch.Length < overflowWords) _relevanceOverflowScratch = new ulong[overflowWords];
            Array.Clear(_relevanceOverflowScratch, 0, overflowWords);
            bool anyRelevant = false;
            for (int i = 0; i < conns.Count; i++)
            {
                var conn = conns[i];
                if (conn.Disconnected || !conn.VersionApproved || conn.AwaitingSceneReady)
                {
                    _relevanceScratch[i] = false;
                    continue;   // blocked connections neither receive re-entry baselines nor seed visibility before catch-up
                }
                var seen = GetVisibleSet(conn.ConnId);
                if (IsRelevant(entry, conn, netId))
                {
                    _relevanceScratch[i] = anyRelevant = true;
                    if (i >= 64) _relevanceOverflowScratch[i / 64 - 1] |= 1ul << (i % 64);
                    bool everNew = GetEverVisibleSet(conn.ConnId).Add(netId);
                    if (seen.Add(netId) && (!everNew || entry.IsDynamic))
                        SendVisibilityState(conn, netId, entry);   // re-entry baseline restore · dynamic object creation
                }
                else
                {
                    _relevanceScratch[i] = false;
                    if (seen.Remove(netId)) InvalidateDeferredRecipient(netId, conn.ConnId);   // prune even while budget-starved; peers keep their queued changes
                }
            }
            return anyRelevant;
        }

        /// <summary>Sends a relevancy transition — the same entry baseline as catch-up (ownership notice first, then spawn/full state), outside the budget.</summary>
        private void SendVisibilityState(ServerConnection conn, ulong netId, ServerObjectEntry entry)
            => SendBaseline(conn, netId, entry, invalidateDeferred: true);

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

        /// <summary>One delta candidate — EnqueuedAt anchors starvation; RelevantMask is tick-local (low 64), Recipients survives deferral.</summary>
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
            public long[] Recipients;   // shared sorted connIds from the enqueue tick, never later connection indexes
            public ulong[] RelevantOverflow;   // pooled words above the inline 64 bits; owned by this candidate
            public long Seq;
            public bool ForceReliable;   // dormancy flush + quiescence resend — must not ride the lossy stream

            public bool IsRecipientIndex(int index)
                => index < 64 ? (RelevantMask & (1ul << index)) != 0
                    : (RelevantOverflow[index / 64 - 1] & (1ul << (index % 64))) != 0;

            public bool Queued;   // only retained queue entries may keep pooled words after a tick aborts

            public bool HasRecipients()
            {
                if (RelevantMask != 0) return true;
                int words = (Recipients.Length - 1) / 64;
                for (int i = 0; i < words; i++)
                    if (RelevantOverflow[i] != 0) return true;
                return false;
            }

            public void RemoveRecipient(long connId)
            {
                int index = Array.BinarySearch(Recipients, connId);
                if (index < 0) return;
                if (index < 64) RelevantMask &= ~(1ul << index);
                else RelevantOverflow[index / 64 - 1] &= ~(1ul << (index % 64));
            }

            public void ReleaseRecipients()
            {
                Recipients = null;
                if (RelevantOverflow == null) return;
                ArrayPool<ulong>.Shared.Return(RelevantOverflow);
                RelevantOverflow = null;
            }
        }

        /// <summary>One (netId, subId, methodId, payload) slot in a per-connection replication batch.</summary>
        private readonly struct ReplicateEntry
        {
            public ReplicateEntry(ulong netId, byte subId, int methodId, byte[] payload)
            {
                NetId = netId; SubId = subId; MethodId = methodId; Payload = payload;
            }

            public ulong NetId { get; }
            public byte SubId { get; }
            public int MethodId { get; }
            public byte[] Payload { get; }

            /// <summary>Bytes this entry occupies in the batch blob — payload + netId(8) + subId(1) + methodId(4) + length(4).</summary>
            public int WireBytes => Payload.Length + 17;
        }

        /// <summary>Per-connection accumulation buffer for one tick's replication batch (entries + running wire size).</summary>
        private sealed class BatchBuffer
        {
            public readonly List<ReplicateEntry> Entries = new();
            public int Bytes;

            public int Count => Entries.Count;

            public void Add(ReplicateEntry e)
            {
                Entries.Add(e);
                Bytes += e.WireBytes;
            }

            public void Clear()
            {
                Entries.Clear();
                Bytes = 0;
            }
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
            channel.SendSpawn(netId, px, py, pz, qx, qy, qz, qw, entry.PrefabId, (byte)entry.Subs.Length, typeKeys, states);
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
