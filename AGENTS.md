# Bring Engine — Master AI Agent Handbook

> **Audience**: AI coding agents building games on **Bring Engine** or extending engine subsystems.
> Every subsystem is documented in `docs/agents/` and demonstrated end-to-end in `src/ShowcaseGame/`.

## 1. Non-Negotiable Architecture Rules

1. **NO OOP — Pure Data + Systems**
   - NEVER use class inheritance, `virtual`, `override`, `abstract`, or interface dispatch in engine or game code.
   - Store all state in `unmanaged struct` tables (Structure-of-Arrays or dense sparse sets) referenced by 32-bit generational handles (`EntityId`, `SceneId`, `BundleId`, `MeshHandle`, `TextureHandle`, `MaterialHandle`, `ShaderHandle`, `FontHandle`, `GltfHandle`, `AnimClipHandle`, `SkeletonHandle`, `TweenHandle`, `EmitterHandle`, `CameraHandle`, `SpriteHandle`, `TextHandle`).
   - Put all behavior in `public static unsafe class <Feature>System` methods iterating contiguous native memory linearly.
   - Use unmanaged function pointers (`delegate*<...>`) for bundle loaders, scene lifecycle hooks, and tween completion callbacks.

2. **Strict NativeAOT Compatibility — Mandatory Release Target (Hard Compile Errors)**
   - **All Bring Engine games release as NativeAOT native executables.** Every game `.csproj` MUST enable `<PublishAot>true</PublishAot>`.
   - `Directory.Build.props` enables `IsAotCompatible=true`, `EnableAotAnalyzer=true`, `EnableTrimAnalyzer=true`, and promotes **all `IL2xxx` and `IL3xxx` warnings to compile errors**.
   - NEVER use `System.Reflection`, `System.Linq.Expressions`, `dynamic`, runtime codegen (`Reflection.Emit`), reflection-based JSON serialization (use `Utf8JsonReader`), or non-AOT-compatible packages.
   - Always verify both engine and game changes with `dotnet publish -c Release -r <rid>` (not just `dotnet build`).

3. **Explicit Native Memory Allocators & Zero-GC Hot Paths**
   - Create `EngineAllocators` (`Persistent`, `Frame`, `Scratch`, `Dynamic`) and `GameAllocators` (`Persistent`, `Level`, `Frame`) at startup.
   - Call `BeginFrame()` every frame to reset `Frame` arenas in O(1).
   - Use `SaveMarker()` / `Rollback()` on `Scratch` arenas for scoped parsing/layout.
   - Store bulk assets inside per-bundle `LinearArena`s and scene data inside per-scene `LinearArena`s so unloading immediately reclaims 100% of native RAM and GPU VRAM.
   - Hot simulation and render loops MUST allocate **0 bytes** on the managed GC heap.

4. **Strict Simulation vs. View Separation & 2-Thread Pipelined Execution**
   - **Simulation** (`BringEngine.Simulation.*`, `<Game>.Simulation.*`): Deterministic fixed-timestep (`Fix32`/`FixVec3`, `DetRng`). ZERO SDL3 or GPU calls. Must run headlessly with bit-exact state hashes across runs.
   - **View** (`BringEngine.Rendering.*`, `<Game>.View.*`): Reads simulation state and `SimEvent` queues to sync 3D glTF models, 2D-in-3D sprites, animated SDF text, particles, lighting, and cameras. Never mutates gameplay truth.
   - **2-Thread Pipeline (`EnginePipelineSystem`)**: Interactive games run a dedicated **Logic/Update Thread** (Frame `N`: Sim + ViewSync + CoreSystems + `BatchRenderer.ExtractRenderPacket` into double-buffered `EngineAllocators.Frame`) concurrently with the **Main/Render Thread** (Frame `N-1`: SDL3 window/events + `BatchRenderer.SubmitRenderPacket` from `EngineAllocators.FrameBack` + `FramePacer`). All SDL3 window/swapchain/event calls remain on the Main Thread per SDL3 requirements.
   - **Borderless Full Window Default**: ALL Bring Engine games MUST launch in **Borderless Full Window** mode by default (`WindowMode.BorderlessFullscreen`, default in `PlatformSystem.InitWindow`), with `F11` runtime toggle to windowed mode and `--windowed` CLI override.

5. **Cache Locality & GPU Batching**
   - Pack vertices/instances in 16/64-byte aligned structs with `[StructLayout(LayoutKind.Sequential)]`.
   - `BatchRenderer.ExtractRenderPacket` performs CPU frustum culling, deterministic sorting & merging of 3D meshes, 2D sprites in 3D/HUD, SDF glyphs, and SoA particles into a self-contained `RenderPacket` inside `FrameArena`.
   - `BatchRenderer.SubmitRenderPacket` uploads all dynamic storage buffers in **ONE `SDL_GPUCopyPass` per frame** (`cycle: true`), renders to an offscreen `SceneColorTexture` (cycling color/depth cleanly on initial clear, binding pipelines before storage buffers, and deduplicating state binds), measures pure GPU time via fence bracket before presenting, and blits to the swapchain on a separate `cmdPresent` so VSync wait never pollutes GPU timings.

## 2. Solution Layout
- `BringEngine.sln` & `Directory.Build.props` — Solution and strict AOT compiler settings.
- `src/BringEngine/` — Core Engine Library (`IsAotCompatible=true`):
  - `Core/Memory/` — `NativeMemoryTracker`, `LinearArena`, `PoolAllocator<T>`, `FreeListAllocator`, `EngineAllocators` (double-buffered `Frame`/`FrameBack`), `NativeSlice<T>`, `NativeList<T>`, `NativeSparseSet<T>`, `NativeHashMap<T>`, `FixedString64/256`, `StringId`.
  - `Core/Profiling/` — `FrameProfiler`, `ProfilerSystem` (zero-GC independent Logic CPU / Render Thread / GPU ms timing, named scopes, rolling FPS).
  - `Core/Threading/` — `EnginePipelineState`, `EnginePipelineSystem` (2-thread pipelined Logic + Render orchestrator, zero-GC, zero locks).
  - `Core/Handles/` — 32-bit generational handles (`HandlePacking`, `EntityId`, ...).
  - `Core/Math/` — `Fix32`, `Fix64`, `FixVec2/3`, `FixQuat`, `DetMath`, `DetRng`, `Vec2/3/4`, `Quat`, `Mat4`, `ColorF/32`, `Bounds3`, `Plane`, `Frustum`, `Ray`.
  - `Simulation/` — `EntityTable`, `TransformTable`, `EngineWorld`, `CameraTable`, `InputState`, `Easing`, `TweenTable`, `AnimationTable`, `ParticleTable`, `RenderComponentTables`, `SceneManager`.
  - `Assets/` — `AssetRegistry`, `BundleManager`, `TextureLoader`, `SdfFontLoader`, `GltfLoader`, `GlbBuilder`.
  - `Rendering/` — `SdlPlatform` (`WindowMode.BorderlessFullscreen` default + `F11` toggle), `FramePacer` (default VSync ON + 60 FPS cap), `GpuContext`, `ShaderSystem` (`SDL_shadercross`), `SceneLighting`, `TextLayoutSystem`, `RenderPacket`, `BatchRenderer`, `ProfilerOverlay` (100% opaque drop-in UI tool with separate `LOGIC` / `RENDER` / `GPU` bars excluding VSync wait).
  - `Shaders/` — `pbr_mesh.hlsl`, `sprite_3d.hlsl`, `sdf_text.hlsl`, `particle.hlsl`.
- `src/ShowcaseGame/` — Playable Reference Game **"Bring Arena"** (`PublishAot=true`, depends on `BringEngine`).
- `tests/BringEngine.Tests/` — Zero-reflection NativeAOT test suite (`PublishAot=true`, 23 tests covering core, sim, tweens, animation, particles, bundles, scenes, glTF, SDF fonts, text layout, 2-thread pipelined rendering, and SDL3_gpu debug validation).

## 3. Subsystem Agent Guides
- [`docs/agents/01-memory-and-allocators.md`](docs/agents/01-memory-and-allocators.md)
- [`docs/agents/02-deterministic-math-and-data.md`](docs/agents/02-deterministic-math-and-data.md)
- [`docs/agents/03-ecs-world-and-systems.md`](docs/agents/03-ecs-world-and-systems.md)
- [`docs/agents/04-scenes-and-asset-bundles.md`](docs/agents/04-scenes-and-asset-bundles.md)
- [`docs/agents/05-gltf-and-3d-rendering.md`](docs/agents/05-gltf-and-3d-rendering.md)
- [`docs/agents/06-2d-sprites-in-3d-and-batching.md`](docs/agents/06-2d-sprites-in-3d-and-batching.md)
- [`docs/agents/07-text-fonts-and-text-effects.md`](docs/agents/07-text-fonts-and-text-effects.md)
- [`docs/agents/08-tweening-and-animation.md`](docs/agents/08-tweening-and-animation.md)
- [`docs/agents/09-particles-and-shaders.md`](docs/agents/09-particles-and-shaders.md)
- [`docs/agents/10-camera-and-headless-testing.md`](docs/agents/10-camera-and-headless-testing.md)
- [`docs/agents/11-vsync-fps-cap-and-profiling.md`](docs/agents/11-vsync-fps-cap-and-profiling.md) — Borderless Full Window default, 2-Thread Pipelined Engine Loop, VSync (default ON), FPS cap (default 60), `FrameProfiler`, `SetPassCalls`, and separate-bar `ProfilerOverlay` UI.
- [`src/ShowcaseGame/AGENT_README.md`](src/ShowcaseGame/AGENT_README.md) — Complete template for creating a new game.

## 4. Build, NativeAOT Publish & Verification Commands
```bash
# Build solution (strict AOT analyzers active)
dotnet build BringEngine.sln -c Release

# Publish & run NativeAOT Engine Test Suite (23 tests incl. 2-thread pipeline, VSync-decoupled GPU timing & SDL3_gpu debug validation)
dotnet publish tests/BringEngine.Tests/BringEngine.Tests.csproj -c Release -r win-x64
./tests/BringEngine.Tests/bin/Release/net10.0/win-x64/publish/BringEngine.Tests.exe

# Publish ShowcaseGame to NativeAOT binary
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64

# Run headless determinism, zero-GC & zero-leak audit
./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --headless --ticks 600

# Run automated GPU render verification (Title + Arena + Additive HUD -> PNGs)
./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --verify-render

# Play ShowcaseGame interactively
./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe
```

## 5. Engine Changes, Mandatory Testing & Releasing

Games built with Bring Engine consume released SDK archives from GitHub (`bratyxd/bring-engine`), so engine changes reach games through verified GitHub releases.

### Mandatory Testing Rules (When Working on the Engine)
1. **Every added feature or bug fix MUST ship with tests in `tests/BringEngine.Tests/`**:
   - Whenever you add an engine capability, public API, or subsystem change, add NativeAOT test cases in `tests/BringEngine.Tests/` that thoroughly exercise the new feature (happy paths, edge cases, zero-GC hot loops via `GC.GetAllocatedBytesForCurrentThread() == 0`, and zero native leaks via `NativeMemoryTracker.LiveBytes == 0`).
   - Whenever you fix an engine bug, add a regression test that fails without the fix and passes with it.
   - A feature or fix and its tests go in the same commit.
2. **Always run the full engine test suite before pushing**:
   - After ANY engine change, verify you did not break anything in the engine by running:
     ```bash
     dotnet build BringEngine.sln -c Release
     dotnet publish tests/BringEngine.Tests/BringEngine.Tests.csproj -c Release -r win-x64
     ./tests/BringEngine.Tests/bin/Release/net10.0/win-x64/publish/BringEngine.Tests.exe
     dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64
     ./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --headless --ticks 600
     ./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --verify-render
     ```
   - Update `docs/agents/` and `AGENTS.md` whenever public APIs, defaults, or workflows change.
3. **Engine changes from a game**:
   - Only modify the engine when it is wrong (bug, crash, violated contract) or lacks a general capability a game cannot reasonably build on its own. Nothing game-specific goes into the engine.
   - Never patch files inside a downloaded `~/.bring/sdk/<tag>/` folder or copy engine sources into a game repository.

### GitHub CI & Releasing (`vX.Y.Z`)
- `.github/workflows/build.yml` runs on every push and PR across Windows (`win-x64`), Linux (`linux-x64`), and macOS (`osx-arm64`), building with strict NativeAOT analyzers, running `BringEngine.Tests`, and verifying `ShowcaseGame`.
- `.github/workflows/release.yml` runs the full cross-platform NativeAOT test suite, packages `bring-sdk-vX.Y.Z.zip`, verifies a standalone NativeAOT build against the extracted SDK archive, and publishes the GitHub Release ONLY when every test passes.
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
