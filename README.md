# Bring Engine

A cross-platform **Desktop CoreCLR (.NET 10)**, **strictly Data-Oriented (Zero-OOP)** 3D & 2D game engine written in C# for **Windows**, **Linux (including Steam Deck / SteamOS)**, and **macOS**, powered by **SDL3** and **SDL3_gpu**.

## Highlights
- **Desktop CoreCLR (.NET 10) + ReadyToRun**: Instant 1–2s `dotnet build` / `dotnet run` iteration, Dynamic PGO CPU-specific SIMD optimization, full .NET NuGet package compatibility (physics, audio, networking, modding, tools), and cross-OS self-contained single-file ReadyToRun desktop executables (`win-x64`, `linux-x64`, `osx-arm64`) with zero C++ toolchain required.
- **Zero-OOP Data + Systems**: Pure unmanaged `struct` tables (SoA / dense sparse sets) iterated by `static unsafe` systems.
- **Explicit Native Memory Allocators**: 64-byte aligned `LinearArena` (`Persistent`, `Frame`, `Scratch`, per-`Scene`, per-`Bundle`), `PoolAllocator<T>`, and `FreeListAllocator` for zero-GC hot loops (zero GC pauses on CoreCLR).
- **Headless Simulation / View Separation & First-Class Gamepad Support**: Deterministic fixed-timestep simulation runs completely without SDL or GPU for instant headless tests and replays. `InputState` includes keyboard, mouse, and **up to 4 SDL3 gamepads** (Xbox / PlayStation / Nintendo / **Steam Deck** incl. back grip paddles & touchpad click, radial deadzone, unified `GetMoveAxis()`, and `PlatformSystem.RumbleGamepad` haptics).
- **3D + Unity-Style 2D Sprites in 3D + 9-Slice UI + Advanced SDF Text**: Instanced PBR 3D meshes, skeletal skinning, glTF 2.0 / `.glb` first-class pipeline, 2D sprites in 3D space (oriented or billboarded), single-draw-call **9-slice UI panels** (`SpriteDrawMode.Sliced` / `SlicedNoCenter`), StbTrueType SDF text with per-character animation/tweening and shader effects, SoA particles, and custom HLSL shaders via `SDL_shadercross`.
- **Unity-Style Asset Compression & Default Atlased `.bpack` Bundles (`BringPacker`)**: Automatic **BC1/BC3 (DXT1/DXT5)** hardware texture block compression (4:1–8:1 VRAM savings), **LZ4 & Brotli** compression for `.bpack` bundles and `.glb`/`.gltf` models (including BC1/BC3 recompression of embedded glTF textures), **default texture atlasing** with 2 px bleed padding, and `--verify-no-loose-textures` build guardrail so final game builds ship zero loose PNG/JPG textures (`docs/agents/13-asset-compression-atlases-and-9slice.md`).
- **Borderless Full Window by Default**: All games built with Bring Engine launch in **Borderless Full Window** (`WindowMode.BorderlessFullscreen`) at native desktop/handheld resolution (16:9, ultrawide, Steam Deck 16:10 `1280×800`) by default, with `F11` runtime toggle and `--windowed` option.
- **2-Thread Pipelined Engine Architecture**: Dedicated **Logic/Update Thread** (Frame `N`: Simulation + ViewSync + CoreSystems + `ExtractRenderPacket` into double-buffered `Frame` arena) runs concurrently with the **Main/Render Thread** (Frame `N-1`: SDL3 window/events/gamepads + `SubmitRenderPacket` from `FrameBack` arena + `FramePacer`), keeping GPU work 1 frame pipelined (`SDL_SetGPUAllowedFramesInFlight(2)`).
- **Native First-Class Data-Oriented Multithreading (`JobSystem`)**: Zero-GC, zero-OOP worker pool (`ParallelFor` with automatic inline serial threshold, async `Schedule`/`ScheduleParallelFor`, DAG `CombineDependencies`, cooperative `RequestCancel`, lock-free per-worker native scratch arenas) plus optional threshold-gated parallel acceleration for large `TransformSystem` hierarchies and `ParticleSystem` pools. Use only where workloads genuinely justify it (`docs/agents/12-multithreading-and-jobs.md`).
- **VSync, Configurable FPS Cap & Opaque Separate-Bar Frame Profiler UI**: Defaults to **VSync ON** and **60 FPS cap** (`FramePacer`), with independent **`LOGIC`**, **`RENDER`**, and **`GPU`** fill bars (pure GPU execution time decoupled from swapchain VSync wait, each measured against its own frame budget), named scopes, `DRAWS` / `SETPASS` / `PASSES` tracking, and a 100% opaque drop-in zero-GC `ProfilerOverlay` UI (`F1` toggle overlay, `F2` toggle VSync, `F3` cycle FPS cap, `F11` toggle borderless/windowed).
- **AI-First Documentation**: See [`AGENTS.md`](AGENTS.md) and [`docs/agents/`](docs/agents/).

## Quick Start
```bash
# Build all projects (instant CoreCLR build)
dotnet build BringEngine.sln -c Release

# Play ShowcaseGame directly
dotnet run --project src/ShowcaseGame/ShowcaseGame.csproj -c Release

# Publish self-contained single-file ReadyToRun desktop build (win-x64 / linux-x64 / osx-arm64)
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64
./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe
```

## Releases & CI
- **Engine tests run on GitHub Actions CI only** (do not keep the local machine busy running engine test suites).
- Every push to `main` is verified by GitHub Actions (`.github/workflows/build.yml`) across Windows (`win-x64`), Linux / Steam Deck (`linux-x64`), and macOS (`osx-arm64`) with CoreCLR `BringEngine.Tests`, `BringPacker`, and `ShowcaseGame` audits.
- Tagged releases (`vX.Y.Z`) run the full test matrix and standalone SDK consumer verification (`.github/workflows/release.yml`) before publishing `bring-sdk-vX.Y.Z.zip` on GitHub Releases.

## License
MIT License — see [LICENSE](LICENSE).
