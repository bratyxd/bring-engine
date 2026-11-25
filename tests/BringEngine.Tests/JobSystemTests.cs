using System;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class JobSystemTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ParallelSquareCtx
    {
        public Fix32* Input;
        public Fix32* Output;
        public int* WorkerHits;
        public int MaxSlots;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DagCtx
    {
        public int* Order;
        public int Cursor;
        public int ValA;
        public int ValB;
        public int ValC;
        public int ValD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CancelCtx
    {
        public JobScheduler* Sched;
        public JobHandle SelfHandle;
        public int Started;
        public int Processed;
        public int SawCancel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScratchCtx
    {
        public JobScheduler* Sched;
        public long* Output;
    }

    public static void RunAll()
    {
        Console.WriteLine("=== Multithreading: Data-Oriented JobSystem, ParallelFor, Job DAGs & Parallel Subsystems ===");
        Assert.Run("JobSystem_ParallelFor_InlineThresholdAndDeterministicMultiThread", &TestParallelForInlineAndMultiThread);
        Assert.Run("JobSystem_DependencyGraph_FanOutFanInAndCombineDependencies", &TestDependencyGraph);
        Assert.Run("JobSystem_AsyncBackgroundJob_PollAndCooperativeCancel", &TestAsyncPollAndCancel);
        Assert.Run("JobSystem_PerWorkerScratchAndZeroGcHotLoop", &TestPerWorkerScratchAndZeroGc);
        Assert.Run("JobSystem_EngineSubsystems_ParallelParticlesAndTransformsBitExact", &TestParallelParticlesAndTransformsBitExact);
    }

    private static void ParallelSquareKernel(void* userData, int begin, int end, int workerIndex)
    {
        ParallelSquareCtx* ctx = (ParallelSquareCtx*)userData;
        if ((uint)workerIndex < (uint)ctx->MaxSlots)
            Interlocked.Increment(ref ctx->WorkerHits[workerIndex]);

        for (int i = begin; i < end; i++)
        {
            Fix32 x = ctx->Input[i];
            ctx->Output[i] = x * x + Fix32.Sin(x);
        }
    }

    private static void TestParallelForInlineAndMultiThread()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);

        JobScheduler* sched = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(sched, ref alloc.Persistent, workerCount: 4);

        try
        {
            const int N = 4096;
            Fix32* input = alloc.Persistent.Alloc<Fix32>(N, 64);
            Fix32* serialOut = alloc.Persistent.Alloc<Fix32>(N, 64);
            Fix32* parallelOut = alloc.Persistent.Alloc<Fix32>(N, 64);
            int* hits = alloc.Persistent.Alloc<int>(sched->TotalThreadSlots, 64);

            for (int i = 0; i < N; i++)
                input[i] = Fix32.FromRatio(i - 2048, 128);

            // 1. Below minParallelCount -> MUST run strictly inline on caller thread (workerIndex 0)
            var smallCtx = new ParallelSquareCtx { Input = input, Output = serialOut, WorkerHits = hits, MaxSlots = sched->TotalThreadSlots };
            JobSystem.ParallelFor(sched, count: 128, batchSize: 64, &smallCtx, &ParallelSquareKernel, minParallelCount: 512);
            Assert.Equal(1, hits[0], "Small loop below minParallelCount ran inline on caller thread");
            for (int w = 1; w < sched->TotalThreadSlots; w++)
                Assert.Equal(0, hits[w], $"Worker {w} stayed idle for small loop below minParallelCount");

            // Compute full serial reference (sched = null)
            JobSystem.ParallelFor(null, count: N, batchSize: 256, &smallCtx, &ParallelSquareKernel);

            // 2. Above minParallelCount -> fans out across pool & produces bit-exact identical Fix32 outputs
            for (int w = 0; w < sched->TotalThreadSlots; w++) hits[w] = 0;
            var parCtx = new ParallelSquareCtx { Input = input, Output = parallelOut, WorkerHits = hits, MaxSlots = sched->TotalThreadSlots };
            JobSystem.ParallelFor(sched, count: N, batchSize: 128, &parCtx, &ParallelSquareKernel, minParallelCount: 256);

            int totalChunks = 0;
            for (int w = 0; w < sched->TotalThreadSlots; w++) totalChunks += hits[w];
            Assert.Equal(N / 128, totalChunks, "Every chunk executed exactly once");

            for (int i = 0; i < N; i++)
                Assert.Equal(serialOut[i].Raw, parallelOut[i].Raw, $"Bit-exact Fix32 match at index {i}");
        }
        finally
        {
            JobSystem.Stop(sched);
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after JobSystem ParallelFor");
    }

    private static void DagJobA(void* ud, int w)
    {
        _ = w;
        DagCtx* c = (DagCtx*)ud;
        c->ValA = 10;
        int idx = Interlocked.Increment(ref c->Cursor) - 1;
        c->Order[idx] = 1;
    }

    private static void DagJobB(void* ud, int w)
    {
        _ = w;
        DagCtx* c = (DagCtx*)ud;
        c->ValB = c->ValA * 3; // depends on A (= 30)
        int idx = Interlocked.Increment(ref c->Cursor) - 1;
        c->Order[idx] = 2;
    }

    private static void DagJobC(void* ud, int w)
    {
        _ = w;
        DagCtx* c = (DagCtx*)ud;
        c->ValC = c->ValA + 7; // depends on A (= 17)
        int idx = Interlocked.Increment(ref c->Cursor) - 1;
        c->Order[idx] = 3;
    }

    private static void DagJobD(void* ud, int w)
    {
        _ = w;
        DagCtx* c = (DagCtx*)ud;
        c->ValD = c->ValB + c->ValC; // depends on Combine(B, C) (= 47)
        int idx = Interlocked.Increment(ref c->Cursor) - 1;
        c->Order[idx] = 4;
    }

    private static void TestDependencyGraph()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        JobScheduler* sched = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(sched, ref alloc.Persistent, workerCount: 4);

        try
        {
            int* order = alloc.Persistent.Alloc<int>(4, 64);
            DagCtx* ctx = alloc.Persistent.Alloc<DagCtx>(1, 64);

            // Stress-test 250 DAG iterations (also verifies DepLink free-list recycling!)
            for (int iter = 0; iter < 250; iter++)
            {
                *ctx = new DagCtx { Order = order };
                JobHandle hA = JobSystem.Schedule(sched, ctx, &DagJobA);
                JobHandle hB = JobSystem.Schedule(sched, ctx, &DagJobB, dependency: hA);
                JobHandle hC = JobSystem.Schedule(sched, ctx, &DagJobC, dependency: hA);
                JobHandle hBC = JobSystem.CombineDependencies(sched, hB, hC);
                JobHandle hD = JobSystem.Schedule(sched, ctx, &DagJobD, dependency: hBC);

                JobSystem.Complete(sched, hD);

                Assert.True(JobSystem.IsComplete(sched, hA) && JobSystem.IsComplete(sched, hD), "All DAG handles completed");
                Assert.Equal(47, ctx->ValD, "DAG output ValD == (10*3) + (10+7)");
                Assert.Equal(1, order[0], "A ran first");
                Assert.Equal(4, order[3], "D ran last after Combine(B, C)");
            }
        }
        finally
        {
            JobSystem.Stop(sched);
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after DAG stress test");
    }

    private static void CancelableRangeKernel(void* userData, int begin, int end, int workerIndex)
    {
        _ = workerIndex;
        CancelCtx* c = (CancelCtx*)userData;
        Interlocked.Increment(ref c->Started);

        for (int i = begin; i < end; i++)
        {
            if (JobSystem.IsCancelRequested(c->Sched, c->SelfHandle))
            {
                Interlocked.Exchange(ref c->SawCancel, 1);
                return;
            }
            Interlocked.Increment(ref c->Processed);
            Thread.SpinWait(200);
        }
    }

    private static void TestAsyncPollAndCancel()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        JobScheduler* sched = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(sched, ref alloc.Persistent, workerCount: 2);

        try
        {
            CancelCtx* ctx = alloc.Persistent.Alloc<CancelCtx>(1, 64);
            ctx->Sched = sched;

            JobHandle h = JobSystem.ScheduleParallelFor(sched, count: 20000, batchSize: 500, ctx, &CancelableRangeKernel);
            ctx->SelfHandle = h;

            // Request cooperative cancel immediately and wait for safe drain before freeing ctx
            JobSystem.RequestCancel(sched, h);
            JobSystem.Complete(sched, h);

            Assert.True(JobSystem.IsComplete(sched, h), "Cancelled job reached Completed safely");
            Assert.True(ctx->Processed < 20000 || ctx->SawCancel != 0, "Cooperative cancel stopped remaining work early");
        }
        finally
        {
            JobSystem.Stop(sched);
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after cancelable background job");
    }

    private static void ScratchKernel(void* userData, int begin, int end, int workerIndex)
    {
        ScratchCtx* ctx = (ScratchCtx*)userData;
        LinearArena* scratch = JobSystem.GetWorkerScratch(ctx->Sched, workerIndex);
        ArenaMarker mark = scratch->SaveMarker();

        int len = end - begin;
        long* temp = scratch->AllocUninitialized<long>(len, 16);
        for (int k = 0; k < len; k++)
            temp[k] = (long)(begin + k) * 3L + 1L;
        for (int k = 0; k < len; k++)
            ctx->Output[begin + k] = temp[k];

        scratch->Rollback(mark);
    }

    private static void TestPerWorkerScratchAndZeroGc()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        JobScheduler* sched = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(sched, ref alloc.Persistent, workerCount: 4, workerScratchBytes: 128 * 1024);

        try
        {
            const int N = 2048;
            long* output = alloc.Persistent.Alloc<long>(N, 64);
            var ctx = new ScratchCtx { Sched = sched, Output = output };

            // Warmup 2 iterations
            for (int w = 0; w < 2; w++)
                JobSystem.ParallelFor(sched, N, batchSize: 128, &ctx, &ScratchKernel, minParallelCount: 256);

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int iter = 0; iter < 50; iter++)
            {
                JobSystem.ParallelFor(sched, N, batchSize: 128, &ctx, &ScratchKernel, minParallelCount: 256);
                JobSystem.ResetAllWorkerScratch(sched);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, gcAfter - gcBefore, "JobSystem ParallelFor + per-worker scratch hot loop allocates 0 GC bytes");
            for (int i = 0; i < N; i++)
                Assert.Equal((long)i * 3L + 1L, output[i], $"Per-worker scratch result valid at {i}");
        }
        finally
        {
            JobSystem.Stop(sched);
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after scratch & zero-GC test");
    }

    private static void TestParallelParticlesAndTransformsBitExact()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var allocSerial = EngineAllocators.Create(12 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var allocPar = EngineAllocators.Create(12 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);

        JobScheduler* sched = allocPar.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(sched, ref allocPar.Persistent, workerCount: 4);

        try
        {
            var worldS = EngineWorld.Create(ref allocSerial.Persistent, maxEntities: 4096, maxParticles: 8192);
            var worldP = EngineWorld.Create(ref allocPar.Persistent, maxEntities: 4096, maxParticles: 8192);

            // 1. Build identical 2048-node 3-level transform hierarchies (> TransformSystem.ParallelThreshold)
            const int rootCount = 512;
            for (int r = 0; r < rootCount; r++)
            {
                var rS = EntitySystem.Spawn(ref worldS.Entities);
                var rP = EntitySystem.Spawn(ref worldP.Entities);
                Vec3 p0 = new Vec3(r * 0.5f, 1.0f, -r * 0.25f);
                Quat q0 = Quat.FromAxisAngle(Vec3.UnitY, r * 0.13f);
                TransformSystem.Add(ref worldS.Transforms, rS, p0, q0, Vec3.One);
                TransformSystem.Add(ref worldP.Transforms, rP, p0, q0, Vec3.One);

                for (int c = 0; c < 3; c++)
                {
                    var cS = EntitySystem.Spawn(ref worldS.Entities);
                    var cP = EntitySystem.Spawn(ref worldP.Entities);
                    Vec3 pc = new Vec3(c - 1f, 0.75f, 0.5f);
                    Quat qc = Quat.FromAxisAngle(Vec3.UnitX, c * 0.4f);
                    TransformSystem.Add(ref worldS.Transforms, cS, pc, qc, new Vec3(0.9f, 0.9f, 0.9f), rS);
                    TransformSystem.Add(ref worldP.Transforms, cP, pc, qc, new Vec3(0.9f, 0.9f, 0.9f), rP);
                }
            }

            // 2. Spawn identical 4096-particle burst (> ParticleSystem.ParallelThreshold) with drag & turbulence
            var emCfg = ParticleSystem.DefaultEmitter(new Vec3(1f, 2f, -1f), seed: 77777UL);
            emCfg.Turbulence = 2.5f;
            emCfg.Drag = 0.35f;
            emCfg.AtlasColumns = 4;
            emCfg.AtlasRows = 2;
            var eS = ParticleSystem.CreateEmitter(ref worldS.Particles, in emCfg);
            var eP = ParticleSystem.CreateEmitter(ref worldP.Particles, in emCfg);
            ParticleSystem.Burst(ref worldS.Particles, eS, 4096);
            ParticleSystem.Burst(ref worldP.Particles, eP, 4096);

            // Step 5 frames: serial vs parallel JobSystem
            for (int f = 0; f < 5; f++)
            {
                allocSerial.BeginFrame();
                allocPar.BeginFrame();
                EngineWorldSystem.StepCoreSystems(ref worldS, ref allocSerial, 1f / 60f, 1280f, 720f, jobs: null);
                EngineWorldSystem.StepCoreSystems(ref worldP, ref allocPar, 1f / 60f, 1280f, 720f, jobs: sched);
            }

            // Verify bit-exact identical transform world positions & matrices
            Assert.Equal(worldS.Transforms.Count, worldP.Transforms.Count, "Transform count matches");
            for (int i = 0; i < worldS.Transforms.Count; i++)
            {
                Vec3 a = worldS.Transforms.WorldPosition[i];
                Vec3 b = worldP.Transforms.WorldPosition[i];
                Assert.Equal(BitConverter.SingleToInt32Bits(a.X), BitConverter.SingleToInt32Bits(b.X), $"Transform[{i}].X bit-exact");
                Assert.Equal(BitConverter.SingleToInt32Bits(a.Y), BitConverter.SingleToInt32Bits(b.Y), $"Transform[{i}].Y bit-exact");
                Assert.Equal(BitConverter.SingleToInt32Bits(a.Z), BitConverter.SingleToInt32Bits(b.Z), $"Transform[{i}].Z bit-exact");
            }

            // Verify bit-exact identical particle state across all 4096 particles
            Assert.Equal(worldS.Particles.ParticleCount, worldP.Particles.ParticleCount, "Particle count matches");
            Assert.True(worldS.Particles.ParticleCount >= 3000, "Large particle pool active");
            for (int i = 0; i < worldS.Particles.ParticleCount; i++)
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(worldS.Particles.PosX[i]), BitConverter.SingleToInt32Bits(worldP.Particles.PosX[i]), $"Particle[{i}].PosX bit-exact");
                Assert.Equal(BitConverter.SingleToInt32Bits(worldS.Particles.PosY[i]), BitConverter.SingleToInt32Bits(worldP.Particles.PosY[i]), $"Particle[{i}].PosY bit-exact");
                Assert.Equal(BitConverter.SingleToInt32Bits(worldS.Particles.PosZ[i]), BitConverter.SingleToInt32Bits(worldP.Particles.PosZ[i]), $"Particle[{i}].PosZ bit-exact");
                Assert.Equal(BitConverter.SingleToInt32Bits(worldS.Particles.Size[i]), BitConverter.SingleToInt32Bits(worldP.Particles.Size[i]), $"Particle[{i}].Size bit-exact");
            }

            // Zero-GC check on parallel StepCoreSystems hot loop
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 10; f++)
            {
                allocPar.BeginFrame();
                EngineWorldSystem.StepCoreSystems(ref worldP, ref allocPar, 1f / 60f, 1280f, 720f, jobs: sched);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "Parallel StepCoreSystems (4096 particles + 2048 transforms) allocates 0 GC bytes");
        }
        finally
        {
            JobSystem.Stop(sched);
            allocPar.Dispose();
            allocSerial.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after parallel engine subsystems test");
    }
}
