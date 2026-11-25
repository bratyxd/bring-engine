# Bring Engine — Master AI Agent Handbook

> **Audience**: AI coding agents building games with **Bring Engine** or extending the engine itself.
> Every subsystem has a dedicated guide in `docs/agents/` updated alongside every milestone.

## 1. Non-Negotiable Architecture Rules

1. **NO OOP — Pure Data + Systems**
   - NEVER use class inheritance, `virtual`, `override`, `abstract`, or interface-based polymorphism in engine or game logic.
   - Store all state in `unmanaged struct` tables (SoA or dense packed arrays) using generational handles (`EntityId`, `MeshHandle`, `TextureHandle`, `TweenHandle`, etc.).
   - Put all behavior in `public static unsafe class <Feature>System` methods that iterate contiguous memory linearly.
   - Use unmanaged function pointers (`delegate*<...>`) instead of delegates/closures for lifecycle hooks and callbacks.

2. **Strict NativeAOT Compatibility**
   - `Directory.Build.props` promotes ALL `IL2xxx` and `IL3xxx` warnings to **compile errors**.
   - NEVER use `System.Reflection`, `System.Linq.Expressions`, `dynamic`, `JsonSerializer.Deserialize<T>()` without source-gen (prefer `Utf8JsonReader`), or runtime codegen.
   - Always verify major changes with `dotnet publish -c Release -r <rid>`.

3. **Explicit Memory Allocators & Zero-GC Hot Paths**
   - Create allocators once at startup (`EngineAllocators`, `GameAllocators`).
   - Use `Frame` arena for per-frame transient data (draw lists, sort keys, temp layout, queries) — it resets in O(1) every frame.
   - Use `Scratch` arena with `SaveMarker()`/`Rollback()` for temporary parsing/decoding.
   - Use per-`Scene` and per-`Bundle` arenas so unloading a scene or asset bundle reclaims 100% of native memory immediately.
   - NEVER allocate managed objects (`new Class()`, `List<T>`, `string` concatenation, LINQ, boxing, closures) inside fixed-update or render loops.

4. **Strict Simulation vs. View Separation**
   - **Simulation** (`BringEngine.Simulation.*`, `<Game>.Simulation.*`): Deterministic fixed-timestep data and systems. MUST NOT call SDL3 or GPU APIs. Must be runnable 100% headlessly.
   - **View** (`BringEngine.Rendering.*`, `<Game>.View.*`): Reads simulation tables to build render batches (3D glTF, 2D sprites in 3D, SDF text, particles, shaders). Never mutates gameplay truth.

5. **Cache Locality & GPU Batching**
   - Favor Structure-of-Arrays (SoA) and dense sparse sets (`NativeSparseSet<T>`) for hot iteration.
   - Align arena allocations to 16/64 bytes; use explicit `[StructLayout(LayoutKind.Sequential)]` on all GPU vertex/instance/uniform structs.
   - Minimize draw calls and GPU state binds: sort and batch 3D meshes (instanced), sprites, SDF glyphs, and particles.

## 2. Project Layout
- `src/BringEngine/` — Core engine library (`IsAotCompatible=true`).
- `src/ShowcaseGame/` — Separate playable reference game (`PublishAot=true`) demonstrating every engine feature.
- `tests/BringEngine.Tests/` — Zero-reflection NativeAOT verification suite (`PublishAot=true`).

## 3. Subsystem Guides (`docs/agents/`)
*(Guides are added as each subsystem lands)*
- `01-memory-and-allocators.md` — Arenas, pools, unmanaged containers, leak detection.
- `02-deterministic-math-and-data.md` — `Fix32`/`Fix64`, deterministic trig, `DetRng`, SIMD render math.
- `03-ecs-world-and-systems.md` — Entities, sparse sets, transform hierarchy, `InputState`.
- `04-scenes-and-asset-bundles.md` — Unity-style scenes (`Single`/`Additive`) and ref-counted asset bundles.
- `05-gltf-and-3d-rendering.md` — First-class glTF 2.0/`.glb` loading, instanced PBR 3D batching, skinning.
- `06-2d-sprites-in-3d-and-batching.md` — Unity-style 2D sprites in 3D space, billboards, sprite sheets, HUD.
- `07-text-fonts-and-text-effects.md` — SDF font baking, 3D/2D layout, per-char animation, tweening & shaders.
- `08-tweening-and-animation.md` — Universal data-driven tweens, sequences, curves, skeletal/sprite clips.
- `09-particles-and-shaders.md` — SoA GPU-batched particles and custom HLSL shaders via `SDL_shadercross`.
- `10-camera-and-headless-testing.md` — Multi-camera setup, follow/orbit/shake, headless determinism testing.
- `src/ShowcaseGame/AGENT_README.md` — Blueprint for AI agents creating a new game on Bring Engine.

## 4. Build & Verify Commands
```bash
dotnet build BringEngine.sln -c Release
dotnet publish tests/BringEngine.Tests/BringEngine.Tests.csproj -c Release -r win-x64
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64
```
