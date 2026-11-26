# Bring Engine

A cross-platform **Desktop CoreCLR (.NET 10)**, **strictly Data-Oriented (Zero-OOP)** 3D & 2D game engine written in C# for **Windows**, **Linux (including Steam Deck / SteamOS)**, and **macOS**, powered by **SDL3** and **SDL3_gpu**.

## Highlights
- **Desktop CoreCLR (.NET 10) + ReadyToRun**: Instant 1–2s `dotnet build` / `dotnet run` iteration, Dynamic PGO CPU-specific SIMD optimization, full .NET NuGet package compatibility (physics, audio, networking, modding, tools), and cross-OS self-contained single-file ReadyToRun desktop executables (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) with zero C++ toolchain required.
- **Zero-OOP Data + Systems**: Pure unmanaged `struct` tables (SoA / dense sparse sets) iterated by `static unsafe` systems.
- **Explicit Native Memory Allocators**: 64-byte aligned `LinearArena` (`Persistent`, `Frame`, `Scratch`, per-`Scene`, per-`Bundle`), `PoolAllocator<T>`, and `FreeListAllocator` for zero-GC hot loops (zero GC pauses on CoreCLR).
- **Headless Simulation / View Separation & First-Class Gamepad Support**: Deterministic fixed-timestep simulation runs completely without SDL or GPU for instant headless tests and replays. `InputState` includes keyboard, mouse, and **up to 4 SDL3 gamepads** (Xbox / PlayStation / Nintendo / **Steam Deck** incl. back grip paddles & touchpad click, radial deadzone, unified `GetMoveAxis()`, and `PlatformSystem.RumbleGamepad` haptics).
- **3D + Unity-Style 2D Sprites in 3D + 9-Slice UI + Advanced SDF Text**: Instanced PBR 3D meshes, skeletal skinning, glTF 2.0 / `.glb` first-class pipeline, 2D sprites in 3D space (oriented or billboarded), single-draw-call **9-slice UI panels** (`SpriteDrawMode.Sliced` / `SlicedNoCenter`), StbTrueType SDF text with per-character animation/tweening and shader effects, SoA particles, and custom HLSL shaders via `SDL_shadercross`.
- **Unity-Style Asset Compression & Default Atlased `.bpack` Bundles (`BringPacker`)**: Automatic **BC1/BC3 (DXT1/DXT5)** hardware texture block compression (4:1–8:1 VRAM savings), **LZ4 & Brotli** compression for `.bpack` bundles and `.glb`/`.gltf` models, **default texture atlasing** with 2 px bleed padding, and `--verify-no-loose-textures` build guardrail (`docs/agents/13-asset-compression-atlases-and-9slice.md`).
- **Borderless Full Window by Default**: All games built with Bring Engine launch in **Borderless Full Window** (`WindowMode.BorderlessFullscreen`) at native desktop/handheld resolution by default, with `F11` runtime toggle and `--windowed` option.
- **2-Thread Pipelined Engine Architecture & Native `JobSystem`**: Dedicated Logic Thread runs concurrently with Main/Render Thread, plus optional zero-GC `JobSystem` (`ParallelFor`, async DAGs, per-worker scratch arenas) for heavy SoA loops (`docs/agents/12-multithreading-and-jobs.md`).
- **Lean Zero-Dependency Core Networking (`BringEngine.Networking`)**: Handle-based async HTTP/HTTPS (`HttpSystem`) and `ws://`/`wss://` WebSockets (`WebSocketSystem`) built on `.NET 10` BCL (0 extra native libs), zero-GC binary `NetWriter`/`NetReader`, pluggable zero-OOP `NetTransportVTable`, and deterministic `LoopbackTransportSystem` for headless testing — while games plug in `LiteNetLib`, `Backdash`, or `Facepunch.Steamworks` when needed (`docs/agents/16-networking-and-multiplayer.md`).
- **Core Zero-GC Rolling Logger (`BringEngine.Core.Logging`)**: Custom `.NET 10` UTF-8 `[InterpolatedStringHandler]` (zero third-party deps, skips argument evaluation when filtered), OS-standard writable log directories (Windows `%LOCALAPPDATA%`, macOS `~/Library/Logs`, Linux/SteamOS `$XDG_STATE_HOME`), multi-file `LogFileHandle` support, and automatic 10,000-line FIFO rolling eviction (`docs/agents/17-logging.md`).
- **VSync, Configurable FPS Cap & Opaque Separate-Bar Frame Profiler UI**: Defaults to **VSync ON** and **60 FPS cap** (`FramePacer`), with independent **`LOGIC`**, **`RENDER`**, and **`GPU`** fill bars (excluding VSync wait), `DRAWS` / `SETPASS` / `PASSES` tracking, and built-in `ProfilerOverlay` (`F1`/`F2`/`F3`/`F11`).

## Documentation Guide (Hub & Topic Guides)
Documentation is organized around a slim master hub ([`AGENTS.md`](AGENTS.md)) that links to focused per-topic guides so information stays modular and easy to navigate:

| Guide | Purpose |
|---|---|
| [`AGENTS.md`](AGENTS.md) | Master hub: 5 core architecture rules, solution layout, and full index of all guides |
| [`src/ShowcaseGame/AGENT_README.md`](src/ShowcaseGame/AGENT_README.md) | Canonical game folder blueprint and golden rules checklist |
| [`docs/agents/01`–`13`, `16`–`17`](docs/agents/) | Focused per-subsystem technical guides (memory, math, ECS/gamepads, scenes, glTF, sprites, SDF text, tweens, shaders, profiling, jobs, `.bpack`/9-slice, networking & multiplayer, logging) |
| [`docs/agents/14-publishing-and-distribution.md`](docs/agents/14-publishing-and-distribution.md) | Cross-OS publishing (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) from one PC, Linux packaging, macOS signing/notarization without a Mac, and build sizes |
| [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md) | Engine development rules, CI-only test matrix, code releases, and automatic docs-only patch releases |

## Quick Start
```bash
# Build all projects (instant CoreCLR build)
dotnet build BringEngine.sln -c Release

# Play ShowcaseGame directly
dotnet run --project src/ShowcaseGame/ShowcaseGame.csproj -c Release

# Publish self-contained single-file ReadyToRun desktop builds (all targets can be built from Windows x64)
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r win-x64   -o dist/win-x64
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r linux-x64 -o dist/linux-x64
dotnet publish src/ShowcaseGame/ShowcaseGame.csproj -c Release -r osx-arm64 -o dist/osx-arm64
```
See [`docs/agents/14-publishing-and-distribution.md`](docs/agents/14-publishing-and-distribution.md) for Linux executable/symlink packaging, macOS `.app` bundling and signing/notarization without a Mac, and baseline release sizes.

## Releases & CI
- **Engine tests run on GitHub Actions CI only** (do not tie up the local machine running engine test suites).
- **Code changes** pushed to `main` or PRs run the full 3-OS CoreCLR matrix (`windows-x64`, `linux-x64`, `macos-arm64`) in `.github/workflows/build.yml`. Tagged code releases (`vX.Y.Z`) run the full test matrix plus standalone SDK verification in `.github/workflows/release.yml` before publishing `bring-sdk-vX.Y.Z.zip`.
- **Docs / README-only updates** pushed to `main` (with zero code changes since the latest tag) **skip the test suite** and automatically bump the patch version (`vX.Y.Z` → `vX.Y.(Z+1)`) and publish a new `bring-sdk-vX.Y.(Z+1).zip` GitHub Release.
- See [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md) for full details.

## License
MIT License — see [LICENSE](LICENSE).
