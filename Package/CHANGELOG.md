## 0.17.0

**Dispatch registration-collision guard (GEN-001 — no wire/generator change).** The dispatch table silently overwrote an existing handler on any re-registration (a lone `Debug.WriteLine` was the only trace) — a cross-assembly FNV-1a method-ID clash would hijack the dispatch invisibly.

### Changed

- User-range method IDs (>= 64): re-registration is idempotent when the handler is the SAME method (delegate method identity, not instance identity — method-group arguments produce fresh delegates per registration pass); a DIFFERENT handler on a taken ID (cross-assembly clash, which compile-time UNINET007 cannot see) is rejected first-wins and surfaced through `LogFault` with both method names. Same handler with a different delivery mode is also a conflict (strict).
- Reserved wire IDs (< 64, wires 13/14) keep the silent overwrite — every assembly registers its own internal handler under the same ID by design ("idempotent across assemblies").
- Tests: EditMode `UniNetDispatchRegistrationTests` — idempotent re-registration, cross-handler rejection (first-wins pinned), reserved-range overwrite, delivery-mode mismatch.

## 0.16.0

**Receive-path payload guards (RPC-002 — no wire/protocol change; generator 0.2.15).** The generated OwnerUpdate receiver (wire 2) had no try/catch — unlike its seven sibling system handlers — and the single-Replicate receiver parsed before its queue registration unguarded: a truncated or forged payload threw out of the handler into DRPC dispatch.

### Fixed

- The generated `__OwnerUpdate_Requested` and `__Replicate_{Name}_Requested` handlers now wrap parsing in an outer try/catch (mirroring the batch wire-13 pattern): a malformed payload dies at the handler boundary — no exception escapes into DRPC dispatch, no queue entry is registered, and the next well-formed message applies normally (per-message isolation).
- The drop is observable without relying on log settings: `NetworkClient.FaultedPayloadCount` (public, Interlocked) increments on every guarded payload drop, plus `LogFault` fires. Intentionally NOT logged-and-hidden.
- Tests: EditMode `OwnerUpdateWireTests` — truncation at every parse boundary (counter + ownership invariance), malformed→valid isolation, empty-delta and forged length-prefix cases, batch-path regression pin.

## 0.15.0

**Targeted ClientRpc companions (RPC-001, ADR-0051 — no wire/protocol change; generator 0.2.14).** `[ClientRpc]` methods were broadcast-only: there was no generated API to send a server-authoritative RPC to the object's owner or to one specific connection.

### Added

- The generator now emits two companions for every `[ClientRpc]`: `{Name}To(long connId, args)` (single-connection send — FishNet TargetRpc parity) and `{Name}ToOwner(args)` (owner-only send — UE owner ClientRpc parity). Accessibility mirrors the declaration; methodId/payload/delivery are identical to the broadcast (wire unchanged — receiver side untouched; filtering happens in the server send loop only).
- The existing `{Name}(args)` broadcast is byte-identical. `To` with an unknown/disconnected connId: no exception, no send. `ToOwner` with owner==0: one warning line, no send. Called on a client: silently ignored (server-authoritative, same as the broadcast).
- Companions are emitted as regular methods (not partials) — no user declaration pairs with them; a name collision with existing game code surfaces as CS0111 at compile time.
- Tests: EditMode `TargetedRpcTests` (broadcast invariance, ToOwner single-receiver, To targeting + payload identity, owner==0 warning, dead connId, client guard) + PlayMode `TargetedRpcPlayTests` (real-loopback 2-connection non-owner non-receipt).

## 0.14.0

**Opt-in server input expiry (MOV-003, ADR-0050 — no wire/generator change; additive constructor option only).** `MoveSimulation.TickServer` re-applied the last input indefinitely while the input queue is starved (hold-last): a connection stall or a grace-session owner kept the server simulating the final forward input forever.

### Added

- `MoveSimulation` constructor option `inputExpirySteps` (default 0 = disabled — existing behavior byte-identical). When input supply stops for that many server steps, `_input` is swapped to `default(TInput)` exactly once and applied until a new input is adopted (idempotent). The expiry timer resets on `OnTeleport`, `ResetOwnershipStream`, `DropPendingHistory`, and every `SetInput` refresh (a live listen-server host feed never expires).
- The hold-last default stays the documented contract; `decay-to-zero` was rejected (the meaning of `TInput` fields belongs to the game; step purity/determinism must not be mutated by the framework).
- Tests: EditMode `MoveSimulationTests` extensions — default contract pin (600 ticks hold-last), expiry cutoff, re-supply reset, 10,000-tick idempotence, boundary resets.

## 0.13.0

**Death-gate pending prediction cleanup (MOV-002, ADR-0049 — no wire/generator/public-surface change beyond one DIM).** The `NetworkTransform.SimulationEnabled` gate paused prediction but never cleared the unacknowledged history: a death -> remote respawn -> re-enable rendered the pre-death predicted position and the first ack replayed dead-era inputs (rollback artifact).

### Fixed

- New `INetworkMoveSimulation.DropPendingHistory()` (default-interface member; default = `OnTeleport()`, i.e. clear everything). The engine's `MoveSimulation` override clears the input queue + history ring only — authoritative `TState` and the latest input survive (cutscene pause keeps velocity; position converges via re-anchor/ack).
- `NetworkTransform` drops the pending history once on the gate-entry edge (edge-triggered — a game legitimately predicting inside the gate is not perpetually cleared). Death-window acks hard-snap onto the authoritative position; the revive edge re-anchors BEFORE that frame's first step (so even a frame that runs a prediction step starts from the respawn position), via the unchanged teleport re-anchor.
- Tests: EditMode `Mov002DeathGateTests` (unit contract, edge guard, revive re-anchor, dead-window ack hard-snap, pending==0 voice contrast) + PlayMode `DeathGatePlayTests` (host-loopback death->respawn->revive integration). RED evidence: dropping the call fails the regression at the defect point.

## 0.12.0

**Destroy-time owner-cache cleanup (OWN-003 — no wire/generator/public-API change).** `NetworkClient.Unregister` removed the object from the registry but left its entry in the client's owner cache, so `GetOwner` kept returning the last owner for a destroyed `netId` (violating the documented "0 = unassigned" contract) and the cache grew unboundedly across spawn/destroy cycles within a session.

### Fixed

- `Unregister` now clears the `netId`'s cached owner inside the same lock, so `GetOwner` returns 0 ("unassigned") after a network destroy and `IsOwner` cannot stay stale-true on destroyed objects. The return value contract ("was it registered?") is unchanged.
- `OwnershipChanged` deliberately does **not** fire for destroys — the event signals live reassignments only; a late OwnerUpdate for a destroyed id is silently adopted as a first baseline (consistent with existing ApplyOwner semantics).

## 0.11.0

**Ownership-transfer condition-field baselines (OWN-002, ADR-0048 — no wire/generator/public-API change).** Ownership transfers (`SetOwner`, `ResumeSession` reattach, orphan adoption) propagated only the OwnerUpdate — unchanged `[Replicated(OwnerOnly/InitialOnly)]` fields never reached the new owner (stuck at defaults), the previous owner kept viewing SkipOwner fields as defaults, and RepNotify never fired for the delivered values.

### Fixed

- Ownership-transfer paths now re-deliver the condition-field baselines to the new owner (owner view) and the previous owner (others view) via a state-only full-state send (no spawn re-sent; budget-deferred deltas for the transferred object are superseded first; relevancy gates apply; connections with an immediate catch-up skip the redundant baseline). RepNotify fires on the delivered baselines with the receiver’s prior value as prev.
- Test fixes: the deferred-budget queue lifecycle test is decoupled from ownership-reassignment baselines (ownership parked on the surviving peer); real-loopback re-adoption test made deterministic.

## 0.10.0

**Game connection approval (CON-002, ADR-0047 — additive only, no breaking change).** The server could only validate the protocol version — games had no way to admit or reject a connection from their own credential checks. Set `options.ConnectionApprover` on the server (an async `Task<bool>` callback receiving `UniNetApprovalRequest{ConnId, ProtocolVersion, Credential}`; runs on the main thread with zero game data flowing until the verdict) and `options.ConnectionCredential` on the client. `false`/throw closes the connection and the client observes `ClientDisconnected(UniNetDisconnectReason.ApprovalRejected)`.

### Changed

- **Wire 7 payload extended** to `[version string][credential string]` — clients always write both strings (legacy clients never sent the second; servers read it conditionally on remaining bytes, so legacy servers ignore it). Declarations over 4096 bytes are discarded before parsing (ADR-0041 precedent). Generator 0.2.13.
- **Callback-pending connections reuse the CON-001 hold** — no ownership, catch-up, replication, spawns, ClientConnected, or game ServerRpcs until the verdict. `ApprovalRequested` guarantees one callback per connection (redeclarations are idempotent; an already-approved connection is never re-validated).
- **`VersionDeclareTimeoutMs` is the total budget** for declaration + callback completion — the sweep kicks a hung validator with VersionMismatch (no indefinite hold). Raise it for slow auth backends.

### Added

- `UniNetDisconnectReason.ApprovalRejected = 6` (explicit value, no renumbering — legacy clients that receive the new value fall through their default kick handling).

## 0.9.0

**Voluntary-leave session cleanup (REC-002, ADR-0046 — additive only, no breaking change).** A voluntary `ClientStop` burns the resume token by contract, yet the server unconditionally kept a grace session for it — an un-resumable session lingered for the whole grace window and blocked fresh-client adoption of orphaned ownership. ClientStop now announces system wire 15 (`IntentionalLeave`, C→S, empty payload) right before the transport close; the server skips the grace save for that departure, tombstones the burned token (`UnknownToken` for the grace window — a stale resume attempt terminates immediately instead of arming a retry), and reassigns ownership at once, so a fresh client joining within the window adopts right away.

### Changed

- **`ClientStop` delivery contract** — the notice rides the reliable channel with a bounded flush window (send-task wait ≤ 250 ms plus a 150 ms drain window, the ADR-0027 `ServerStopAsync` precedent; loopback hands off well under a millisecond). A faulted or timed-out send degrades to the legacy grace behavior — no new failure mode. `ClientStop` remains synchronous with a worst-case block of ~400 ms.
- **Compatibility** — legacy clients that never send wire 15 keep the exact legacy grace behavior (flag stays false). The mixed-version case is gated by the default package-version handshake. Generator 0.2.12 (wire-15 registration + handler emitted alongside 7/8/10/12).

## 0.8.0

**Scene transition remainders (SCN-001/002/003, ADR-0045 — wire unchanged; bundled generator bumped to 0.2.11).** A joiner after a transition completed never received the current network scene (permanent — scene-object baselines were silently dropped on the unregistered client), catch-up full state raced the client's scene registration (dropped, never resent — permanent initial-value drift), and a stale same-name report could complete the gate early.

### Changed

- **Current-scene tracking (SCN-001)** — completing a transition records `_currentScene`; every joining connection now receives wire 11 for it (`PendingScene ?? CurrentScene`). Servers that never transitioned keep the exact legacy behavior (no wire 11, immediate catch-up).
- **Scene-ready catch-up hold (SCN-002)** — a connection that received wire 11 gets no game data (catch-up, replication tick, spawn/destroy broadcasts, ownership notices) until its ready report. The report is sent after client registration (`UniNetManager.OnSceneLoaded` registers, then reports), so full-state baselines always land on registered objects. Every valid report reruns ownership reassignment and full-state catch-up (owner before state), including for existing participants; only a held initial attach raises `ClientConnected`, and subscriber faults cannot skip gate evaluation; CON-001 approval + scene-ready compose as two ordered stages. Ownership KEEP is unaffected by the hold — a re-transition never steals an approved owner's objects (only orphan adoption waits for ready connections).
- **Stale-report guard (SCN-003)** — per-connection `SceneLoadSent` plus a per-connection scene-generation counter, reset/incremented at every `LoadScene` start and at each wire 11 send: only reports arriving after that connection's wire 11 for the current transition count. The generated wire-12 handler captures the connection's generation at dispatch receipt (`CaptureSceneLoadGeneration`) and only reports whose captured generation matches the current one are accepted — closing the main-queue gap where a stale report could land after a newer send. Reports arriving before the send (bogus or stale) are rejected; no broader freshness guarantee is claimed. Accepted limitation: an already-sent old same-scene report arriving after the new wire 11 is indistinguishable without transition tokens and can release the hold/complete the gate early. Token, wire 11/12, and public system-channel signature changes are out of scope (ADR-0045).
- Budget-deferred replication intersects stable enqueue-time connection-ID membership with current relevancy at send time, including >64 clients. New full baselines (catch-up, relevancy re-entry, or spawn) supersede only that object's stale queued deliveries to that recipient; peers retain their changes. Shared ID snapshots plus inline/pooled visibility bits replace per-candidate recipient HashSets.
- Pending post-approval attach work alone sends the scene instruction, avoiding duplicate loads when `LoadScene` races accepted approval. Pooled recipient overflow words are released on hook/channel aborts unless still owned by a retained queue entry. Deferred sends merge sorted stable IDs linearly; disconnect/irrelevance pruning releases empty entries even under ongoing budget pressure without dropping peers' changes.
- Destroy IDs for approved scene-held connections are retained and delivered once on valid ready, after client registration but before ownership/catch-up baselines, only if still absent on the server. Same-netId re-registration suppresses the stale destroy in favor of current catch-up; disconnect retires pending IDs. Ready recipients still receive destroys immediately, and unapproved clients receive none.
- A client that never reports (scene asset failure) stays held — kick/retry remains game responsibility (ADR-0035 contract).

### Migration

- Wire payloads are unchanged. The bundled generator is bumped to 0.2.11 — its wire-12 handler now captures the scene generation at dispatch receipt and calls the new three-argument `OnClientSceneLoaded` overload; no consumer action is required (the generator ships inside the package), and legacy two-argument generated code still compiles. New public APIs: `NetworkServer.CaptureSceneLoadGeneration(connId)` and `OnClientSceneLoaded(connId, scene, generation)` — only needed when reporting scenes manually. The behavior is a contract tightening transparent to game code (reports are automatic).

## 0.7.0

**Resume result reporting + auto-retry (breaking — REC-001, ADR-0044).** `ReconnectAsync` returning true only ever meant "the connection established" — the server's reattach outcome was discarded, rejections were complete silence, and the fast-reconnect race (reconnecting before the server noticed the old connection died) silently spawned a fresh player every time.

### Changed (breaking)

- **`NetworkServer.ResumeSession` returns `GraceResumeResult` instead of `bool`** — `Reattached`, `SessionNotFound` (detach race — retryable window included), `Expired`, `UnknownToken` (guessed/consumed token, invalid connection). Direct callers: `false` → the exact reason, `true` → `GraceResumeResult.Reattached`. Every failure still consumes nothing (SEC-001 intact).

### Added

- **Wire 14 — resume result to the requesting connection**: `{ok, reason, retryableRemainingMs}`. Consumed and swept-expired tokens are tombstoned (bounded by the grace window) so a double-consume loser learns `UnknownToken` and a swept session reports `Expired` — neither masquerades as a retryable `SessionNotFound`. Generator 0.2.10 (server hub send + client receiver registration).
- **Client observability**: `NetworkClient.LastResumeResult` + `NetworkClient.ResumeResultReceived` event (dispatch thread — marshal via `QueueOnMain`). Fresh connections without a token receive no wire 14.
- **Auto-retry**: while a `SessionNotFound` retry window is armed, `NetworkClient.TickResumeRetry` (driver Update cadence) re-sends the resume request every 0.5 s until the window closes or a terminal result arrives — the detach race converges to `Reattached` instead of a silently fresh player.
- `IUniNetSystemChannel.SendResumeResult` — default no-op (test channels unaffected); the generated server hub implements the wire send.

### Migration

- Update direct `ResumeSession` calls from `bool` to the enum. Game code observing reconnects should subscribe to `ResumeResultReceived` — `ReconnectAsync`'s return value is (and remains) only the connection attempt.
- No change for standard clients; both ends should update together (default version handshake rejects mixed 0.6.x/0.7.x).

## 0.6.0

**Connection approval lifecycle: game data is held until an accepted version declaration (CON-001, ADR-0043).** ADR-0028 rejected *mismatched* versions but a connection that never declared wire 7 at all was accepted unconditionally — it received ownership, catch-up, replication, and could invoke game ServerRpcs without ever proving a compatible protocol.

### Changed

- **Undeclared connections are held.** While version checking is active (non-empty `ProtocolVersion`, the default), a connection receives nothing but the Welcome until its wire-7 declaration is accepted: no ownership reassignment (neither as candidate nor receiver), no catch-up, no replication/spawn/destroy sends, no `LoadScene` broadcast, no scene-gate participation, no `ClientConnected` event, and no game ServerRpc execution. System wires (7/8/10/12) stay live so the handshake itself completes. A disconnect before approval raises no `ClientDisconnected` and saves no grace session (the game never saw the connection).
- **Accepting a declaration releases the held work in the legacy order** (pending scene join → reassignment → catch-up → `ClientConnected`), exactly once per connection under any queue ordering; a redeclaration is idempotent. A mismatch still kicks with `VersionMismatch`.
- **Generator 0.2.9** — the generated server hub's game-ServerRpc dispatch checks `NetworkServer.IsVersionApproved(sender)` before the rate limit and quietly drops unapproved calls (`NetworkServer.UnapprovedServerRpcDrops` is the only observable).
- **Empty `ProtocolVersion` keeps the exact legacy behavior** (immediate synchronous approval at attach).

### Added

- **`UniNetEndpointOptions.VersionDeclareTimeoutMs` / `NetworkServer.VersionDeclareTimeoutMs`** (server-only, default 10000, 0 = hold indefinitely without kicking) — undeclared connections are swept at the driver tick cadence and kicked with `VersionMismatch` (the existing kick path). Set from `UniNetManager.ServerAsync`.
- **`NetworkServer.IsVersionApproved(connId)` / `UnapprovedServerRpcDrops`** — approval query and the quiet-drop counter.

### Migration

- Real clients need nothing — generated client hubs have declared automatically since 0.2.3 (ADR-0028).
- Custom channels / test doubles that expect game data must model the declaration (`server.OnClientVersionDeclared(ch.UniNetConnId, server.ExpectedProtocolVersion)`), exactly what a real client does.
- No wire-format change; the generator bump only adds the dispatch gate.

## 0.5.0

**Movement ownership transfer: stream reset + epoch (breaking — MOV-001, ADR-0042).** An ownership transfer (SetOwner, reassignment adoption, session resume) used to leave the movement engine's sequence guard stuck on the previous owner's stream — the new owner's first inputs were rejected until they climbed past the old tenure's last sequence, and a stale in-flight packet from the previous owner re-poisoned a reset stream.

### Changed (breaking)

- **`INetworkMoveSimulation` gains `ResetOwnershipStream()`** — implementers of the interface (fully custom movement engines) must add it. It clears the server input queue, the owner-client history, and the sequence guards while PRESERVING the authoritative game state and the latest input — the deliberate contract difference from `OnTeleport`, which also resets state (a jump arc must not resume after a teleport; an ownership handoff keeps velocity/cooldowns so authority continues seamlessly). Users of `MoveSimulation<TInput,TState>` + `NetworkTransform` need no code change.
- **Movement input envelope layout gains an epoch prefix** — `UniNetBlobEnvelope.Data` for movement inputs is now `[uint epoch][int length][byte[] input]` (MessageProtocol standard codecs). The `RpcSubmitMove` signature, the envelope message type, and all system messages are unchanged, so no generator rebuild is needed; code that hand-builds/parses the movement envelope must add the prefix (+8 bytes per input). With the default `ProtocolVersion` the handshake compares package versions (ADR-0028) — update both ends together (0.4.x ↔ 0.5.0 mismatch rejects the client).

### Added

- **`NetworkClient.OwnershipChanged(netId, prev, next)`** — Core event raised only when an object's owner value actually changes (first assignment is a baseline and does not fire; repeated same-owner notices are silent). Raised on the DRPC dispatch thread: Unity subscribers must marshal via `UniNetEnvironment.QueueOnMain` and null-guard their local netId lookup (notices arrive for unregistered objects too).
- **Movement ownership epoch** — `NetworkTransform` now replicates a server-authoritative `[Replicated(OwnerOnly)] uint` movement epoch, bumped on every tenure→tenure owner change (direct transfer, or release) and paired with the stream reset; adoption from unowned (0 → conn) is a timing-independent baseline — no bump, no reset (a virgin initial stream, or a release whose bump already cut the boundary while the ownership gate blocks senders). The owner stamps the epoch onto every input; the server quietly drops epoch-mismatched inputs (`NetworkTransform.MoveEpochMismatches` counter is the observable) so a stale in-flight packet from a previous tenure cannot poison the new stream even when it passes the current-owner gate. The owner client rebinds its session (sequence restart, accumulator/history clear, authoritative re-anchor) on BOTH the ownership event and the epoch replication — the epoch delta (unreliable + quiescence resend, ADR-0039) is the self-healing backstop for a missed event. Predicted state converges on the first ack rollback. Host (authority) instances skip the client-side reset (double-reset guard).

### Migration

- Direct `INetworkMoveSimulation` implementers: add `ResetOwnershipStream()` (clear queues/history/sequence guards, keep state; see `MoveSimulation<TInput,TState>` for reference).
- Custom code touching the movement envelope layout: write `[epoch][length][bytes]` and verify the epoch server-side before adoption.
- Everyone else: no action; both ends of the connection must be 0.5.0 (default version handshake).

## 0.4.1

**Late-joiners learn existing ownership (OWN-001 — no wire change).** Late-join catch-up and scene-object relevancy re-entry omitted ownership notices (dynamic re-entry already sent them, but after spawn and only for nonzero owners) — a late-joining client's owner cache stayed 0 (0 = unassigned), so `IsOwner` and owner-only behavior were wrong until some later ownership change happened to broadcast.

### Fixed

- **Ownership baseline on every entry path.** Late-join catch-up and relevancy re-entry (scene and dynamic) now send `OwnerUpdate(netId, owner)` for each object **before** its full state/spawn baseline, reusing the existing wire 2. Owner 0 is an explicit baseline too — it overwrites stale client metadata. Relevancy filtering is unchanged: irrelevant objects are excluded from the baseline (no broadcast to all connections).
- **Safe receiver ordering.** The generated `OwnerUpdate` receiver updates the `NetworkClient` owner cache synchronously on the dispatch thread, while replication/spawn payloads apply on the main queue — a notice sent before the state is already visible when full state applies and `Notify` callbacks check `IsOwner`.
- **Dynamic visibility re-entry** previously sent the ownership notice only for a non-zero owner and after the spawn; it now rides the same baseline (owner first, spawn second), matching catch-up exactly. The initial dynamic spawn broadcast (`BroadcastSpawn`) follows the same order too (owner notice, 0 included, immediately before the spawn state).
- Note: with the **default** `ProtocolVersion` the package-version handshake compares versions (ADR-0028) — update both ends together (0.4.0 ↔ 0.4.1 mismatch rejects the client). Custom `ProtocolVersion` games are unaffected. No generator change is required for this release.

## 0.4.0

**Session tokens are cryptographically random (breaking — wire 1/10 change).** Predictable seed+1 tokens let a malicious client guess a neighbor's token and steal their grace-session ownership (SEC-001).

### Changed (breaking)

- **`IssueSessionToken` / `ServerConnection.SessionToken` / `NetworkClient.SessionToken` — `ulong` → `string`.** The token is now 32 bytes of `RandomNumberGenerator` entropy as an opaque Base64 string (44 chars, `NetworkServer.SessionTokenBytes`/`MaxSessionTokenChars`). Empty string = no session (inactive/fresh connection). Tokens are unique across live connections and pending grace sessions; issuance is thread-safe.
- **Wire 1 (Welcome) / wire 10 (ResumeRequest) carry the token as a string.** Breaking for the wire: with the **default** `ProtocolVersion` a package-version difference now rejects the client (a 0.3.x client against a 0.4.0 server — ADR-0028 handshake). If your game sets a **custom** `ProtocolVersion`, the handshake compares YOUR identifier — bump it on **both ends** in this release (the Arena demo does: `arena-1` → `arena-2-uninet-0.4`), otherwise pre-0.4.0 clients keep passing the handshake against an incompatible wire. Generator 0.2.8 required.

### Fixed (security)

- **SEC-001 — session-token forgery.** `ResumeSession` rejects wrong, guessed, expired, or unissued tokens AND invalid/dead new-connection IDs **without consuming the grace session** — a probing attacker can no longer burn a victim's grace window or take their ownership. Expired sessions are released by the sweep (which also reassigns orphans).
- The generated resume handler rejects wire-10 payloads over the format bound **before** parsing — int32 UTF8 length prefix + token = at most `NetworkServer.MaxResumeRequestPayloadBytes` (48 bytes) — and drops malformed input (truncated payload / forged length prefix; the reader throws before allocating the declared length) quietly, so a flooding client can neither force a large allocation nor spam the console. Rejections consume nothing: a later genuine request still resumes.
- Session tokens are never written to logs or exceptions; the editor debugger shows presence (`issued (44 ch)`), not the value.

### Migration

- If your code stored `SessionToken` as `ulong`, switch to `string` (empty = none). `ReconnectAsync`/`ClientAsync` token carry and the automatic resume request are unchanged from the game side.

## 0.3.0

**Unreleased development version — never published.** At the time of the 0.4.0 release preparation, public package tags stopped at 0.2.2; 0.3.0 existed only as the repository's development state between 0.2.2 and 0.4.0. Recorded here so the consumer changelog is continuous.

### Changed (breaking — development state)

- **State-based movement prediction (ADR-0038)** — `MoveSimulation<TInput,TState>` engine: game-defined input/state structs + pure step rule, fixed-step simulation (default 60 Hz), server input queue, owner history ring (128), rollback with server-state blob restoration. Replaced the stateless contract destructively: `NetworkTransform.MovementStep`/`MovementRule`/`SubmitMove`/`PredictOwner`/`SoftRate`/`FullReconciliation` removed. Jump/dash/teleport implemented in the Arena example.
- **Batched replication + per-object delivery reliability (ADR-0039, wire 13)** — per-connection, per-tick reliable and unreliable batches (≤2 messages/tick, 1000 B cap); `NetworkBehaviour.NetworkReplicationDelivery` (`ReliableOrdered` default / `Unreliable` = Sequenced); still-object reliable re-send after 0.25 s silence; dormancy-flush forces reliable.
- **Default replication rate 30 Hz (ADR-0040)** — `NetworkUpdateFrequencyHz` default 0 → 30 (step merging); 0 opts back into every-tick sending.
- Remote rendering improvements: teleport discontinuity snap (axis-wise buffer clear instead of interpolating through), adaptive interpolation delay (arrival-interval EMA), replication-only remote interpolation fix.

## 0.2.2

**Late-scene registration no longer deadlocks listen servers.**

### Fixed

- **Deferred scene-load registration (ADR-0025 revision)** — the v0.2.1 hook registered synchronously inside the `sceneLoaded` callback. When the load itself was initiated from inside an RPC dispatch (game-side `LoadScene` in a multicast implementation), registration re-entered the main pump's callstack and deadlocked listen-server clients (reproduced deterministically in the consumer's 3-instance verification). Registration now runs on the next main-pump drain — a clean driver `Update` callstack — via `UniNetEnvironment.QueueOnMain`.
- Consumers keeping the documented scene-ready gate see no behavioral change: late-scene registration completes on the frame after the load, well before any scene-targeted events under the gate.

# UniNet Changelog

Detailed internal history: `Document/changelog.md` in the repository root (Korean).

## 0.2.1

**Scene objects in scenes loaded after connecting now register automatically.**

### Fixed

- **Late-scene registration (ADR-0025)** — scene-object registration previously ran once at `ServerAsync`/`ClientAsync` time, so `NetworkBehaviour`s in scenes loaded **after** connecting (lobby → gameplay flow) stayed unregistered on both ends: `IsServer`/`IsClient` stayed false and their RPC sends were silent no-ops. A `sceneLoaded` hook (driver, process-lifetime) now re-runs registration on every scene load.
- Re-registration is **idempotent** — objects already registered under the current session are skipped (no duplicate slots, no snapshot re-init over live state). `RegisterAllToServer`/`RegisterAllToClient` now return the number of newly registered objects (internal API).
- New runtime log (fires only when a scene load registers something): `[UniNet] 씬 로드 네트 등록 — {scene}: 서버 +N, 클라 +M`.

### Notes

- The server must not send RPCs targeting a late scene's objects before clients finish loading it — keep your scene-ready gate (consumer-side ordering contract, documented in the manual).
- Unloaded scenes leave stale dictionary entries behind — harmless (scene ids are unique per scene asset, destroyed instances are swept by the server's per-frame destroy sweep).
- `SceneManager.CreateScene` (runtime empty-scene creation) does **not** fire `sceneLoaded` — the hook covers **loads** only. Use dynamic spawns for runtime-created scenes.

## 0.2.0

**Spawnable prefab spawning — a rework of dynamic spawns. Breaking.**

### Highlights

- **Mark prefabs as spawnable in the editor** — add a `NetworkSpawnable` marker to a prefab root and save once. The editor stamps a stable id (prefab GUID hash) and bakes a runtime lookup table (`Assets/Resources/UniNet/UniNetPrefabTable.asset`) — fully automatic, nothing to configure.
- **`NetworkInstantiate` now spawns prefabs, and only prefabs** — unmarked prefabs and runtime-created `new GameObject(...)` templates are rejected with a warning. The server decides what can exist on the network, at edit time.
- **Every client instantiates the same prefab** — the spawn message carries the prefab id; each client resolves it through its own baked table and instantiates **its own copy of that exact prefab** (renderer, model, and presets included — no more empty shells). Both ends must ship the same prefab assets.
- **Multiple prefabs per component type** — the old one-prefab-per-type catalog limit is gone. Two prefabs with the same `NetworkBehaviour` spawn side by side.

### Breaking changes & migration

- `UniNetManager.RegisterPrefab<T>(...)` **removed** — delete the calls; the baked table replaces the runtime catalog.
- Runtime template spawning (`new GameObject(...)` + `NetworkInstantiate(template)`) **removed** — mark a prefab instead (edit-time marking is the only path; a marker added at runtime has no stamped id and is rejected).
- Client-side sub auto-restore **removed** — a client prefab whose `NetworkBehaviour` composition differs from the server's is now **rejected** ("spawn slot mismatch" error) instead of silently re-assembled. Keep prefabs identical on both ends.
- Spawn message wire format changed (prefab id added) — update server and clients together.

### Added

- `NetworkInstantiate(original, position, rotation, configure)` overload.
- Ghost catch-up guard — destroyed registered instances no longer throw during late-join catch-up.

### Also included since v0.1.1 (unreleased as 0.1.2)

- **Stable scene-object netIds** — scene `NetworkBehaviour`s get their `netId` stamped into the scene asset at save time (hash of the object's `GlobalObjectId`). Both ends agree even when hierarchies differ at runtime (renames, sibling reorders, added/removed objects, `DontDestroyOnLoad`) or across different build versions. Save each scene once to stamp; unstamped objects keep the legacy hierarchy-path hash behavior.

## 0.1.2

- **씬 오브젝트 netId 안정화** — 씬 저장 시 `NetworkBehaviour`에 GameObject GlobalObjectId 해시를 직렬화 각인 (신규 에디터 어셈블리 `UniNet.Unity.Editor`). 이름 변경·형제 재배치·오브젝트 추가/삭제·DontDestroyOnLoad·빌드 버전 차이에서도 서버-클라 netId 일치. 미각인 오브젝트는 기존 계층 경로 해시로 동작 — 씬을 한 번 저장하면 각인된다 (ADR-0023)

## 0.1.1

- `NetworkInstantiate(original, ownerConnId)` — 명시적 소유자 동적 스폰 (접속 순간 그 접속 소유 토큰 발급). `RegisterDynamicObject(components, ownerConnId)` 선택 인자 동반
- `ReassignOwnership` — 살아 있는 소유자는 유지, 고아(소유자 0·단절)만 라운드로빈 재배정. 재접속·재시작 시 소유권 도용 방지
- `UniNetEnvironment.ServerChanged` — 서버 인스턴스 설정 순간(접속 수락 전, 메인 스레드) 이벤트. 폴링 경쟁 없는 라이프사이클 후크
- 스테일 동적 스폰 잔재 방지 — `IsDynamicSpawn` 마커, 씬 등록 스킵, `ClientStop`/`ServerStop` 시 `SweepDynamicSpawns()` (낡은 netId 재활용 충돌·이중 조인·오소유 읽기 방지)

## 0.1.0

- 초기 패키지 구성 — UniNet.Core / UniNet.Unity 어셈블리
- 기반 스택(DRPC 3.5.0 · MessageProtocol 3.2.0 · Communication 2.7.0 · LiteNetLib 2.1.4 · BouncyCastle 2.7.0) netstandard2.1 DLL 패키지 동봉 (ADR-0021)
- 소스 생성기 3종(DRPC · MessageProtocol · UniNet.CodeGenerator 0.2.1) RoslynAnalyzer 라벨 동봉
- P1~P4 기능: RPC(4종) · [Replicated] 변수 복제(델타) · 동적 스폰/파괴 · 고급 리플리케이션 정책 · NetworkTransform · P4 훅
