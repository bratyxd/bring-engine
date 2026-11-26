# Agent Guide 16 — Core Networking (HTTP/HTTPS, WebSockets) & Multiplayer

> **Scope**: Using Bring Engine's lean, zero-dependency `BringEngine.Networking` subsystem (`HttpSystem`, `WebSocketSystem`, `NetWriter`/`NetReader`, `NetTransportVTable`, `LoopbackTransportSystem`) and integrating game-level real-time multiplayer NuGet packages (`LiteNetLib`, `Backdash`, `Facepunch.Steamworks`) without bloating core engine code.

---

## 1. Architecture Philosophy — Lean Core + Pluggable Multiplayer

Bring Engine keeps networking **strictly lean** inside core `BringEngine`:
- **Built into `BringEngine.Networking` (0 extra NuGet dependencies, 0 extra native binaries)**:
  - **`HttpSystem` (`HttpRequestHandle`)** — Async non-blocking HTTP/HTTPS (`GET`/`POST`/`PUT`/`DELETE`/`PATCH`/`HEAD`, headers, TLS 1.2/1.3, automatic GZip/Brotli, timeouts, cancellation) backed by `.NET 10`'s pooled `SocketsHttpHandler`.
  - **`WebSocketSystem` (`WebSocketHandle`)** — Persistent bidirectional `ws://` & `wss://` connections (lobbies, matchmaking, chat, casual/turn-based multiplayer, signaling, live-ops) backed by `.NET 10`'s `ClientWebSocket`, with multi-frame RFC 6455 message reassembly.
  - **`NetWriter` / `NetReader` / `NetHash.Crc32`** — Zero-GC unmanaged little-endian binary wire serialization with first-class support for `Fix32`, `Fix64`, `FixVec2`, `FixVec3`, `FixQuat`, `EntityId`, `StringId`, `FixedString64/256`, varints, and bounds-checked overflow flags.
  - **`NetTransportVTable` + `LoopbackTransportSystem`** — Zero-OOP function-pointer transport contract (`delegate*<...>`, no C# interfaces) plus a deterministic in-memory loopback transport (configurable latency, jitter, packet loss) for `--headless` CI multiplayer tests.
- **Game-Level Responsibility (add only the NuGet your game actually needs)**:
  - Real-time client-server or P2P reliable UDP → **`LiteNetLib` (2.x)**
  - Deterministic 2–4 player P2P rollback netcode → **`Backdash`**
  - Steam / Steam Deck P2P relays & lobbies → **`Facepunch.Steamworks`** (`SteamNetworkingSockets`)
  - Custom protocols → `.NET 10` `System.Net.Sockets.Socket` or any CoreCLR NuGet package.

### What NOT to Use
- **Do NOT use `SDL3_net`**: No TLS/HTTPS/WSS, no HTTP/WebSockets, no reliable UDP, and not bundled in `SDL3-CS` (would break cross-OS ReadyToRun publishing).
- **Do NOT rely on `System.Net.Quic` (HTTP/3) for cross-platform desktop games**: Native `MsQuic` is not officially supported on macOS and requires external `libmsquic` on Linux/SteamOS.

---

## 2. Two-Zone Async Architecture, Threading & Determinism Rules

Bring Engine uses a **Two-Zone Async Architecture** so core engine code and frame loops stay strictly Zero-OOP, Zero-GC, and deterministic while games with heavy live-ops can still use idiomatic `.NET 10` async tooling cleanly:

| Zone | Where It Applies | Pattern to Use | Hard Rules |
|---|---|---|---|
| **Zone 1 — Handle + Frame Polling** | **All core `BringEngine` APIs**, `Simulation/`, `View/`, CPU background jobs, simple 1–2 step HTTP calls, WebSockets, and real-time packet polling | **32-bit Generational Handle + Native Mailbox + Zero-GC Frame Poll** (`HttpSystem`, `WebSocketSystem`, `JobSystem`, `NetTransportVTable`) | **0 GC** on Logic/Render threads; **0 `async`/`await` or `Task<T>`**; **0 managed closures**; always call `ReleaseResponse`/`ReleaseEvent` and `Stop` |
| **Zone 2 — Quarantined Cold-Path `async`/`await`** | **Game-level heavy multi-step cold-path orchestration** (`src/<Game>/Services/`): login + OAuth/JWT refresh, exponential retries, parallel catalog/config/events fetch, IAP receipt verification, CDN patch downloads, or `Task`-based SDKs (PlayFab, Nakama, EOS, Discord) | Standard C# `async`/`await` + `Task` using `.NET 10` `HttpClient` or the service's NuGet SDK on background threads with a shutdown `CancellationToken` | Background tasks MUST **NEVER** read/mutate `GameSimState`, `EngineWorld`, `EngineAllocators`, or SDL3/GPU directly — publish plain unmanaged result structs / immutable snapshots into a **thread-safe mailbox** polled once per frame on the Logic Thread (0 GC) |

```text
[Zone 2: Game Services/ (async/await, Task, retries, OAuth, SDKs)]
                            │
           (plain unmanaged result / snapshot)
                            ▼
            [Thread-Safe Mailbox / Ring Buffer]
                            │
                (Poll once per frame — 0 GC)
                            ▼
[Zone 1: Logic Thread — Simulation/ & View/ (Zero-OOP, deterministic)]
```

### Mandatory Boundary Checklist
1. **Keep sockets and `Task`s completely outside `Simulation/`**:
   - `Simulation/` (`GameSimSystems.StepFixed`) MUST stay pure deterministic unmanaged math with zero socket or I/O calls so `--headless` verification and replays always work offline.
   - Poll `HttpSystem`, `WebSocketSystem`, your multiplayer transport, or your `Services/` mailbox at the **start of the Logic Thread tick**, decode into plain unmanaged input/command structs, and pass those into `Simulation/`.
2. **Allocate engine networking state in `EngineAllocators.Persistent`**:
   - Like `JobScheduler` and `EnginePipelineState`, allocate `HttpClientState*` and `WebSocketClientState*` in stable pinned native memory (`engAlloc.Persistent.Alloc<...>(1, 64)`), call `Start(...)` once at startup, and call `Stop(...)` cleanly **before** `engAlloc.Dispose()`.
3. **Zero-GC Logic Thread Polling & Zero Native Leaks**:
   - `HttpSystem.PollCompleted`, `HttpSystem.IsPending`, `HttpSystem.ReleaseResponse`, `WebSocketSystem.SendBinary`, `WebSocketSystem.SendText`, `WebSocketSystem.PollEvent`, and `WebSocketSystem.ReleaseEvent` allocate **0 managed GC bytes** on the calling thread.
   - **ALWAYS** call `HttpSystem.ReleaseResponse(ref resp)` and `WebSocketSystem.ReleaseEvent(ref ev)` after consuming payloads so `NativeMemoryTracker.LiveBytes == 0`. (`Stop()` also drains and frees any unpolled buffers safely.)
4. **Never use `async`/`await` inside `StepFixed` or `OnViewSync`, and never wrap `HttpSystem` back into `Task<T>`**:
   - If a game's `Services/` layer wants `Task`-based HTTP with retries/OAuth, use `.NET 10`'s pooled `HttpClient` directly inside `Services/` rather than double-wrapping `HttpSystem`.

---

## 3. Core API Usage

### A. Async HTTP / HTTPS (`HttpSystem`)
```csharp
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Networking;

// Startup (allocate in Persistent arena):
HttpClientState* http = engAlloc.Persistent.Alloc<HttpClientState>(1, 64);
HttpSystem.Start(http, ref engAlloc.Persistent, maxConcurrent: 32, defaultTimeoutMs: 10000);

// Fire-and-forget request with custom headers (non-blocking):
HttpHeader* hdrs = stackalloc HttpHeader[1];
hdrs[0] = HttpHeader.Create("Authorization", "Bearer my-token");
HttpRequestHandle req = HttpSystem.PostJson(
    http,
    "https://api.example.com/v1/scores",
    "{\"score\":4200}"u8,
    headers: new ReadOnlySpan<HttpHeader>(hdrs, 1),
    userTag: 1UL);

// Cancel anytime if the player leaves the screen:
// HttpSystem.Cancel(http, req);

// Logic Thread (poll every frame — 0 GC):
while (HttpSystem.PollCompleted(http, out HttpResponse resp))
{
    try
    {
        if (resp.IsSuccess)
        {
            ReadOnlySpan<byte> utf8Body = resp.AsBodySpan();
            // Parse JSON via System.Text.Json Utf8JsonReader or source-generated JsonSerializerContext
        }
    }
    finally
    {
        HttpSystem.ReleaseResponse(ref resp); // MUST free native BodyPtr!
    }
}

// Shutdown (before engAlloc.Dispose()):
HttpSystem.Stop(http);
```

### B. Persistent WebSockets (`WebSocketSystem`)
```csharp
using BringEngine.Core.Handles;
using BringEngine.Networking;

// Startup:
WebSocketClientState* ws = engAlloc.Persistent.Alloc<WebSocketClientState>(1, 64);
WebSocketSystem.Start(ws, ref engAlloc.Persistent, maxConnections: 8);

// Connect asynchronously (ws:// or wss://):
WebSocketHandle sock = WebSocketSystem.Connect(
    ws,
    "wss://lobby.example.com/ws",
    subProtocol: "game.v1",
    keepAliveMs: 20000);

// Send binary or UTF-8 text from Logic Thread (0 GC):
byte* pkt = engAlloc.Frame.AllocUninitialized<byte>(64, 16);
var writer = NetWriter.Create(pkt, 64);
writer.WriteByte(1); // opcode
writer.WriteFixVec3(in playerPos);
WebSocketSystem.SendBinary(ws, sock, writer.WrittenSpan());

// Poll incoming events on Logic Thread (0 GC):
while (WebSocketSystem.PollEvent(ws, out WebSocketEvent ev))
{
    try
    {
        switch (ev.Kind)
        {
            case WebSocketEventKind.Connected:
                break;
            case WebSocketEventKind.BinaryMessage:
                var reader = NetReader.Create(ev.DataPtr, ev.DataLength);
                byte opcode = reader.ReadByte();
                if (!reader.HasOverflow) { /* apply command */ }
                break;
            case WebSocketEventKind.TextMessage:
                ReadOnlySpan<byte> utf8 = ev.AsDataSpan();
                break;
            case WebSocketEventKind.Closed:
            case WebSocketEventKind.Error:
                break;
        }
    }
    finally
    {
        WebSocketSystem.ReleaseEvent(ref ev); // MUST free native DataPtr!
    }
}

// Graceful close & shutdown:
WebSocketSystem.Close(ws, sock, closeStatus: 1000, reason: "quit");
WebSocketSystem.Stop(ws);
```

### C. Zero-GC Binary Serialization (`NetWriter` / `NetReader`) & JSON
- **Binary packets**: Use `NetWriter` / `NetReader` over `EngineAllocators.Frame` or `stackalloc byte[...]` buffers.
  - Supports little-endian primitives, `VarUInt32`/`VarInt32` (ZigZag LEB128), `Fix32`, `Fix64`, `FixVec2`, `FixVec3`, `FixQuat`, `EntityId`, `StringId`, `FixedString64`, `FixedString256`, length-prefixed `Bytes`, and `NetHash.Crc32`.
  - Never throws on corrupted/truncated untrusted network data — check `reader.HasOverflow` and drop malformed packets cleanly.
- **JSON (HTTP/WebSocket APIs)**: Use `.NET 10`'s built-in `System.Text.Json` with **`Utf8JsonReader`** (zero-GC) or **Source Generators** (`[JsonSerializable(typeof(MyDto))]`) — avoid reflection-heavy serializers.

### D. Deterministic Headless Multiplayer Testing (`LoopbackTransportSystem`)
Test packet loss, latency, ordering, and state sync inside `--headless` CI tests with **0 real sockets** and **0 GC**:
```csharp
var cfg = new LoopbackSimConfig
{
    OneWayLatencyTicks = 3, // ~50 ms one-way at 60 Hz
    JitterTicks = 2,
    PacketLossPermille = 50, // 5% unreliable drop
    RngSeed = 0x12345UL,
};
LoopbackLink* link = gameAlloc.Persistent.Alloc<LoopbackLink>(1, 64);
LoopbackTransportSystem.Init(link, ref gameAlloc.Persistent, in cfg);

NetTransportVTable client = LoopbackTransportSystem.GetVTable(&link->PeerA);
NetTransportVTable server = LoopbackTransportSystem.GetVTable(&link->PeerB);
```

---

## 4. Implementing Custom & Real-Time Multiplayer in Your Game

Because Bring Engine runs on **Desktop CoreCLR (.NET 10)**, any game can reference battle-tested NuGet packages directly in `src/<YourGame>/<YourGame>.csproj` and bridge them into the engine's Zero-OOP/Zero-GC loop via `NetTransportVTable` (so your game code works identically against real sockets AND `LoopbackTransportSystem` in headless tests!).

### Pattern 1 — General Client-Server / P2P Reliable UDP (`LiteNetLib 2.x`)
Best all-round choice for real-time action, survival, co-op, or arena games (100% pure C#, MIT, zero native binaries, works cleanly across `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`):
```xml
<!-- In src/<YourGame>/<YourGame>.csproj -->
<PackageReference Include="LiteNetLib" Version="2.1.5" />
```
- Create a thin adapter (e.g., `src/<YourGame>/Net/LiteNetTransport.cs`) that keeps `NetManager` inside a static slot (just like `HttpSystem`/`WebSocketSystem`), runs `netManager.PollEvents(maxEvents: 64)` once per Logic frame, and copies received `NetPacketReader.GetRemainingBytesSpan()` bytes into a pre-allocated native ring buffer exposed through `NetTransportVTable`.
- Map `NetDeliveryMode` directly to `LiteNetLib.DeliveryMethod` (`Unreliable`, `Sequenced`, `ReliableUnordered`, `ReliableOrdered`).
- Use `WebSocketSystem` or `HttpSystem` for lobby discovery / matchmaking / NAT punchthrough signaling, then hand peers off to `LiteNetLib` UDP.

### Pattern 2 — Deterministic P2P Rollback Netcode (`Backdash`)
Best choice for 2–4 player competitive fighting, arcade, sports, or twin-stick games (100% pure C#, MIT, zero native binaries):
```xml
<PackageReference Include="Backdash" Version="*" />
```
- **Why Bring Engine pairs uniquely well with Rollback**:
  1. `Simulation/` already uses bit-exact `Fix32`/`FixVec3` + `DetRng` (no floating-point cross-OS desyncs).
  2. `GameSimState` lives in a flat unmanaged `Level` arena — saving/loading rollback state snapshots is a fast contiguous `Buffer.MemoryCopy` (or fixed struct blit) + `ComputeStateHash()` / `NetHash.Crc32` desync check!
  3. `View/` is already decoupled from `Simulation/` via `SimEvent`s — suppress visual/audio triggers during rollback re-simulation ticks and emit only on confirmed/current render frames.

### Pattern 3 — Steam / Steam Deck Multiplayer (`Facepunch.Steamworks`)
Best choice when shipping on Steam (gives you **Steam Datagram Relay** — NAT-free P2P without port forwarding, DDoS protection, built-in encryption, Steam Lobbies, and friend invites):
```xml
<PackageReference Include="Facepunch.Steamworks" Version="*" />
```
- Wrap `SteamNetworkingSockets` (`SocketManager` / `ConnectionManager`) behind your game's `NetTransportVTable` and pump `SteamClient.RunCallbacks()` once per frame outside `Simulation/`.
- Keep Steam SDK redistributables strictly inside your game project — never inside core `BringEngine`.

### Pattern 4 — Headless Dedicated Linux Server
Because Bring Engine enforces strict `Simulation/` vs. `View/` separation (zero SDL3 or GPU references inside `Simulation/`):
- Create a small server entrypoint (or `--dedicated-server` CLI mode in `Program.cs`) that initializes `GameAllocators` + `GameSimState` + your `LiteNetLib` / `WebSocket` server loop **without calling `PlatformSystem.InitWindow` or `GpuContext`**.
- Publish with `dotnet publish -c Release -r linux-x64` to run the exact same authoritative `Fix32` simulation on any Linux VPS or container.

---

## 5. Cross-OS Deployment Notes

- **Windows (`win-x64`)**: `HttpSystem` and `WebSocketSystem` use native Windows Schannel TLS and the Windows root certificate store.
- **Linux & Steam Deck (`linux-x64`)**: `.NET 10` TLS uses system **OpenSSL 1.1.1+ / 3.x** and `/etc/ssl/certs` (present on SteamOS and all modern Linux distros).
- **macOS (`osx-arm64` / `osx-x64`)**: Uses Apple's native Security/Network TLS stack (no OpenSSL dependency). Standard outbound HTTPS/WSS/UDP works under Hardened Runtime; note that macOS 15+ may show a Local Network permission prompt only if a game performs LAN broadcast discovery.
- **Zero shipped size penalty**: Core `BringEngine.Networking` adds **~0 MB** to the self-contained CoreCLR baselines documented in [`14-publishing-and-distribution.md`](14-publishing-and-distribution.md).
