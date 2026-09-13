# DS_MessageProtocol

A source-generated binary message serialization library for .NET, built for game servers and other allocation-sensitive applications. Declare message types with attributes; a Roslyn source generator emits `Serialize` / `Deserialize` implementations at compile time, and the runtime `MessageSerializer` provides registration, dispatch, and pooled-buffer hot paths.

- **Targets:** `netstandard2.1` (Unity-compatible) and `net6.0+`
- **Wire format:** compact binary headers (1–7 bytes) + little-endian payload, strict UTF-8 strings
- **Version:** 2.3.9

## Packages

| NuGet package | Description |
| ------------- | ----------- |
| **MessageProtocol** | Main package — the single entry point for applications. Contains the Core runtime DLL and ships the CodeGenerator as a Roslyn analyzer (`analyzers/dotnet/cs`). |
| **MessageProtocol.Core** | Serialization runtime API only (`MessageSerializer`, message contracts). Use when you don't need generated code. |
| **MessageProtocol.CodeGenerator** | Standalone Roslyn analyzer/source generator (netstandard2.0). For advanced or fine-grained reference scenarios. |

### Install

```bash
dotnet add package MessageProtocol
```

Core runtime only:

```bash
dotnet add package MessageProtocol.Core
```

**Unity:** the Unity Package Manager doesn't consume NuGet directly — copy the built DLLs (target `netstandard2.1`) from the packages into your project and reference them. See [Compatibility](#compatibility) for the Unity code-generation profile.

## Requirements

- A runtime supporting .NET Standard 2.1 or later (net6.0+), or Unity with a compatible API level.
- Message types must be declared `partial` so the generator can complete them.
- All attributes live in the `MessageProtocol` namespace (the serializer itself in `MessageProtocol.Serialize`).

## QuickStart

Install the main package:

```bash
dotnet add package MessageProtocol
```

Declare a message type:

```csharp
using MessageProtocol;

[Message(MessageKind.Standalone, 1)]
public partial class PlayerSpawn
{
    public int PlayerId { get; set; }
    public string? Name { get; set; }
    public List<int>? Inventory { get; set; }
}
```

Serialize and deserialize (`MessageSerializer` lives in the `MessageProtocol.Serialize` namespace):

```csharp
using MessageProtocol.Serialize;

var msg = new PlayerSpawn
{
    PlayerId = 7,
    Name = "host",
    Inventory = new List<int> { 1, 2, 3 },
};

byte[] bytes = MessageSerializer.Serialize(msg);
var decoded = MessageSerializer.Deserialize<PlayerSpawn>(bytes);
```

That's it — generated message types **register themselves** on module load via a generated `[ModuleInitializer]`; no manual registration is required. Allocation-sensitive send loops can use the pooled path instead of `Serialize` (see [Cautions](#cautions)):

```csharp
using (var pooled = MessageSerializer.SerializePooled(msg))
{
    // pooled.Span / pooled.Length — same wire bytes as Serialize
} // Dispose returns the buffer to the ArrayPool
```

Prefer zero bookkeeping? `[Message]` (no arguments) infers the message kind from the hierarchy and derives the ID from the type's full name — see [Message kinds and categories](#message-kinds-and-categories).

## Feature Guide

### Message kinds and categories

`[Message(MessageKind kind = Automatic, uint id = 0, MessageCategory category = Category0)]` is the single entry point for declaring a message:

| `MessageKind` | Meaning |
| ------------ | ------- |
| `Automatic` (default) | Kind **inferred** from the hierarchy (see below) |
| `Standalone` | Independent message (4-byte header) |
| `Parent` | Parent of a message family — the top of an inheritance hierarchy |
| `Child` | Child message — requires a `Parent` (or `[Message]`) base; manual id ≠ 0 |
| `NonId` | Message without an ID (1-byte header); must not take `id`/`category` arguments (`MSGPROT018`) |

Omitting `id` (or passing `0`) derives the ID from the type's **full-name hash** (see below); passing `id` assigns it manually (`Automatic` + `id` works too). The `category` nibble (0..15) is optional and defaults to `Category0` — use a single category member.

| Related attribute | Purpose |
| ----------------- | ------- |
| `[GenericMessage(typeof(Construction), ClassId = n)]` | Declares a closed generic construction; repeatable (`AllowMultiple`) |

Rules:

- ID values range `0 .. 2^24-1` (the wire ID is the 3 low bytes of the header). `ClassId` ranges `1 .. 2^24-1`.
- The composed wire MessageId (flags + category + value) must be **unique across your protocol** — duplicates are rejected at compile time (`MSGPROT014`) and would otherwise fail assembly load at runtime.
- Message types must be `partial`; hierarchy violations (element without root, root with root parent, …) are compile-time diagnostics.

Example group hierarchy and category:

```csharp
[Message(MessageKind.Parent, 10, MessageCategory.Category3)]
public partial class ShapeRoot { public string? Name { get; set; } }

[Message(MessageKind.Child, 11)]
public partial class Circle : ShapeRoot { public double Radius { get; set; } }
```

Generic messages close over specific constructions with a single attribute; declared constructions are auto-registered on module load for both sides:

```csharp
[Message(40)]
[GenericMessage(typeof(Envelope<PlayerSpawn>), ClassId = 1)]
[GenericMessage(typeof(Envelope<Circle>), ClassId = 2)]
public partial class Envelope<T>
{
    public T? Value { get; set; }
    public string? Note { get; set; }
}
```

#### `MessageKind.Automatic` — inferred kinds and hash IDs

`[Message]` with the default `Automatic` kind derives **both** the kind and the ID for you:

- **Kind inference**: a base in the hierarchy carrying `[Message]` → **Child**; otherwise, if another `[Message]` type in the same compilation derives from it → **Parent**; otherwise → **Standalone**.
- **Hash ID**: the ID is the FNV-1a 32-bit hash of the type's full name (BCL `Type.FullName` conventions: namespace dots, `+` for nesting, `` `n `` arity), masked to the wire's 24 bits. The algorithm and name format are **frozen** — renaming a type changes its ID; both peers must ship the same name.

```csharp
[Message]                                    // no derived [Message] types → Standalone
public partial class ChatText { public string? Text { get; set; } }

[Message]                                    // AutoEvent children derive → Parent
public partial class AutoEvent { public long Timestamp { get; set; } }

[Message]                                    // message-attributed ancestor → Child
public partial class PlayerJoined : AutoEvent { public int PlayerId { get; set; } }

[Message]                                    // same for PlayerLeft, …
public partial class PlayerLeft : AutoEvent { public string? Reason { get; set; } }
```

- **Collisions are compile errors, not re-hashes**: two hash-ID types whose full names hash to the same 24-bit value fail with `MSGPROT016` (rename one, or assign an explicit id: `[Message(id: …)]`). A child message whose hash resolves to `0` fails with `MSGPROT017`. IDs never silently change after you ship.
- **Cross-assembly derivation works**: in another project you can derive from a message base compiled elsewhere (e.g. a shared protocol DLL) — just apply `[Message]` to the derived type and the generator follows the referenced base chain, registering the new element.
- **Generic declarations**: `[Message]` on an open generic declaration replaces the declaration's MessageId with its hash; closed constructions still use the manual `[GenericMessage(typeof(...), ClassId = n)]` declarations shown above.
- **Non-public types are supported**: generated partials follow the declared accessibility (`internal` works).
- `MessageIdHash.FromFullName(name)` (runtime helper, same single source as the generator) computes the ID for a given full name — handy for diagnostics and tooling.

### Supported member types

| Category | Supported |
| -------- | --------- |
| Primitives | `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `char` |
| String | `string` (nullable allowed) |
| Enums | Serialized as their underlying primitive |
| Collections | 1-D arrays `T[]`, `List<T>`, `IList<T>` (element types follow these rules recursively); `byte[]` as raw data (length + contents) |
| Nested messages | Serializable objects as members (object graph) |
| Polymorphic members | Abstract message-typed members — the concrete element is written **with its header**, so the receiver restores the exact derived type |
| References | Cyclic and shared references round-trip via object-ID back-references (no infinite loops, identity preserved) |

Unsupported member types (e.g. `Dictionary<,>`, nullable value types) are rejected with a **compile-time diagnostic** (`MSGPROT006`) — nothing is silently skipped.

Serialization order = wire order: members are written in declaration order, following the base class chain from the root down. This order is part of wire compatibility — do not reorder members of a shipped message type.

### Member control

```csharp
[Message(MessageKind.Standalone, 4)]
public partial class MemberControl
{
    public int Kept { get; set; }

    [MessageIgnore]                 // excluded from serialization
    public int Skipped { get; set; }

    [MessageInclude]                // opt a non-public member in
    int _hidden;
}
```

Selection priority: `MessageIgnore` > `MessageInclude` > public accessibility. Static members and indexers are never serialized.

### Compile-time code generation

For every attributed `partial` message type the generator produces:

- `static Serialize(...)` / `static Deserialize(...)` implementations,
- a `MessageId` constant (for ID-carrying messages),
- a `[ModuleInitializer]` registration so types are usable with zero setup.

The generated text is deterministic. The generator also validates your protocol at compile time — key diagnostics:

| Diagnostic | Meaning |
| ---------- | ------- |
| `MSGPROT001` | Message type must be `partial` |
| `MSGPROT005` | ID value out of range (`0 .. 2^24-1`) |
| `MSGPROT006` | Unsupported member type |
| `MSGPROT010` | Type cannot be generated (abstract, or no parameterless constructor) |
| `MSGPROT012` | **Warning** — member declared as a *concrete* base with derived message types: derived members are silently dropped (make the base `abstract` to get polymorphic dispatch instead) |
| `MSGPROT013` | `MessageCategory` out of range (0..15) |
| `MSGPROT014` | Duplicate composed wire MessageId across two message types |
| `MSGPROT016` | full-name hash collision — rename one type or assign an explicit id (`[Message(id: …)]`) |
| `MSGPROT017` | child message hash resolved to `0` (reserved) — rename or assign an explicit id |
| `MSGPROT018` | `[Message]` arguments do not match the kind (`NonId` with `id`/`category`, or an undefined `MessageKind` value) |

### Runtime `MessageSerializer`

| API | Purpose |
| --- | ------- |
| `Serialize<T>(T)` | Generic cached path — no runtime-type dispatch, no boxing |
| `Serialize(object)` / `SerializeToWriter` | Runtime-type dispatch — polymorphism (base variable + derived instance) |
| `SerializePooled<T>(T)` / `SerializePooled(object)` | ArrayPool-backed result (`PooledBuffer`) — see Cautions |
| `Deserialize<T>(byte[] \| Span \| Memory)` | Generic deserialization |
| `Deserialize(byte[] \| Span \| Memory)` | Object dispatch by header MessageId (Standalone/Group only; `NonId` frames are rejected) |
| `DeserializeExact<T>(...)` | Strict variant — the frame must be consumed exactly; leftover bytes fail with `InvalidDataException` |
| `RegisterHasIdMessage<T>()` / `RegisterNonIdMessage<T>()` | Manual registration (including delegate overloads) |
| `RegisterType(Type)` | Reflection-based registration (manual implementations) |
| `RegisterGenericConstruction<T>(classId)` / `GetGenericClassId<T>()` | Generic construction registry |

Manual message implementation is supported: expose the same contract shape (`IMessageSerializable<T>` or `IHasIdMessageSerializable<T>`) and register it. With manual implementations you write the header yourself — header byte first, then the 3-byte ID.

### Wire format

| Layout | Header |
| ------ | ------ |
| Byte 0 | flags (high nibble) + category (low nibble) |
| Non-ID message | 1-byte header |
| ID message | 4-byte header (1 + 3-byte MessageId value) |
| Generic message | 7-byte header (1 + 3-byte MessageId + 3-byte construction ClassId) |

`MessageWireFormat` exposes header sizes, constants, and compose/parse helpers (`ComposeHeaderByte`, `ComposeMessageId`, `GetFlags`); `MessageFlag` lists the flag nibbles (`NonIdMessage`, `Standalone`, `Parent`, `Child`, `Generic`).

## Cautions

**`SerializePooled` returns a pooled buffer you must dispose.** The returned `PooledBuffer` owns a rented buffer — call `Dispose` after use (the buffer goes back to the pool). `Dispose` is idempotent and safe on any struct copy: copies share one ownership holder, so no matter which copy disposes, the pool return happens exactly once.

**Deserialization rejects untrusted input at the entry point — by design.** Foreign-type bytes, forged headers, out-of-range reference tags, invalid UTF-8, negative string-length prefixes (other than the `-1` null marker), oversized collection length prefixes, and invalid `decimal` flags all throw (`InvalidDataException` or similar) instead of quietly reinterpreting data. Nested object depth is capped per buffer — default **64** on both read and write; exceeding it throws `InvalidDataException` on read / `InvalidOperationException` on write. This guard prevents unrecoverable stack overflows from hostile or accidental deep/cyclic graphs. For legitimately deep graphs, raise the cap on **both sides**:

```csharp
var reader = new MessageBufferReader(buffer, maxNestingDepth);
var writer = MessageBufferWriter.Create(initialCapacity, maxNestingDepth);
```

**Category values: use a single `MessageCategory` member.** The category enum is `[Flags]`-style, but combining members (e.g. `Category1 | Category4`) is interpreted as *a different single category* on the wire — always use one named member (`Category0`..`Category15`).

**Concrete base-typed members drop derived members (warning `MSGPROT012`).** If a member's static type is a non-abstract message type that has derived message types, instances are written as the declared base — derived-only fields are lost without an exception. Declare the base `abstract` to switch to polymorphic runtime dispatch.

**No schema evolution.** Member layout is frozen once shipped: both peers must agree on member set and order (ADR-0006). Use `DeserializeExact` when you want trailing bytes to fail loudly instead of being tolerated as transport padding.

**`NonIdMessage` types cannot be routed by object deserialization.** `MessageSerializer.Deserialize(bytes)` (object dispatch) works for ID-carrying messages only; `NonId` frames are rejected with `InvalidDataException`. Use `Deserialize<T>` for those.

**Unity / netstandard2.1 profile.** On runtimes without `CollectionsMarshal` the generator emits a fallback (slower, allocation-equivalent) code path. This profile is validated by the `Test/MessageProtocol.NetStandardFixtures` project, which builds an assembly from that profile and executes round-trips against it.

## Benchmarks

Baseline measured with BenchmarkDotNet v0.15.8 (`MemoryDiagnoser`, in-process emit toolchain), .NET 9.0.12, Windows 11, AMD Ryzen 9 7940HS (2026-09-08, v2.3.1). The in-process toolchain suits *relative* comparison; treat absolute values as indicative (±10–15% run-to-run noise).

| Scenario | Workload | Median | Allocated |
| -------- | -------- | ------ | --------- |
| SerializeBytes | Typical message (int, long, float, 17-char string, `List<int>`×8) → `byte[]` | 54.6 ns | 104 B |
| SerializePooledFlat | Same message via `SerializePooled` (incl. release) | 57.3 ns | 32 B |
| DeserializeTyped | Generic deserialization of that message | 52.8 ns | 192 B |
| DeserializeDispatch | Object-dispatch deserialization | 71.6 ns | 192 B |
| SerializeStringHeavy | 4×100-char ASCII strings | 138.1 ns | 448 B |
| DeserializeStringHeavy | Above, deserialized | 168.6 ns | 944 B |
| SerializeSharedGraph | Depth-5 chain + shared subtree (back-references) | 288.3 ns | 528 B |
| DeserializeSharedGraph | Above, deserialized | 287.3 ns | 872 B |
| SerializeLargeCollections | `List<int>`×100k (bulk copy) + `string[]`×1k | 71 µs | 411 KB |
| DeserializeLargeCollections | Above, deserialized | 134 µs | 448 KB |

Highlights:

- **Generic hot path has no dictionary lookups and no boxing** — static per-type caches.
- `SerializePooled` cuts allocations from 104 B → 32 B (only the ownership holder) at equal speed on the same workload; it also avoids the exact-size result copy on large payloads.
- Most of the `byte[]` path's allocation is the exact-size result copy itself.
- Reference-tracked graphs (shared/cyclic) cost ~5× a flat message per node (tag bytes + context bookkeeping) — plan message shapes accordingly.
- Deserialization validates collection lengths and `decimal` flags *before* allocating, so hostile frames can't trigger huge allocations.

### Comparison with MemoryPack and MessagePack-CSharp

Head-to-head measurement (2026-09-09): DS_MessageProtocol vs **MemoryPack 1.21.4** vs **MessagePack-CSharp 3.1.4** (StandardResolver, standard configuration) — same machine, same BenchmarkDotNet job: AMD Ryzen 9 7940HS, Windows 11, .NET 9.0.12, BenchmarkDotNet v0.15.8 (DefaultJob + MemoryDiagnoser). Workloads mirror the four fixtures above: flat (scalars + `List<int>`×8), string-heavy (4×100-char strings), object graph (depth-5 chain), large collections (`List<int>`×100k + `string[]`×1k).

**Verdict: DS_MessageProtocol is fastest or tied in every measured scenario** — there was no scenario where a competitor won on speed. Baseline allocations and wire size are on par, and shared/cyclic graph support, the built-in message header (framing + type check), and the zero-allocation `SerializePooled` path are DS-only.

Speed (ns/op unless noted; lower is better; bold = winner):

| Shape | Operation | DS_MessageProtocol | MemoryPack | MessagePack-CSharp |
| ----- | --------- | -----------------: | ---------: | -----------------: |
| Flat | Serialize | **65.4** | 94.6 | 88.8 |
| Flat | Deserialize | **39.4** | 54.6 | 121.0 |
| String-heavy | Serialize | **114.6** | 115.6 | 161.5 |
| String-heavy | Deserialize | 125.1 | **119.4** | 242.8 |
| Graph | Serialize | **180.2** (shared) | 201.5 (tree) | 461.8 (tree) |
| Graph | Deserialize | **192.5** (shared) | 272.1 (tree) | 639.1 (tree) |
| Large | Serialize | **48.0 µs** | 67.4 µs | 225.9 µs |
| Large | Deserialize | **58.3 µs** | 58.8 µs | 537.2 µs |

- On flat and graph shapes DS is 1.1–1.45× faster than MemoryPack and 1.4–3.1× faster than MessagePack-CSharp across the board.
- DS wins the graph shape **while performing reference tracking** (back-reference tags) — the competitors do no reference tracking at all: MemoryPack and MessagePack-CSharp have no shared/cyclic graph support (MessagePack removed its preserve-reference API in 3.1.4), so they serialize a tree variant of the fixture.
- Large collections: DS ≈ MemoryPack (leadership alternates between runs — treat as tied); MessagePack-CSharp is 4–5× slower serializing and ~9× slower deserializing.

Allocations (B/op, GC-counter measurement):

| Shape | Serialize / Deserialize | DS_MessageProtocol | MemoryPack | MessagePack-CSharp |
| ----- | ---------------------- | ------------------: | ---------: | -----------------: |
| Flat | Ser / De | 104 / 192 | 104 / 192 | 64 / 192 |
| String-heavy | Ser / De | 448 / 944 | 464 / 944 | 440 / 944 |
| Graph | Ser / De | 528 / 872 | 176 / 792 | 96 / 792 |
| Large | Ser / De | 410,928 / 448,033 | 414,928 / 448,067 | **989,168** / 448,049 |

- Baseline allocations are effectively identical across all three ("output buffer + restored objects"); DS's +352 B on graph serialize is the price of reference tracking.
- MessagePack-CSharp allocates 2.4× on large-collection serialize (internal buffer growth strategy).
- Only DS offers a zero-allocation send path (`SerializePooled`).

Wire size (bytes):

| Shape | DS_MessageProtocol | MemoryPack | MessagePack-CSharp |
| ----- | -----------------: | ---------: | -----------------: |
| Flat | 77 (incl. 4B header) | 78 | **39** |
| String-heavy | **420** (UTF-8) | 433 (UTF-16) | 409 |
| Shared graph | **60** (sharing saves) | — (unsupported) | — (unsupported) |
| Large | 410,902 | 414,899 | 489,117 |

- MessagePack-CSharp's varint halves the wire for small-integer messages (39 vs 77) but loses on strings and large collections.
- MemoryPack stores strings as UTF-16, costing bytes on non-ASCII text.
- DS figures include the 4-byte message header; competitor figures are payload only — in production they still pay for length-prefix framing and a type ID on top.

Library notes: MemoryPack is a fastest-class rival with good Unity support, but no reference tracking and a UTF-16 wire. MessagePack-CSharp has the largest ecosystem, `[Key]`-based schema evolution (appending trailing fields), and varint strength — but was slowest here, allocates 2.4× on large serialize, and triggers NuGet security advisories (NU1902/NU1903) on 3.1.4.

Fairness notes: laptop environment, single representative run (±2× run-to-run variance observed on the large shape — trust the tied/faster verdicts, not absolute values); 21 of 24 speed items ran in the same process/job, the 3 string-heavy deserialize items ran in a separate run on the same machine/job. Full methodology and raw data: `Document/04-Improvements/Performance-Comparison.md`.

Reproduce the single-library baseline:

```bash
dotnet run -c Release --project Test/MessageProtocol.Benchmarks
```

The tables above are the curated baseline from `Document/03-Reference/Performance-Baseline.md` (2026-09-08, v2.3.1) — treat that document as authoritative. Raw BenchmarkDotNet output also lives under `BenchmarkDotNet.Artifacts/results/` and `artifacts/bench-compare/`, but those are working artifacts: later runs overwrite them in place, so a raw file may contain fewer or different scenarios than the curated table (the current baseline artifact holds only a 3-scenario spot-check with different absolute values). The head-to-head comparison tables above are transcribed from `Document/04-Improvements/Performance-Comparison.md`, which is the authoritative comparison record.

## Performance Contract

The implementation maintains these properties on hot paths (verified by benchmarks on change):

- Generic serialize/deserialize: no dictionary lookups, no boxing (static caches).
- `ArrayPool`-backed buffers (`SerializePooled` / `PooledBuffer`).
- Span-based reads/writes; strings decoded without intermediate arrays; `decimal` via an allocation-free path.
- Generated code batches capacity reservation for fixed-size primitive runs.
- The nesting-depth guard costs two inline increments per nested object — zero for flat messages, no allocations.
- Writer growth uses `long` arithmetic with headroom clamping, preserving geometric growth beyond 1 GB; over-limit demands fail without attempting allocation.

## Compatibility

- Targets `netstandard2.1` + `net6.0` — Unity-compatible.
- `ModuleInitializer` polyfill included for older toolchains.
- Tests run on `net8.0` and `net9.0`; the netstandard2.1 fallback profile is executed (not just text-asserted) via the NetStandardFixtures project.

## Repository

| Path | Contents |
| ---- | -------- |
| `Source/` | Product code — Core (runtime), CodeGenerator (analyzer), MessageProtocol (meta package), Shared (wire rules) |
| `Test/` | Spec-based unit tests · BenchmarkDotNet benchmarks · netstandard2.1 fixtures |
| `Sandbox/` | Executable acceptance scenarios (42 checks; exit code 0 on pass): round-trips, dispatch, generics, depth guards, trust-boundary rejections |
| `Document/` | Documentation vault — start at `Document/01-Overview/Home.md`; feature spec (`02-Architecture/Feature-Spec.md`), public API reference (`03-Reference/Public-API.md`), performance baseline (`03-Reference/Performance-Baseline.md`) |
| `Legacy/` | v1 reference implementation (read-only) |

Source and issues: [github.com/BIGSUNGG/DS_MessageProtocol](https://github.com/BIGSUNGG/DS_MessageProtocol)

Related sibling projects: **DS_Communication** (network transport) and **DS_RPC** (distributed RPC, built on both).
