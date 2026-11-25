# Bring Engine

A cross-platform, **NativeAOT-first**, **strictly Data-Oriented (Zero-OOP)** 3D & 2D game engine written in C# (.NET 10), powered by **SDL3** and **SDL3_gpu**.

## Highlights
- **Strict NativeAOT (Mandatory Release Target)**: All games built on Bring Engine ship as NativeAOT binaries (`<PublishAot>true</PublishAot>`). All trimming and AOT analyzer warnings (`IL2xxx`, `IL3xxx`) are hard compile errors via `Directory.Build.props` (zero reflection / zero dynamic codegen).
- **Zero-OOP Data + Systems**: Pure unmanaged `struct` tables (SoA / dense sparse sets) iterated by `static unsafe` systems.
- **Explicit Native Memory Allocators**: 64-byte aligned `LinearArena` (`Persistent`, `Frame`, `Scratch`, per-`Scene`, per-`Bundle`), `PoolAllocator<T>`, and `FreeListAllocator` for zero-GC hot loops.
- **Headless Simulation / View Separation**: Deterministic fixed-timestep simulation runs completely without SDL or GPU for instant headless tests and replays.
- **3D + Unity-Style 2D Sprites in 3D + 9-Slice UI + Advanced SDF Text**: Instanced PBR 3D meshes, skeletal skinning, glTF 2.0 / `.glb` first-class pipeline, 2D sprites in 3D space (oriented or billboarded), single-draw-call **9-slice UI panels** (`SpriteDrawMode.Sliced` / `SlicedNoCenter`), StbTrueType SDF text with per-character animation/tweening and shader effects, SoA particles, and custom HLSL shaders via `SDL_shadercross`.
- **Unity-Style Asset Compression & Default Atlased `.bpack` Bundles (`BringPacker`)**: Automatic **BC1/BC3 (DXT1/DXT5)** hardware texture block compression (4:1–8:1 VRAM savings), **LZ4 & Brotli** compression for `.bpack` bundles and `.glb`/`.gltf` models (including BC1/BC3 recompression of embedded glTF textures), **default texture atlasing** with 2 px bleed padding, and `--verify-no-loose-textures` build guardrail so final game builds ship zero loose PNG/JPG textures (`docs/agents/13-asset-compression-atlases-and-9slice.md`).
- **Borderless Full Window by Default**: All games built with Bring Engine launch in **Borderless Full Window** (`WindowMode.BorderlessFullscreen`) at native desktop resolution by default, with `F11` runtime toggle and `--windowed` option.
- **2-Thread Pipelined Engine Architecture**: Dedicated **Logic/Update Thread** (Frame `N`: Simulation + ViewSync + CoreSystems + `ExtractRenderPacket` into double-buffered `Frame` arena) runs concurrently with the **Main/Render Thread** (Frame `N-1`: SDL3 window/events + `SubmitRenderPacket` from `FrameBack` arena + `FramePacer`), keeping GPU work 1 frame pipelined (`SDL_SetGPUAllowedFramesInFlight(2)`).
- **Native First-Class Data-Oriented Multithreading (`JobSystem`)**: Zero-GC, zero-OOP worker pool (`ParallelFor` with automatic inline serial threshold, async `Schedule`/`ScheduleParallelFor`, DAG `CombineDependencies`, cooperative `RequestCancel`, lock-free per-worker native scratch arenas) plus optional threshold-gated parallel acceleration for large `TransformSystem` hierarchies and `ParticleSystem` pools. Use only where workloads genuinely justify it (`docs/agents/12-multithreading-and-jobs.md`).
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

## Releases & CI
- Every push to `main` is verified by GitHub Actions (`.github/workflows/build.yml`) across Windows, Linux, and macOS with NativeAOT `BringEngine.Tests` and `ShowcaseGame` audits.
- Tagged releases (`vX.Y.Z`) run the full test matrix and standalone SDK consumer verification (`.github/workflows/release.yml`) before publishing `bring-sdk-vX.Y.Z.zip` on GitHub Releases.

## License
MIT License — see [LICENSE](LICENSE).
