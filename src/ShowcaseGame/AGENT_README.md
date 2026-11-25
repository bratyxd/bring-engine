# AI Agent Game Blueprint — Building Games on Bring Engine (`ShowcaseGame`)

> Use `src/ShowcaseGame/` as your **canonical template** whenever creating a new game with Bring Engine.

## 1. Folder Structure (Strict Simulation vs. View Separation)
```text
src/<YourGame>/
├── <YourGame>.csproj           # PublishAot=true, ProjectReference -> BringEngine
├── Program.cs                  # Bootstraps allocators, EngineWorld, Bundles, Scenes, Headless & Windowed loops
├── Simulation/                 # ZERO SDL/GPU references! Runs headlessly & deterministically.
│   ├── GameAllocators.cs       # Persistent, Level, Frame LinearArenas created at startup
│   ├── GameSimState.cs         # Unmanaged SoA structs (Fix32/FixVec3, DetRng, SimEventQueue)
│   └── GameSimSystems.cs       # Static unsafe systems: ResetMatch, StepFixed, ComputeStateHash
├── View/                       # Reads GameSimState & SimEventQueue; never mutates gameplay truth.
│   ├── GameAssets.cs           # Asset bundle loader function pointers (.glb, textures, SDF fonts)
│   ├── GameViewState.cs        # Visual handles (EntityId, SpriteHandle, TextHandle, CameraHandle, EmitterHandle)
│   └── GameViewSystems.cs      # Scene hooks (OnLoad, OnUnload, OnViewSync) syncing visuals
└── Assets/
    ├── models/*.glb            # First-class glTF 2.0 binary models
    └── Shaders/*.hlsl          # Custom game HLSL shaders (SDL_shadercross conventions)
```

## 2. Golden Rules Checklist for Game Agents
1. **Allocate once at startup**: Create `EngineAllocators` and `GameAllocators` in `Program.Main`. Call `BeginFrame()` every frame; NEVER allocate managed objects in `StepFixed` or `OnViewSync`.
2. **Pure Data + Static Systems**: No classes for entities, enemies, bullets, or UI widgets. Put arrays in `GameSimState` (`FixVec3* EnemyPos`, `byte* EnemyAlive`, ...) and iterate linearly in `static unsafe class GameSimSystems`.
3. **Deterministic Gameplay**: Use `Fix32` / `FixVec3` and `DetRng` inside `Simulation/`. Never call `Random.Shared` or `DateTime.Now`.
4. **Decoupled View Sync via Events**: Emit plain `SimEvent` structs from `Simulation/` (`EnemyHit`, `EnemyKilled`, `ItemCollected`). Consume them in `View/` to trigger `ParticleSystem.Burst`, `CameraSystem.AddTrauma`, `TweenSystem`, and floating 3D SDF `TextData`.
5. **Bundles & Unity-Like Scenes**:
   - Put shared assets (font, glow texture, UI sprites) in a `common` bundle.
   - Put scene-specific `.glb` models in scene-specific bundles (`title`, `arena`).
   - Switch main scenes with `SceneLoadMode.Single` (automatically frees old unshared bundles & RAM/VRAM!).
   - Overlay HUD/pause menus with `SceneLoadMode.Additive` + a secondary Orthographic camera (`DepthOrder = 100`, `LayerMask = 1u << 31`).
6. **Custom Shaders + Tweens**:
   - Compile custom HLSL shaders via `ShaderSystem.CompileFromFile(...)` and attach to `MaterialAssetData.CustomShader`, `SpriteData.CustomShader`, `TextData.CustomShader`, or `ParticleEmitterData.CustomShader`.
   - Animate `MaterialAssetData.CustomParams` directly with `TweenSystem.TweenFloatPtr(ref world.Tweens, &mat->CustomParams.X, ...)`.
7. **VSync, FPS Cap & Common Profiler UI (Standard in Every Game)**:
   - Default to **VSync ON** and **60 FPS cap** via `FramePacer.CreateDefault()` + `FramePacerSystem.ApplyVSync(ref pacer, ref *gpu, true)`.
   - Wire `FrameProfiler` (`FrameProfiler.Create(ref engAlloc.Persistent)`) and `ProfilerOverlaySystem.Create(ref world, mainFont, whiteTex, w, h)` so every game automatically gets the drop-in CPU ms, GPU ms, FPS, Draw Calls, SetPass Calls, and CPU scope overlay (persisting across all scenes!).
   - Standard debug hotkeys: **`F1`** toggle Profiler UI, **`F2`** toggle VSync, **`F3`** cycle FPS cap (60/120/144/Max/30).
   - See [`docs/agents/11-vsync-fps-cap-and-profiling.md`](../../docs/agents/11-vsync-fps-cap-and-profiling.md) for the exact per-frame loop ordering (`BeginFrame` -> scopes -> `RenderFrame(..., profiler)` -> `EndCpuWork` -> `EndFrameAndPace` -> `EndFrame`).
8. **Always Verify Both Headless & NativeAOT**:
   ```bash
   dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64
   ./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --headless --ticks 600
   ./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --verify-render
   ```
