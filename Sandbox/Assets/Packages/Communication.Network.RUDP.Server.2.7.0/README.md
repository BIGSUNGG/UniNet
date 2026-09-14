# DS_Communication

Connection-oriented transport layer library for .NET. It provides **Session**, **Channel**, **Framing**, and **Pipeline**, and leaves serialization entirely to the application via the `IMessageConverter` interface — the library ships no built-in serializer (pair it with [DS_MessageProtocol](https://github.com/BIGSUNGG/DS_MessageProtocol) or any `IBufferWriter<byte>`-based formatter). Targets **.NET Standard 2.1** and is Unity compatible.

Two transports are implemented today:

- **TCP** — `NetworkStream`/`SslStream` byte streams with 4-byte little-endian length-prefix framing. Optional TLS via `SslStream`.
- **RUDP** — reliable/unordered/sequenced delivery over UDP (LiteNetLib 2.1.4, hidden behind library-owned types). Optional DTLS 1.2 (BouncyCastle) and optional CRC32c packet integrity.

Sister projects: **DS_MessageProtocol** (serialization) and **DS_RPC** (distributed RPC, built on both).

## Packages

| Package | Contents | Dependencies |
| --- | --- | --- |
| `Communication.Shared` | `Session`/`ISession`, `MessagePipeline`, `IByteChannel`/`IMessageChannel` contracts, `LengthPrefixFramer`/`LengthPrefixFrameReader`, `SendOptions`, `DisconnectReason` | none |
| `Communication.Network.TCP.Shared` | `TcpSession`, `StreamByteChannel`, `TcpTransportOptions`, `TcpTlsOptions`, `SocketKeepAliveOptions` | Shared |
| `Communication.Network.TCP.Server` | `TcpListener` accept loop | TCP.Shared |
| `Communication.Network.TCP.Client` | `TcpConnector` connect | TCP.Shared |
| `Communication.Network.RUDP.Shared` | `RudpSession`, `RudpMessageChannel`, `RudpSendOptions`/`RudpDeliveryMethod`, `RudpTransportOptions`, `RudpTlsOptions` (internal LiteNetLib/BouncyCastle hosts) | Shared, LiteNetLib 2.1.4, BouncyCastle.Cryptography 2.7.0 |
| `Communication.Network.RUDP.Server` | `RudpListener` accept loop | RUDP.Shared |
| `Communication.Network.RUDP.Client` | `RudpConnector` connect | RUDP.Shared |

## Installation

```console
dotnet add package Communication.Shared
dotnet add package Communication.Network.TCP.Server   # TCP server apps
dotnet add package Communication.Network.TCP.Client   # TCP client apps
dotnet add package Communication.Network.RUDP.Server  # RUDP server apps
dotnet add package Communication.Network.RUDP.Client  # RUDP client apps
```

Install only the side you need; `Server` and `Client` each pull in the matching `.Shared` package transitively. LiteNetLib and BouncyCastle are referenced only by `Communication.Network.RUDP.Shared` and their types never appear in the public API.

## Quick Start

A converter and a handler are all you write. Serialization format is your choice — the example uses JSON for brevity.

```csharp
using System.Buffers;
using System.Text.Json;
using Communication.Shared.Messages;
using Communication.Shared.Sessions;

public sealed class ChatMessage { public string Text { get; set; } = ""; }

// Injected by the app — the library has no built-in serializer.
public sealed class JsonChatConverter : IMessageConverter
{
    public void Serialize(object message, IBufferWriter<byte> writer) =>
        writer.Write(JsonSerializer.SerializeToUtf8Bytes(message, message.GetType()));

    public object Deserialize(ReadOnlySpan<byte> message) =>
        JsonSerializer.Deserialize<ChatMessage>(message) ?? new ChatMessage();
}

// Type-registered synchronous dispatcher.
public sealed class ChatHandler : MessageHandler
{
    public ChatHandler(ISession session) : base(session) =>
        Register<ChatMessage>(m => Console.WriteLine($"recv: {m.Text}"));
}
```

### TCP server + client

```csharp
using System.Net;
using Communication.Network.TCP;
using Communication.Shared.Sessions;

// Server — the accept loop hands out channels; the app creates sessions.
using var listener = new TcpListener(IPAddress.Any, 32000);
listener.Accepted += channel =>
{
    var session = new TcpSession(channel, new JsonChatConverter(), s => new ChatHandler(s));
    session.Disconnected += (_, e) => Console.WriteLine($"peer left: {e.Reason}");
};
listener.Start(new TcpTransportOptions { MaxConnections = 1000 });

// Client — ConnectAsync returns bool; the channel is exposed afterwards, session is the app's.
var connector = new TcpConnector();
if (!await connector.ConnectAsync("127.0.0.1", 32000)) return;

using var client = new TcpSession(connector.Channel!, new JsonChatConverter(), s => new ChatHandler(s));
client.Disconnected += (_, e) => Console.WriteLine($"disconnected: {e.Reason}");

await client.SendAndFlushAsync(new ChatMessage { Text = "hello" }); // waits until written to the wire
```

### RUDP server + client

```csharp
using System.Net;
using Communication.Network.RUDP;
using Communication.Shared.Sessions;

// Server — same ownership rule as TCP: the app creates sessions in Accepted.
// Accepted channels are IMessageChannel; create the session synchronously inside
// the callback (message channels do not buffer messages received before you subscribe).
using var listener = new RudpListener(IPAddress.Any, 32000);
listener.Accepted += channel =>
{
    var session = new RudpSession(channel, new JsonChatConverter(), s => new ChatHandler(s));
    session.Disconnected += (_, e) => Console.WriteLine($"client left: {e.Reason}");
};
listener.Start(new RudpTransportOptions { MaxConnections = 100, ConnectionKey = "my-app-key" });
Console.WriteLine($"listening on {listener.LocalPort}");

// Client
var connector = new RudpConnector();
if (!await connector.ConnectAsync("127.0.0.1", 32000,
        new RudpTransportOptions { ConnectionKey = "my-app-key" })) return;

using var client = new RudpSession(connector.Channel!, new JsonChatConverter(), s => new ChatHandler(s));

// Per-message delivery — pass RudpSendOptions (shared instances, zero allocation).
// Omitting options sends as ReliableOrdered.
await client.SendAsync(new ChatMessage { Text = "chat" }, RudpSendOptions.ReliableOrdered);
await client.SendAsync(new ChatMessage { Text = "!" },  RudpSendOptions.Unreliable);
await client.SendAndFlushAsync(new ChatMessage { Text = "final" }); // default: ReliableOrdered
```

Runnable end-to-end samples live in [`Sandbox/Chat.TCP`](Sandbox/Chat.TCP/) and [`Sandbox/Chat.RUDP`](Sandbox/Chat.RUDP/) (both support a `--selftest` in-process round-trip mode). TCP-specific behavior is detailed in [TCP.md](TCP.md), RUDP in [RUDP.md](RUDP.md).

## Features

### Session lifecycle

- Connectors/listeners only open channels (`ConnectAsync` → `bool`, then `Channel`; `Accepted` event). The **application creates every session** (`new TcpSession(channel, converter, handlerFactory)`), injecting converter, handler factory, and queue options (ADR 0006).
- Disconnect is observed through exactly one event: `Session.Disconnected` with `DisconnectReason` — `Local`, `Remote`, `Error`, `Timeout`, or `FlowControl` (plus `DisconnectedEventArgs.Exception` where applicable). Fires exactly once per session; a subscriber added after the disconnect is replayed once immediately, so late subscription can never miss it. Subscriber exceptions are isolated.
- `session.Disconnect()` and `Dispose()` produce `Disconnected(Local)`. Sending on a disconnected (or not-yet-attached) session returns an already-faulted `Task` — it never throws synchronously and never silently drops.
- **No reconnect, no heartbeat.** After `Disconnected`, the app reconnects (`ConnectAsync` + a new session; on the server side, a fresh session per accept) and implements ping/keep-alive at the application level. TCP keep-alive is available as a user-configured socket option.

### Serialization injection (`IMessageConverter`)

- `void Serialize(object message, IBufferWriter<byte> writer)` — writes into pooled buffers; no per-message `byte[]` on the send path.
- `object Deserialize(ReadOnlySpan<byte> message)` — span is valid only for the call.
- You own the format and its safety: never use type-selecting serializers (`BinaryFormatter`, `TypeNameHandling.All`, …) on untrusted input. See [Security](Document/04-Guides/Security.md).

### Framing and frame limits (byte channels)

- Wire format: 4-byte little-endian length prefix + payload (`LengthPrefixFramer`).
- `MessageQueueOptions.MaxFrameLength` (default **4 MB**, absolute ceiling 64 MB) — oversized frames are isolated on send and rejected with an `Error` disconnect on receive. Message channels (RUDP) enforce the same limit before deserialization.
- The receive buffer grows only with actually accumulated bytes (never pre-allocates from a declared length), defending against memory-amplification attacks.
- `MessageQueueOptions.FrameTimeout` (default **30 s**, from the first byte of a frame) — an incomplete frame disconnects with `DisconnectReason.Timeout` (slowloris defense). Fully idle connections are not affected; heartbeat remains an app concern.

### Send queue, backpressure, and coalesce batching

- Sends are queued; at `MessageQueueOptions.MaxPendingMessages` (default **10,000**) the sender asynchronously waits for space — no drops, no exceptions.
- Byte-channel sends are coalesced: queued frames are batched into a single channel write up to `CoalesceLimitBytes` (default **65,536** bytes; a batch may overshoot by at most one frame).
- A failing serialization or length check isolates only that item — its `SendAndFlushAsync` faults, slots are returned, and the send loop keeps running. A send failure never tears down the session.
- `SendAndFlushAsync` completes only after the message is on the wire; `SendAsync` is the fire-and-forget queueing path.

### Receive path

- Byte channels: EOF at a frame boundary → `Disconnected(Remote)`; mid-frame EOF or malformed length (≤ 0, over limit) → `Disconnected(Error)`.
- Message channels: payloads arrive with boundaries preserved (no framer); the over-limit pending-receive guard force-closes with `DisconnectReason.FlowControl` instead of accumulating without bound.
- Deserialized messages go to your `IMessageHandler`. Handlers are synchronous (`void HandleMessage(object)`); `MessageHandler` provides concurrent-safe `Register<T>()` and falls back to the most specific registered base type/interface. Handler exceptions are traced and isolated — the receive loop survives.
- `MessageQueueOptions.InlineDispatch` (default `false`) runs handlers directly on the receive loop for hot paths; the message-channel (RUDP) path always queues because receive callbacks share the transport's polling thread.

### Flow control

- Message-channel (RUDP) receive path counts slot-waits toward `MaxPendingMessages` and closes with `FlowControl` when exceeded. The byte-channel path applies backpressure by pausing reads. Either way, memory use stays inside declared limits.

## Security overview

Transport security is optional and opt-in; plain text remains the default for backward compatibility.

- **TCP TLS (`SslStream`)** — set `TcpTransportOptions.Tls` (`TcpTlsOptions`). The server sets `ServerCertificate`; the TLS handshake completes before the channel reaches `Accepted`/`Channel`. Client certificate validation defaults to OS policy; `TargetHost` and `RemoteCertificateValidation` allow overrides (never install an always-true callback). `HandshakeTimeout` defaults to 15 s (slowloris defense); failed/timed-out handshakes close the connection without affecting the accept loop.
- **RUDP packet integrity (CRC32c)** — `RudpTransportOptions.Crc32cEnabled` (default `false`; **both ends must match** — wire-incompatible). Each packet carries a CRC32c checksum and checksum-violating packets (corrupted or forged) are discarded before protocol processing. Detection only: an unkeyed CRC can be recomputed by an active attacker; no confidentiality or authentication.
- **RUDP connection key** — `RudpTransportOptions.ConnectionKey` gates connection requests. The default value `"DS_Communication.RUDP"` is a public constant; starting a server with it logs a Trace warning. Replace it per app on public networks (it is a filter, not authentication).
- **RUDP DTLS** — set `RudpTransportOptions.Tls` (`RudpTlsOptions`, BouncyCastle, DTLS 1.2). The handshake runs on the established connection before the channel is delivered. The client must provide pinning (`RemoteCertificateValidation`, e.g. via `RudpTlsOptions.GetSha256Fingerprint`, recommended) or `TargetHost` name matching — name matching alone now requires an explicit opt-in (`AllowNameOnlyCertificateMatch = true`, 2.7.0+; without it the certificate is rejected, and even when opted in the certificate validity period is enforced, since name-only matching cannot stop a self-signed MITM). With neither, the server certificate is rejected by default (fail-closed). Messages over 16,381 bytes travel only as `ReliableOrdered` (internal chunking, 64 MB reassembly ceiling).

Details: [Document/04-Guides/Security.md](Document/04-Guides/Security.md) · TCP TLS: [TCP.md](TCP.md#security-tls-via-sslstream) · RUDP security: [RUDP.md](RUDP.md#security-crc32c-connection-key-and-dtls) · ADRs [0008](Document/05-Decisions/0008-tcp-tls-sslstream.md), [0009](Document/05-Decisions/0009-rudp-tls-dtls.md).

## Documentation

| Document | Contents |
| --- | --- |
| [TCP.md](TCP.md) | TCP usage and behavior — listener, connector, options, TLS, lifecycle, full example |
| [RUDP.md](RUDP.md) | RUDP usage and behavior — delivery methods, options, polling thread model, DTLS, full example |
| [Document/03-Reference/Public-API.md](Document/03-Reference/Public-API.md) | Full public API contract |
| [Document/03-Reference/Configuration.md](Document/03-Reference/Configuration.md) | Every option and default |
| [Document/02-Architecture/Overview.md](Document/02-Architecture/Overview.md) | Architecture — layers, channels, pipeline ([Session](Document/02-Architecture/Session.md), [Pipeline](Document/02-Architecture/Pipeline.md), [Channel](Document/02-Architecture/Channel.md), [Handler](Document/02-Architecture/Handler.md)) |
| [Document/04-Guides/Getting-Started.md](Document/04-Guides/Getting-Started.md) | More usage patterns (keep-alive, reconnect loop, heartbeat) |
| [Document/04-Guides/Security.md](Document/04-Guides/Security.md) | Converter safety constraints and production checklist |
| [Document/05-Decisions/](Document/05-Decisions/) | ADRs 0001–0009 — why the stack is shaped this way |

## Repository layout

| Path | Contents |
| --- | --- |
| `Source/` | The 7 library packages |
| `Test/Communication.Tests` | xUnit tests (`dotnet test`) |
| `Sandbox/Chat.TCP` | TCP chat sample (`--selftest` for in-process verification) |
| `Sandbox/Chat.RUDP` | RUDP chat sample (`--selftest`, `--tls-selftest`, `--bench`) |
| `Document/` | Documentation vault — entry point [Document/01-Overview/Home.md](Document/01-Overview/Home.md) |
| `Legacy/` | Archive of the previous stack; not maintained |

## Release

GitHub Actions (`nuget-publish.yml`): a `v*` tag publishes `Communication.Shared`, `tcp/v*` the three TCP packages, `rudp/v*` the three RUDP packages (current version 2.6.0).
