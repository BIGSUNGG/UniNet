# UniNet

[![upm-package](https://github.com/BIGSUNGG/UniNet/actions/workflows/upm.yml/badge.svg)](https://github.com/BIGSUNGG/UniNet/actions/workflows/upm.yml)

A server-authoritative networking framework for Unity, targeting the feature level of Unreal Engine's Network Framework. Declare RPCs and replicated state directly on `MonoBehaviour` — a Roslyn source generator wires the dispatch, serialization, and replication plumbing.

> Principle — **"Simple to use, powerful under the hood."**

- **RPC** — `[ServerRpc]` / `[ClientRpc]` / `[MulticastRpc]` on `NetworkBehaviour`, object-scoped, with per-call delivery modes.
- **State replication** — `[Replicated]` fields synchronize automatically; only changed fields (or changed collection elements) are sent.
- **Built for Unity server builds** — one API on both sides; the server is authoritative whether you run dedicated or listen-server.
- **Zero external setup** — transport (RUDP + DTLS), serialization, and RPC plumbing are bundled as DLLs plus three source generators inside the package. One Git URL installs everything.

Sister projects (bundled): [DS_Communication](https://github.com/BIGSUNGG/DS_Communication) (transport), [DS_MessageProtocol](https://github.com/BIGSUNGG/DS_MessageProtocol) (serialization), [DS_RPC](https://github.com/BIGSUNGG/DS_RPC) (RPC plumbing).

## Requirements

- Unity `6000.0` or newer (Unity 6 LTS). Server = Unity server build (dedicated or listen). Runtime targets `netstandard2.1`.
- Package Manager Git URL install; the repository is public, so consumers need no authentication.

## Features

### RPC

- Three object-scoped RPC kinds: `[ServerRpc]` (client → server), `[ClientRpc]` (server → specific clients), `[MulticastRpc]` (server → everyone; called on a client it executes locally without propagating).
- Per-declaration delivery mode on Client/Multicast RPCs — `Delivery.ReliableOrdered` (default) or `Delivery.Unreliable` for loss-tolerant data such as movement or FX. (The bundled RUDP transport provides five delivery modes underneath.)
- **Owner-only ServerRpc by default** — the server dispatch rejects calls from non-owners (spoofed `netId` payloads cannot invoke other objects' RPCs) with a rate-limited warning log; opt out with `[ServerRpc(RequireOwnership = false)]` for cross-client reporting.
- **Opt-in validation hooks** — `[ServerRpc(Validate = true)]` awaits a `{Name}_Validate` (`Task<bool>`) before the implementation; returning `false` skips execution.
- `[Message]` message parameters and fields with official polymorphism support — declare a base type, send a subclass, cast on the server.
- Offline and host paths execute locally; malformed declarations fail at compile time with `UNINET0xx` diagnostics.

### Variable replication

- `[Replicated]` fields sync server → clients; each server tick diffs the snapshot and sends only the changed fields.
- `RepNotify` — `Notify = nameof(Callback)` fires on clients with the previous value whenever the field changes over the network.
- **Conditional replication** — `ReplicateCondition.OwnerOnly` / `SkipOwner` / `InitialOnly` restrict who receives a field (initial full state at spawn for `InitialOnly`, nothing after).
- Late-join catch-up — connecting clients receive the full state of every scene and dynamic object automatically.
- Host keeps the authoritative original (client-side re-application rules never fight the server copy).

### Dynamic objects & ownership

- `NetworkInstantiate` / `NetworkDestroy` — spawn and destroy dynamic networked objects in one call; position/rotation and full initial state propagate to every client. Optional `configure` callback seeds private `[Replicated]` fields on the clone before it propagates.
- **Explicit-owner spawn** — `NetworkInstantiate(original, ownerConnId)` spawns an object owned by a specific connection the moment it connects.
- Ownership reassignment keeps live owners intact and reassigns only orphans (`ReassignOwnership`) — reconnections and restarts cannot steal ownership.
- Optional `RegisterPrefab<T>` catalog for custom visuals/pre-configured prefabs; without it, clients build objects as an empty `GameObject` + components, and the server-side component composition is restored automatically from the spawn message.
- Ghost prevention — a registered object destroyed with a plain `Destroy` is detected and the destroy is propagated.
- **Multiple `NetworkBehaviour`s per object** — two-level identity (`netId` + `SubId` slot); each component gets its own RPCs and replicated fields (up to 255 per object). Same-type duplicates are allowed.
- **Network roles** — `IsServer` / `IsClient` / `IsOwner` on every behaviour model authority and ownership (UE's Authority / AutonomousProxy / SimulatedProxy as three booleans). Client and server share one API and branch on the role.

### Serialization control

- **Custom field serializers** — `[Replicated(Serializer = typeof(T))]` delegates a field's wire format to a static `Write`/`Read` pair (e.g. quantized positions in 6 bytes). An optional `Equals` suppresses resends when the quantized value didn't change. This also unlocks non-primitive types such as `Vector3` for replication.
- **Collection element deltas** — `T[]` / `List<T>` fields synchronize per element: `Set` / `Insert` / `RemoveAt` / `Clear` operations only, instead of resending the whole collection (up to 65,535 elements). Elements may be primitives, `string`, `[Message]` types, or custom-serialized types.

### Bandwidth control (UE NetDriver parity)

- **Relevancy / visibility** — `NetworkCullDistance` per object, an `IsNetworkRelevant(connId)` override hook, and server-side `SetViewerPosition` per connection. Objects leaving relevancy stop sending deltas (the client keeps its last state); re-entering restores the baseline.
- **Priority & starvation compensation** — `NetworkPriority` orders sends under bandwidth pressure using the UE `GetNetPriority` formula (priority × wait time), so starved objects eventually go through.
- **Dormancy** — `NetworkDormant = true` skips delta comparison entirely for idle objects; `FlushNetworkDormancy()` releases it and flushes accumulated changes in one shot.
- **Update frequency** — `NetworkUpdateFrequencyHz` caps how often an object's changes are sent (e.g. projectiles at 30 Hz); misses coalesce to the latest value.
- **Budgets** — global `ReplicationBudgetPerTickBytes` plus per-type `SetReplicationChannelBudget(type, bytes)` so a flood of bullets cannot starve other object types. Oversized single deltas force through.

### Prediction & timing

- **`UniNetTime`** — server-authoritative monotonic clock, auto-synced to clients every second (EMA offset, anti-regression clamp). Query `UniNetTime.Now` anywhere.
- **`NetworkTransform`** — a built-in component covering position replication, owner-side client prediction, and remote interpolation. You inject only the movement rule (`MovementRule` lambda, shared verbatim by server and prediction), submit inputs with `SubmitMove`, and gate simulation with `SimulationEnabled`.
- **`SnapshotBuffer<T>`** — client interpolation buffer; render remote objects at `Now − InterpolationDelay` (default 0.12 s) so network jitter never reaches the screen.
- **Lag compensation** — opt in with `NetworkRewindHistory = true`; the server records position history, and hit-scan logic queries `GetHistoryPosition(serverTime)` against the shooter's (clamped) aim time.
- **Grid visibility** — `SetVisibilityGrid(cellSize, radius)` replaces distance checks with spatial cell membership for large worlds (RepGraph-style).

### Connection lifecycle & shutdown

- Server events `ClientConnected` / `ClientDisconnected` (by `connId`), fired on the main thread; disconnect fires **before** ownership reassignment so you can destroy the leaving connection's avatars precisely. Reassignment is guaranteed even if a subscriber throws.
- Client side: `UniNetManager.ClientDisconnected` event for session loss (server shutdown, network drop) and `IsClientConnected` state query. `UniNetEnvironment.ServerChanged` fires the moment the server instance is set, before any connection is accepted.
- Subscriber exceptions are isolated — one handler's bug never kills the others or the pump.
- Start/stop one-liners: `UniNetManager.HostAsync / ServerAsync / ClientAsync` and `ServerStopAsync / ClientStop / HostStop` — stop is idempotent and clears the listener so the same port re-listens cleanly.

### Security & transport

- RUDP transport (reliable/unordered/sequenced delivery) over UDP.
- Optional **DTLS 1.2** encryption with certificate pinning.
- Connection key, handshake timeouts, connection caps, optional CRC32c packet integrity — all configured through `UniNetEndpointOptions`; game code never touches the underlying stack types.

Everything above is implemented and verified (EditMode/PlayMode tests + two-process RUDP round-trips). The Unreal-feature mapping matrix lives in [`Document/roadmap.md`](Document/roadmap.md).

## Quick Start

### 1. Install

Add one line to your project's `Packages/manifest.json`:

```json
"com.ds.uninet": "https://github.com/BIGSUNGG/UniNet.git?path=/Package#v0.1.1"
```

`#v0.1.1` pins a version tag (omit it to track the default branch). Releases: [Releases](https://github.com/BIGSUNGG/UniNet/releases) · details: [Document/deployment.md](Document/deployment.md)

### 2. Declare a networked object

RPC methods are `partial` declarations — the body goes in `{Name}_Implementation`; validation hooks opt in with `[ServerRpc(Validate = true)]` and go in `{Name}_Validate`.

```csharp
using System.Threading.Tasks;
using UniNet.Unity;
using UnityEngine;

public sealed partial class Player : NetworkBehaviour
{
    [Replicated(Notify = nameof(OnHpChanged))]  // server-authoritative field + RepNotify callback
    private int _hp = 100;

    // Runs on clients whenever _hp changes over the network (receives the previous value)
    private void OnHpChanged(int prevHp) { /* update UI */ }

    [ServerRpc(Validate = true)]           // client → server; owner-only by default + opt-in validation
    private partial void RpcRequestHit(int damage);

    private Task<bool> RpcRequestHit_Validate(int damage)   // runs before the implementation; false skips it
        => Task.FromResult(damage > 0);

    private void RpcRequestHit_Implementation(int damage)   // runs on the server only
    {
        _hp -= damage;
        if (_hp <= 0) RpcPlayDeathFx();
    }

    [ClientRpc(Delivery.Unreliable)]       // server → client(s), delivery mode per declaration
    private partial void RpcPlayHitFx(int damage);
    private void RpcPlayHitFx_Implementation(int damage) { /* FX */ }

    [MulticastRpc]                          // server → server + all clients (client calls stay local)
    private partial void RpcPlayDeathFx();
    private void RpcPlayDeathFx_Implementation() { /* death FX */ }

    private void Update()
    {
        if (!IsOwner) return;
        if (Input.GetKeyDown(KeyCode.Space))
            RpcRequestHit(10);
    }
}
```

### 3. Start the network and run

```csharp
// Security, timeouts, and DTLS are configured via UniNetEndpointOptions.
await UniNetManager.HostAsync(7777);                    // server + client in one process (development)
// await UniNetManager.ServerAsync(7777);               // dedicated server
// await UniNetManager.ClientAsync("127.0.0.1", 7777);  // client
```

- **Host mode** — press Play with the host bootstrap and everything works in one editor.
- **Two processes** — run a server build and a client build (or use Unity's Multiplayer Play Mode for one editor with virtual players). The Arena demo verifies this path (`Sandbox-2proc-*.log` round-trips); the Basics README explains the dedicated-server split.
- Minimal runnable sample: import **UniNet ▸ Samples ▸ Basics** in the Package Manager.

Full API usage walkthrough: [Package/Documentation~/index.md](Package/Documentation~/index.md)

## Usage

### Conditional replication

```csharp
using static UniNet.Unity.Net;   // unqualified NetworkInstantiate / NetworkDestroy

public sealed partial class Projectile : NetworkBehaviour
{
    [Replicated] private float _x;                                               // everyone, always
    [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnDamageChanged))]
    private int _damage;                                                         // owning client only
    [Replicated(ReplicateCondition.InitialOnly)] private int _seed;             // once, at spawn
    [Replicated(Serializer = typeof(PositionQuantized))] private Vector3 _pos;  // custom serializer (quantized); unlocks Vector3
    [Replicated] private List<int> _scores = new();                             // element-level deltas (FastArray)

    private void Update()
    {
        if (!IsServer) return;                   // server-authoritative simulation
        _x += 8f * Time.deltaTime;
        if (_age >= _lifetime) UniNetManager.NetworkDestroy(gameObject);
    }
}
```

Late joiners are automatically caught up with every existing dynamic object.

### Dynamic spawn / destroy

```csharp
// On the server: one call clones, registers, and propagates the spawn to all clients.
// The return value is the registered instance (the original stays put).
var projectile = NetworkInstantiate(_projectilePrefab, pos, rot);
UniNetManager.NetworkDestroy(projectile);        // destroy sync

// Private [Replicated] initial values (the InitialOnly baseline) go through the configure callback,
// executed on the clone right after duplication, right before propagation:
var avatar = UniNetManager.NetworkInstantiate(_avatarPrefab,
    clone => clone.GetComponent<Player>().InitServerState("Alpha"));

// Optional client-side prefab catalog for custom visuals. Unregistered types spawn as
// GameObject + AddComponent, and the server's component composition is restored automatically.
UniNetManager.RegisterPrefab<Projectile>(projectilePrefab);

// Spawn for a specific connection, the moment it connects:
var token = NetworkInstantiate(_avatarPrefab, ownerConnId: connId);
```

### Multiple network behaviours on one object

```csharp
public sealed partial class MovementBrain : NetworkBehaviour { [Replicated] public int Speed; /* ... */ }
public sealed partial class HealthTank : NetworkBehaviour
{
    [Replicated(ReplicateCondition.OwnerOnly)] public int Armor;
}

var template = new GameObject("robot");
template.AddComponent<MovementBrain>();
template.AddComponent<HealthTank>();
var go = UniNetManager.NetworkInstantiate(template);   // every component registers in its own SubId slot
```

Do not add or remove `NetworkBehaviour`s at runtime — slot order must match on both ends (dynamic-spawn composition mismatches are rejected at spawn; scene objects rely on this contract).

### Bandwidth policies

```csharp
public sealed partial class Bullet : NetworkBehaviour
{
    public void Init()
    {
        NetworkUpdateFrequencyHz = 30f;   // cap trajectory sends at 30 Hz (misses coalesce to latest)
        NetworkCullDistance = 24f;        // no sends to connections beyond this radius
    }

    public override bool IsNetworkRelevant(long viewerConnId)   // optional per-viewer hook
        => viewerConnId == _allowedConnId;
}

public sealed partial class Player : NetworkBehaviour
{
    private void Die()      => NetworkDormant = true;    // stop diffing entirely while idle/dead
    private void Respawn()
    {
        _hp = MaxHp;
        FlushNetworkDormancy();        // release dormancy; accumulated changes flush at once
        NetworkPriority = 2f;          // higher priority wins when bandwidth is short
    }
}

// Server bootstrap — cull origin per connection and bandwidth budgets per type:
server.SetViewerPosition(connId, x, y, z);
server.SetReplicationChannelBudget(typeof(Bullet), 512);   // bullets can't starve other types
server.ReplicationBudgetPerTickBytes = 8192;               // global per-tick budget (0 = unlimited)
```

### NetworkTransform — built-in movement prediction & interpolation

```csharp
public sealed partial class Character : NetworkBehaviour
{
    private NetworkTransform _nt;

    private void Awake()
    {
        _nt = GetComponent<NetworkTransform>();           // add a NetworkTransform alongside this behaviour
        _nt.MovementRule = (ref float x, ref float y, ref float z,
                            float ix, float iy, float iz, float dt) =>
        {
            x += ix * 5f * dt;                            // your movement rule — shared by server & prediction
        };
    }

    private void HandleInput()
    {
        _nt.SubmitMove(inputX, inputY, 0);                // owner client: apply immediately (predict) + send
        _nt.SimulationEnabled = IsAlive();                // gate — pauses/resumes server & prediction together
    }
}
// Remote clients: NetworkTransform receives positions and renders at Now − InterpolationDelay (0.12 s default).
```

### Lag compensation (rewind)

```csharp
public sealed partial class Player : NetworkBehaviour
{
    private void Awake() => NetworkRewindHistory = true;  // server records position history

    // The shooter sends the server time it aimed at; the server rewinds targets for the hit test.
    private partial void RpcFire(float dirX, float dirY, double hitTime);
    private void RpcFire_Implementation(float dirX, float dirY, double hitTime)
    {
        double t = Math.Clamp(hitTime, UniNetTime.Now - 1.0, UniNetTime.Now);   // trust-boundary clamp
        foreach (var target in FindTargets())
        {
            target.GetHistoryPosition(t, out float hx, out _, out float hz);    // query past position
            if (HitTest(dirX, dirY, hx, hz)) { target.ApplyDamage(); break; }
        }
    }
}

// Server bootstrap — optional grid visibility for large worlds:
server.SetVisibilityGrid(10f, 30f);
```

### Connection lifecycle events

```csharp
// Server (authority) — handle joins and leaves yourself (see the Arena example):
server.ClientConnected += connId =>
{
    // Fires after welcome, ownership assignment, and catch-up — safe to spawn an avatar right here.
    SpawnAvatar(connId);
};
server.ClientDisconnected += connId =>
{
    // Fires BEFORE ownership reassignment — identify objects owned by the leaving connection:
    DestroyOwnedAvatar(connId);   // → UniNetManager.NetworkDestroy propagates the despawn to all clients
};

// Client — notice session loss (server shutdown, network drop):
UniNetManager.ClientDisconnected += () => ShowReconnectPrompt();
bool alive = UniNetManager.IsClientConnected;
```

All events fire on the main thread. Subscriber exceptions are isolated.

### Shutdown

Stop explicitly so the same port re-listens without binding failures:

```csharp
await UniNetManager.ServerStopAsync();   // also ServerStop / ClientStop / HostStopAsync / HostStop (sync)
```

Idempotent — no-op when not listening. Stop also sweeps dynamically spawned objects, so a fresh session in the same process never collides with stale netIds. Call the sync versions from `OnApplicationQuit`.

## Sample & example game

- **Basics** — minimal RPC + variable replication sample, installable from the Package Manager (`UniNet ▸ Samples ▸ Basics`). Source: [`Package/Samples~/Basics`](Package/Samples~/Basics).
- **Arena shooter** — a top-down 2–4 player demo exercising nearly every implemented feature (all three RPC kinds, validation hooks, conditional replication, dynamic spawn, bandwidth policies, client prediction, hitscan lag compensation, time sync, grid visibility; custom serializers and collection deltas have their own demo, `Sandbox/Assets/Scripts/Usage/SerializationUsage.cs`). Uses only Unity primitives — no assets. Runs with Multiplayer Play Mode (main editor = server, virtual players = clients). Scene: `Sandbox/Assets/Scenes/Arena.unity` · code: `Sandbox/Assets/Scripts/Arena/` · matrix: [Document/examples/arena-shooter.md](Document/examples/arena-shooter.md)

## Repository layout

| Path | Contents |
| --- | --- |
| `Package/` | The UniNet UPM package (`com.ds.uninet`) — the library itself, with bundled dependencies, sample, and user docs |
| `Sandbox/` | Unity 6000.0.83f1 sandbox project — spikes, demos, tests (disposable) |
| `CodeGenerator/` | `UniNet.CodeGenerator` — the Roslyn source generator source |
| `Document/` | Documentation vault — definitions, architecture, decisions (SSoT) |

## Documentation

| Document | Contents |
| --- | --- |
| [skills/uninet/SKILL.md](skills/uninet/SKILL.md) | **Agent skill** — this whole manual as a `SKILL.md` for AI agents (pi / Claude Code / Codex compatible); install into your repo so your coding agent knows UniNet |
| [Package/Documentation~/index.md](Package/Documentation~/index.md) | User manual — install & quickstart (shipped with the package) |
| [Document/roadmap.md](Document/roadmap.md) | Unreal Network Framework parity matrix — every feature, mapped |
| [Document/features/](Document/features/) | Per-feature documents — RPC & replication, connection lifecycle, custom serialization, collection deltas, bandwidth policies, prediction & timing hooks |
| [Document/architecture.md](Document/architecture.md) | Architecture overview |
| [Document/deployment.md](Document/deployment.md) | Distribution channel & release procedure |
| [Document/decisions/](Document/decisions/) | ADRs — why the framework is shaped this way |
| [Document/00-INDEX.md](Document/00-INDEX.md) | Documentation index |

## Release

A `v*` tag is the deployment: CI validates package integrity and auto-creates the GitHub Release (the `Package/CHANGELOG.md` section becomes the release note). Consumer install always pins a tag — [Releases](https://github.com/BIGSUNGG/UniNet/releases).
