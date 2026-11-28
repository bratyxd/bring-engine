# Bring Engine

A cross-platform **Desktop CoreCLR (.NET 10)**, **strictly Data-Oriented (Zero-OOP)** 3D & 2D game engine written in C# for **Windows**, **Linux (including Steam Deck / SteamOS)**, and **macOS**, powered by **SDL3** and **SDL3_gpu**.

## Highlights
- **Desktop CoreCLR (.NET 10) + ReadyToRun**: Instant 1–2s `dotnet build` / `dotnet run` iteration, Dynamic PGO CPU-specific SIMD optimization, full .NET NuGet package compatibility (physics, audio, networking, modding, tools), and cross-OS self-contained single-file ReadyToRun desktop executables (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) with zero C++ toolchain required.
- **Zero-OOP Data + Systems**: Pure unmanaged `struct` tables (SoA / dense sparse sets) iterated by `static unsafe` systems.
- **Explicit Native Memory Allocators**: 64-byte aligned `LinearArena` (`Persistent`, `Frame`, `Scratch`, per-`Scene`, per-`Bundle`), `PoolAllocator<T>`, and `FreeListAllocator` for zero-GC hot loops (zero GC pauses on CoreCLR).
- **Headless Simulation / View Separation, Full Input & First-Class Gamepad Support**: Deterministic fixed-timestep simulation runs completely without SDL or GPU for instant headless tests and replays. `InputState` & `PlatformSystem` include full keyboard scancodes, mouse buttons (`Pressed`/`Down`/`Released`), cursor lock / relative mouse mode (`SetMouseLock`), cursor visibility, zero-GC UTF-8 text input (`StartTextInput`), window focus/minimize tracking, and **up to 4 SDL3 gamepads** (Xbox / PlayStation / Nintendo / **Steam Deck** incl. back grip paddles & touchpad click, radial deadzone, unified `GetMoveAxis()`, and `PlatformSystem.RumbleGamepad` haptics).
- **3D PBR + Directional Sun Shadows + Spine-Grade 2D Skeletal Rigging (`Rig2D`) + 2D Sprites + 9-Slice UI + SDF Text**: Instanced PBR 3D meshes with **texel-snapped directional sun shadow mapping (3×3 PCF soft shadows)** for static & skinned meshes, glTF 2.0 / `.glb` first-class pipeline, **Zero-OOP / 0-GC 2D skeletal rigging & deformable mesh animation (`Rig2D`)** (1/2-bone IK, transform constraints, deterministic spring/cloth physics, weighted meshes + FFD, skins, sequence slots, 8-track mixer, single-draw-call `rig_2d.hlsl` batching, plus the zero-editor `BringRig2D` CLI workbench — `docs/agents/20-2d-skeletal-rigging.md`), 2D sprites in 3D space (oriented or billboarded), single-draw-call **9-slice UI panels**, StbTrueType SDF text, SoA particles, and custom HLSL shaders via `SDL_shadercross`.
- **Zero-Dependency Core Audio System (`BringEngine.Audio`)**: Built on SDL3's native `SDL_AudioStream` with a dedicated `BringEngine.AudioMixer` background thread, zero-GC playback (`PlayOneShot`, `PlayLoop`, pitch, stereo pan, voice stealing), 4 volume/mute buses (`Master`, `Music`, `Sfx`, `Ui`), built-in WAV decoder + compressed `.bpack` loading, and automatic headless fallback for CI (`docs/agents/19-audio.md`).
- **Unity-Style Asset Compression & Default Atlased `.bpack` Bundles (`BringPacker`)**: Automatic **BC1/BC3 (DXT1/DXT5)** hardware texture block compression (4:1–8:1 VRAM savings), **LZ4 & Brotli** compression for `.bpack` bundles and `.glb`/`.gltf` models, **default texture atlasing** with 2 px bleed padding, and `--verify-no-loose-textures` build guardrail (`docs/agents/13-asset-compression-atlases-and-9slice.md`).
- **Borderless Full Window by Default**: All games built with Bring Engine launch in **Borderless Full Window** (`WindowMode.BorderlessFullscreen`) at native desktop/handheld resolution by default, with `F11` runtime toggle and `--windowed` option.
- **2-Thread Pipelined Engine Architecture & Native `JobSystem`**: Dedicated Logic Thread runs concurrently with Main/Render Thread, plus optional zero-GC `JobSystem` (`ParallelFor`, async DAGs, per-worker scratch arenas) for heavy SoA loops (`docs/agents/12-multithreading-and-jobs.md`).
- **Lean Zero-Dependency Core Networking (`BringEngine.Networking`)**: Handle-based async HTTP/HTTPS (`HttpSystem`) and `ws://`/`wss://` WebSockets (`WebSocketSystem`) built on `.NET 10` BCL (0 extra native libs), zero-GC binary `NetWriter`/`NetReader`, pluggable zero-OOP `NetTransportVTable`, and deterministic `LoopbackTransportSystem` for headless testing — while games plug in `LiteNetLib`, `Backdash`, or `Facepunch.Steamworks` when needed (`docs/agents/16-networking-and-multiplayer.md`).
- **Core Zero-GC Rolling Logger (`BringEngine.Core.Logging`) & Crash-Safe Save System (`BringEngine.Core.Storage`)**: Shared OS-standard app-data folders (`PlatformPaths`: Windows `%LOCALAPPDATA%`, macOS `~/Library/Application Support`, Linux/SteamOS `$XDG_DATA_HOME`), dedicated non-blocking background threads for both logging (`BringEngine.LogWriter`) and async saving (`BringEngine.SaveWorker`), zero-GC UTF-8 `[InterpolatedStringHandler]` logging with **automatic `[ERROR]` exception capture** (`FirstChance` / `Unhandled` / `UnobservedTask`) and 10,000-line FIFO trimming (`docs/agents/17-logging.md`), plus atomic `.tmp`/`.bak` saves with CRC32 corruption recovery, LZ4/Brotli compression, Raw JSON settings, `BSAV`+JSON profiles, and versioned binary `SaveChunks` for large worlds (`docs/agents/18-save-system.md`).
- **VSync, Configurable FPS Cap & Opaque Separate-Bar Frame Profiler UI**: Defaults to **VSync ON** and **60 FPS cap** (`FramePacer`), with independent **`LOGIC`**, **`RENDER`**, and **`GPU`** fill bars (excluding VSync wait), `DRAWS` / `SETPASS` / `PASSES` tracking, and built-in `ProfilerOverlay` (`F1`/`F2`/`F3`/`F11`).

## Documentation Guide (Hub & Topic Guides)
Documentation is organized around a slim master hub ([`AGENTS.md`](AGENTS.md)) that links to focused per-topic guides so information stays modular and easy to navigate:

| Guide | Purpose |
|---|---|
| [`AGENTS.md`](AGENTS.md) | Master hub: 6 core rules (architecture + mandatory engine PR review), solution layout, and full index of all guides |
| [`src/ShowcaseGame/AGENT_README.md`](src/ShowcaseGame/AGENT_README.md) | Canonical game folder blueprint and golden rules checklist |
| [`docs/agents/01`–`13`, `16`–`20`](docs/agents/) | Focused per-subsystem technical guides (memory, math, ECS/input/gamepads, scenes, glTF/3D/shadows, sprites, SDF text, tweens, shaders, profiling, jobs, `.bpack`/9-slice, networking & multiplayer, logging, save system, core audio, `Rig2D` 2D skeletal rigging) |
| [`docs/agents/14-publishing-and-distribution.md`](docs/agents/14-publishing-and-distribution.md) | Cross-OS publishing (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) from one PC, Linux packaging, macOS signing/notarization without a Mac, and build sizes |
| [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md) | Engine development rules, mandatory post-implementation PR review (`Neon xhigh` review plan → `Neon high` fix phase), CI-only test matrix, code releases, and automatic docs-only patch releases |

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
- **Mandatory Engine PR Review & Fix Phase**: Whenever engine code is updated, after the implementor agent finishes, every agent MUST use **`Neon xhigh`** to create a PR review plan from scratch and **`Neon high`** to apply that review plan in a dedicated fix phase before pushing to CI or releasing.
- **Engine tests run on GitHub Actions CI only** (do not tie up the local machine running engine test suites).
- **Code changes** pushed to `main` or PRs run the full 3-OS CoreCLR matrix (`windows-x64`, `linux-x64`, `macos-arm64`) in `.github/workflows/build.yml`. Tagged code releases (`vX.Y.Z`) run the full test matrix plus standalone SDK verification in `.github/workflows/release.yml` before publishing `bring-sdk-vX.Y.Z.zip`.
- **Docs / README-only updates** pushed to `main` (with zero code changes since the latest tag) **skip the test suite** and automatically bump the patch version (`vX.Y.Z` → `vX.Y.(Z+1)`) and publish a new `bring-sdk-vX.Y.(Z+1).zip` GitHub Release.
- See [`docs/agents/15-engine-ci-and-releasing.md`](docs/agents/15-engine-ci-and-releasing.md) for full details.

## License
MIT License — see [LICENSE](LICENSE).
