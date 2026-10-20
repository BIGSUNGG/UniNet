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
