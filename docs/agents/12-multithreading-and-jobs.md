# Agent Guide 12 — Native First-Class Multithreading & Data-Oriented Job System

## 1. Two-Tier Threading Architecture
Because Bring Engine is strictly **Data-Oriented** (64-byte aligned SoA tables, unmanaged structs, explicit native arenas), multithreading is built in as a **first-class, zero-GC, zero-OOP** capability (`BringEngine.Core.Threading`):

1. **Tier 1 — 2-Thread Pipelined Frame Loop (`EnginePipelineSystem`, always on)**
   - Runs **Logic Thread** (Frame `N`: Sim + ViewSync + CoreSystems + `ExtractRenderPacket`) concurrently with **Main/Render Thread** (Frame `N-1`: SDL3 events + `SubmitRenderPacket` + `FramePacer`).
   - See [`11-vsync-fps-cap-and-profiling.md`](11-vsync-fps-cap-and-profiling.md).
2. **Tier 2 — Data-Oriented Worker Pool (`JobScheduler` + `JobSystem`, opt-in where work justifies it)**
   - Fork-join **`JobSystem.ParallelFor`** with automatic serial inline fallback (`minParallelCount`).
   - Async **`JobSystem.Schedule`** / **`JobSystem.ScheduleParallelFor`** + DAG **`JobSystem.CombineDependencies`**.
   - Cooperative cancellation (**`JobSystem.RequestCancel`** / **`IsCancelRequested`**) for multi-frame background generation.
   - Lock-free per-worker native scratch arenas (**`JobSystem.GetWorkerScratch(sched, workerIndex)`**).
   - **0 GC bytes allocated** per job, **0 OOP**, **100% NativeAOT**, and **`NativeMemoryTracker.LiveBytes == 0`** on teardown.

## 2. CRITICAL RULE: Do NOT Multithread Everything!
> **Only parallelize when it genuinely makes sense.** Multithreading tiny loops wastes CPU on synchronization, cache-line bouncing, and wakeups — making frames *slower*, not faster.

### When to USE `JobSystem`
- **Large independent SoA loops** (typically **>= 512–2048 elements** or **>= 0.5 ms** in `ProfilerOverlay` / `ProfilerSystem`):
  - Thousands of enemies, boids, projectiles, or particles (`ParticleSystem.Step(..., jobs)` auto-parallelizes only above `2048` live particles).
  - Large transform hierarchies (`TransformSystem.PropagateHierarchy(..., jobs)` auto-parallelizes only above `1024` transforms).
  - Broadphase spatial grids, flow-field / Dijkstra / A* batch pathfinding, cloth / soft-body / steering integration.
- **Multi-frame background work** that would otherwise cause a frame hitch (> 2 ms):
  - Procedural terrain / voxel / dungeon chunk generation, heavy procedural `.glb`/mesh baking, big asset table preprocessing.
  - Schedule with `JobSystem.ScheduleParallelFor(...)`, poll `JobSystem.IsComplete(sched, handle)` each frame, and never stall a render frame.

### When to STAY Single-Threaded (Do NOT Multithread!)
- Small loops (< ~500 items or < 0.2 ms in `ProfilerOverlay`) — a single-threaded L1 linear loop over SoA data finishes in microseconds.
- Sequential state machines, HUD / UI layout (`ProfilerOverlay`, menus), camera updates (<= 32 cameras), `TweenSystem` (small counts touching shared targets), input polling.
- Anything touching **SDL3** or **`GpuContext`** (SDL3 window, events, and GPU swapchain MUST remain strictly on the Main Thread!).
- **Always measure first** with `ProfilerSystem.BeginScope` / `EndScope` and watch `LOGIC` ms in `ProfilerOverlay`.

## 3. Lifecycle Setup (`JobScheduler` in `Persistent` Arena)
Allocate `JobScheduler` in stable pinned native memory (`EngineAllocators.Persistent`) once at startup and stop it cleanly before disposing arenas:
```csharp
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

// Startup (Main Thread):
// workerCount = -1 picks RecommendedWorkerCount() = clamp(ProcessorCount - 2, 1, 32)
// leaving dedicated headroom for Main/Render Thread + Logic Thread!
// Pass workerCount = 0 for deterministic single-thread debugging.
JobScheduler* jobs = engAlloc.Persistent.Alloc<JobScheduler>(1, 64);
JobSystem.Start(jobs, ref engAlloc.Persistent, workerCount: -1);

// Inside Logic Thread (optional acceleration for large transform/particle counts):
EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, dt, vpW, vpH, jobs: jobs);

// Shutdown (after EnginePipelineSystem.Stop):
JobSystem.Stop(jobs);
```

## 4. Fork-Join `ParallelFor` Over Game SoA Tables
Pass `minParallelCount` so small entity counts run **inline on the calling thread with zero sync cost**, and large counts automatically fan out across workers (with the calling thread actively helping execute chunks!):
```csharp
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BoidJobCtx
{
    public FixVec3* Pos;
    public FixVec3* Vel;
    public FixVec3* OutPos; // disjoint output buffer for deterministic bit-exact reads!
    public Fix32 Dt;
}

private static unsafe void StepBoidsRange(void* userData, int begin, int end, int workerIndex)
{
    _ = workerIndex;
    BoidJobCtx* ctx = (BoidJobCtx*)userData;
    for (int i = begin; i < end; i++)
        ctx->OutPos[i] = ctx->Pos[i] + ctx->Vel[i] * ctx->Dt;
}

// Call inside GameSimSystems.StepFixed (pass JobScheduler* jobs = null for headless/single-thread!):
var ctx = new BoidJobCtx { Pos = s.BoidPos, Vel = s.BoidVel, OutPos = s.BoidNextPos, Dt = dt };
JobSystem.ParallelFor(
    jobs,
    count: s.BoidCount,
    batchSize: 256,
    userData: &ctx,
    callback: &StepBoidsRange,
    minParallelCount: 1024); // < 1024 stays 100% single-threaded inline!
```

## 5. Multi-Frame Async Background Jobs & Cancellation
For heavy procedural generation (maps, nav grids, chunk meshes), schedule asynchronously and poll across frames without blocking gameplay:
```csharp
// Kick off at level load (write into preallocated GameAllocators.Level buffer):
view->GenJob = JobSystem.ScheduleParallelFor(
    jobs, count: chunkCount, batchSize: 4,
    userData: view->GenCtx, callback: &GenerateChunksRange);

// Inside GenerateChunksRange: check cooperative cancellation!
if (JobSystem.IsCancelRequested(ctx->Jobs, ctx->Handle)) return;

// Poll every frame (non-blocking, 0 GC):
if (view->GenJob.IsValid && JobSystem.IsComplete(jobs, view->GenJob))
{
    view->GenJob = JobHandle.Invalid;
    // Commit generated buffers / upload meshes on Main Thread barrier
}

// On scene unload or restart BEFORE ResetLevel() / freeing buffers:
if (view->GenJob.IsValid)
{
    JobSystem.RequestCancel(jobs, view->GenJob);
    JobSystem.Complete(jobs, view->GenJob); // MUST drain before freeing/resetting arenas!
    view->GenJob = JobHandle.Invalid;
}
```

## 6. Dependency DAGs (`CombineDependencies`) & Per-Worker Scratch
Fan out independent heavy jobs and join them before a dependent pass — with zero locks and zero GC:
```csharp
JobHandle hFlowField = JobSystem.ScheduleParallelFor(jobs, cellCount, 256, &flowCtx, &BuildFlowField);
JobHandle hGrid      = JobSystem.ScheduleParallelFor(jobs, agentCount, 256, &gridCtx, &BuildSpatialHash);
JobHandle hBoth      = JobSystem.CombineDependencies(jobs, hFlowField, hGrid);
JobHandle hSteer     = JobSystem.ScheduleParallelFor(jobs, agentCount, 256, &steerCtx, &SteerAgents, dependency: hBoth);
JobSystem.Complete(jobs, hSteer);
```

Need temporary scratch buffers inside a parallel job? Never share `EngineAllocators.Scratch` across threads — use the worker's own lock-free arena:
```csharp
LinearArena* scratch = JobSystem.GetWorkerScratch(ctx->Jobs, workerIndex);
ArenaMarker mark = scratch->SaveMarker();
int* temp = scratch->AllocUninitialized<int>(256, 16);
// ... temp work ...
scratch->Rollback(mark);
```

## 7. Determinism & Thread-Safety Checklist
1. **Disjoint writes only**: Range `[begin, end)` writes ONLY to indices `[begin, end)` (double-buffer read/write arrays when items read neighbors, or write into per-`workerIndex` slots and reduce sequentially in `0..TotalThreadSlots-1` order).
2. **Never share mutable `DetRng`**: Seed per-item or per-chunk deterministic hashes (`DetRng.Create(baseSeed ^ (ulong)i)`) inside parallel kernels so results are bit-exact regardless of worker count.
3. **Always `Complete` before arena reset/dispose**: Call `JobSystem.RequestCancel` + `JobSystem.Complete` before `gameAlloc.ResetLevel()`, `SceneSystem.UnloadScene`, or `Dispose()`.
4. **Zero SDL3 / GPU calls inside jobs**: Keep all rendering & window calls on the Main Thread; keep `Simulation/` pure unmanaged math + memory (`JobSystem` works identically in `--headless` tests with `workerCount = 0` or `workerCount = 4`).
