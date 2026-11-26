# AI Agent Game Blueprint — Building Games on Bring Engine (`ShowcaseGame`)

> Use `src/ShowcaseGame/` as your **canonical template** whenever creating a new game with Bring Engine.
> For the full subsystem index see [`AGENTS.md`](../../AGENTS.md); for cross-OS publishing, packaging, macOS signing without a Mac, and shipped build sizes see [`docs/agents/14-publishing-and-distribution.md`](../../docs/agents/14-publishing-and-distribution.md).

## 1. Folder Structure (Strict Simulation vs. View Separation)
```text
src/<YourGame>/
├── <YourGame>.csproj           # Desktop CoreCLR (.NET 10) + ReadyToRun on publish, ProjectReference -> BringEngine
├── Program.cs                  # Bootstraps allocators, EngineWorld, Bundles, Scenes, Headless & Windowed loops
├── Simulation/                 # ZERO SDL/GPU references! Runs headlessly & deterministically.
│   ├── GameAllocators.cs       # Persistent, Level, Frame LinearArenas created at startup
│   ├── GameSimState.cs         # Unmanaged SoA structs (Fix32/FixVec3, DetRng, SimEventQueue)
│   └── GameSimSystems.cs       # Static unsafe systems: ResetMatch, StepFixed, ComputeStateHash
├── View/                       # Reads GameSimState & SimEventQueue; never mutates gameplay truth.
│   ├── GameAssets.cs           # Asset bundle loader function pointers (.bpack, .glb, textures, SDF fonts)
│   ├── GameViewState.cs        # Visual handles (EntityId, SpriteHandle, TextHandle, CameraHandle, EmitterHandle)
│   └── GameViewSystems.cs      # Scene hooks (OnLoad, OnUnload, OnViewSync) syncing visuals
└── Assets/
    ├── models/*.glb            # First-class glTF 2.0 binary models
    └── Shaders/*.hlsl          # Custom game HLSL shaders (SDL_shadercross conventions)
```

## 2. Golden Rules Checklist for Game Agents
1. **Desktop CoreCLR (.NET 10) + Self-Contained ReadyToRun (Windows, Linux / Steam Deck, macOS)**:
   - Games run on **CoreCLR JIT** for fast 1–2s `dotnet build` / `dotnet run` iteration, Dynamic PGO SIMD throughput, and full .NET NuGet package compatibility (physics, audio, networking, modding, serializers, etc.).
   - Publishing with `-r <rid>` (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) produces a standalone **self-contained single-file ReadyToRun** executable (no `.NET` install required on the player's PC or Steam Deck, and all targets can be cross-published from Windows `x64` — see [`docs/agents/14-publishing-and-distribution.md`](../../docs/agents/14-publishing-and-distribution.md)).
   - Keep hot loops (`StepFixed`, `OnViewSync`, `ExtractRenderPacket`) strictly zero-OOP and zero-GC so CoreCLR never pauses mid-frame.
2. **Allocate once at startup**: Create `EngineAllocators` and `GameAllocators` in `Program.Main`. Call `BeginFrame()` every frame; NEVER allocate managed objects in `StepFixed` or `OnViewSync`.
3. **Pure Data + Static Systems**: No classes for entities, enemies, bullets, or UI widgets. Put arrays in `GameSimState` (`FixVec3* EnemyPos`, `byte* EnemyAlive`, ...) and iterate linearly in `static unsafe class GameSimSystems`.
4. **Deterministic Gameplay + Seamless Keyboard & Gamepad Input**: Use `Fix32` / `FixVec3` and `DetRng` inside `Simulation/`. Never call `Random.Shared` or `DateTime.Now`. Use `input.GetMoveAxis()` and `GamepadButton` (`South`/`East`/`Start`/Steam Deck paddles) alongside keyboard bindings so games play natively on Desktop AND Steam Deck; trigger `PlatformSystem.RumbleGamepad` on the Main Thread for hit/explosion haptics.
5. **Decoupled View Sync via Events**: Emit plain `SimEvent` structs from `Simulation/` (`EnemyHit`, `EnemyKilled`, `ItemCollected`). Consume them in `View/` to trigger `ParticleSystem.Burst`, `CameraSystem.AddTrauma`, `TweenSystem`, and floating 3D SDF `TextData`.
6. **Compressed Asset Bundles (`.bpack`), Default Atlasing, 9-Slice UI & Unity-Like Scenes**:
   - Store raw source `.png`/`.jpg`/`.glb`/`.ttf` assets outside `dist/` (e.g., `raw_assets/<bundle>/`) and compile them with `BringPacker` (`tools/BringPacker`) into compressed `assets/<bundle>.bpack` bundles (BC1/BC3 GPU-compressed default atlases + 9-slice `SpriteRegionData` + Brotli/LZ4-compressed `.glb` models with BC1/BC3 embedded textures). Load via `BundleSystem.RegisterPack`.
   - **Ship ZERO loose PNG/JPG textures in final builds** — verify with `BringPacker --verify-no-loose-textures dist`. Use `RenderComponentSystem.SpriteFromRegion` / `DefaultUiSlicedPanel` for 9-sliced HUD panels that batch in a single draw call (`docs/agents/13-asset-compression-atlases-and-9slice.md`).
   - Put shared assets (font, glow texture, UI atlas) in a `common` bundle and scene-specific `.glb` models in scene-specific bundles (`title`, `arena`).
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
9. **First-Class Data-Oriented Multithreading (`JobSystem`) — Only When It Makes Sense**:
   - Use `JobScheduler` + `JobSystem` (`ParallelFor`, async `ScheduleParallelFor` + `IsComplete`, `CombineDependencies`, `RequestCancel`, per-worker `GetWorkerScratch`) when profiling shows a genuinely heavy SoA loop (hundreds/thousands of independent entities, particles, transforms, spatial/flow grids) or multi-frame background generation (maps, nav grids, chunk meshes).
   - **Do NOT multithread tiny loops** (< ~500–1000 items or < 0.5 ms) — pass a sensible `minParallelCount` so small workloads stay single-threaded inline with zero sync overhead.
   - Keep parallel kernels deterministic (disjoint writes, per-item seeded `DetRng`, zero SDL3/GPU calls, always `RequestCancel` + `Complete` before resetting/disposing arenas). See [`docs/agents/12-multithreading-and-jobs.md`](../../docs/agents/12-multithreading-and-jobs.md).
10. **Core Networking & Custom Multiplayer (Zero Core Bloat)**:
   - Use core `HttpSystem` (HTTP/HTTPS), `WebSocketSystem` (`ws://`/`wss://`), `NetWriter`/`NetReader`, and `NetTransportVTable` / `LoopbackTransportSystem` for web APIs, lobbies, and headless multiplayer testing; keep all sockets outside `Simulation/` and always call `ReleaseResponse`/`ReleaseEvent`.
   - For real-time UDP multiplayer, add only the NuGet your game needs (`LiteNetLib 2.x`, `Backdash` rollback, or `Facepunch.Steamworks`) directly to `<YourGame>.csproj`. See [`docs/agents/16-networking-and-multiplayer.md`](../../docs/agents/16-networking-and-multiplayer.md).
11. **Verify Desktop Publish, Cross-OS Targets, Headless Determinism & GPU Render**:
    ```bash
    # Windows (default local verification)
    dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64 -o dist/win-x64
    ./dist/win-x64/ShowcaseGame.exe --headless --ticks 600
    ./dist/win-x64/ShowcaseGame.exe --verify-render

    # Cross-OS desktop targets (all publishable directly from Windows x64 — no Mac required)
    dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r linux-x64 -o dist/linux-x64
    dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r osx-arm64 -o dist/osx-arm64
    ```
    See [`docs/agents/14-publishing-and-distribution.md`](../../docs/agents/14-publishing-and-distribution.md) for Linux executable/symlink packaging, macOS `.app` bundling, Gatekeeper/notarization (`rcodesign` / macOS CI), and baseline release sizes.
