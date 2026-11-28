# Bring Engine — Master AI Agent Guide (Hub)

> **Audience**: AI coding agents building games on **Bring Engine** or maintaining engine subsystems.
> **How to navigate these docs**: This file is the **master guide and index**. Read the 6 core rules below, then open only the specific topic guide you need under [`docs/agents/`](docs/agents/) or [`src/ShowcaseGame/AGENT_README.md`](src/ShowcaseGame/AGENT_README.md) so you don't load unrelated details into context.

---

## 1. Non-Negotiable Architecture & Workflow Rules

1. **NO OOP — Pure Data + Static Systems**
   - NEVER use class inheritance, `virtual`, `override`, `abstract`, or interface dispatch in engine or game code.
   - Store all state in `[StructLayout(LayoutKind.Sequential)]` `unmanaged struct` tables (SoA / dense sparse sets) referenced by 32-bit generational handles (`EntityId`, `SceneId`, `BundleId`, `MeshHandle`, `TextureHandle`, `MaterialHandle`, `ShaderHandle`, `FontHandle`, `GltfHandle`, `AnimClipHandle`, `SkeletonHandle`, `AnimPlayerHandle`, `SpriteAnimPlayerHandle`, `SpriteAnimClipHandle`, `TweenHandle`, `EmitterHandle`, `CameraHandle`, `SpriteHandle`, `TextHandle`, `JobHandle`, `HttpRequestHandle`, `WebSocketHandle`, `LogFileHandle`, `SaveOpHandle`, `AudioClipHandle`, `AudioVoiceHandle` — 26 total).
   - Put behavior in `public static unsafe class <Feature>System` methods and use unmanaged function pointers (`delegate*<...>`) for callbacks.

2. **Desktop CoreCLR (.NET 10) + Self-Contained ReadyToRun — Windows, Linux (Steam Deck), macOS**
   - **Desktop only**: targets `win-x64`, `linux-x64` (incl. Steam Deck / SteamOS), and `osx-arm64` / `osx-x64` on **CoreCLR JIT (.NET 10)** with Dynamic PGO and **Self-Contained Single-File ReadyToRun** (`PublishReadyToRun=true`, `PublishSingleFile=true`, `SelfContained=true`) on publish — no NativeAOT constraints and no C++ toolchain required.
   - Cross-OS publishing from Windows/Linux (including macOS without owning a Mac), packaging, signing, and build sizes are documented in [`docs/agents/14-publishing-and-distribution.md`](docs/agents/14-publishing-and-distribution.md).

3. **Explicit Native Memory Allocators & Zero-GC Hot Paths**
   - Use 64-byte aligned `EngineAllocators` (`Persistent`, double-buffered `Frame`/`FrameBack`, `Scratch`, `Dynamic`), `GameAllocators` (`Persistent`, `Level`, `Frame`), per-bundle `LinearArena`s, and per-scene `LinearArena`s.
   - Hot simulation and render loops MUST allocate **0 bytes** on the managed GC heap (`GC.GetAllocatedBytesForCurrentThread() == 0`) and exit with **0 native leaks** (`NativeMemoryTracker.LiveBytes == 0`). See [`docs/agents/01-memory-and-allocators.md`](docs/agents/01-memory-and-allocators.md).

4. **Strict Simulation vs. View Split, Gamepads, 2-Thread Pipeline & Borderless Full Window**
   - **Simulation** (`*.Simulation.*`): Deterministic fixed-timestep (`Fix32`/`FixVec3`, `DetRng`), ZERO SDL3 or GPU calls, headless-testable.
   - **Input (`InputState`) & Platform (`PlatformSystem`)**: Full keyboard scancodes, mouse (`IsMousePressed`/`Down`/`Released`, relative mouse lock `SetMouseLock`, `SetCursorVisible`), zero-GC UTF-8 text input (`StartTextInput`/`StopTextInput`), window focus/minimize state (`IsWindowFocused`, `WasWindowFocusLost`, `IsWindowMinimized`), and up to **4 SDL3 gamepads** (Xbox / PlayStation / Nintendo / Steam Deck back paddles & touchpad, radial deadzone, `GetMoveAxis()`, Main-Thread `PlatformSystem.RumbleGamepad`).
   - **Audio (`BringEngine.Audio.AudioSystem`)**: Zero-dependency SDL3 `SDL_AudioStream` software mixer on its own dedicated `BringEngine.AudioMixer` thread (0 GC playback, WAV + `.bpack` RawBlob loading, `Master`/`Music`/`Sfx`/`Ui` volume & mute buses, pitch/pan/loop/voice-stealing, headless CI fallback). See [`docs/agents/19-audio.md`](docs/agents/19-audio.md).
   - **View (`*.View.*` / `BringEngine.Rendering.*`)**: Reads simulation state & `SimEvent`s; never mutates gameplay truth. Keep visual feel **smooth and restrained** (prefer `OutCubic`/`InOutSine` easing, damped camera motion, modest particles, and light infrequent trauma `0.15–0.25` over heavy juice), and keep **text static (`TextAnimFlags.None`, no text animations) unless explicitly requested**.
   - **2-Thread Pipeline (`EnginePipelineSystem`)**: Logic Thread (Frame `N`) runs concurrently with Main/Render Thread (Frame `N-1`). All SDL3 window/event/swapchain/rumble calls stay on the Main Thread.
   - **Two-Zone Async Architecture (Mandatory for Engine & Games)**:
     - **Zone 1 — Core Engine APIs, `Simulation/`, `View/`, and simple async work**: MUST use **32-bit Generational Handles + Native Ring-Buffer Mailbox + Zero-GC Frame Polling + `Cancel`/`Release`/`Stop`** (`JobSystem`, `HttpSystem`, `WebSocketSystem`, `SaveSystem`, future async I/O/streaming). NEVER expose `Task<T>`, `async`/`await`, or managed callback closures in core engine APIs or frame loops.
     - **Zone 2 — Game-level heavy cold-path orchestration** (multi-step live-ops, auth/JWT refresh, retries, IAP, CDN patching, backend SDKs like PlayFab/Nakama/EOS): Quarantine standard C# `async`/`await` + `Task` (`.NET 10` `HttpClient` or NuGet SDKs) inside an isolated background `Services/` layer with a shutdown `CancellationToken`. Background `async` code MUST NEVER touch `GameSimState`, `EngineWorld`, `EngineAllocators`, or SDL3/GPU directly — publish plain unmanaged results/snapshots into a thread-safe mailbox polled synchronously once per frame on the Logic Thread (0 GC). See [`docs/agents/16-networking-and-multiplayer.md`](docs/agents/16-networking-and-multiplayer.md).
   - **Multithreading (`JobSystem`)**: Use only when profiling justifies it (heavy SoA loops or multi-frame background jobs — never tiny loops). See [`docs/agents/12-multithreading-and-jobs.md`](docs/agents/12-multithreading-and-jobs.md).
   - **Defaults**: Launch in **Borderless Full Window** (`WindowMode.BorderlessFullscreen`), **VSync ON**, **60 FPS cap**, and wire `ProfilerOverlay` (`F1` overlay, `F2` VSync, `F3` FPS cap, `F11` borderless/windowed). See [`docs/agents/11-vsync-fps-cap-and-profiling.md`](docs/agents/11-vsync-fps-cap-and-profiling.md).
   - **Selective, High-Signal Logging (`LogSystem`)**: Runs on its own dedicated background thread (`BringEngine.LogWriter`) and **automatically logs any thrown/unhandled/unobserved exception as `[ERROR]`** (`hookExceptions: true` default) without manual user calls. Both core engine and game code MUST log selectively — **never spam** per-frame/per-entity hot loops at `Info`+, and **never stay silent** on key milestones. Log important lifecycle/state transitions and failures with detailed context (IDs, counts, timings, codes, paths) so logs clearly explain what happened. Always stop all other subsystems (pipeline, GPU, platform, audio, networking, saves, jobs) **before** calling `LogSystem.Stop(log)` and before disposing arenas. See [`docs/agents/17-logging.md`](docs/agents/17-logging.md).

5. **GPU Batching, Default Compressed Atlases, `.bpack` Bundles & 9-Slice UI**
   - Pack all raw `.png`/`.jpg`/`.glb`/`.ttf` source assets into **BC1/BC3 GPU-compressed atlased `.bpack` bundles** (Brotli/LZ4) via `BringPacker`, and ship **ZERO loose PNG/JPG textures** in `dist/` (`BringPacker --verify-no-loose-textures dist`).
   - Use 9-slice `SpriteDrawMode.Sliced` / `SlicedNoCenter` for scalable UI panels in a single instanced draw call. See [`docs/agents/13-asset-compression-atlases-and-9slice.md`](docs/agents/13-asset-compression-atlases-and-9slice.md).

6. **Mandatory PR Review & Fix Phase for Engine Code Updates (Every Agent)**
   - Whenever **engine code** is updated (any change under `src/BringEngine/`, `tools/BringPacker/`, `src/ShowcaseGame/`, shaders, project files, or `tests/BringEngine.Tests/`), work is **NOT** done when the implementor agent finishes.
   - **PR Review Plan (`Neon xhigh`)**: After the implementor agent is done, use **`Neon xhigh`** to perform a fresh PR code review directly against the actual diff and affected files and **create a review plan from scratch**.
   - **Fix Phase (`Neon high`)**: Hand that review plan to **`Neon high`** to **apply the review plan and run a fix phase** (fixes, regression tests, doc sync, clean build — with `Neon xhigh` re-verifying blocking fixes) before pushing to GitHub Actions CI or tagging a release.
   - Pure docs/README-only updates (`*.md`, `docs/**`) are exempt. See [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md).

---

## 2. Solution Layout

- `BringEngine.sln` & `Directory.Build.props` — Solution and Desktop CoreCLR (`.NET 10`) build settings.
- `src/BringEngine/` — Core Engine Library (`net10.0`):
  - `Core/` — `Memory/` (`LinearArena`, `PoolAllocator<T>`, `FreeListAllocator`, `EngineAllocators`, native containers, `FixedString*`, `StringId`), `Logging/` (`LogSystem`, zero-GC UTF-8 `[InterpolatedStringHandler]`, rolling files), `Storage/` (`PlatformPaths`, `SaveSystem`, atomic `.tmp`/`.bak` `BSAV` + Raw JSON + `SaveChunkWriter`/`SaveChunkReader`), `Profiling/` (`FrameProfiler`, `ProfilerSystem`), `Threading/` (`EnginePipelineSystem`, `JobSystem`), `Handles/`, `Math/` (`Fix32/64`, `FixVec2/3`, `DetMath`, `DetRng`, `Vec/Quat/Mat4`).
  - `Audio/` — `WavDecoder` (8/16/24/32-bit PCM & IEEE Float WAV + procedural wave helpers), `AudioSystem` (dedicated `BringEngine.AudioMixer` thread, SDL3 `SDL_AudioStream`, 4 volume/mute buses, pitch/pan/loop/stealing, headless fallback).
  - `Networking/` — Zero-dependency `.NET 10` BCL networking (`HttpSystem`, `WebSocketSystem`), zero-GC binary `NetWriter`/`NetReader`/`NetHash.Crc32`, pluggable `NetTransportVTable`, and deterministic `LoopbackTransportSystem`.
  - `Simulation/` — `EntityTable`, `TransformTable`, `EngineWorld`, `CameraTable`, `InputState`, `TweenTable`, `AnimationTable`, `ParticleTable`, `RenderComponentTables`, `SceneManager`.
  - `Assets/` — `AssetRegistry`, `BundleManager`, `Compression/` (BC1/BC3 `BRTX`, LZ4/Brotli, `BGLB`), `Atlases/` (`AtlasPacker`), `Packing/` (`AssetPackBuilder` / `AssetPackLoader`), `TextureLoader`, `SdfFontLoader`, `GltfLoader`, `GlbBuilder`.
  - `Rendering/` — `SdlPlatform`, `FramePacer`, `GpuContext`, `ShaderSystem` (`SDL_shadercross`), `SceneLighting` (incl. directional shadow mapping), `TextLayoutSystem`, `RenderPacket`, `BatchRenderer`, `ProfilerOverlay`.
  - `Shaders/` — Built-in HLSL shaders (`pbr_mesh.hlsl`, `shadow_depth.hlsl`, `sprite_3d.hlsl`, `sdf_text.hlsl`, `particle.hlsl`).
- `tools/BringPacker/` — CoreCLR CLI bundler for `.bpack` bundles and `--verify-no-loose-textures`.
- `src/ShowcaseGame/` — Canonical reference game (**"Bring Arena"**).
- `tests/BringEngine.Tests/` — CoreCLR engine test suite (run on GitHub Actions CI only).

---

## 3. Documentation Map (Jump to the Guide You Need)

### Building Games & Shipping Releases
| Topic | Guide |
|---|---|
| **New Game Blueprint & Golden Rules** | [`src/ShowcaseGame/AGENT_README.md`](src/ShowcaseGame/AGENT_README.md) |
| **Cross-OS Building, Publishing, Signing (incl. macOS without a Mac) & Sizes** | [`docs/agents/14-publishing-and-distribution.md`](docs/agents/14-publishing-and-distribution.md) |
| **Working on Bring Engine: Mandatory PR Review, CI-Only Tests & Releases** | [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md) |

### Engine Subsystems (`docs/agents/`)
| # | Guide | Covers |
|---|---|---|
| 01 | [`01-memory-and-allocators.md`](docs/agents/01-memory-and-allocators.md) | `EngineAllocators`, `LinearArena`, `PoolAllocator<T>`, `NativeSparseSet`, `NativeHashMap`, leak tracking |
| 02 | [`02-deterministic-math-and-data.md`](docs/agents/02-deterministic-math-and-data.md) | `Fix32`/`FixVec3`, `DetMath`, `DetRng`, generational handles, coordinate conventions |
| 03 | [`03-ecs-world-and-systems.md`](docs/agents/03-ecs-world-and-systems.md) | `EntityTable`, `TransformTable`, full keyboard/mouse/lock/focus/text input & **SDL3 Gamepad / Steam Deck input & rumble** |
| 04 | [`04-scenes-and-asset-bundles.md`](docs/agents/04-scenes-and-asset-bundles.md) | Ref-counted `BundleManager` and Unity-like `SceneManager` (`Single` vs. `Additive`) |
| 05 | [`05-gltf-and-3d-rendering.md`](docs/agents/05-gltf-and-3d-rendering.md) | `GltfLoader` (`.glb`/`.gltf`), `GlbBuilder`, instanced PBR batching, skeletal skinning, **directional sun shadow mapping (3×3 PCF)** |
| 06 | [`06-2d-sprites-in-3d-and-batching.md`](docs/agents/06-2d-sprites-in-3d-and-batching.md) | 2D sprites in 3D/HUD, GPU batching rules, custom HLSL conventions |
| 07 | [`07-text-fonts-and-text-effects.md`](docs/agents/07-text-fonts-and-text-effects.md) | `SdfFontLoader`, world & HUD SDF `TextData` (**static `TextAnimFlags.None` by default**; optional effects only when requested) |
| 08 | [`08-tweening-and-animation.md`](docs/agents/08-tweening-and-animation.md) | `TweenSystem` (31 curves — smooth `OutCubic`/`InOutSine` defaults, pointer binding, chaining), skeletal & sprite animation |
| 09 | [`09-particles-and-shaders.md`](docs/agents/09-particles-and-shaders.md) | SoA `ParticleSystem`, bursts, custom HLSL shaders via `SDL_shadercross` |
| 10 | [`10-camera-and-headless-testing.md`](docs/agents/10-camera-and-headless-testing.md) | Multi-camera, `AddTrauma`, picking, deterministic headless verification |
| 11 | [`11-vsync-fps-cap-and-profiling.md`](docs/agents/11-vsync-fps-cap-and-profiling.md) | Borderless fullscreen default, 2-thread loop, VSync/FPS cap, `FrameProfiler`, `ProfilerOverlay` |
| 12 | [`12-multithreading-and-jobs.md`](docs/agents/12-multithreading-and-jobs.md) | Data-oriented `JobSystem` (`ParallelFor`, async DAGs, cancel, worker scratch, when NOT to parallelize) |
| 13 | [`13-asset-compression-atlases-and-9slice.md`](docs/agents/13-asset-compression-atlases-and-9slice.md) | BC1/BC3 texture compression, LZ4/Brotli `.bpack` & GLB compression, default atlasing, `BringPacker`, 9-slice UI |
| 16 | [`16-networking-and-multiplayer.md`](docs/agents/16-networking-and-multiplayer.md) | Lean core `HttpSystem`, `WebSocketSystem`, zero-GC `NetWriter`/`NetReader`, `NetTransportVTable`, `LoopbackTransport`, and game-level multiplayer (`LiteNetLib`, `Backdash`, `Facepunch.Steamworks`) |
| 17 | [`17-logging.md`](docs/agents/17-logging.md) | Zero-GC core `LogSystem`, unified OS paths (`PlatformPaths`), `LogFileHandle`, 10k-line FIFO trimming, and selective logging discipline |
| 18 | [`18-save-system.md`](docs/agents/18-save-system.md) | Crash-safe atomic `SaveSystem` (`.tmp`/`.bak`, CRC32 recovery, LZ4/Brotli), Raw JSON vs. `BSAV`+JSON vs. versioned binary `SaveChunks` |
| 19 | [`19-audio.md`](docs/agents/19-audio.md) | Zero-GC core `AudioSystem` (SDL3 `SDL_AudioStream`, dedicated mixer thread, WAV/`.bpack`, `Master`/`Music`/`Sfx`/`Ui` buses, pitch/pan/loop, headless fallback) |

---

## 4. Quick Command Reference

> **CI-Only Engine Testing**: Do **NOT** run `BringEngine.Tests` or heavy multi-project publishes locally. Push to GitHub and let CI run them. See [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md).

```bash
# Instant CoreCLR build / interactive ShowcaseGame run
dotnet build BringEngine.sln -c Release
dotnet run --project src/ShowcaseGame/ShowcaseGame.csproj -c Release

# Cross-OS self-contained single-file ReadyToRun game publish (all targets work from Windows x64!)
dotnet publish src/<Game>.csproj -c Release -r win-x64   -o dist/win-x64
dotnet publish src/<Game>.csproj -c Release -r linux-x64 -o dist/linux-x64
dotnet publish src/<Game>.csproj -c Release -r osx-arm64 -o dist/osx-arm64
```

- **Full cross-OS publishing, Linux `+x`/symlink notes, macOS signing/notarization without a Mac, and shipped size baselines**: [`docs/agents/14-publishing-and-distribution.md`](docs/agents/14-publishing-and-distribution.md).
- **Git identity & GitHub account**: When committing or pushing to `bratyxd/bring-engine`, always use the **`bratyxd`** git identity (`git config user.name` = `bratyxd`) and GitHub CLI account (`gh auth switch --user bratyxd`).
- **Engine PR review, testing & releasing**: Every engine code update MUST run a post-implementation PR review (**`Neon xhigh`** creates a review plan from scratch → **`Neon high`** applies it in a fix phase), include tests in `tests/BringEngine.Tests/`, and ship via a tagged release (`vX.Y.Z` matching `EngineInfo.Version`). **Docs / README-only pushes to `main`** (zero code changes since the latest tag) skip the code review & test cycle, bump the patch tag (`vX.Y.Z` → `vX.Y.(Z+1)`), and publish a new SDK release. See [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md).
