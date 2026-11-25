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
1. **Mandatory NativeAOT Release Target (Non-Negotiable)**:
   - **All Bring Engine games are released as NativeAOT native binaries.** Every game `.csproj` MUST set `<PublishAot>true</PublishAot>` (plus inheriting `Directory.Build.props`, where all `IL2xxx` and `IL3xxx` AOT/trim analyzer warnings are hard compile errors).
   - **NEVER** use `System.Reflection`, `System.Linq.Expressions`, `dynamic`, runtime codegen (`Reflection.Emit`), reflection-based JSON (`JsonSerializer` without source gen — use `Utf8JsonReader`/`Utf8JsonWriter` or binary formats instead), or non-AOT-compatible NuGet packages.
   - Always treat `dotnet publish -c Release -r <rid>` (not just `dotnet build` or `dotnet run`) as the required verification gate before considering any game feature done.
2. **Allocate once at startup**: Create `EngineAllocators` and `GameAllocators` in `Program.Main`. Call `BeginFrame()` every frame; NEVER allocate managed objects in `StepFixed` or `OnViewSync`.
3. **Pure Data + Static Systems**: No classes for entities, enemies, bullets, or UI widgets. Put arrays in `GameSimState` (`FixVec3* EnemyPos`, `byte* EnemyAlive`, ...) and iterate linearly in `static unsafe class GameSimSystems`.
4. **Deterministic Gameplay**: Use `Fix32` / `FixVec3` and `DetRng` inside `Simulation/`. Never call `Random.Shared` or `DateTime.Now`.
5. **Decoupled View Sync via Events**: Emit plain `SimEvent` structs from `Simulation/` (`EnemyHit`, `EnemyKilled`, `ItemCollected`). Consume them in `View/` to trigger `ParticleSystem.Burst`, `CameraSystem.AddTrauma`, `TweenSystem`, and floating 3D SDF `TextData`.
6. **Bundles & Unity-Like Scenes**:
   - Put shared assets (font, glow texture, UI sprites) in a `common` bundle.
   - Put scene-specific `.glb` models in scene-specific bundles (`title`, `arena`).
   - Switch main scenes with `SceneLoadMode.Single` (automatically frees old unshared bundles & RAM/VRAM!).
   - Overlay HUD/pause menus with `SceneLoadMode.Additive` + a secondary Orthographic camera (`DepthOrder = 100`, `LayerMask = 1u << 31`).
7. **Custom Shaders + Tweens**:
   - Compile custom HLSL shaders via `ShaderSystem.CompileFromFile(...)` and attach to `MaterialAssetData.CustomShader`, `SpriteData.CustomShader`, `TextData.CustomShader`, or `ParticleEmitterData.CustomShader`.
   - Animate `MaterialAssetData.CustomParams` directly with `TweenSystem.TweenFloatPtr(ref world.Tweens, &mat->CustomParams.X, ...)`.
8. **Borderless Full Window, 2-Thread Pipeline, VSync/FPS Cap & Common Profiler UI (Standard in Every Game)**:
   - **Borderless Full Window by Default**: Always create interactive windows with `PlatformSystem.InitWindow(title, 1280, 720, hidden: false, mode: WindowMode.BorderlessFullscreen)` (already the default!). Support `--windowed` CLI flag and `F11` runtime toggle (`PlatformSystem.ToggleBorderlessFullscreen(ref window)`).
   - **2-Thread Pipelined Loop (`EnginePipelineSystem`)**: Run Simulation + ViewSync + `StepCoreSystems` + `BatchRenderer.ExtractRenderPacket` on the dedicated Logic Thread while the Main/Render Thread concurrently submits the previous frame's immutable `RenderPacket` (`EnginePipelineSystem.StepPipelinedFrame`). Execute `SceneSystem.LoadScene` + `BatchRenderer.UploadPendingAssets` cleanly at the frame barrier after `EnginePipelineSystem.FlushBeforeSceneTransition`.
   - **VSync ON + 60 FPS Cap**: Default via `FramePacer.CreateDefault()` + `FramePacerSystem.ApplyVSync(ref *pacer, ref *gpu, true)`.
   - **Common Opaque Profiler Overlay**: Wire `FrameProfiler` and `ProfilerOverlaySystem.Create(ref *world, mainFont, whiteTex, w, h)` for automatic independent **`LOGIC`**, **`RENDER`**, and **`GPU`** (pure GPU work excluding VSync wait) fill bars, FPS, `DRAWS`, `SETPASS`, `PASSES`, and named scopes (persists across all scenes!).
   - **Standard Hotkeys**: **`F1`** toggle Profiler UI, **`F2`** toggle VSync, **`F3`** cycle FPS cap (60/120/144/Max/30), **`F11`** toggle Borderless Full Window / Windowed.
   - See [`docs/agents/11-vsync-fps-cap-and-profiling.md`](../../docs/agents/11-vsync-fps-cap-and-profiling.md) for the exact 2-thread loop blueprint.
9. **Always Verify NativeAOT Publish, Headless Determinism & GPU Render**:
   ```bash
   dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64
   ./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --headless --ticks 600
   ./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe --verify-render
   ```
