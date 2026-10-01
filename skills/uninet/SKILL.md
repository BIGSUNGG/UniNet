---
name: uninet
description: User manual for the UniNet Unity networking library (com.ds.uninet) — server-authoritative RPCs, replicated state, dynamic spawning, bandwidth policies, prediction, lag compensation, compile diagnostics & runtime errors, and advanced tuning. Use when writing, reviewing, or debugging Unity game code that consumes UniNet, or when wiring a Unity project to a dedicated game server.
---

# UniNet — User Manual

This is the consumer-facing manual for **UniNet** (`com.ds.uninet`), written for developers and AI agents using the library in **their own Unity project**. It covers every implemented feature, wrong-usage failure modes, and deep tuning paths. It does not require access to the UniNet repository — everything a consumer needs is here.

## Overview

UniNet is a **server-authoritative** networking framework for Unity, targeting the feature level of Unreal Engine's Network Framework. You declare RPCs and replicated state directly on `MonoBehaviour` subclasses; a Roslyn source generator (bundled with the package) wires dispatch, serialization, and replication.

- **Principle** — "Simple to use, powerful under the hood."
- **Server is always authoritative.** Clients send `[ServerRpc]`s and receive replicated state. Works for dedicated servers and listen-servers (host) alike — one API on both sides.
- **Requirements** — Unity 6000.0+ (Unity 6 LTS), runtime targets `netstandard2.1`, server runs as a Unity server build.
- **Zero external setup** — transport (RUDP + optional DTLS), serialization, and RPC plumbing ship as DLLs inside the package.

### Install

Add one line to your project's `Packages/manifest.json`:

```json
"com.ds.uninet": "https://github.com/BIGSUNGG/UniNet.git?path=/Package#v0.1.1"
```

- `#v0.1.1` pins a release tag (omit to track the default branch).
- All dependencies are bundled — no NuGet feed, no extra config. Private repos work if your editor account can `git clone`.

### Mental model

| Concept | Meaning |
| --- | --- |
| `NetworkBehaviour` | Base class for networked components. Declare `partial` — the generator fills in the plumbing. |
| `netId` + `SubId` | Two-level identity: one `netId` per GameObject, one `SubId` slot per `NetworkBehaviour` component. Scene objects: the id is **stamped into the scene asset at save time** (hash of the `GlobalObjectId`) — stable across renames, sibling reorders, hierarchy changes, `DontDestroyOnLoad`, and differing build versions. Never-stamped objects fall back to the legacy hierarchy-path hash. Dynamic spawns: server-assigned. |
| `IsServer` / `IsClient` / `IsOwner` | Role booleans on every behaviour (≈ UE Authority / AutonomousProxy / SimulatedProxy). Branch simulation and input on them. |
| Server tick | The server diffs `[Replicated]` snapshots every tick and sends only changed fields to each client. |
| Main-thread contract | All RPC execution, replication application, and lifecycle events fire on the Unity main thread. You never touch network threads. |

### Quick start

```csharp
using System.Threading.Tasks;
using UniNet.Unity;
using UnityEngine;

public sealed partial class Player : NetworkBehaviour
{
    [Replicated(Notify = nameof(OnHpChanged))]   // server-authoritative field, auto-synced
    private int _hp = 100;

    private void OnHpChanged(int prevHp) { /* update UI with previous value */ }

    [ServerRpc(Validate = true)]                 // client → server; owner-only by default
    private partial void RpcRequestHit(int damage);
    private Task<bool> RpcRequestHit_Validate(int damage) => Task.FromResult(damage > 0);
    private void RpcRequestHit_Implementation(int damage) => _hp -= damage;

    [ClientRpc(Delivery.Unreliable)]             // server → specific client(s)
    private partial void RpcPlayHitFx(int damage);
    private void RpcPlayHitFx_Implementation(int damage) { /* FX */ }

    private void Update()
    {
        if (!IsOwner) return;
        if (Input.GetKeyDown(KeyCode.Space)) RpcRequestHit(10);
    }
}

// Bootstrap — pick one:
await UniNetManager.HostAsync(7777);                    // server + client in one process (dev)
// await UniNetManager.ServerAsync(7777);               // dedicated server
// await UniNetManager.ClientAsync("127.0.0.1", 7777);  // client
```

Rules that follow from the design:

- RPC methods are `partial` declarations — the body goes in `{Name}_Implementation`, never in the declaration.
- Wrong declarations fail at **compile time** with `UNINET0xx` diagnostics (see Exception & Error).
- Minimal runnable sample: Package Manager → **UniNet ▸ Samples ▸ Basics**.

## Core Usage

### RPC — three kinds

| Attribute | Direction | Notes |
| --- | --- | --- |
| `[ServerRpc]` | client → server | **Owner-only by default**; runs on the server. |
| `[ClientRpc]` | server → specific clients | A client-side call is ignored (UE parity). |
| `[MulticastRpc]` | server → everyone (+ server) | Called on a client it executes locally without propagating. |

- **Delivery modes** — `[ClientRpc(Delivery.Unreliable)]` / `[MulticastRpc(Delivery.Unreliable)]` for loss-tolerant data (movement, FX). Default is `Delivery.ReliableOrdered`. The bundled RUDP transport provides five delivery modes underneath.
- **Owner enforcement (security default)** — the server rejects `ServerRpc` calls from connections that do not own the object (spoofed `netId` payloads cannot invoke another object's RPC). Opt out for cross-client reporting with `[ServerRpc(RequireOwnership = false)]`. Rejections are logged with a rate-limited warning (see Exception & Error).
- **Validation hooks (opt-in)** — `[ServerRpc(Validate = true)]` awaits `{Name}_Validate` (same parameters, returns `Task<bool>`) after the ownership check; returning `false` skips the implementation. There is **no automatic detection**: a `_Validate` method without the flag never runs (warning UNINET012).
- **Message parameters** — RPC parameters can be primitives, `string`, or `[Message]` types (MessageProtocol). `[Message]` types support **official polymorphism**: declare the base type, send a subclass, cast on receipt.

```csharp
[Message] public partial class DamageMsg { public int Amount { get; set; } }
[Message] public partial class CriticalHitMsg : DamageMsg { public float Multiplier { get; set; } = 2f; }

[ServerRpc]
private partial void RpcApplyDamage(DamageMsg damage);      // declare base type

RpcApplyDamage(new CriticalHitMsg { Amount = 20 });          // send subclass

private void RpcApplyDamage_Implementation(DamageMsg damage)
{
    if (damage is CriticalHitMsg crit) { /* subclass fields survive the wire */ }
}
```

Every concrete type sent must carry `[Message]` (non-ID message kinds are unsupported). Collections cannot be RPC parameters directly — wrap them in a `[Message]` type.

### Replicated state

```csharp
[Replicated] private float _x;                                          // everyone, always
[Replicated(Notify = nameof(OnDamageChanged))] private int _damage;     // RepNotify: fires on clients with previous value
[Replicated(ReplicateCondition.OwnerOnly)] private int _aimYaw;         // owning client only
[Replicated(ReplicateCondition.SkipOwner)] private int _teamId;         // everyone except the owner
[Replicated(ReplicateCondition.InitialOnly)] private int _seed;         // once at spawn/catch-up, never again
[Replicated(Serializer = typeof(PositionQuantized))] private Vector3 _pos;  // custom wire format (see Advanced)
[Replicated] private List<int> _scores = new();                         // element-level deltas (see Advanced)
```

- Server-authoritative only: clients receive; the server diffs per tick and sends only changed fields (or changed collection elements).
- **RepNotify** — `Notify = nameof(Callback)` fires on clients whenever the field changes over the network, receiving the **previous value**. On the host, the callback fires without re-applying the value (the host keeps the authoritative original).
- **Late-join catch-up** — a connecting client automatically receives the full state of every scene and dynamic object.
- The server keeps the authoritative original on the host process; client-side re-application rules never fight the server copy.

### Dynamic objects & ownership

```csharp
using static UniNet.Unity.Net;   // optional: unqualified NetworkInstantiate / NetworkDestroy

// One call on the server: clones the original, registers it, and propagates the spawn
// (netId, type keys, transform, full initial state) to all clients.
// The RETURN VALUE is the registered instance — the original template stays put, clean it up yourself.
var projectile = NetworkInstantiate(_projectilePrefab, pos, rot);
UniNetManager.NetworkDestroy(projectile);        // destroy propagates to all clients

// Seed private [Replicated] initial values (the InitialOnly baseline) via the configure callback —
// Object.Instantiate only copies public/[SerializeField] fields, so private state needs this:
var avatar = UniNetManager.NetworkInstantiate(_avatarPrefab,
    clone => clone.GetComponent<Player>().InitServerState("Alpha"));

// Spawn owned by a specific connection, the moment it connects:
var token = NetworkInstantiate(_avatarPrefab, ownerConnId: connId);
```

- **Client-side visuals** — optional `UniNetManager.RegisterPrefab<T>(prefab)` maps a type to a prefab. Unregistered types spawn as an empty `GameObject` + components, and the server's component composition is restored automatically from the spawn message.
- **Ghost prevention** — a registered object destroyed with plain `Destroy` is detected and the destroy is propagated. Prefer `NetworkDestroy` (propagation is immediate and intentional, not detected after the fact).
- **Multiple `NetworkBehaviour`s per object** — two-level identity gives each component its own SubId slot (up to 255 per object, same-type duplicates allowed). **Never add/remove `NetworkBehaviour`s at runtime** — slot order must match on both ends.
- **Ownership** — `NetworkInstantiate(original, ownerConnId)` spawns pre-owned; `ReassignOwnership` keeps live owners intact and reassigns only orphans, so reconnections and restarts cannot steal ownership.
- Transform is propagated **once at spawn**. Ongoing movement is your job: `[Replicated]` fields or the `NetworkTransform` component (see Advanced).

### Connection lifecycle & shutdown

```csharp
// Server (authority):
server.ClientConnected += connId =>
{
    // Fires AFTER welcome, ownership assignment, and catch-up — safe to spawn an avatar here.
};
server.ClientDisconnected += connId =>
{
    // Fires BEFORE ownership reassignment — identify and destroy the leaver's objects here:
    // server.GetEntry(netId).OwnerConnId tells you who owns what.
};

// Client:
UniNetManager.ClientDisconnected += () => ShowReconnectPrompt();   // server shutdown, network drop
bool alive = UniNetManager.IsClientConnected;                       // session state query
UniNetEnvironment.ServerChanged += () => { /* server instance set, before any connection is accepted */ };
```

- All events fire on the **main thread**. Subscriber exceptions are isolated — one broken handler never kills the others or the pump. Disconnect-event subscribers cannot prevent ownership reassignment (guaranteed by the framework).
- A voluntary `ClientStop` does **not** fire the client disconnect event; it only flips `IsClientConnected`.
- **Stop explicitly** — `await UniNetManager.ServerStopAsync()` (also `ServerStop`, `ClientStop`, `HostStopAsync`, `HostStop`). Stop is idempotent, sweeps dynamically spawned objects, and releases the listener so the same port re-listens cleanly. Call the sync versions from `OnApplicationQuit`. Skipping stop leaves the socket bound — the next start fails with an RUDP binding error.

## Wrong Usage, Exceptions & Errors

### Compile-time diagnostics (UNINET0xx)

The source generator rejects broken declarations before you can ship them. Fix as directed:

| ID | Severity | Trigger | Fix |
| --- | --- | --- | --- |
| UNINET001 | Error | `NetworkBehaviour` subtype is not `partial` | Declare the type `partial`. |
| UNINET002 | Error | RPC parameter / replicated field type unsupported | Use a primitive, `string`, or a `[Message]`-marked type (non-ID kinds excluded). For other types use a custom serializer on fields. |
| UNINET003 | Error | RPC method has a body or is not `partial` | Declare `partial` without a body; write the body in `{Name}_Implementation`. |
| UNINET004 | Error | `{Name}_Implementation` missing or signature mismatch | Add the implementation with exactly the declaration's parameters. |
| UNINET005 | Error | `{Name}_Validate` signature mismatch | Same parameters as the RPC, returns `Task<bool>`. |
| UNINET006 | Error | RepNotify callback signature mismatch | The `Notify` method takes exactly one argument of the field's type, returns `void`. |
| UNINET007 | Error | Generated method ID collision | Rename one of the colliding RPC methods. |
| UNINET008 | Error | More than 32 `[Replicated]` fields in one type | The delta mask is a 32-bit uint — split the type across multiple `NetworkBehaviour`s. |
| UNINET009 | Error | Generated symbol name collision between two types | Rename one of the types. |
| UNINET010 | Error | `ReplicateCondition` with `OwnerOnly` + `SkipOwner` combined | Contradiction (nobody would receive it) — pick one. |
| UNINET011 | Error | `Validate = true` but `{Name}_Validate` missing | Add the hook: same parameters, `Task<bool>` return. |
| UNINET012 | Warning | `_Validate` method exists but no `Validate = true` flag | Opt in with `[ServerRpc(Validate = true)]` — there is no auto-detection. |
| UNINET013 | Error | Custom serializer contract violation | Provide `static void Write(ref MessageBufferWriter, in T)` + `static T Read(ref MessageBufferReader)` (optional `static bool Equals(in T, in T)`). |
| UNINET014 | Error | Collection element type unsupported | Elements must be primitives, `string`, `[Message]` types, or serializer-designated types. |
| UNINET015 | Error | Nested collection (`T[][]`, `List<List<T>>`) | Wrap inner collections in a `[Message]` type or flatten. (The 65,535 element cap is a runtime check — see the runtime table.) |

### Runtime errors & warnings

All runtime log messages are emitted **in Korean with a `[UniNet]` prefix** — these are stable strings you can grep for:

| Symptom (actual log) | Cause | Fix |
| --- | --- | --- |
| `[UniNet] 씬 저장 전 오브젝트의 GlobalObjectId를 만들 수 없다 — 경로 해시 폴백 유지 (0)` (editor, on scene save) | The scene was never saved before (no scene GUID yet), so the stable scene id could not be stamped. | Save the scene once — the next save stamps it. Unstamped objects fall back to the path hash (legacy behavior). |
| `[UniNet] 씬 안에서 안정 netId 중복 — 등록 시 조용히 덮어써진다…` (editor, on scene save) | Two objects in one scene resolved to the same stable id — impossible under Unity's GlobalObjectId guarantees; indicates a Unity-level anomaly. | Report it with the scene; the duplicate is logged instead of silently overwritten at registration. |
| `[UniNet] ServerRpc 거부 — 비소유 발신: <Type>.<Method> netId=… sender=… owner=…` (rate-limited: first per sender + max 1 per 5 s globally) | A `ServerRpc` arrived from a connection that does not own the object. Security default, not a bug. | If the call is legitimately cross-client, declare it `[ServerRpc(RequireOwnership = false)]` and validate input server-side. If not, investigate the spoofing/misrouting client. |
| `[UniNet] NetworkInstantiate 는 서버에서 NetworkBehaviour 컴포넌트가 있는 원본에만 유효하다 (호출 무시 — null 반환)` | Called on a client, offline, or on an object without `NetworkBehaviour`s. | Call it on the server (or host) with a template that has networked components. |
| `[UniNet] NetworkDestroy 는 서버에서 NetworkBehaviour 컴포넌트가 있는 오브젝트에만 유효하다` | Same misuse for destroy. | Destroy from the server; clients receive the despawn. |
| `[UniNet] 스폰 실패 — 등록되지 않은 타입 키 … (netId=…)` on the client | The client build lacks a `NetworkBehaviour` type the server spawned (assembly not referenced, type stripped). | Ship the same assemblies on both ends; check code stripping. |
| `[UniNet] 스폰 슬롯 불일치 (netId=…) — … 서버 N개/클라 M개 …` | Client prefab/scene object has different `NetworkBehaviour` composition or order than the server. | Make prefab composition identical on both ends; never Add/Remove networked components at runtime. |
| `[UniNet] 오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한) — 스폰 거부/등록 건너뜀` | More than 255 networked components on one object. | Split the object. |
| `RUDP 리스너 바인딩 실패` (binding failure on the next start) | Previous session was not stopped — the port is still bound. | Always `ServerStopAsync`/`HostStopAsync` (or sync variants in `OnApplicationQuit`) before starting again. Stop is idempotent. |
| `[UniNet] 송신 실패(정지·재접속 경로의 정상 종료 노이즈): <message>` | Sends racing the stop path (`InvalidOperationException`). Already handled inside generated code as a warning, not a throw. | Benign during shutdown; if you see it outside shutdown, check for manual sends on a closed session. |
| Server-tick exception on a collection field (that object's replication skips the tick) | A `T[]`/`List<T>` field exceeded the 65,535 element cap — a **runtime** check (UNINET015 only catches nesting at compile time). | Keep collections under 65,535 elements; split across fields or wrap in a `[Message]` type. |
| One handler's exception appears in the log, other subscribers still ran | Lifecycle-event subscriber isolation working as designed. | Fix the throwing handler; the framework only quarantines it. |
| Server-tick log of a serializer fault | A custom serializer's `Write`/`Read` threw during payload generation. The tick skipped that payload (fault isolation via `LogFault`). | Fix the serializer; it runs on the server payload path, not inside the dispatch isolation. |

### Wrong-usage patterns that misbehave quietly

- **Adding/removing `NetworkBehaviour`s at runtime** breaks the SubId slot contract. Scene objects have no detection window (contract-only); dynamic spawns are rejected at spawn. Keep composition fixed.
- **Mutating a `[Message]`-typed replicated field or collection element in place** does not sync — dirty checks use reference equality. Replace the instance (allocate a new message) instead.
- **Changing an `OwnerOnly` field while the object has no owner** can lose intermediate values to the next owner (the server snapshot is not split per receiver group). Re-seed state on ownership change if it matters.
- **Firing a one-way RPC immediately after connecting** can be lost (race before welcome/ownership). Send after `ClientConnected` (server) or after the connection is confirmed (client).
- **Moving the transform of a networked object on a client** does nothing authoritative — the server owns positions. Use `[Replicated]` fields or `NetworkTransform`.
- **Expecting transform sync after spawn** — spawn propagates position/rotation once; ongoing movement needs `[Replicated]` fields or `NetworkTransform`.
- **Reliance on plain `Destroy` for networked objects** works only because ghost detection propagates the destroy after the fact. Use `NetworkDestroy` for deterministic despawn.
- **Collections as RPC parameters** — unsupported. Wrap the collection in a `[Message]` type.
- **Forgetting `SetViewerPosition` per connection** silently disables distance culling (relevancy hook AND distance; no viewer position ⇒ no cull).

## Advanced — Tuning & Deep Usage

### Bandwidth control (UE NetDriver parity)

Per-object policy members on `NetworkBehaviour` + server-side globals. Policy-free objects behave exactly like the basic (immediate) mode.

```csharp
public sealed partial class Bullet : NetworkBehaviour
{
    public void Init()
    {
        NetworkUpdateFrequencyHz = 30f;   // cap sends at 30 Hz; missed ticks coalesce to the latest value
        NetworkCullDistance = 24f;        // no sends beyond this radius from the viewer
    }

    public override bool IsNetworkRelevant(long viewerConnId)   // optional per-viewer relevancy override
        => viewerConnId == _allowedConnId;
}

public sealed partial class Player : NetworkBehaviour
{
    private void Die()
    {
        NetworkDormant = true;             // freeze: skip delta comparison entirely while idle/dead
    }
    private void Respawn()
    {
        _hp = MaxHp;
        FlushNetworkDormancy();            // release dormancy; accumulated changes flush at once
        NetworkPriority = 2f;              // higher priority wins under bandwidth pressure
    }
}

// Server bootstrap:
server.SetViewerPosition(connId, x, y, z);                 // cull origin per connection (required for culling)
server.SetReplicationChannelBudget(typeof(Bullet), 512);   // per-type per-tick byte budget
server.ReplicationBudgetPerTickBytes = 8192;               // global per-tick budget (0 = unlimited)
server.SetVisibilityGrid(10f, 30f);                        // optional: grid visibility instead of distance
server.ClearVisibilityGrid();                              // back to distance-based relevancy
```

Tuning notes:

- **Scheduling order** — candidates and deferred sends are merged, then sent in `priority × (1 + waitSeconds / 0.1)` order (UE `GetNetPriority` formula). Starved objects eventually go through — you do not need custom fairness logic.
- **Budgets** — a type over its channel budget is deferred, not dropped; a single delta larger than the whole budget forces through so one huge object cannot stall.
- **Dormancy** skips comparison (cheapest idle state); flush is a single batched send. Use it for dead/idle objects, not for pausing gameplay state you expect to change soon.
- **Relevancy transitions** — leaving relevancy stops deltas but does **not** despawn on the client (it keeps the last state); re-entering restores a baseline via full state. Design your game around frozen-at-last-state off-screen objects.
- **Grid visibility** replaces distance checks with spatial cell membership (quantization error ≤ `cellSize`). For large worlds with many objects, this is the scalable path (RepGraph-style).

### Custom field serializers

Delegate a `[Replicated]` field's wire format to your own static pair — this is how you get quantized positions (6 bytes instead of 12) and how **non-primitive types** (e.g. `Vector3`) become replicable at all.

```csharp
public static class PositionQuantized
{
    public static void Write(ref MessageBufferWriter w, in Vector3 value)
    {
        w.WriteInt16(Quantize(value.x));    // any MessageBufferWriter primitives, freely combined
        w.WriteInt16(Quantize(value.y));
        w.WriteInt16(Quantize(value.z));
    }
    public static Vector3 Read(ref MessageBufferReader r)
        => new(Dequantize(r.ReadInt16()), Dequantize(r.ReadInt16()), Dequantize(r.ReadInt16()));
}

[Replicated(Serializer = typeof(PositionQuantized), Notify = nameof(OnPosition))]
private Vector3 _position;
```

Contract details that matter:

- Exact signatures: `static void Write(ref MessageBufferWriter, in T)` + `static T Read(ref MessageBufferReader)`. The `in` modifier is optional (both `in T` and plain `T` accepted). No registration call — the attribute binds it.
- **Optional `static bool Equals(in T, in T)`** — when provided, it drives the dirty check. For quantized fields this suppresses resends when the quantized value did not change (the raw float may have moved; the wire value did not). Without it, `object.Equals` is used. `null` arguments never reach your `Equals` — the framework handles null↔null (skip) and null↔value (force send) itself.
- Serializer-designated fields bypass the primitive/`[Message]` type validation (that's the point) — but the contract itself is compile-checked (UNINET013).
- Spawn/catch-up full-state writes go through the same serializer, so initial state arrives quantized too.
- A serializer throwing during server payload generation is isolated per tick (fault log), not fatal to the process — but fix it; that field stops syncing.

### Collection element deltas (FastArray-style)

`T[]` and `List<T>` `[Replicated]` fields sync **per element**: only `Set(i)` / `Insert(i)` / `RemoveAt(i)` / `Clear` operations are sent, not the whole collection. Element cap is 65,535 (u16 op/index encoding) — exceeding it throws a runtime exception that is tick-isolated (only that object's replication skips). No extra attribute — the generator detects the field type.

```csharp
[Replicated(Notify = nameof(OnInventory))]                 // one RepNotify per applied delta batch (shallow-copied prev)
private List<int> _inventory;

[Replicated(Serializer = typeof(ItemQuantized))]           // per-element custom serialization composes fine
private Item[] _slots;
```

- Elements may be primitives, `string`, `[Message]` types, or serializer-designated types. Nested collections are rejected (UNINET015).
- Delivery rides the reliable-ordered replication channel, so the client simply replays ops in order — no sequence numbers or resync machinery.
- Dirty detection keeps a shadow copy per collection field. The diff is an O(n) heuristic optimized for append and end-region edits; heavy mid-collection insertion churn is the known weak spot (correct but possibly more ops than minimal).
- `[Message]` elements are compared by reference — mutate-and-hope does not sync; replace the element.
- The collection consumes one mask bit like any field (32-field limit unchanged), and `InitialOnly`/conditions/dormancy/frequency policies all apply to it unchanged.

### Prediction, timing & lag compensation

Primitives are provided by the library; the game assembles them.

**`UniNetTime`** — server-authoritative monotonic clock. The server's clock is the authority; clients sync every second (EMA offset, α = 0.25) with an anti-regression clamp. Query `UniNetTime.Now` anywhere — safe for gameplay math on both sides.

**`NetworkTransform`** — built-in component covering position replication, owner-side prediction, and remote interpolation. You provide only the movement rule; server and prediction share the same function, so prediction cannot drift from authority.

```csharp
public sealed partial class Character : NetworkBehaviour
{
    private NetworkTransform _nt;

    private void Awake()
    {
        _nt = GetComponent<NetworkTransform>();
        _nt.MovementRule = (ref float x, ref float y, ref float z,
                            float ix, float iy, float iz, float dt) =>
        {
            x += ix * 5f * dt;    // your movement — shared verbatim by server & prediction
        };
    }

    private void HandleInput()
    {
        _nt.SubmitMove(inputX, inputY, 0);      // owner client: apply immediately (predict) + send
        _nt.SimulationEnabled = IsAlive();      // gate: pauses/resumes server & prediction together
    }
}
```

Knobs: `InterpolationDelay` (default 0.12 s — remote objects render at `Now − delay`, absorbing jitter), `SnapThreshold` (0.5 — hard snap on reconciliation), `SoftRate` (0.15 — soft-correction rate), `PredictOwner` (toggle owner prediction). Reconciliation blends: hard snap beyond `SnapThreshold`, exponential soft correction otherwise.

**`SnapshotBuffer<T>`** — the public client-side interpolation buffer (`Add(time, value)` + `TrySample(renderTime)`). `NetworkTransform`'s remote rendering runs on it; use it directly for your own replicated values that need jitter-free rendering — sample at `Now − InterpolationDelay` so network jitter never reaches the screen.

**Lag compensation (rewind)** — opt in per behaviour; the server records a 128-sample position history ring buffer.

```csharp
private void Awake() => NetworkRewindHistory = true;   // on the target (server records its positions)

private partial void RpcFire(float dirX, float dirY, double hitTime);   // client attaches its aim time
private void RpcFire_Implementation(float dirX, float dirY, double hitTime)
{
    // Trust boundary: the client-supplied time is untrusted input — clamp before use.
    double t = Math.Clamp(hitTime, UniNetTime.Now - 1.0, UniNetTime.Now);
    foreach (var target in FindTargets())
    {
        target.GetHistoryPosition(t, out float hx, out _, out float hz);
        if (HitTest(dirX, dirY, hx, hz)) { target.ApplyDamage(); break; }
    }
}
```

### Security & transport tuning

All transport-level configuration lives in `UniNetEndpointOptions` — game code never touches the underlying stack types:

- `ConnectionKey` — shared secret for handshakes (reject foreign clients early).
- `ConnectTimeoutMs`, `MaxConnections` — handshake timeout and connection cap.
- `EnableCrc32c` — optional packet integrity check.
- DTLS 1.2 — `ServerCertificate` (X509Certificate2), `TlsTargetHost`, `TlsAllowNameOnlyCertificateMatch`, or a custom `TlsCertificateValidation` callback (supports certificate pinning).

Checklist for a production dedicated server: set `ConnectionKey`, `MaxConnections`, enable DTLS with pinning, and keep `[ServerRpc]` ownership enforcement on (opt out only per-call, with server-side validation). Remember that client-supplied data (`hitTime`, RPC arguments, `_Validate` inputs) is untrusted — clamp and validate at the server boundary, as shown above.

### Threading & failure-isolation model (what you can rely on)

- Network receive happens on a network thread, but everything user-visible is queued to the main thread and drained in the driver update — never touch Unity APIs from your own threads.
- RPC dispatch, RepNotify callbacks, and lifecycle events are individually exception-isolated: one fault is logged, the pump survives, remaining work drains next frame.
- Server replication tick faults (custom serializer, field access) are isolated per tick via `LogFault`.
- Ownership reassignment on disconnect is guaranteed even if a disconnect subscriber throws (server invariant: no orphaned owners).

## Known Limitations

- No transform/hierarchy parenting sync for dynamic objects — spawn flat objects and parent on each client yourself.
- No direct collection RPC parameters — wrap in `[Message]`.
- 32 `[Replicated]` fields per `NetworkBehaviour` type (mask width); 255 `NetworkBehaviour`s per object (SubId width).
- OwnerOnly fields with no owner can lose intermediate changes to the next owner (single server snapshot).
- First one-way RPC right after connect may be lost (pre-welcome race) — send after the connection is confirmed.
- Ownership assignment is a round-robin minimal policy, not a game-specific allocator.
- Off-screen (non-relevant) objects freeze at their last replicated state on the client; they do not despawn.
- Diff-based replication is per-tick polling — no weaved/conflated per-property streams beyond the frequency cap.
- Loading the same scene **twice additively** duplicates the stamped scene ids by definition — unsupported for scene objects; spawn such instances dynamically instead.
- Scene-id stamping requires the scene to be saved in the editor at least once; a mismatch between a stamped and an unstamped end (e.g. one side on an old build) is not detected — keep scene assets in sync.
