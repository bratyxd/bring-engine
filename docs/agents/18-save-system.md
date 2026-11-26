# Agent Guide 18 — Local Save System (`SaveSystem`, `PlatformPaths` & `SaveChunks`)

> **Scope**: Using Bring Engine's built-in, crash-safe, zero-dependency local save system (`BringEngine.Core.Storage`) with unified OS app-data directories, atomic `.tmp` + `.bak` writes, CRC32 corruption auto-recovery, LZ4/Brotli compression, and Zone 1 async handle polling.

---

## 1. Unified OS App-Data Directory (`PlatformPaths`)

Both `LogSystem` and `SaveSystem` share a single OS-standard per-game parent folder via `PlatformPaths` (headless-safe, zero SDL3 dependency, Steam Cloud friendly):

| Platform | Shared App Parent (`ResolveAppDataDirectory`) | Logs Subfolder | Saves Subfolder |
|---|---|---|---|
| **Windows** | `%LOCALAPPDATA%\<AppName>\` | `Logs\` | `Saves\` |
| **macOS** | `~/Library/Application Support/<AppName>/` | `Logs/` | `Saves/` |
| **Linux / Steam Deck (SteamOS)** | `$XDG_DATA_HOME/<AppName>/` *(fallback `~/.local/share/<AppName>/`)* | `logs/` | `saves/` |

Pass `directoryOverride` to `SaveSystem.Start(...)` for portable builds or isolated CI tests.

---

## 2. Choosing the Right Save Option (Mandatory Decision Guide for Game Agents)

Do **not** blindly use one format for everything — choose the lane that matches the save data type:

| Save Type | Typical Size | Recommended Mode | Serialization | Compression |
|---|---|---|---|---|
| **User Settings / Keybinds / Graphics Config** (`settings.json`) | 0.5 KB – 20 KB | **Mode A — Raw (`SaveRawAsync` / `SaveRawSync`)** | `.NET 10` `System.Text.Json` source generator (`[JsonSerializable]`) | None (plain human-editable JSON on disk, still atomic `.tmp` + `.bak`) |
| **Player Profile, Progression, Inventory, Quests, RPG/Roguelite Saves** (`slot_1.bsav`) | 1 KB – 1 MB | **Mode B — `BSAV` + `SavePayloadKind.JsonUtf8`** | `.NET 10` `System.Text.Json` source generator | `PackCompressionMode.Lz4` *(fast autosaves)* or `Brotli` *(tiny cloud saves)* |
| **Huge Worlds, Tilemaps, Voxels, Factories, Replays, Large SoA Entity Dumps** (`world_0.bsav`) | 1 MB – 50+ MB | **Mode C — `BSAV` + `SavePayloadKind.Chunked`** | **`SaveChunkWriter` / `SaveChunkReader`** + `NetWriter`/`NetReader` or raw SoA `ReadOnlySpan<T>` | `PackCompressionMode.Lz4` |

### Why NOT `MemoryPack`?
- Do **not** add `MemoryPack` for long-lived game saves: its sequential layout breaks old saves whenever fields are reordered, inserted, or refactored across game patches.
- Mode B (`BSAV` + JSON) handles schema evolution effortlessly for 90% of games, and Mode C (`SaveChunkWriter` + `LZ4`) handles multi-megabyte binary world dumps with **independent per-chunk versioning** (unknown chunks skipped, missing chunks use defaults) and **0 extra NuGet dependencies**.

---

## 3. Crash Safety & Automatic Corruption Recovery

Every save mode (Raw and `BSAV`) runs crash-safe atomic disk I/O automatically:
1. Write full bytes to `<slot>.tmp` in the `Saves/` folder and `Flush(flushToDisk: true)`.
2. Copy the existing good `<slot>` → `<slot>.bak` (if `createBackup: true`).
3. Atomically rename `<slot>.tmp` → `<slot>`.
4. On load, `BSAV` validates Magic (`"BSAV"`), format version, uncompressed/compressed sizes, decompression, and **IEEE CRC32**. If the primary file is missing or corrupted, `SaveSystem` **automatically recovers from `<slot>.bak`** and sets `result.LoadedFromBackup == 1` (log a `Warn` via `LogSystem` when this happens!).
5. **Steam Cloud tip**: sync primary files (`*.bsav`, `settings.json`) and exclude `*.tmp` and `*.bak`.

---

## 4. Usage Examples

### A. Startup & Shutdown (`SaveSystemState` in `EngineAllocators.Persistent`)
```csharp
using BringEngine.Assets.Compression;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Core.Storage;

SaveSystemState* saves = engAlloc.Persistent.Alloc<SaveSystemState>(1, 64);
SaveSystem.Start(saves, ref engAlloc.Persistent, appName: "MyGame");

// Boot-time blocking load for settings.json (Mode A — Raw):
SaveResult cfgRes = SaveSystem.LoadRawSync(saves, "settings.json");
try
{
    if (cfgRes.IsSuccess)
        settings = JsonSerializer.Deserialize(cfgRes.AsDataSpan(), GameJsonContext.Default.GameSettings)!;
}
finally
{
    SaveSystem.ReleaseResult(ref cfgRes);
}

// ... game loop ...

// Shutdown (drains all queued saves cleanly, frees unpolled native buffers -> LiveBytes == 0):
SaveSystem.Stop(saves);
```

### B. Mode B — Autosaving Profile/Progression JSON Asynchronously (0 Frame Hitch, 0 GC Poll)
```csharp
// Serialize cold DTO via source-generated System.Text.Json (outside StepFixed):
byte[] jsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(profileDto, GameJsonContext.Default.ProfileSaveDto);

// Enqueue off-thread LZ4 + CRC32 + atomic write (strict FIFO order per slot):
SaveOpHandle op = SaveSystem.SaveAsync(
    saves,
    slotName: "slot_1.bsav",
    payload: jsonUtf8,
    schemaVersion: 2,
    kind: SavePayloadKind.JsonUtf8,
    compression: PackCompressionMode.Lz4,
    userTag: 1UL);

// Logic Thread polls once per frame (0 GC bytes):
while (SaveSystem.PollCompleted(saves, out SaveResult res))
{
    try
    {
        if (res.OpKind == SaveOpKind.Load && res.IsSuccess)
        {
            if (res.LoadedFromBackup != 0)
                LogSystem.Warn(log, $"Recovered {res.SlotName} from .bak backup (schema v{res.GameSchemaVersion})");
            ApplyProfile(res.AsDataSpan(), res.GameSchemaVersion);
        }
    }
    finally
    {
        SaveSystem.ReleaseResult(ref res); // MUST free native DataPtr!
    }
}
```

### C. Mode C — Large Worlds & SoA Dumps with Versioned Binary Chunks
```csharp
// Writing chunks (0 GC with native SaveChunkWriter):
var chunks = SaveChunkWriter.CreateGrowable(initialCapacity: 65536);
try
{
    chunks.WriteChunk(StringId.Hash("meta"), chunkVersion: 1, metaNetWriter.WrittenSpan());
    chunks.WriteChunk(StringId.Hash("tiles"), chunkVersion: 2, new ReadOnlySpan<ushort>(sim.Tiles, sim.TileCount));
    chunks.WriteChunk(StringId.Hash("entities"), chunkVersion: 1, new ReadOnlySpan<FixVec3>(sim.Pos, sim.EntityCount));

    SaveSystem.SaveAsync(
        saves, "world_0.bsav", chunks.WrittenSpan(),
        schemaVersion: 3, SavePayloadKind.Chunked, PackCompressionMode.Lz4);
}
finally
{
    chunks.Dispose(); // Frees temporary native chunk buffer
}

// Reading chunks back (forward/backward patch-safe):
SaveChunkReader reader = loadRes.CreateChunkReader();
if (reader.TryFind(StringId.Hash("tiles"), out SaveChunkView tilesChunk))
{
    ReadOnlySpan<ushort> loadedTiles = tilesChunk.AsSpan<ushort>();
    // Branch on tilesChunk.Version if migrating tile layout
}
if (reader.TryFind(StringId.Hash("weather"), out SaveChunkView weatherChunk))
{
    // Optional chunk added in a newer patch — safely skipped on old saves!
}
```

---

## 5. Rules Checklist
1. **Pick the right mode for each file**: Raw JSON for human-editable `settings.json`; `BSAV` + `JsonUtf8` for normal progression/profile saves; `BSAV` + `Chunked` for multi-MB worlds/SoA arrays.
2. **Never do disk I/O inside `Simulation/`**: Snapshot `GameSimState` cleanly at a tick boundary and enqueue `SaveSystem.SaveAsync` from the Logic/View orchestration layer.
3. **Always call `SaveSystem.ReleaseResult(ref res)`** and call **`SaveSystem.Stop(saves)` before `engAlloc.Dispose()`** so all in-flight saves finish cleanly and `NativeMemoryTracker.LiveBytes == 0`.
4. **Always bump `schemaVersion` (or individual chunk versions)** when changing save formats and log load/backup-recovery/failure events via `LogSystem`.
