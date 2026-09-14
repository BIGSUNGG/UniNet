# DS_RPC (DRPC)

DRPC is a distributed RPC library for .NET that runs over reliable UDP. You declare your API as C# interfaces, annotate methods with `[RemoteProcedure]`, and a Roslyn source generator emits typed async stubs, incoming dispatch, connection wiring, and payload encoding — you never write transport or serialization code.

- **Transport** (RUDP, DTLS 1.2) is delegated to [DS_Communication](https://github.com/BIGSUNGG/DS_Communication).
- **Serialization** is delegated to [DS_MessageProtocol](https://github.com/BIGSUNGG/DS_MessageProtocol).
- **DRPC itself** handles contract declaration, source generation, and the runtime hubs.

All runtime packages target `netstandard2.1` (the source generator targets `netstandard2.0`), so they are usable from modern .NET and other `netstandard2.1`-capable targets such as Unity 2021.2+ (they are **not** consumable from .NET Framework, which tops out at `netstandard2.0`). Since 3.5.0 the generator references Roslyn 4.3, so it also runs directly inside Unity 6's bundled compiler — no rebuilt `-unity` package is needed.

## Features

- Interface-based RPC contracts: declare once, call from the client with `await hub.MethodAsync(...)`, implement on the server with a `{Method}_Implementation` partial.
- Five delivery modes per method (`Unreliable`, `ReliableUnordered`, `Sequenced`, `ReliableOrdered`, `ReliableSequenced`) — chosen per declaration, mapped onto the RUDP stack.
- Fire-and-forget one-way calls (`OneWay = true`) with no response wait.
- Two-way RPC: the server can call back into connected clients through the same stub pattern.
- DTO parameters and returns via MessageProtocol messages (`[Message(MessageKind.NonId)]`), plus polymorphic delivery through message groups.
- Generic procedures (`[GenericProcedure]`) with compile-time-checked allowed type sets.
- Per-call response timeouts (`TimeoutMs`), caller-side cancellation tokens, and an opt-in server-side validation gate (`Validation = true`).
- Opt-in DTLS 1.2 packet encryption with certificate pinning.
- A structured error model surfaced as `RpcFaultException` with typed error codes.

Not in scope (handled by the sibling stacks): low-level sockets and the transport itself (DS_Communication), and the general serialization engine (DS_MessageProtocol). The payload wire format guarantees one-way encoding only — no cross-version wire compatibility is promised.

## Quick Start

This walkthrough builds a minimal client → server round trip. A complete, runnable version of everything shown here lives in [`Sandbox/`](Sandbox/).

### 0. Install the packages

Contract project (shared by both sides):

```powershell
dotnet add package DRPC.Attribute
dotnet add package DRPC.Shared
dotnet add package MessageProtocol
```

Client and server app projects:

```powershell
dotnet add package DRPC.Client      # client app
dotnet add package DRPC.Server      # server app
```

Both apps must also reference the shared contract project (mirrors `Sandbox/`, where each app has a `ProjectReference` to `Sandbox.Contracts`):

```powershell
dotnet add ./Client reference ./Contracts/Contracts.csproj
dotnet add ./Server reference ./Contracts/Contracts.csproj
```

Then reference the source generator as an analyzer in each project that declares or implements a hub (client and server apps):

```xml
<PackageReference Include="DRPC.CodeGenerator" Version="2.13.0"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

> Keep `MessageProtocol` referenced only by the contract project — its serializer generators do not need to propagate to the apps.

### 1. Declare the contract

`IServerProcedureDeclarations` = the server implements, the client calls. `IClientProcedureDeclarations` = the reverse. Return types are **plain** (no `Task`) — the generated stubs are already async.

```csharp
using DRPC;
using DRPC.Shared.Interface;

public interface IGameServerProcedures : IServerProcedureDeclarations
{
    [RemoteProcedure(methodId: 0)]                          // default: ReliableOrdered
    int Add(int value1, int value2);

    [RemoteProcedure(RpcDeliveryMode.Sequenced, 2)]         // delivery-mode override
    void SetPosition(int playerId, float x, float y);

    [RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 3, OneWay = true)]
    void LogChat(string text);                              // fire-and-forget
}

public interface IGameClientProcedures : IClientProcedureDeclarations
{
    [RemoteProcedure(methodId: 0)]
    float EchoSum(List<float> values);
}
```

Both sides must share the same contract; `methodId` values are the wire dispatch keys.

### 2. Implement the server side

Declare a `partial` hub class inheriting `ServerHub<server contract, client contract>` and fill in a `{Name}_Implementation` partial for each served method. The generator emits everything else.

```csharp
using DRPC.Server.Network;

public partial class GameServerHub : ServerHub<IGameServerProcedures, IGameClientProcedures>
{
    private partial Task<int> Add_Implementation(int value1, int value2)
        => Task.FromResult(value1 + value2);

    private partial Task SetPosition_Implementation(int playerId, float x, float y)
        => Task.CompletedTask;

    private partial Task LogChat_Implementation(string text)
    {
        Console.WriteLine($"chat: {text}");
        return Task.CompletedTask;
    }
}
```

### 3. Implement the client hub

The walkthrough's server reverse-calls `EchoSum` on every client that connects, so the client must provide a hub class too — declare a `partial` class inheriting `ClientHub<server contract, client contract>` and fill in the implementation for each method in the client contract:

```csharp
using DRPC.Client.Network;

public partial class GameClientHub : ClientHub<IGameServerProcedures, IGameClientProcedures>
{
    private partial Task<float> EchoSum_Implementation(List<float> values)
        => Task.FromResult(values.Sum());
}
```

Without this class the client project does not compile: `GameClientHub` is the generated-contract type the walkthrough connects with, and the server's reverse call needs `EchoSum_Implementation` to exist.

### 4. Listen and connect

```csharp
// Server: listen; the callback runs per connected peer.
await using var handle = await GameServerHub.ListenAsync(9050, "secret-key", async hub =>
{
    // Reverse call into the client through its contract stub.
    float sum = await hub.EchoSumAsync(new List<float> { 1.5f, 2.25f });
});
Console.ReadLine();   // keep the server alive
```

```csharp
// Client: connect, then await the generated stubs.
using var hub = await GameClientHub.ConnectAsync("127.0.0.1", 9050, "secret-key");

int result = await hub.AddAsync(2, 3);                       // 5
await hub.SetPositionAsync(7, 1.25f, -3.5f);                 // Sequenced
await hub.LogChatAsync("hello");                             // one-way
```

That is the full loop: `ListenAsync` on the server, `ConnectAsync` on the client, `{Method}Async` stubs to call, `{Method}_Implementation` partials to serve. Run the Sandbox demo (below) to see it working end to end.

## Packages and Requirements

| Package | Contents |
| -------- | -------- |
| `DRPC.Attribute` | `[RemoteProcedure]`, `[GenericProcedure]`, `RpcDeliveryMode` (no dependencies) |
| `DRPC.Shared` | `HubBase` runtime, wire messages, error model, `RpcEndpointOptions` |
| `DRPC.Client` / `DRPC.Server` | Side-specific hub bases, generated `ConnectAsync` / `ListenAsync` wiring |
| `DRPC.CodeGenerator` | Roslyn source generator (development dependency, analyzer-only reference) |

Current versions: DRPC packages **3.5.0** (release tags are authoritative), `MessageProtocol` **3.1.0** (the unified `[Message]` attribute — see below), `Communication.Network.RUDP.*` / `Communication.Shared` **2.7.0**.

Runtime packages target `netstandard2.1` and run on Unity and other `netstandard2.1`-capable frameworks. Building this repository or the sandbox from source requires the .NET 10 SDK.

## Declaring a Contract

```csharp
[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, methodId: 0, OneWay = false)]
int Add(int value1, int value2);
```

- `[RemoteProcedure]` with no arguments means `ReliableOrdered` and `methodId` inferred from declaration order (the generator warns with `DRPCGEN004` — prefer explicit IDs).
- **Trap:** the first positional argument is the *mode*, not the method ID. `[RemoteProcedure(0)]` means `Unreliable`, not method ID 0. To set only the ID, write `[RemoteProcedure(methodId: 3)]`.
- Duplicate method IDs in one contract are a compile error (`DRPCGEN005`).
- Contract methods use plain return types, cannot return `Task`, cannot use `ref`/`out`, cannot be overloaded, and cannot be generic without `[GenericProcedure]` (all rejected as `DRPCGEN003`).
- Responses and errors are sent back using the delivery mode registered for the request's method ID.

## Delivery Modes

| `RpcDeliveryMode` | Guarantee |
| ------------------ | --------- |
| `Unreliable` | No delivery guarantee; fastest possible send. |
| `ReliableUnordered` | Delivered, but arrival order is not guaranteed. |
| `Sequenced` | Tolerates loss and reordering; newer calls supersede older ones — suited to continuous state updates. |
| `ReliableOrdered` | **Default.** Every call delivered, in order. |
| `ReliableSequenced` | Reliable delivery with newest-wins ordering for state streams. |

Semantics are provided by the RUDP transport stack; DRPC's `RpcDeliveryMode` is the only delivery enum you ever touch.

## Two-Way RPC (Server → Client)

The server's `onConnected` callback receives the peer hub, whose client-contract members are outgoing stubs:

```csharp
await using var handle = await GameServerHub.ListenAsync(9050, "secret-key", async hub =>
{
    float sum = await hub.EchoSumAsync(new List<float> { 1.5f, 2.25f, 4f });
});
```

The client implements its own contract the same way as the server does:

```csharp
public partial class GameClientHub : ClientHub<IGameServerProcedures, IGameClientProcedures>
{
    private partial Task<float> EchoSum_Implementation(List<float> values)
        => Task.FromResult(values.Sum());
}
```

Naming is ownership-based: the client-side hub inherits `ClientHub`, the server-side hub inherits `ServerHub`.

## DTOs and Polymorphic Messages

DTO parameters and return values are MessageProtocol message types. Decorate them once in the contract project with the unified `[Message]` attribute:

```csharp
using MessageProtocol;

[Message(MessageKind.NonId)]   // plain DTO: no wire ID, no category argument allowed
public partial class Player
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
```

Then use them directly in contracts — `[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] PlayerJoined Join(Player player);`

**Group polymorphism:** declare a parent (`MessageKind.Parent`) with an explicit wire ID and category, and child types (`MessageKind.Child`) inheriting it, then send derived instances through the parent-typed parameter. The concrete type is preserved on the wire and restored on arrival:

```csharp
[Message(MessageKind.Parent, 11, MessageCategory.Category2)]
public partial class ChatLine { public string Text { get; set; } = string.Empty; }

[Message(MessageKind.Child)]   // no ID argument — omitted ID defaults to a hash of the type's full name
public partial class ShoutChatLine : ChatLine { }

// Declared as: void ChatMessage(ChatLine line);  — sending ShoutChatLine arrives as ShoutChatLine.
```

## Generic Procedures

Generic contract methods require allowed type sets declared per type-parameter slot with `[GenericProcedure]` (`AllowMultiple`, one per slot):

```csharp
[RemoteProcedure(methodId: 5)]
[GenericProcedure(typeof(int), typeof(string))]
T GetConfig<T>();                       // call: await hub.GetConfigAsync<int>()

[RemoteProcedure(methodId: 6)]
[GenericProcedure(typeof(int), typeof(string))]
string Describe<T>(T value);            // call: await hub.DescribeAsync(42) — T inferred

[RemoteProcedure(methodId: 7)]
[GenericProcedure(0, typeof(int), typeof(string))]   // slot 0
[GenericProcedure(1, typeof(float), typeof(double))] // slot 1
[GenericProcedure(2, typeof(Player), typeof(ChatLine))] // slot 2
T1 Blend<T1, T2, T3>(T2 left, T3 right); // supported combinations are the Cartesian product
```

- A generic method whose type parameter appears only as a `[GenericMessage]` parameter (e.g. `void Unwrap<T>(GiftBox<T> box)`) inherits the allowed set from that message's `[GenericMessage]` composition declarations instead. `T` must be an ID-header message type there (`MessageKind.Standalone` / `Parent`/`Child` types — not `MessageKind.NonId` or primitives).
- Calls or declarations outside the allowed sets fail at compile time (`DRPCGEN008` / `DRPCGEN007`, `DRPCGEN009`) with a runtime throw as backstop.
- Generic type-parameter constraints (`where T : ...`) are not supported.
- **Wire compatibility warning:** the emitted combination index depends on declaration order. Reordering `[GenericProcedure]` lists changes the wire format for existing peers — this is not detected at compile time; manage it as a versioning concern.

## Timeouts and Cancellation

- Hub-wide response timeout: `hub.RpcTimeout` (default 30 seconds; `TimeSpan.Zero` or less, or `Timeout.InfiniteTimeSpan`, means unlimited). Expiry surfaces as `TimeoutException`.
- Per-call budget: `[RemoteProcedure(methodId: 9, TimeoutMs = 400)]` overrides the wait budget for that call only (positive milliseconds; `0`/negative is `DRPCGEN010`; combining `TimeoutMs` with `OneWay` warns `DRPCGEN011`).
- Cancellation: every round-trip stub takes an optional trailing `CancellationToken`. Cancelling ends the wait immediately and frees the call slot; late responses are ignored. The request is not recalled — the remote implementation still runs to completion. One-way stubs take no token because there is no wait.
- `hub.MaxPendingCalls` (default 0 = unlimited) caps outstanding outgoing calls; reaching the limit fails new calls immediately with `InvalidOperationException` (fail-fast, no queuing).

## Validation Gate

Opt in per method with `Validation = true` to run an argument-level check before the implementation:

```csharp
[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 9, Validation = true)]
int TransferGold(int fromPlayer, int toPlayer, int amount);
```

```csharp
// Server hub: must return true for the implementation to run.
private partial Task<bool> TransferGold_Validate(int fromPlayer, int toPlayer, int amount)
    => Task.FromResult(amount > 0 && fromPlayer != toPlayer);

private partial Task<int> TransferGold_Implementation(int fromPlayer, int toPlayer, int amount)
    => Task.FromResult(amount);
```

- `_Validate` runs before `{Name}_Implementation`; returning `false` skips the implementation and the caller observes `RpcFaultException` with `RpcErrorCode.ValidationFailed` (7). One-way calls have no response channel, so a failed gate silently skips them.
- Fail-closed: if `Validation = true` but `_Validate` is not implemented, the generated partial declaration fails compilation — a missing validator can never silently pass.

## DTLS Encryption

Encryption is opt-in via `RpcEndpointOptions` and delegated to the transport stack (DTLS 1.2). Set any TLS field on either endpoint to enable it.

```csharp
// Server: provide a certificate.
var serverOptions = new RpcEndpointOptions
{
    ConnectionKey = "secret-key",
    ServerCertificate = certificate,          // X509Certificate2
};
await using var handle = await GameServerHub.ListenAsync(9050, serverOptions, OnConnected);

// Client: verify the server by target host (SAN/CN match) or by pinning.
var clientOptions = new RpcEndpointOptions { ConnectionKey = "secret-key" };
clientOptions.TlsCertificateValidation = der =>
    RudpTlsOptions.GetSha256Fingerprint(der) == expectedSha256Fingerprint;
using var hub = await GameClientHub.ConnectAsync("127.0.0.1", 9050, clientOptions);
```

Rules that matter:

- **Both ends must enable encryption.** A plaintext endpoint still completes the RUDP handshake but no RPC ever succeeds — this is a wire incompatibility, not a fallback.
- A client with neither `TlsTargetHost` nor `TlsCertificateValidation` set **rejects** the server certificate by default (fail-closed).
- The DTLS handshake completes after the connection key is accepted; handshake failure or timeout fails the connection. Handshake tuning knobs are intentionally not exposed.
- `RpcEndpointOptions.EnableCrc32c` is a separate both-ends-must-match integrity flag.

## Connection Options

`RpcEndpointOptions` bundles endpoint settings (all default to unset): `ConnectionKey`, `ConnectTimeoutMs` (0 = built-in default of roughly 5 s), `MaxConnections` (server-side concurrent-accept cap, 0 = unlimited), `EnableCrc32c`, and the TLS fields above. Pass it to `ConnectAsync(host, port, options)` / `ListenAsync(port, options, onConnected)` instead of the plain `connectionKey` string overloads.

For code that composes hubs manually instead of using generated wiring, `RpcClient.ConnectAsync<THub>` (with a connect-timeout overload) and `RpcHost.ListenAsync<THub>` (with a `maxConnections` overload) are the underlying helpers.

The server listen handle (`RpcListenHandle`) is `IAsyncDisposable`, exposes `ListenTask` (guaranteed to complete on stop/cancel), and `ActiveConnectionCount`.

## Error Model

Remote and local failures surface as exceptions on the calling side:

| `RpcErrorCode` | Value | Observed as |
| ---------------- | ----- | ----------- |
| `Unhandled` | 1 | `RpcFaultException` — the peer's implementation body threw. |
| `UnknownMethod` | 2 | `RpcFaultException` — method ID not registered on the peer. |
| `Timeout` | 3 | `TimeoutException` on the caller — never sent on the wire. |
| `Disconnected` | 4 | `InvalidOperationException` on the caller — never sent on the wire. |
| `Overloaded` | 5 | `RpcFaultException` — peer's `MaxConcurrentIncoming` exceeded. |
| `PermissionDenied` | 6 | `RpcFaultException` — peer's `AuthorizeRequestAsync` hook denied the call. |
| `ValidationFailed` | 7 | `RpcFaultException` — peer's `_Validate` gate rejected the call. |

```csharp
try
{
    await hub.TransferGoldAsync(7, 7, -50);
}
catch (RpcFaultException fault) when (fault.ErrorCode == RpcErrorCode.ValidationFailed)
{
    Console.WriteLine($"rejected: {fault.Message}");   // CallId and original message available
}
```

## Hub Runtime Knobs

Configure on the hub instance, ideally right after connect/listen:

```csharp
hub.RpcTimeout = TimeSpan.FromSeconds(5);   // default 30 s
hub.MaxConcurrentIncoming = 32;             // default 0 = unlimited; set only while idle
hub.SendErrorDetails = false;               // stop leaking exception text in Unhandled errors
hub.MaxPendingCalls = 256;                  // default 0 = unlimited
hub.Disconnected += () => Console.WriteLine("dropped");
hub.Dispose();                              // cancels pending calls, closes the session
```

- `MaxConcurrentIncoming`: excess non-one-way calls get `Overloaded`; one-way calls are dropped. Only set it immediately after connection or while idle.
- `SendErrorDetails` (default `true`): `false` sends a fixed generic message instead of exception details — recommended for internet-exposed endpoints. Server-side trace logs always keep the full detail.
- `AuthorizeRequestAsync(int methodId)`: override on a server hub for per-method permission checks. Denials return `PermissionDenied` (one-way calls are dropped silently). The check runs before the method table lookup, so it does not reveal whether a method exists.
- `LastDisconnectReason`: read inside the `Disconnected` handler for the observed cause of the drop.

## Generator Diagnostics

The source generator rejects invalid hubs at compile time:

| ID | Severity | Meaning |
| ---- | -------- | ------- |
| `DRPCGEN001` | error | Hub class is not `partial`. |
| `DRPCGEN002` | error | Class does not inherit `ClientHub<,>`/`ServerHub<,>` with contract type arguments. |
| `DRPCGEN003` | error | Unsupported member: unsupported types, `ref`/`out`, generic method without `[GenericProcedure]`, `Task` return, or overloads. |
| `DRPCGEN004` | warning | Method ID inferred from declaration order — prefer explicit `methodId`. |
| `DRPCGEN005` | error | Duplicate method ID within one contract. |
| `DRPCGEN006` | error | `OneWay = true` but return type is not `void`. |
| `DRPCGEN007`–`009` | error | Invalid generic-procedure declarations or undeclared type arguments at call sites. |
| `DRPCGEN010`–`011` | error/warning | `TimeoutMs` misuse (non-positive; combined with `OneWay`). |

## Building and Testing

```powershell
dotnet build DRPC.slnx -c Release
dotnet test  DRPC.slnx -c Release     # 132 tests: 47 generator, 47 unit, 38 E2E over real RUDP loopback
```

Use `-c Release`: Debug builds can fail to copy `DRPC.CodeGenerator.dll` while a language server holds the file.

## Sandbox Demo

`Sandbox/` contains a complete working demo (contracts, server, client) covering: delivery modes, one-way, DTOs, group polymorphism, generic procedures, the validation gate, and DTLS:

```powershell
dotnet build DRPC.slnx -c Release
dotnet run --no-build -c Release --project Sandbox/Sandbox.Server   # listens on 127.0.0.1:9050, key "sandbox-key"
dotnet run --no-build -c Release --project Sandbox/Sandbox.Client   # in a second terminal
```

The server console shows the calls arriving (including the server→client reverse calls), and stops on ENTER. Run the server with `--tls` to enable DTLS with a self-signed certificate — it prints the SHA-256 fingerprint, which you pass to the client as `--tls <fingerprint>` for pinning.

## Documentation and Legacy

- Deeper documentation lives in the [`Document/`](Document/) Obsidian vault — start at [`Document/01-Overview/Home.md`](Document/01-Overview/Home.md); the full public API surface is [`Document/03-Reference/Public-API.md`](Document/03-Reference/Public-API.md), and production hardening guidance is [`Document/04-Guides/Production-Hardening.md`](Document/04-Guides/Production-Hardening.md).
- The pre-2.0 1.x codebase and its docs are archived under [`Legacy/`](Legacy/).
