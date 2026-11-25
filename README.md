# Bring Engine

A cross-platform, **NativeAOT-first**, **strictly Data-Oriented (Zero-OOP)** 3D & 2D game engine written in C# (.NET 10), powered by **SDL3** and **SDL3_gpu**.

## Highlights
- **Strict NativeAOT**: All trimming and AOT analyzer warnings (`IL2xxx`, `IL3xxx`) are compile errors via `Directory.Build.props`.
- **Zero-OOP Data + Systems**: Pure unmanaged `struct` tables (SoA / dense sparse sets) iterated by `static unsafe` systems.
- **Explicit Native Memory Allocators**: 64-byte aligned `LinearArena` (`Persistent`, `Frame`, `Scratch`, per-`Scene`, per-`Bundle`), `PoolAllocator<T>`, and `FreeListAllocator` for zero-GC hot loops.
- **Headless Simulation / View Separation**: Deterministic fixed-timestep simulation runs completely without SDL or GPU for instant headless tests and replays.
- **3D + Unity-Style 2D Sprites in 3D + Advanced SDF Text**: Instanced PBR 3D meshes, skeletal skinning, glTF 2.0 / `.glb` first-class pipeline, 2D sprites in 3D space (oriented or billboarded), StbTrueType SDF text with per-character animation/tweening and shader effects, SoA particles, and custom HLSL shaders via `SDL_shadercross`.
- **Borderless Full Window by Default**: All games built with Bring Engine launch in **Borderless Full Window** (`WindowMode.BorderlessFullscreen`) at native desktop resolution by default, with `F11` runtime toggle and `--windowed` option.
- **2-Thread Pipelined Engine Architecture**: Dedicated **Logic/Update Thread** (Frame `N`: Simulation + ViewSync + CoreSystems + `ExtractRenderPacket` into double-buffered `Frame` arena) runs concurrently with the **Main/Render Thread** (Frame `N-1`: SDL3 window/events + `SubmitRenderPacket` from `FrameBack` arena + `FramePacer`), keeping GPU work 1 frame pipelined (`SDL_SetGPUAllowedFramesInFlight(2)`).
- **VSync, Configurable FPS Cap & Opaque Separate-Bar Frame Profiler UI**: Defaults to **VSync ON** and **60 FPS cap** (`FramePacer`), with independent **`LOGIC`**, **`RENDER`**, and **`GPU`** fill bars (pure GPU execution time decoupled from swapchain VSync wait, each measured against its own frame budget), named scopes, `DRAWS` / `SETPASS` / `PASSES` tracking, and a 100% opaque drop-in zero-GC `ProfilerOverlay` UI (`F1` toggle overlay, `F2` toggle VSync, `F3` cycle FPS cap, `F11` toggle borderless/windowed).
- **AI-First Documentation**: See [`AGENTS.md`](AGENTS.md) and [`docs/agents/`](docs/agents/).

## Quick Start
```bash
# Build all projects (AOT analyzers active)
dotnet build BringEngine.sln -c Release

# Run NativeAOT test suite
dotnet publish tests/BringEngine.Tests/BringEngine.Tests.csproj -c Release -r win-x64
./tests/BringEngine.Tests/bin/Release/net10.0/win-x64/publish/BringEngine.Tests.exe

# Publish & run ShowcaseGame (NativeAOT)
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64
./src/ShowcaseGame/bin/Release/net10.0/win-x64/publish/ShowcaseGame.exe
```
