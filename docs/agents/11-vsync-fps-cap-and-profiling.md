# Agent Guide 11 — Borderless Full Window, 2-Thread Pipeline, VSync/FPS Cap & Frame Profiling

## 1. Mandatory Engine Defaults (All Bring Engine Games)
Every interactive game built on Bring Engine MUST default to:
- **Window Mode = Borderless Full Window** (`WindowMode.BorderlessFullscreen` — default in `PlatformSystem.InitWindow`, runs at native desktop resolution via `SDL_SetWindowFullscreenMode(win, NULL)` + `SDL_SetWindowFullscreen(win, true)`).
  - Bind **`F11`** to `PlatformSystem.ToggleBorderlessFullscreen(ref window)` and optionally support `--windowed` CLI override.
  - Hidden offscreen verification windows (`hidden: true`) automatically stay in exact-size windowed mode (e.g. `1280x720`).
- **VSync = ON** (`SDL.GPUPresentMode.VSync`, configured automatically by `GpuSystem.Create` and `FramePacer.CreateDefault()`).
- **FPS Cap = 60** (`FramePacer.DefaultTargetFps = 60`; set `0` for uncapped).
- **2-Thread Pipelined Execution** (`EnginePipelineSystem`): Logic Thread (Frame `N`) runs concurrently with Main/Render Thread (Frame `N-1`) and GPU (`SDL_SetGPUAllowedFramesInFlight(device, 2)`).

## 2. 2-Thread Pipelined Architecture (`EnginePipelineSystem`)
Because SDL3 requires `SDL_PollEvent` and `SDL_WaitAndAcquireGPUSwapchainTexture` to execute on the **Main Thread**, Bring Engine splits each frame cleanly across 2 threads with **zero shared mutable state during frame execution**:

```text
Frame N:   [Logic Thread] Sim -> ViewSync -> CoreSystems -> ExtractRenderPacket (into Frame)
Frame N-1: [Main/Render ] SubmitRenderPacket (reads FrameBack) -> GPU Submit -> FramePacer
           ------------------- Quiescent Barrier (Swap Frame <-> FrameBack) -------------------
```

- **Logic Thread** owns `EngineWorld`, game simulation, `EngineAllocators.Frame`, and `Scratch`. NEVER calls SDL3 or GPU APIs.
- **Main / Render Thread** owns `PlatformWindow`, `GpuContext`, `FramePacer`, and reads **only** the immutable `RenderPacket` in `EngineAllocators.FrameBack`.
- **Quiescent Barrier**: Between `StepPipelinedFrame` calls, swap `Frame <-> FrameBack` (`alloc.SwapFrameArenas()`), handle debug hotkeys (`F1`/`F2`/`F3`/`F11`), and run scene transitions safely:
  ```csharp
  EnginePipelineSystem.FlushBeforeSceneTransition(pipeline, ref *gpu);
  SceneSystem.LoadScene(ref *scenes, ref *world, ref engAlloc, ref *bundles, StringId.Hash("Arena"), SceneLoadMode.Single);
  BatchRenderer.UploadPendingAssets(ref *gpu, ref *world);
  ```

## 3. Controlling VSync, FPS Cap & Borderless Full Window
```csharp
using BringEngine.Rendering.Platform;

// Window (defaults to Borderless Full Window!)
var window = PlatformSystem.InitWindow("My Game", 1280, 720, hidden: false, mode: WindowMode.BorderlessFullscreen);

// Pacer (defaults to 60 FPS cap + VSync ON)
FramePacer pacer = FramePacer.CreateDefault();
FramePacerSystem.ApplyVSync(ref pacer, ref *gpu, pacer.VSyncEnabled != 0);

// Standard runtime hotkeys (at quiescent barrier)
if (input.IsKeyPressed(KeyCode.F1))  ProfilerOverlaySystem.Toggle(ref *profOverlay, ref *world);
if (input.IsKeyPressed(KeyCode.F2))  FramePacerSystem.ToggleVSync(ref *pacer, ref *gpu);
if (input.IsKeyPressed(KeyCode.F3))  FramePacerSystem.CycleFpsCapPreset(ref *pacer); // 60->120->144->Max->30
if (input.IsKeyPressed(KeyCode.F11)) PlatformSystem.ToggleBorderlessFullscreen(ref window);
```

## 4. Pipelined Frame Profiling (`FrameProfiler` + `ProfilerSystem`)
Because **Logic CPU**, **Render Thread**, and **GPU** execute pipelined 1 frame apart, each stage independently receives the full frame budget (`16.67 ms` at 60 FPS). `FrameProfiler` tracks all three separately (zero GC, headless-safe):
- **`LogicCpuMs` / `SmoothedLogicMs`**: Logic/Update Thread duration (Frame `N`: Sim + ViewSync + Core + Packet Extract).
- **`RenderCpuMs` / `SmoothedRenderMs`**: Render Thread CPU encoding/upload duration (Frame `N-1`, excludes VSync wait & GPU fence wait).
- **`GpuFrameMs` / `SmoothedGpuMs`**: Hardware GPU execution time measured via SDL3_gpu fence (Frame `N-1`).
- **`CurrentFps` / `InstantFps` / `FrameTotalMs`**: 30-frame rolling average and instantaneous FPS.
- **Named Scopes**: Wrap any Logic-thread subsystem with `ProfilerSystem.BeginScope(ref *profiler, "GameAI")` / `EndScope`.
- **GPU Batch Metrics (`RenderStats`)**: `DrawCalls`, `SetPassCalls`, `PipelineBinds`, `RenderPasses`, `CopyPasses`, `MeshInstances`, `SpriteInstances`, `GlyphInstances`, `ParticleInstances`, `Triangles`, `CulledMeshes`, `FrameArenaUsedBytes`.

## 5. Common Built-In Profiler UI Overlay (`ProfilerOverlaySystem`)
Drop-in **100% opaque, high-contrast SDF UI** on its own persistent layer (`LayerMask = 1u << 30`, `DepthOrder = 1000`, `OwnerScene = SceneId.Invalid`, `ClearFlags = None`), using virtual 720p vertical scaling so text stays crisp and comfortably readable at 720p, 1080p, 1440p, 4K, and ultrawide:
- **Survives all `Single` and `Additive` scene switches automatically.**
- **0 GC bytes per frame.**
- **Displays 3 SEPARATE horizontal fill bars** (never stacked/combined, all excluding VSync/FPS-cap idle wait!):
  1. **`LOGIC` (Cyan)** — Logic Thread time (Sim + ViewSync + Core + Packet Extract, Frame `N`) vs. `budgetMs` (turns red if `> 85%`)
  2. **`RENDER` (Emerald)** — Render Thread CPU time (GPU Upload + Command Recording, Frame `N-1`) vs. `budgetMs` (turns red if `> 85%`)
  3. **`GPU` (Gold)** — Pure GPU Hardware work (Copy + Shaders into offscreen `SceneColorTexture`, decoupled from swapchain VSync Present blit!) vs. `budgetMs` (turns red if `> 85%`)
- Plus Header (`PROFILER [F1] VSYNC:ON CAP:60 [2T]`), FPS & Total Frame ms, `DRAWS | SETPASS | PASSES`, `OBJS | TRIS | ARENA`, and Top CPU Scopes.

> **Important GPU Stability Rules (Enforced Automatically by `BatchRenderer`)**:
> - `depthInfo.Cycle = (ci == 0)` when clearing depth on the primary pass; 2D UI overlay cameras use `ClearFlags = CameraClearFlags.None` so they never clear 3D depth mid-frame.
> - Graphics pipelines are always bound **before** vertex storage buffers & fragment samplers.
> - Sorting uses deterministic `(SortKey, OriginalIndex)` tie-breaking and unique `OrderInLayer` values to guarantee zero UI/scene flicker.
