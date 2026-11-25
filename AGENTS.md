# Bring Engine — Master AI Agent Handbook

> **Audience**: AI coding agents building games on **Bring Engine** or extending engine subsystems.
> Every subsystem is documented in `docs/agents/` and demonstrated end-to-end in `src/ShowcaseGame/`.

## 1. Non-Negotiable Architecture Rules

1. **NO OOP — Pure Data + Systems**
   - NEVER use class inheritance, `virtual`, `override`, `abstract`, or interface dispatch in engine or game code.
   - Store all state in `unmanaged struct` tables (Structure-of-Arrays or dense sparse sets) referenced by 32-bit generational handles (`EntityId`, `SceneId`, `BundleId`, `MeshHandle`, `TextureHandle`, `MaterialHandle`, `ShaderHandle`, `FontHandle`, `GltfHandle`, `AnimClipHandle`, `SkeletonHandle`, `TweenHandle`, `EmitterHandle`, `CameraHandle`, `SpriteHandle`, `TextHandle`).
   - Put all behavior in `public static unsafe class <Feature>System` methods iterating contiguous native memory linearly.
   - Use unmanaged function pointers (`delegate*<...>`) for bundle loaders, scene lifecycle hooks, and tween completion callbacks.

2. **Strict NativeAOT Compatibility (Hard Compile Errors)**
   - `Directory.Build.props` enables `IsAotCompatible=true`, `EnableAotAnalyzer=true`, `EnableTrimAnalyzer=true`, and promotes **all `IL2xxx` and `IL3xxx` warnings to compile errors**.
   - NEVER use `System.Reflection`, `System.Linq.Expressions`, `dynamic`, or reflection-based JSON serialization (use `Utf8JsonReader`).
   - Always verify with `dotnet publish -c Release -r <rid>`.

3. **Explicit Native Memory Allocators & Zero-GC Hot Paths**
   - Create `EngineAllocators` (`Persistent`, `Frame`, `Scratch`, `Dynamic`) and `GameAllocators` (`Persistent`, `Level`, `Frame`) at startup.
   - Call `BeginFrame()` every frame to reset `Frame` arenas in O(1).
   - Use `SaveMarker()` / `Rollback()` on `Scratch` arenas for scoped parsing/layout.
   - Store bulk assets inside per-bundle `LinearArena`s and scene data inside per-scene `LinearArena`s so unloading immediately reclaims 100% of native RAM and GPU VRAM.
   - Hot simulation and render loops MUST allocate **0 bytes** on the managed GC heap.

4. **Strict Simulation vs. View Separation (Headless First)**
   - **Simulation** (`BringEngine.Simulation.*`, `<Game>.Simulation.*`): Deterministic fixed-timestep (`Fix32`/`FixVec3`, `DetRng`). ZERO SDL3 or GPU calls. Must run headlessly with bit-exact state hashes across runs.
   - **View** (`BringEngine.Rendering.*`, `<Game>.View.*`): Reads simulation state and `SimEvent` queues to sync 3D glTF models, 2D-in-3D sprites, animated SDF text, particles, lighting, and cameras. Never mutates gameplay truth.

5. **Cache Locality & GPU Batching**
   - Pack vertices/instances in 16/64-byte aligned structs with `[StructLayout(LayoutKind.Sequential)]`.
   - `BatchRenderer.RenderFrame` performs CPU frustum culling, sorts & merges 3D meshes into instanced draw calls, batches 2D sprites in 3D/HUD, batches SDF glyphs, batches SoA particles, uploads all dynamic storage buffers in **ONE `SDL_GPUCopyPass` per frame**, and deduplicates pipeline/buffer binds.

## 2. Solution Layout
- `BringEngine.sln` & `Directory.Build.props` — Solution and strict AOT compiler settings.
- `src/BringEngine/` — Core Engine Library (`IsAotCompatible=true`):
  - `Core/Memory/` — `NativeMemoryTracker`, `LinearArena`, `PoolAllocator<T>`, `FreeListAllocator`, `EngineAllocators`, `NativeSlice<T>`, `NativeList<T>`, `NativeSparseSet<T>`, `NativeHashMap<T>`, `FixedString64/256`, `StringId`.
  - `Core/Handles/` — 32-bit generational handles (`HandlePacking`, `EntityId`, ...).
  - `Core/Math/` — `Fix32`, `Fix64`, `FixVec2/3`, `FixQuat`, `DetMath`, `DetRng`, `Vec2/3/4`, `Quat`, `Mat4`, `ColorF/32`, `Bounds3`, `Plane`, `Frustum`, `Ray`.
  - `Simulation/` — `EntityTable`, `TransformTable`, `EngineWorld`, `CameraTable`, `InputState`, `Easing`, `TweenTable`, `AnimationTable`, `ParticleTable`, `RenderComponentTables`, `SceneManager`.
  - `Assets/` — `AssetRegistry`, `BundleManager`, `TextureLoader`, `SdfFontLoader`, `GltfLoader`, `GlbBuilder`.
  - `Rendering/` — `SdlPlatform`, `GpuContext`, `ShaderSystem` (`SDL_shadercross`), `SceneLighting`, `TextLayoutSystem`, `BatchRenderer`.
  - `Shaders/` — `pbr_mesh.hlsl`, `sprite_3d.hlsl`, `sdf_text.hlsl`, `particle.hlsl`.
- `src/ShowcaseGame/` — Playable Reference Game **"Bring Arena"** (`PublishAot=true`, depends on `BringEngine`).
- `tests/BringEngine.Tests/` — Zero-reflection NativeAOT test suite (`PublishAot=true`, 19 tests covering core, sim, tweens, animation, particles, bundles, scenes, glTF, SDF fonts, text layout, and real SDL3_gpu instanced rendering).

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
- [`src/ShowcaseGame/AGENT_README.md`](src/ShowcaseGame/AGENT_README.md) — Complete template for creating a new game.

## 4. Build, NativeAOT Publish & Verification Commands
```bash
# Build solution (strict AOT analyzers active)
dotnet build BringEngine.sln -c Release

# Publish & run NativeAOT Engine Test Suite (19 tests incl. real SDL3_gpu batching)
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
