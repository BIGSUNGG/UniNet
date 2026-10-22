using System;
using System.Collections.Generic;

namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Client-side runtime — keeps scene and dynamically spawned objects registered (one netId per object plus its
    /// component array), and stores the connection ID and ownership map the server announced.
    /// </summary>
    public sealed class NetworkClient
    {
        // LIF-001 — process-wide client-generation counter — parity with NetworkServer.Generation: every
        // NetworkClient instance creation stamps the next generation (1-based), so a client stop → start (new
        // instance) invalidates NetworkBehaviour registration stamps on its own, exactly like a server restart
        // does. Deliberately public for the same reason as NetworkServer.Generation (UniNet.Core grants
        // InternalsVisibleTo to test assemblies only, and UniNet.Unity reads this across the boundary).
        // Process-local identity — never on the wire.
        private static long _generationCounter;

        /// <summary>LIF-001 — generation of the most recently created NetworkClient instance (starts at 1).</summary>
        public static ulong Generation => (ulong)System.Threading.Interlocked.Read(ref _generationCounter);

        /// <summary>LIF-001 — stamps a fresh generation at instance creation (see <see cref="Generation"/>).</summary>
        public NetworkClient() => System.Threading.Interlocked.Increment(ref _generationCounter);

        private readonly object _gate = new();
        private readonly Dictionary<ulong, object[]> _objects = new();
        private readonly Dictionary<ulong, long> _owners = new();

        /// <summary>This client's connection ID assigned by the server (set when the Welcome message arrives).</summary>
        public long LocalConnId { get; private set; }

        /// <summary>
        /// The server's protocol version as declared in the Welcome (ADR-0028). Null before the Welcome arrives. Informational —
        /// enforcement is server-side (a mismatched client is kicked with VersionMismatch).
        /// </summary>
        public string ServerProtocolVersion { get; set; }

        /// <summary>Session token issued by the server in the Welcome (ADR-0034/0041) — an opaque Base64 string presented back
        /// on reconnect for grace-session reattach. Empty (or null) = no session: fresh connection, resume disabled/inactive.</summary>
        public string SessionToken { get; set; } = string.Empty;

        /// <summary>Client-side UniNet traffic counters (ADR-0029) — payload bytes/packets received by this client and sent by its RPCs.</summary>
        public UniNetTrafficCounters TrafficStats { get; } = new();

        private int _roundTripUs = -1000;   // microseconds backing store — the -1 ms sentinel encodes as -1000

        /// <summary>
        /// Round-trip time in ms measured by the client's ping probe (ADR-0029 wire 8/9 — send/echo, EMA-smoothed).
        /// -1 until the first round trip completes. Volatile-backed: the echo handler runs on the dispatch thread.
        /// </summary>
        public double RoundTripTimeMs
        {
            get => System.Threading.Volatile.Read(ref _roundTripUs) / 1000.0;
            set => System.Threading.Volatile.Write(ref _roundTripUs, (int)(value * 1000.0));   // runtime-assigned (generated echo handler, ADR-0029)
        }

        /// <summary>
        /// Why the last session ended — assigned by the generated client hub when a disconnect notice (ADR-0027)
        /// arrives, read by UniNetManager when raising ClientDisconnected. None when no notice was received
        /// (voluntary stop or an unexplained transport loss).
        /// Written on the DRPC dispatch ThreadPool and read on the transport thread — volatile-backed so a weakly
        /// ordered core cannot observe a stale None after the notice dispatch (reason would degrade to ConnectionLost).
        /// </summary>
        public UniNetDisconnectReason LastDisconnectReason
        {
            get => (UniNetDisconnectReason)System.Threading.Volatile.Read(ref _lastDisconnectReason);
            set => System.Threading.Volatile.Write(ref _lastDisconnectReason, (int)value);
        }
        private int _lastDisconnectReason = (int)UniNetDisconnectReason.None;

        private long _faultedPayloadCount;

        /// <summary>
        /// Malformed inbound payloads (short/truncated/oversized) dropped by the generated client hub's parsing
        /// guards (RPC-002). Log-independent observability — the counter, not the log, is the contract: a drop is
        /// always observable here even when fault logging is disabled or a no-op (engine-less hosts), and the
        /// deliberately silent per-drop path avoids log spam under a hostile stream (subscribers may throttle).
        /// Written on the DRPC dispatch thread — Interlocked-backed.
        /// </summary>
        public long FaultedPayloadCount => System.Threading.Interlocked.Read(ref _faultedPayloadCount);

        /// <summary>Records one dropped malformed payload — called by the generated client hub's parsing guards (RPC-002).
        /// Deliberately public: the generated receive guards are emitted into EVERY assembly that owns NetworkBehaviours
        /// (game code included), and UniNet.Core only grants InternalsVisibleTo to the test assemblies — an internal
        /// member here would fail to compile in UniNet.Unity and user assemblies (CS0122).</summary>
        public void CountFaultedPayload() => System.Threading.Interlocked.Increment(ref _faultedPayloadCount);

        /// <summary>Registers an object — binds a NetworkBehaviour component array (in slot order) to a netId. Shared by scene objects and dynamic spawns.</summary>
        public void Register(ulong netId, object[] components)
        {
            lock (_gate) _objects[netId] = components;
        }

        /// <summary>
        /// Removes a registration (destroy sync) — and with it the object's cached owner (OWN-003), so
        /// <see cref="GetOwner"/> returns 0 ("unassigned") for a destroyed netId instead of its last owner.
        /// Returns true if it was registered.
        /// </summary>
        /// <remarks>
        /// <see cref="OwnershipChanged"/> is deliberately NOT raised here: that event notifies subscribers about
        /// owner REASSIGNMENT of a live object (MOV-001), while destruction is state hygiene — firing it for every
        /// destroy would flood subscribers during Arena-style bulk departures. Should the server ever re-announce an
        /// owner for this netId anyway, that notice enters as a first-ever baseline in <see cref="ApplyOwner"/> and
        /// stays silent — consistent with the spawn-baseline contract.
        /// </remarks>
        public bool Unregister(ulong netId)
        {
            lock (_gate)
            {
                bool wasRegistered = _objects.Remove(netId);
                _owners.Remove(netId);   // OWN-003 — a destroyed netId must not keep reporting its last owner (GetOwner's 0 = unassigned contract, and IsOwner with it)
                return wasRegistered;
            }
        }

        /// <summary>Returns the object's component array (null if unknown) — for object-level checks.</summary>
        public object[] Get(ulong netId)
        {
            lock (_gate) return _objects.TryGetValue(netId, out var o) ? o : null;
        }

        /// <summary>Returns the instance in a sub slot (used for RPC routing and applying replication).</summary>
        public object Get(ulong netId, byte subId)
        {
            lock (_gate)
            {
                return _objects.TryGetValue(netId, out var o)
                    && subId < o.Length ? o[subId] : null;
            }
        }

        /// <summary>Applies the Welcome message — sets this client's connection ID (called by the generated client hub).</summary>
        public void SetLocalConnId(long connId) => LocalConnId = connId;

        /// <summary>
        /// Raised when an object's owner actually changes (MOV-001) — (netId, previous owner, new owner). Fires on the
        /// DRPC dispatch thread (from the generated OwnerUpdate receiver): Unity subscribers MUST marshal to the main
        /// thread (<see cref="UniNetEnvironment.QueueOnMain"/>) before touching engine state, and look the netId up
        /// locally — notices can arrive for objects this client has not registered (null guard). Not raised for a
        /// first-ever assignment (spawn/catch-up baselines — a fresh simulation needs no ownership reset) nor for a
        /// repeated same-owner notice.
        /// </summary>
        public event Action<ulong, long, long> OwnershipChanged;

        /// <summary>Applies an ownership update (OwnerUpdate) — called by the generated client hub. Raises
        /// <see cref="OwnershipChanged"/> only when the owner value actually changes (a repeated same-owner notice,
        /// e.g. a re-sent baseline or a redundant SetOwner broadcast, applies silently).</summary>
        public void ApplyOwner(ulong netId, long ownerConnId)
        {
            long prev;
            bool changed;
            lock (_gate)
            {
                changed = _owners.TryGetValue(netId, out prev) && prev != ownerConnId;   // first-ever assignment is a baseline, not a change — no event
                _owners[netId] = ownerConnId;
            }
            if (changed)
                OwnershipChanged?.Invoke(netId, prev, ownerConnId);   // outside the gate — subscribers may re-enter freely
        }

        /// <summary>Returns the owner connection of an object (0 = unassigned).</summary>
        public long GetOwner(ulong netId)
        {
            lock (_gate) return _owners.TryGetValue(netId, out var o) ? o : 0;
        }

        // ── REC-001 — resume result (wire 14) ─────────────────────────

        /// <summary>Last resume outcome (REC-001) — None until a wire-14 result arrives. Volatile-backed (applied on the dispatch thread).</summary>
        public GraceResumeResult LastResumeResult
        {
            get => (GraceResumeResult)System.Threading.Volatile.Read(ref _lastResumeResult);
            private set => System.Threading.Volatile.Write(ref _lastResumeResult, (int)value);
        }
        private int _lastResumeResult = (int)GraceResumeResult.None;

        /// <summary>Raised when a resume result arrives (REC-001, wire 14) — on the DRPC dispatch thread: Unity
        /// subscribers must marshal via <see cref="UniNetEnvironment.QueueOnMain"/>. Fires for every terminal
        /// outcome, success included; never for a fresh connection with no token.</summary>
        public event Action<GraceResumeResult> ResumeResultReceived;

        private double _resumeRetryRemainingMs;      // > 0 while a SessionNotFound retry window is armed
        private double _resumeRetryDeadline = -1;    // LocalClock domain — computed on the FIRST main-thread tick (the Unity clock throws off-main; the CON-001 lesson)
        private double _resumeRetryNextSendAt;

        /// <summary>Applies a wire-14 resume result (the generated client receiver calls this — dispatch thread).
        /// Arms the retry window for SessionNotFound; every other outcome disarms it (Expired/UnknownToken are terminal).</summary>
        public void ApplyResumeResult(bool ok, byte resultCode, double retryableRemainingMs)
        {
            var result = (GraceResumeResult)resultCode;
            lock (_gate)
            {
                LastResumeResult = result;
                _resumeRetryRemainingMs = result == GraceResumeResult.SessionNotFound && retryableRemainingMs > 0 ? retryableRemainingMs : 0;
                _resumeRetryDeadline = -1;   // recomputed on the next main-thread tick — no Unity clock access here
                _resumeRetryNextSendAt = 0;
            }
            ResumeResultReceived?.Invoke(result);   // outside the gate — subscribers may re-enter freely
        }

        /// <summary>
        /// Re-sends the resume request (wire 10) while a SessionNotFound retry window is armed (REC-001) — call from
        /// the driver Update (main thread). Sends at 0.5 s intervals until the server's retryable window closes or
        /// a terminal result arrives, so the detach race (a reconnect faster than the server noticed the old
        /// connection died) converges to Reattached instead of silently spawning a fresh player.
        /// </summary>
        public void TickResumeRetry()
        {
            var sender = UniNetEnvironment.ClientSender;
            string token = SessionToken;
            if (sender == null || string.IsNullOrEmpty(token)) return;
            double now = UniNetTime.LocalClock();
            lock (_gate)
            {
                if (_resumeRetryRemainingMs <= 0) return;
                if (_resumeRetryDeadline < 0)
                    _resumeRetryDeadline = now + _resumeRetryRemainingMs / 1000.0;   // first tick after arming — main thread only
                if (now >= _resumeRetryDeadline) { _resumeRetryRemainingMs = 0; return; }   // window closed without a terminal result — stop retrying
                if (now < _resumeRetryNextSendAt) return;
                _resumeRetryNextSendAt = now + 0.5;
            }
            var w = MessageProtocol.Serialize.MessageBufferWriter.Create();
            w.WriteString(token);
            byte[] payload = w.ToArray();
            sender.UniNetSend(10, payload, global::DRPC.RpcDeliveryMode.ReliableOrdered);   // same wire-10 envelope as the hub's initial request
        }
    }
}
