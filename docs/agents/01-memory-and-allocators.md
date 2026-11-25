# Agent Guide 01 — Memory Allocators & Unmanaged Containers

## Core Principle
Bring Engine and every game built on it **allocate native memory up front** and **never allocate on the managed GC heap inside hot loops**.

## Allocator Lifetimes (`EngineAllocators`)
Create `EngineAllocators` (and your game's `GameAllocators`) once at startup and pass them by `ref`:

| Allocator | Lifetime | Reset Policy | Use For |
| :--- | :--- | :--- | :--- |
| `alloc.Persistent` (`LinearArena`) | Whole app | Never (disposed on exit) | Core tables, registries, permanent state |
| `alloc.Frame` (`LinearArena`) | Single frame | `alloc.BeginFrame()` every frame | Render queues, sort keys, temp vertices, spatial queries |
| `alloc.Scratch` (`LinearArena`) | Single scope | `SaveMarker()` / `Rollback()` | glTF/JSON decoding, atlas packing, temp sorting |
| `alloc.Dynamic` (`FreeListAllocator`) | Variable | Individual `Free(ptr)` | Dynamic runtime buffers not tied to a scene/frame |
| Per-Scene / Per-Bundle `LinearArena` | Scene or Bundle | `Dispose()` on unload | Scene entities/buffers, loaded asset data (reclaims 100% RAM) |

## Code Patterns

### Using `LinearArena` & Scoped Rollbacks
```csharp
using BringEngine.Core.Memory;

// Persistent allocation
var arena = LinearArena.Create(4 * 1024 * 1024);
int* scores = arena.Alloc<int>(1024, alignment: 64);

// Frame allocation (auto-reset at start of next frame)
var drawItems = alloc.Frame.AllocSlice<DrawItem>(maxItems);

// Scratch scoped allocation
ArenaMarker mark = alloc.Scratch.SaveMarker();
byte* tempBuf = alloc.Scratch.Alloc<byte>(65536);
// ... decode work ...
alloc.Scratch.Rollback(mark); // O(1) instant reclaim
```

### Cache-Friendly Containers (Zero GC)
- `NativeSlice<T>` — non-owning pointer + length (`AsSpan()`).
- `NativeList<T>` — `CreateInArena(ref arena, cap)` for fixed-cap zero-free lists, or `CreateGrowable(cap)` (must `Dispose()`).
- `NativeSparseSet<T>` — O(1) `Has`/`AddOrReplace`/`GetRef`/`Remove` (swap-back) + contiguous `DenseValues[0..Count]` for L1 cache hits.
- `NativeHashMap<TValue>` — open-addressing hash map keyed by `ulong` (`StringId.Value` or `Handle.Raw`).
- `FixedString64` / `FixedString256` / `StringId` — zero-GC UTF-8 strings and FNV-1a 64-bit hashes.

## Leak Verification
All native memory goes through `NativeMemoryTracker`. After disposing all arenas/containers, `NativeMemoryTracker.LiveBytes` MUST equal `0` and `NativeMemoryTracker.LiveBlocks` MUST equal `0`.
