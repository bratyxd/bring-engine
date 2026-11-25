# Bring Engine — Master AI Agent Handbook

> **Audience**: AI coding agents building games on **Bring Engine** or extending engine subsystems.
> Every subsystem is documented in `docs/agents/` and demonstrated end-to-end in `src/ShowcaseGame/`.

## 1. Non-Negotiable Architecture Rules

1. **NO OOP — Pure Data + Systems**
   - NEVER use class inheritance, `virtual`, `override`, `abstract`, or interface dispatch in engine or game code.
   - Store all state in `unmanaged struct` tables (Structure-of-Arrays or dense sparse sets) referenced by 32-bit generational handles (`EntityId`, `SceneId`, `BundleId`, `MeshHandle`, `TextureHandle`, `MaterialHandle`, `ShaderHandle`, `FontHandle`, `GltfHandle`, `AnimClipHandle`, `SkeletonHandle`, `TweenHandle`, `EmitterHandle`, `CameraHandle`, `SpriteHandle`, `TextHandle`, `JobHandle`).
   - Put all behavior in `public static unsafe class <Feature>System` methods iterating contiguous native memory linearly.
   - Use unmanaged function pointers (`delegate*<...>`) for bundle loaders, scene lifecycle hooks, and tween completion callbacks.

2. **Desktop CoreCLR (.NET 10) + ReadyToRun — Windows, Linux (incl. Steam Deck), macOS**
   - **Bring Engine targets Desktop platforms exclusively**: Windows (`win-x64`), Linux & Steam Deck / SteamOS (`linux-x64`), and macOS (`osx-arm64`) running on **CoreCLR JIT (.NET 10)** with Dynamic PGO and **Self-Contained Single-File ReadyToRun** (`PublishReadyToRun=true`, `PublishSingleFile=true`, `SelfContained=true`) for release builds.
   - **No NativeAOT constraints**: games and tools get instant 1–2s `dotnet build`/`dotnet run` iteration, full .NET NuGet package compatibility (physics, audio, networking, modding, serializers, ImGui, etc.), and cross-OS self-contained desktop publishing with no C++ linker toolchain required.
   - Keep hot loops strictly zero-GC and data-oriented so CoreCLR's Tiered Dynamic PGO delivers maximum AVX2/AVX-512/NEON throughput with zero GC pauses.

3. **Explicit Native Memory Allocators & Zero-GC Hot Paths**
   - Create `EngineAllocators` (`Persistent`, `Frame`, `Scratch`, `Dynamic`) and `GameAllocators` (`Persistent`, `Level`, `Frame`) at startup.
   - Call `BeginFrame()` every frame to reset `Frame` arenas in O(1).
   - Use `SaveMarker()` / `Rollback()` on `Scratch` arenas for scoped parsing/layout.
   - Store bulk assets inside per-bundle `LinearArena`s and scene data inside per-scene `LinearArena`s so unloading immediately reclaims 100% of native RAM and GPU VRAM.
   - Hot simulation and render loops MUST allocate **0 bytes** on the managed GC heap.

4. **Strict Simulation vs. View Separation, First-Class Gamepad Input & 2-Thread Pipelined Execution**
   - **Simulation** (`BringEngine.Simulation.*`, `<Game>.Simulation.*`): Deterministic fixed-timestep (`Fix32`/`FixVec3`, `DetRng`). ZERO SDL3 or GPU calls. Must run headlessly with bit-exact state hashes across runs.
   - **First-Class Keyboard, Mouse & SDL3 Gamepad Input (`InputState`)**: Unmanaged 4-controller `GamepadState` embedded directly in `InputState` (zero pointers, zero GC, deterministic replayable) supporting Xbox, PlayStation, Nintendo, and **Steam Deck** (including 4 back grip paddles `LeftPaddle1..RightPaddle2`, touchpad click, triggers, scaled radial deadzone, unified `GetMoveAxis()` combining WASD/Arrows + Left Stick + D-Pad, and `PlatformSystem.RumbleGamepad` / `RumbleGamepadTriggers` haptics on the Main Thread).
   - **View** (`BringEngine.Rendering.*`, `<Game>.View.*`): Reads simulation state and `SimEvent` queues to sync 3D glTF models, 2D-in-3D sprites, animated SDF text, particles, lighting, cameras, and controller rumble. Never mutates gameplay truth.
   - **2-Thread Pipeline (`EnginePipelineSystem`)**: Interactive games run a dedicated **Logic/Update Thread** (Frame `N`: Sim + ViewSync + CoreSystems + `BatchRenderer.ExtractRenderPacket` into double-buffered `EngineAllocators.Frame`) concurrently with the **Main/Render Thread** (Frame `N-1`: SDL3 window/events/gamepads + `BatchRenderer.SubmitRenderPacket` from `EngineAllocators.FrameBack` + `FramePacer`). All SDL3 window/swapchain/event/rumble calls remain on the Main Thread per SDL3 requirements.
   - **Native First-Class Multithreading (`JobScheduler` + `JobSystem`) — Only When It Makes Sense**: Bring Engine ships a zero-GC, zero-OOP data-oriented worker pool (`ParallelFor` with automatic inline `minParallelCount` threshold, async `Schedule`/`ScheduleParallelFor`, DAG `CombineDependencies`, cooperative `RequestCancel`, and lock-free per-worker `GetWorkerScratch` native arenas). **Do NOT multithread everything for the sake of it** — small loops (< ~500–1000 items or < 0.5 ms) stay faster single-threaded in L1 cache. Use `JobSystem` only for genuinely heavy independent SoA loops (thousands of entities/particles/transforms, spatial/flow grids) or multi-frame background generation (`docs/agents/12-multithreading-and-jobs.md`).
   - **Borderless Full Window Default**: ALL Bring Engine games MUST launch in **Borderless Full Window** mode by default (`WindowMode.BorderlessFullscreen`, default in `PlatformSystem.InitWindow`), with `F11` runtime toggle to windowed mode and `--windowed` CLI override (handles desktop 16:9, ultrawide, and Steam Deck 16:10 `1280×800` natively with zero letterboxing).

5. **Cache Locality, GPU Batching, Default Compressed Atlasing & `.bpack` Bundling**
   - Pack vertices/instances in 16/64-byte aligned structs with `[StructLayout(LayoutKind.Sequential)]`.
   - **Default Compressed Atlasing & Unity-Style Asset Compression**: Pack all 2D/UI sprites into **BC1/BC3 GPU block-compressed atlases** by default (`AtlasPacker` / `AssetPackBuilder` / `BringPacker`), compress `.glb`/`.gltf` models (inlining external buffers/images, re-encoding embedded textures to BC1/BC3 `BRTX`, and compressing streams with **Brotli** or **LZ4** into `.bpack` bundles), and ship **ZERO loose PNG/JPG textures** in final builds (`BringPacker --verify-no-loose-textures dist`). Use 9-slice `SpriteDrawMode.Sliced` / `SlicedNoCenter` (`SpriteFromRegion` / `DefaultUiSlicedPanel`) for crisp scalable 2D UI panels that batch into a single instanced draw call (`docs/agents/13-asset-compression-atlases-and-9slice.md`).
   - `BatchRenderer.ExtractRenderPacket` performs CPU frustum culling, deterministic sorting & merging of 3D meshes, 2D sprites in 3D/HUD (including 9-sliced sub-quads), SDF glyphs, and SoA particles into a self-contained `RenderPacket` inside `FrameArena`.
   - `BatchRenderer.SubmitRenderPacket` uploads all dynamic storage buffers in **ONE `SDL_GPUCopyPass` per frame** (`cycle: true`), renders to an offscreen `SceneColorTexture` (cycling color/depth cleanly on initial clear, binding pipelines before storage buffers, and deduplicating state binds), measures pure GPU time via fence bracket before presenting, and blits to the swapchain on a separate `cmdPresent` so VSync wait never pollutes GPU timings.

## 2. Solution Layout
- `BringEngine.sln` & `Directory.Build.props` — Solution and Desktop CoreCLR (.NET 10) settings.
- `src/BringEngine/` — Core Engine Library (`net10.0`):
  - `Core/Memory/` — `NativeMemoryTracker`, `LinearArena`, `PoolAllocator<T>`, `FreeListAllocator`, `EngineAllocators` (double-buffered `Frame`/`FrameBack`), `NativeSlice<T>`, `NativeList<T>`, `NativeSparseSet<T>`, `NativeHashMap<T>`, `FixedString64/256`, `StringId`.
  - `Core/Profiling/` — `FrameProfiler`, `ProfilerSystem` (zero-GC independent Logic CPU / Render Thread / GPU ms timing, named scopes, rolling FPS).
  - `Core/Threading/` — `EnginePipelineState`, `EnginePipelineSystem` (2-thread pipelined Logic + Render orchestrator, zero-GC, zero locks), `JobScheduler`, `JobSystem` (data-oriented `ParallelFor`, async job DAGs, cooperative cancel, per-worker native scratch arenas).
  - `Core/Handles/` — 32-bit generational handles (`HandlePacking`, `EntityId`, ...).
  - `Core/Math/` — `Fix32`, `Fix64`, `FixVec2/3`, `FixQuat`, `DetMath`, `DetRng`, `Vec2/3/4`, `Quat`, `Mat4`, `ColorF/32`, `Bounds3`, `Plane`, `Frustum`, `Ray`.
  - `Simulation/` — `EntityTable`, `TransformTable`, `EngineWorld`, `CameraTable`, `InputState` (keyboard, mouse & 4-slot `GamepadState` w/ Steam Deck paddles & radial deadzone), `Easing`, `TweenTable`, `AnimationTable`, `ParticleTable`, `RenderComponentTables`, `SceneManager`.
  - `Assets/` — `AssetRegistry` (incl. `SpriteRegionData`), `BundleManager` (`.bpack`, JSON manifests, loader hooks), `Compression/` (`TextureCompressionSystem` BC1/BC3 `BRTX`, `PackCompressionSystem` LZ4/Brotli & `BGLB`), `Atlases/` (`AtlasPacker`), `Packing/` (`AssetPackBuilder` / `AssetPackLoader`), `TextureLoader`, `SdfFontLoader`, `GltfLoader`, `GlbBuilder`.
  - `Rendering/` — `SdlPlatform` (`WindowMode.BorderlessFullscreen` default + `F11` toggle + `SDL_Gamepad` polling & rumble haptics), `FramePacer` (default VSync ON + 60 FPS cap), `GpuContext` (native BC1/BC3 VRAM upload + fallback), `ShaderSystem` (`SDL_shadercross`), `SceneLighting`, `TextLayoutSystem`, `RenderPacket`, `BatchRenderer` (incl. 9-slice UI batch expansion), `ProfilerOverlay` (100% opaque drop-in UI tool with separate `LOGIC` / `RENDER` / `GPU` bars excluding VSync wait).
  - `Shaders/` — `pbr_mesh.hlsl`, `sprite_3d.hlsl`, `sdf_text.hlsl`, `particle.hlsl`.
- `tools/BringPacker/` — CoreCLR CLI asset bundler (self-contained single-file ReadyToRun on publish): builds default-atlased BC1/BC3 + LZ4/Brotli `.bpack` bundles from raw asset folders and enforces `--verify-no-loose-textures`.
- `src/ShowcaseGame/` — Playable Reference Game **"Bring Arena"** (CoreCLR + ReadyToRun on publish, depends on `BringEngine`).
- `tests/BringEngine.Tests/` — CoreCLR engine test suite (35 tests covering core, data-oriented `JobSystem` multithreading & bit-exact parallel subsystems, sim, gamepad input & radial deadzone, tweens, animation, particles, bundles, scenes, glTF, BC1/BC3 & LZ4/Brotli compression, default atlasing, `.bpack` zero-loose-texture bundling, 9-slice UI batching, SDF fonts, text layout, 2-thread pipelined rendering, and SDL3_gpu debug validation).

## 3. Subsystem Agent Guides
- [`docs/agents/01-memory-and-allocators.md`](docs/agents/01-memory-and-allocators.md)
- [`docs/agents/02-deterministic-math-and-data.md`](docs/agents/02-deterministic-math-and-data.md)
- [`docs/agents/03-ecs-world-and-systems.md`](docs/agents/03-ecs-world-and-systems.md) — Entities, SoA transforms, keyboard/mouse & **SDL3 Gamepad / Steam Deck input & rumble**.
- [`docs/agents/04-scenes-and-asset-bundles.md`](docs/agents/04-scenes-and-asset-bundles.md)
- [`docs/agents/05-gltf-and-3d-rendering.md`](docs/agents/05-gltf-and-3d-rendering.md)
- [`docs/agents/06-2d-sprites-in-3d-and-batching.md`](docs/agents/06-2d-sprites-in-3d-and-batching.md)
- [`docs/agents/07-text-fonts-and-text-effects.md`](docs/agents/07-text-fonts-and-text-effects.md)
- [`docs/agents/08-tweening-and-animation.md`](docs/agents/08-tweening-and-animation.md)
- [`docs/agents/09-particles-and-shaders.md`](docs/agents/09-particles-and-shaders.md)
- [`docs/agents/10-camera-and-headless-testing.md`](docs/agents/10-camera-and-headless-testing.md)
- [`docs/agents/11-vsync-fps-cap-and-profiling.md`](docs/agents/11-vsync-fps-cap-and-profiling.md) — Borderless Full Window default, 2-Thread Pipelined Engine Loop, VSync (default ON), FPS cap (default 60), `FrameProfiler`, `SetPassCalls`, and separate-bar `ProfilerOverlay` UI.
- [`docs/agents/12-multithreading-and-jobs.md`](docs/agents/12-multithreading-and-jobs.md) — Native first-class data-oriented `JobSystem` (`ParallelFor`, async DAGs, `CombineDependencies`, cooperative cancel, per-worker scratch arenas, and when NOT to multithread).
- [`docs/agents/13-asset-compression-atlases-and-9slice.md`](docs/agents/13-asset-compression-atlases-and-9slice.md) — Unity-style BC1/BC3 GPU texture compression, LZ4/Brotli `.bpack` & glTF/GLB compression, default texture atlasing (zero loose textures in final builds), `BringPacker` CLI, and 9-slice 2D UI sprites.
- [`src/ShowcaseGame/AGENT_README.md`](src/ShowcaseGame/AGENT_README.md) — Complete template for creating a new game.

## 4. Build, Publish & CI Verification Commands
> **CI-Only Engine Testing**: Do **NOT** run engine test suites locally and tie up the developer's machine. Push engine changes to GitHub and let GitHub Actions (`.github/workflows/build.yml` / `.github/workflows/release.yml`) execute the full cross-platform test matrix (`gh run watch --exit-status`). The commands below document what CI runs (and how to build/run `ShowcaseGame` or `BringPacker` when needed):
```bash
# Solution build (Desktop CoreCLR .NET 10)
dotnet build BringEngine.sln -c Release

# CoreCLR Engine Test Suite (executed on GitHub Actions CI across win-x64, linux-x64, osx-arm64 — 35 tests)
dotnet ./tests/BringEngine.Tests/bin/Release/net10.0/BringEngine.Tests.dll

# Self-contained Single-File ReadyToRun BringPacker CLI tool
dotnet publish tools/BringPacker/BringPacker.csproj -c Release -r win-x64
./tools/BringPacker/bin/Release/net10.0/win-x64/publish/BringPacker.exe --help

# Self-contained Single-File ReadyToRun ShowcaseGame (Windows / Linux & Steam Deck / macOS)
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64
./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --headless --ticks 600
./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --verify-render

# Play ShowcaseGame interactively (instant CoreCLR dev run)
dotnet run --project src/ShowcaseGame/ShowcaseGame.csproj -c Release
```

## 5. Engine Changes, Mandatory Testing & Releasing

Games built with Bring Engine consume released SDK archives from GitHub (`bratyxd/bring-engine`), so engine changes reach games through verified GitHub releases.

### Mandatory Testing Rules (When Working on the Engine)
1. **Every added feature or bug fix MUST ship with tests in `tests/BringEngine.Tests/`**:
   - Whenever you add an engine capability, public API, or subsystem change, add test cases in `tests/BringEngine.Tests/` that thoroughly exercise the new feature (happy paths, edge cases, zero-GC hot loops via `GC.GetAllocatedBytesForCurrentThread() == 0`, and zero native leaks via `NativeMemoryTracker.LiveBytes == 0`).
   - Whenever you fix an engine bug, add a regression test that fails without the fix and passes with it.
   - A feature or fix and its tests go in the same commit.
2. **Run engine tests on GitHub Actions CI ONLY — do NOT keep the local machine busy**:
   - **NEVER** run the engine test suite or heavy multi-project publishes locally on the user's machine.
   - Instead, commit and push your changes (or open a PR) and watch GitHub Actions (`.github/workflows/build.yml`) execute the full CoreCLR build, `BringEngine.Tests` (35 tests), `BringPacker`, and `ShowcaseGame` (`--headless` + `--verify-render`) matrix across Windows, Linux, and macOS:
     ```bash
     git push origin main
     gh run watch --exit-status
     ```
   - Update `docs/agents/` and `AGENTS.md` whenever public APIs, defaults, or workflows change.
3. **Engine changes from a game**:
   - Only modify the engine when it is wrong (bug, crash, violated contract) or lacks a general capability a game cannot reasonably build on its own. Nothing game-specific goes into the engine.
   - Never patch files inside a downloaded `~/.bring/sdk/<tag>/` folder or copy engine sources into a game repository.

### GitHub CI & Releasing (`vX.Y.Z`)
- `.github/workflows/build.yml` runs on every push and PR across Windows (`win-x64`), Linux / Steam Deck (`linux-x64`), and macOS (`osx-arm64`), building on Desktop CoreCLR, running `BringEngine.Tests`, and verifying self-contained ReadyToRun `BringPacker` and `ShowcaseGame`.
- `.github/workflows/release.yml` runs the full cross-platform test suite, packages `bring-sdk-vX.Y.Z.zip`, verifies standalone builds against the extracted SDK archive, and publishes the GitHub Release ONLY when every test passes.
- Version numbers live in `src/BringEngine/EngineInfo.cs` (`EngineInfo.Version = "X.Y.Z"`): bump patch for bug fixes, minor for new features/API.

```bash
# 1. Confirm main CI is green
gh run list --branch main --limit 1

# 2. Bump version in src/BringEngine/EngineInfo.cs, commit & push
git add src/BringEngine/EngineInfo.cs
git commit -m "build: version X.Y.Z"
git push
gh run watch --exit-status

# 3. Tag and push to trigger verified GitHub release
git tag vX.Y.Z
git push origin vX.Y.Z
gh run watch --exit-status
gh release view vX.Y.Z
```

A release is public and immutable: tag only from a green `main`, never move or reuse a tag; fix any issue in the next patch release.
