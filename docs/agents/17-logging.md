# Agent Guide 17 — Core Zero-GC Logging System (`LogSystem`)

> **Scope**: Using Bring Engine's built-in, zero-dependency, zero-GC hot-path core logging system (`BringEngine.Core.Logging`) with OS-standard writable log directories, default + custom log files, and automatic 10,000-line FIFO rolling truncation.

---

## 1. Platform Log Directories (Headless-Safe, Zero SDL3 Dependency)

`LogSystem.ResolvePlatformLogDirectory(appName)` delegates to `PlatformPaths.ResolveLogsDirectory(appName)` so logs and saves share the exact same OS-standard per-game parent folder without requiring SDL3 initialization (works identically in windowed games, `--headless` tests, and dedicated Linux servers):

| Platform | Dynamic Standard Path | Default File |
|---|---|---|
| **Windows** | `%LOCALAPPDATA%\<AppName>\Logs\` | `engine.log` |
| **macOS** | `~/Library/Application Support/<AppName>/Logs/` | `engine.log` |
| **Linux / Steam Deck (SteamOS)** | `$XDG_DATA_HOME/<AppName>/logs/` → fallback `~/.local/share/<AppName>/logs/` | `engine.log` |

Pass `directoryOverride` to `LogSystem.Start(...)` when you need a portable folder or isolated test directory.

---

## 2. Format, Priorities & Rolling 10,000-Line FIFO Cap

Every line is formatted on a dedicated background writer thread as:
```text
[2026-11-26 14:20:22.123] [INFO ] Scene 'arena' loaded (142 entities)
[2026-11-26 14:20:22.456] [WARN ] Packet dropped from peer 2
[2026-11-26 14:20:22.789] [ERROR] Failed to open bundle: arena.bpack
```
- **Priorities (`LogLevel`)**: `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`, `None`.
- **Single-line guarantee**: Embedded `\r` / `\n` characters are sanitized to spaces so every log entry is strictly one line.
- **Line limit (default `10,000` lines)**:
  - Applies to both the default file (`engine.log`) and any additional file opened via `LogSystem.OpenFile(...)` unless a custom `maxLines` is supplied.
  - Once a file exceeds `maxLines`, the oldest lines are evicted starting from **line 1** (FIFO) using an O(1) native line-offset ring buffer + amortized chunked compaction on the background thread (and exact compaction to `<= maxLines` on `Flush()`, `CloseFile()`, `Stop()`, and `Error`/`Fatal` auto-flush).
  - Reopening an existing log file on startup scans its line offsets once so the cap persists seamlessly across sessions.

---

## 3. Zero-GC Hot-Path Usage (Custom `.NET 10` `[InterpolatedStringHandler]`)

`LogSystem` uses built-in `.NET 10` `[InterpolatedStringHandler]` + `IUtf8SpanFormattable` (zero third-party string packages like `ZString`):
- **0 GC bytes** on the calling thread for interpolated strings, `ReadOnlySpan<byte>` (`"..."u8`), and `ReadOnlySpan<char>`.
- **Filtered-out skip**: When `level < MinLevel`, the C# compiler skips evaluating *every* interpolated argument and skips all formatting (`~1 ns`, `0 bytes`).
- **First-class Bring Engine types**: Formats `Fix32`, `Fix64`, `FixVec2`, `FixVec3`, `Vec2`, `Vec3`, `FixedString64`, `FixedString256`, `StringId`, and handles (`EntityId`, `SceneId`, `BundleId`, `JobHandle`, `HttpRequestHandle`, `WebSocketHandle`, `LogFileHandle`) directly into UTF-8 with 0 GC.
- **Thread-safe**: Safe to call concurrently from Main Thread, Logic Thread, and `JobSystem` worker threads.

```csharp
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Memory;

// Startup (allocate LogState in EngineAllocators.Persistent):
LogState* log = engAlloc.Persistent.Alloc<LogState>(1, 64);
LogSystem.Start(
    log,
    ref engAlloc.Persistent,
    appName: "MyGame",
    defaultFileName: "engine.log",   // default
    defaultMaxLines: 10_000,         // default
    minLevel: LogLevel.Info,
    mirrorToConsole: false);

// Log to default file (0 GC on hot paths; Trace/Debug skip argument evaluation when MinLevel == Info):
LogSystem.Info(log, $"Arena loaded: {enemyCount} enemies at {spawnPos} ent={heroId}");
LogSystem.Warn(log, "Low memory watermark reached"u8);
LogSystem.Error(log, $"Shader failed: {shaderName}"); // auto-flushes to disk by default

// Optional: open a separate log file (also defaults to 10,000 lines; adjustable per file):
LogFileHandle netLog = LogSystem.OpenFile(log, "network.log");                  // 10,000 lines
LogFileHandle combatLog = LogSystem.OpenFile(log, "combat.log", maxLines: 2000); // 2,000 lines

LogSystem.Info(log, netLog, $"Peer {peerId} connected (rtt={rttMs}ms)");
LogSystem.Write(log, combatLog, LogLevel.Debug, $"Crit hit dmg={dmg}");

// Close a secondary file anytime:
LogSystem.CloseFile(log, combatLog);

// Explicit flush barrier (e.g. before crash dialog or reading file):
LogSystem.Flush(log);

// Shutdown (BEFORE engAlloc.Dispose() — flushes, enforces exact line limits, frees all native buffers):
LogSystem.Stop(log);
```

---

## 4. Selective, High-Signal Logging Discipline (Mandatory for Engine & Game Agents)

Logs exist so a single file lets you immediately understand **what happened, when, and why** — without burying signal in noise or leaving blank gaps:

- **Do NOT spam hot loops**: Never log every frame, every tick, every entity/particle update, or every network packet at `Info` or higher. Reserve ultra-verbose per-tick diagnostics for `Trace`/`Debug` (which cost ~1 ns and 0 bytes when filtered out).
- **Do NOT stay silent either**: Log **meaningful lifecycle milestones, state transitions, and concrete errors** with enough structured context (IDs, counts, timings, status codes, paths, positions/states) to diagnose issues from the log alone.
- **What Engine Core should log**:
  - Startup/shutdown milestones (OS, GPU driver/backend, window mode/resolution, VSync/FPS cap, allocator budgets).
  - Asset bundle & scene loads/unloads (bundle/scene name, load duration, entity/asset counts, memory reclaimed).
  - Recoverable fallbacks, shader/asset compile warnings, network connection open/close/errors, and fatal failures with exact resource names/codes.
- **What Games should log**:
  - Match/run/level lifecycle (seed, mode, scene transition, wave/phase/boss transitions, win/loss summary and final stats).
  - Significant player/multiplayer/save/live-ops events (peer connect/disconnect/desync, save/load path & version, auth/store failures with status codes).
  - Unexpected gameplay states or invalid handles with exact entity/handle IDs and relevant parameters — never vague `"Something failed"` messages.

### Technical Rules
1. **Keep `Simulation/` deterministic**: Do not make gameplay truth depend on logging state or wall-clock timestamps; prefer logging from `View/`, startup/shutdown, or diagnostic hooks.
2. **Always call `LogSystem.Stop(log)` before `engAlloc.Dispose()`**: Guarantees all buffered lines reach disk, every file is trimmed to `<= maxLines`, and `NativeMemoryTracker.LiveBytes == 0`.
3. **Inspect resolved paths via `LogSystem.GetLogDirectory(log)` / `LogSystem.GetFilePath(log, handle)`** when building in-game "Open Logs Folder" buttons or bug-report exporters.
