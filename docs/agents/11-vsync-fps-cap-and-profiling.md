# Agent Guide 11 — VSync, FPS Cap, Frame Profiling & Common Profiler UI

## 1. Defaults (Always On Out of the Box)
Bring Engine defaults every windowed game to:
- **VSync = ON** (`SDL.GPUPresentMode.VSync`, set automatically in `GpuSystem.Create` and `FramePacer.CreateDefault()`)
- **FPS Cap = 60** (`FramePacer.DefaultTargetFps = 60`; set `0` for uncapped)

## 2. Controlling VSync & FPS Cap (`FramePacer` + `FramePacerSystem`)
```csharp
using BringEngine.Rendering.Platform;

// Startup (defaults to 60 FPS cap + VSync ON)
FramePacer pacer = FramePacer.CreateDefault();
// Or custom: FramePacer.Create(targetFps: 120, vsync: false);
FramePacerSystem.ApplyVSync(ref pacer, ref *gpu, pacer.VSyncEnabled != 0);

// Runtime hotkeys (recommended standard bindings)
if (world.Input.IsKeyPressed(KeyCode.F2)) FramePacerSystem.ToggleVSync(ref pacer, ref *gpu);
if (world.Input.IsKeyPressed(KeyCode.F3)) FramePacerSystem.CycleFpsCapPreset(ref pacer); // 60->120->144->Max->30

// Per-frame pacing
FramePacerSystem.BeginFrame(ref pacer);
// ... update & render ...
FramePacerSystem.EndFrameAndPace(ref pacer, useSdlPreciseDelay: true);
```
- When VSync is disabled, `GpuSystem.SetVSync(ref gpu, false)` checks `SDL.WindowSupportsGPUPresentMode` and picks `Immediate`, falling back to `Mailbox`, then `VSync`.
- Frame pacing uses `SDL.DelayPrecise` (sub-millisecond cross-platform sleep) in windowed mode and high-precision spin-sleep in headless mode.

## 3. Frame Profiling (`FrameProfiler` + `ProfilerSystem`)
`FrameProfiler` (`BringEngine.Core.Profiling`) is an unmanaged, zero-GC, headless-safe profiler that tracks:
- **`CpuFrameMs` / `SmoothedCpuMs`**: Pure CPU execution time in ms (automatically excludes VSync/FPS-cap sleep AND GPU fence wait!).
- **`GpuFrameMs` / `SmoothedGpuMs`**: Real GPU execution time in ms measured by `BatchRenderer.RenderFrame(..., profiler: profiler)` using SDL3_gpu fences (`MeasureGpuWithFence = 1`).
- **`CurrentFps` / `InstantFps` / `FrameTotalMs`**: Smoothed (30-frame rolling window) and instantaneous wall-to-wall FPS.
- **Named CPU Scopes**: Profile any engine or game system with zero heap allocations:
  ```csharp
  int tok = ProfilerSystem.BeginScope(ref *profiler, "GameAI");
  GameAiSystem.Step(ref sim, dt);
  ProfilerSystem.EndScope(ref *profiler, tok);
  ```
- **GPU Batch Metrics (`RenderStats`)**:
  - `DrawCalls`: Total `SDL_DrawGPUIndexedPrimitives` calls.
  - `SetPassCalls`: Number of GPU shader/pipeline or texture/material pass switches (Unity-equivalent SetPass metric).
  - `PipelineBinds`, `RenderPasses`, `CopyPasses`, `MeshInstances`, `SpriteInstances`, `GlyphInstances`, `ParticleInstances`, `Triangles`, `CulledMeshes`, `FrameArenaUsedBytes`.

## 4. Common Built-In Profiler UI Overlay (`ProfilerOverlaySystem`)
Every game built on Bring Engine should wire in the common `ProfilerOverlay` (`BringEngine.Rendering.Profiling`) — it renders on its own persistent layer (`LayerMask = 1u << 30`, `DepthOrder = 1000`, `OwnerScene = SceneId.Invalid`) so it **automatically survives all `Single` and `Additive` scene loads/unloads** and allocates **0 GC bytes per frame**!

```csharp
using BringEngine.Rendering.Profiling;

// 1. Create once after your shared 'common' bundle loads font + white texture:
var profOverlay = ProfilerOverlaySystem.Create(
    ref world, mainFont, whiteTex, window.PixelWidth, window.PixelHeight,
    anchor: ProfilerAnchor.TopRight, startVisible: true);

// 2. Toggle with F1:
if (world.Input.IsKeyPressed(KeyCode.F1))
    ProfilerOverlaySystem.Toggle(ref profOverlay, ref world);

// 3. Update every frame (before StepCoreSystems & RenderFrame):
ProfilerOverlaySystem.Update(ref profOverlay, ref world, in *profiler, in gpu->LastFrameStats, in pacer, window.PixelWidth, window.PixelHeight);

// 4. RenderFrame with profiler pointer attached:
BatchRenderer.RenderFrame(ref *gpu, ref world, ref engAlloc, in lighting, captureToOffscreen: false, profiler: profiler);

// 5. Mark EndCpuWork BEFORE pacing, and EndFrame AFTER pacing:
ProfilerSystem.EndCpuWork(ref *profiler);
FramePacerSystem.EndFrameAndPace(ref pacer, useSdlPreciseDelay: true);
ProfilerSystem.EndFrame(ref *profiler);

// 6. Teardown on shutdown:
ProfilerOverlaySystem.Destroy(ref profOverlay, ref world);
```

### What the Common UI Panel Displays
1. **Header**: `PROFILER [F1]   VSYNC:ON  CAP:60`
2. **FPS & Total Frame**: `FPS: 60.0   FRAME: 16.67 ms`
3. **CPU & GPU Timings**: `CPU: 0.78 ms  |  GPU: 1.15 ms`
4. **Draw & Pass Telemetry**: `DRAWS: 18  |  SETPASS: 14  |  PASSES: 4`
5. **Objects & Memory**: `OBJS: 540  |  TRIS: 1450  |  ARENA: 340KB`
6. **Top CPU Scopes** + **Visual CPU (cyan) / GPU (gold) Frame Budget Bar** (`0..targetBudgetMs`).
