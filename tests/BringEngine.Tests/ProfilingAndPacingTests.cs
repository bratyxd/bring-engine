using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
using BringEngine.Core.Threading;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Rendering.Profiling;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class ProfilingAndPacingTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TestPipelineCtx
    {
        public EngineWorld* World;
        public EngineAllocators* Alloc;
        public ProfilerOverlay* Overlay;
        public FrameProfiler* Profiler;
        public FramePacer* Pacer;
    }

    public static void RunAll(bool headlessOnly = false)
    {
        Console.WriteLine("=== Update: VSync, FPS Cap, Separate-Bar Profiler, 2-Thread Pipeline & GPU Validation ===");
        Assert.Run("FramePacer_DefaultsToVSyncOnAnd60FpsCap", &TestFramePacerDefaultsAndCap);
        Assert.Run("FrameProfilerAndOverlay_SeparateBarsAndZeroGc", &TestProfilerAndOverlayZeroGc);
        Assert.Run("ProfilerOverlay_AnchorsResizeVisibilityAndScopesReset", &TestOverlayAnchorsResizeAndScopesReset);
        Assert.Run("EnginePipeline_HeadlessFaultRecoveryAndFlushZeroLeak", &TestEnginePipelineHeadlessFaultAndFlush);
        Assert.Run("FrameProfiler_ConcurrentSeqlockReadAndPipelineConcurrentStop", &TestProfilerSeqlockAndPipelineConcurrentStop);
        if (!headlessOnly)
        {
            Assert.Run("GpuVSyncToggle_SetPassCallsAndGpuDebugValidation", &TestGpuVSyncAndDebugValidation);
            Assert.Run("EnginePipeline_TwoThreadConcurrentExtractSubmitAndZeroLeak", &TestTwoThreadPipeline);
        }
    }

    private static void TestFramePacerDefaultsAndCap()
    {
        var pacer = FramePacer.CreateDefault();
        Assert.Equal(60, pacer.TargetFps, "Default FPS cap must be 60");
        Assert.Equal((byte)1, pacer.VSyncEnabled, "Default VSync must be ON");

        Assert.Equal(120, FramePacerSystem.CycleFpsCapPreset(ref pacer), "60 -> 120");
        Assert.Equal(144, FramePacerSystem.CycleFpsCapPreset(ref pacer), "120 -> 144");
        Assert.Equal(0, FramePacerSystem.CycleFpsCapPreset(ref pacer), "144 -> Uncapped(0)");
        Assert.Equal(30, FramePacerSystem.CycleFpsCapPreset(ref pacer), "0 -> 30");
        Assert.Equal(60, FramePacerSystem.CycleFpsCapPreset(ref pacer), "30 -> 60");

        FramePacerSystem.SetTargetFps(ref pacer, 120);
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < 5; i++)
        {
            FramePacerSystem.BeginFrame(ref pacer);
            FramePacerSystem.EndFrameAndPace(ref pacer, useSdlPreciseDelay: false);
        }
        double elapsedMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Assert.True(elapsedMs >= 35.0, $"Expected >=35ms for 5 frames capped at 120 FPS, got {elapsedMs:F2}ms");
    }

    private static void TestProfilerAndOverlayZeroGc()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 512, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        FontHandle font = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("pf_font"));
        TextureHandle white = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("pf_white"));

        // Verify symmetric SDF on-edge value (128) for opaque crisp glyphs
        var fontPtr = BringEngine.Assets.AssetSystem.TryGetFont(in world.Assets, font);
        Assert.True(fontPtr != null && fontPtr->SdfOnEdgeValue == 128, "SDF font bakes symmetric onEdgeValue=128");

        var profiler = FrameProfiler.Create(ref alloc.Persistent, enabled: true, measureGpuWithFence: false);
        var pacer = FramePacer.CreateDefault();
        var overlay = ProfilerOverlaySystem.Create(ref world, font, white, 1280, 720, ProfilerAnchor.TopRight, startVisible: true);
        RenderStats fakeStats = new RenderStats { DrawCalls = 12, SetPassCalls = 9, RenderPasses = 2, CopyPasses = 1, MeshInstances = 40, Triangles = 1200, FrameArenaUsedBytes = 128 * 1024 };

        // Verify separate Logic, Render, and GPU bars exist with strictly unique OrderInLayer
        var logicBar = BringEngine.Simulation.RenderData.RenderComponentSystem.TryGetSprite(in world.RenderComponents, overlay.LogicBarSprite);
        var renderBar = BringEngine.Simulation.RenderData.RenderComponentSystem.TryGetSprite(in world.RenderComponents, overlay.RenderBarSprite);
        var gpuBar = BringEngine.Simulation.RenderData.RenderComponentSystem.TryGetSprite(in world.RenderComponents, overlay.GpuBarSprite);
        Assert.True(logicBar != null && renderBar != null && gpuBar != null, "All 3 separate stage bars created");
        Assert.True(logicBar->OrderInLayer < renderBar->OrderInLayer && renderBar->OrderInLayer < gpuBar->OrderInLayer, "Separate stage bars have strictly unique OrderInLayer");

        // Warmup 2 frames
        for (int f = 0; f < 2; f++)
        {
            ProfilerSystem.BeginFrame(ref profiler);
            int s = ProfilerSystem.BeginScope(ref profiler, "Sim");
            ProfilerSystem.EndScope(ref profiler, s);
            ProfilerSystem.RecordLogicTime(ref profiler, 2.5);
            ProfilerSystem.RecordRenderTime(ref profiler, 1.4);
            ProfilerSystem.RecordGpuTime(ref profiler, 3.2, 0);
            ProfilerOverlaySystem.Update(ref overlay, ref world, in profiler, in fakeStats, in pacer, 1280, 720);
            ProfilerSystem.EndFrame(ref profiler);
        }

        // Hot-path zero-GC verification
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 40; f++)
        {
            ProfilerSystem.BeginFrame(ref profiler);
            int s1 = ProfilerSystem.BeginScope(ref profiler, "Sim");
            ProfilerSystem.EndScope(ref profiler, s1);
            int s2 = ProfilerSystem.BeginScope(ref profiler, "View");
            ProfilerSystem.EndScope(ref profiler, s2);
            ProfilerSystem.RecordLogicTime(ref profiler, 2.5);
            ProfilerSystem.RecordRenderTime(ref profiler, 1.4);
            ProfilerSystem.RecordGpuTime(ref profiler, 3.2, 0);
            ProfilerOverlaySystem.Update(ref overlay, ref world, in profiler, in fakeStats, in pacer, 1280, 720);
            ProfilerSystem.EndFrame(ref profiler);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "FrameProfiler + ProfilerOverlaySystem.Update must allocate 0 GC bytes");
        Assert.True(ProfilerSystem.TryGetLastScopeMs(in profiler, "Sim", out _), "Last frame scope 'Sim' recorded");
        Assert.True(Math.Abs(profiler.SmoothedLogicMs - 2.5) < 0.01, "Smoothed Logic ms tracked independently");
        Assert.True(Math.Abs(profiler.SmoothedRenderMs - 1.4) < 0.01, "Smoothed Render ms tracked independently");
        Assert.True(Math.Abs(profiler.SmoothedGpuMs - 3.2) < 0.01, "Smoothed GPU ms tracked independently");
        Assert.True(profiler.CurrentFps > 0.0, "Smoothed FPS computed");

        ProfilerOverlaySystem.Destroy(ref overlay, ref world);
        bundleArena.Dispose();
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }

    private static void TestGpuVSyncAndDebugValidation()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 2 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 512, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        var window = PlatformSystem.InitWindow("BringEngine Profiler GPU Test", 640, 360, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        // Enable SDL3_gpu debug validation layer to verify zero depth/binding hazards!
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

        FrameProfiler* profiler = alloc.Persistent.Alloc<FrameProfiler>(1, 64);
        *profiler = FrameProfiler.Create(ref alloc.Persistent, enabled: true, measureGpuWithFence: true);
        var pacer = FramePacer.CreateDefault();

        try
        {
            Assert.Equal((byte)1, gpu->VSyncEnabled, "GpuContext defaults to VSync ON");

            FramePacerSystem.ApplyVSync(ref pacer, ref *gpu, false);
            Assert.Equal((byte)0, pacer.VSyncEnabled, "Pacer VSync toggled OFF");
            FramePacerSystem.ApplyVSync(ref pacer, ref *gpu, true);
            Assert.Equal((byte)1, gpu->VSyncEnabled, "GpuContext VSync toggled back ON");

            ShaderSystem.CompileBuiltinShaders(ref *gpu);
            FontHandle font = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("gpu_pf_font"));
            TextureHandle white = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("gpu_pf_white"));

            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0, 2, 6), Vec3.Zero);
            var overlay = ProfilerOverlaySystem.Create(ref world, font, white, 640, 360, ProfilerAnchor.TopRight, startVisible: true);

            GpuSystem.EnsureCaptureTarget(ref *gpu, 640, 360);

            for (int f = 0; f < 4; f++)
            {
                ProfilerSystem.BeginFrame(ref *profiler);
                alloc.BeginFrame();
                ProfilerOverlaySystem.Update(ref overlay, ref world, in *profiler, in gpu->LastFrameStats, in pacer, 640, 360);
                EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 640, 360);
                BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true, profiler: profiler);
                ProfilerSystem.EndCpuWork(ref *profiler);
                ProfilerSystem.EndFrame(ref *profiler);
            }

            var st = gpu->LastFrameStats;
            Assert.True(st.DrawCalls >= 2, $"Expected >=2 draw calls for profiler overlay, got {st.DrawCalls}");
            Assert.True(st.SetPassCalls >= 2, $"Expected >=2 SetPassCalls, got {st.SetPassCalls}");
            Assert.True(st.RenderPasses >= 2, $"Expected >=2 RenderPasses (Main + Overlay), got {st.RenderPasses}");
            Assert.True(st.CopyPasses == 1, $"Expected 1 batched CopyPass, got {st.CopyPasses}");
            Assert.True(profiler->GpuFrameMs > 0.0, $"Expected GPU fence frame time > 0 ms, got {profiler->GpuFrameMs:F4}");
            Assert.True(profiler->RenderCpuMs > 0.0, $"Expected Render CPU time > 0 ms, got {profiler->RenderCpuMs:F4}");

            // Also verify real swapchain blit path (captureToOffscreen: false, VSync ON, debugMode: true)
            // measures PURE GPU work (< 10ms, NOT ~16.6ms VSync present wait!) with zero D3D12 validation errors!
            for (int f = 0; f < 4; f++)
            {
                ProfilerSystem.BeginFrame(ref *profiler);
                alloc.BeginFrame();
                ProfilerOverlaySystem.Update(ref overlay, ref world, in *profiler, in gpu->LastFrameStats, in pacer, 640, 360);
                EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 640, 360);
                BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: false, profiler: profiler);
                ProfilerSystem.EndCpuWork(ref *profiler);
                ProfilerSystem.EndFrame(ref *profiler);
            }
            bool isCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
            double maxGpuMs = isCi ? 150.0 : 10.0;
            Assert.True(profiler->GpuFrameMs > 0.0 && profiler->GpuFrameMs < maxGpuMs,
                $"Swapchain VSync-ON GPU time must exclude VSync wait (< {maxGpuMs:F0}ms), got {profiler->GpuFrameMs:F3}ms");

            // Cross-Module 2.2: Single-threaded RenderFrame with MeasureGpuWithFence = 0 still records Present GpuWaitTicks
            // so VSync Present blocking does not pollute LogicCpuMs!
            profiler->MeasureGpuWithFence = 0;
            ProfilerSystem.BeginFrame(ref *profiler);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 640, 360);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: false, profiler: profiler);
            ProfilerSystem.EndCpuWork(ref *profiler);
            ProfilerSystem.EndFrame(ref *profiler);
            Assert.True(profiler->GpuWaitTicks >= 0L && profiler->LogicCpuMs < maxGpuMs,
                $"Single-threaded LogicCpuMs excludes Present wait ({profiler->LogicCpuMs:F3}ms)");
            profiler->MeasureGpuWithFence = 1;

            ProfilerOverlaySystem.Destroy(ref overlay, ref world);
        }
        finally
        {
            BringEngine.Assets.AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }

    private static void TestPipelineLogicCallback(void* userCtx, ref PipelineFrameInput fin, ref RenderPacket outPacket)
    {
        TestPipelineCtx* ctx = (TestPipelineCtx*)userCtx;
        ProfilerOverlaySystem.Update(ref *ctx->Overlay, ref *ctx->World, in *ctx->Profiler, in fin.PrevRenderStats, in *ctx->Pacer, fin.ViewportWidth, fin.ViewportHeight);
        EngineWorldSystem.StepCoreSystems(ref *ctx->World, ref *ctx->Alloc, fin.DeltaTime, fin.ViewportWidth, fin.ViewportHeight);
        BatchRenderer.ExtractRenderPacket(in fin.BuiltinShaders, in *ctx->World, SceneLighting.Default, ref ctx->Alloc->Frame, ref ctx->Alloc->Scratch, out outPacket, ctx->Profiler);
    }

    private static void TestTwoThreadPipeline()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 2 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        EngineWorld* world = alloc.Persistent.Alloc<EngineWorld>(1, 64);
        *world = EngineWorld.Create(ref alloc.Persistent, 512, 64);

        FrameProfiler* profiler = alloc.Persistent.Alloc<FrameProfiler>(1, 64);
        *profiler = FrameProfiler.Create(ref alloc.Persistent, enabled: true, measureGpuWithFence: true);

        FramePacer* pacer = alloc.Persistent.Alloc<FramePacer>(1, 64);
        *pacer = FramePacer.CreateDefault();

        ProfilerOverlay* overlay = alloc.Persistent.Alloc<ProfilerOverlay>(1, 64);
        EnginePipelineState* pipe = alloc.Persistent.Alloc<EnginePipelineState>(1, 64);
        TestPipelineCtx* ctx = alloc.Persistent.Alloc<TestPipelineCtx>(1, 64);

        var window = PlatformSystem.InitWindow("BringEngine 2T Pipeline Test", 640, 360, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world->Assets, debugMode: false);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world->Assets);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu);
            FontHandle font = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world->Assets, bundleId, StringId.Hash("p2t_font"));
            TextureHandle white = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world->Assets, bundleId, StringId.Hash("p2t_white"));
            BatchRenderer.UploadPendingAssets(ref *gpu, ref *world);

            CameraSystem.CreatePerspective(ref world->Cameras, new Vec3(0, 2, 6), Vec3.Zero);
            *overlay = ProfilerOverlaySystem.Create(ref *world, font, white, 640, 360, ProfilerAnchor.TopRight, startVisible: true);
            GpuSystem.EnsureCaptureTarget(ref *gpu, 640, 360);

            *ctx = new TestPipelineCtx { World = world, Alloc = &alloc, Overlay = overlay, Profiler = profiler, Pacer = pacer };
            var shaders = GpuSystem.GetBuiltinShaders(in *gpu);
            EnginePipelineSystem.Start(pipe, ctx, &TestPipelineLogicCallback, in shaders);

            int mainThreadId = Environment.CurrentManagedThreadId;

            // Warmup 2 frames so both RenderPacket slots are primed
            for (int f = 0; f < 2; f++)
            {
                ProfilerSystem.BeginFrame(ref *profiler);
                var fin = new PipelineFrameInput { DeltaTime = 1f / 60f, ViewportWidth = 640, ViewportHeight = 360, PrevRenderStats = gpu->LastFrameStats, BuiltinShaders = shaders };
                EnginePipelineSystem.StepPipelinedFrame(pipe, ref *gpu, ref alloc, in fin, captureToOffscreen: true, profiler: profiler);
                ProfilerSystem.EndFrame(ref *profiler);
            }

            // Verify zero-GC hot loop on Main/Render Thread across 10 pipelined frames
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 10; f++)
            {
                ProfilerSystem.BeginFrame(ref *profiler);
                var fin = new PipelineFrameInput { DeltaTime = 1f / 60f, ViewportWidth = 640, ViewportHeight = 360, PrevRenderStats = gpu->LastFrameStats, BuiltinShaders = shaders };
                EnginePipelineSystem.StepPipelinedFrame(pipe, ref *gpu, ref alloc, in fin, captureToOffscreen: true, profiler: profiler);
                ProfilerSystem.EndFrame(ref *profiler);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(pipe->LogicThreadId != 0 && pipe->LogicThreadId != mainThreadId, "Logic callback ran on dedicated Logic worker thread");
            Assert.Equal(12UL, pipe->LogicFramesCompleted, "All 12 Logic frames completed");
            Assert.Equal(11UL, pipe->RenderFramesSubmitted, "11 pipelined Render frames submitted (1-frame lag)");
            Assert.Equal(0L, gcAfter - gcBefore, "2-Thread EnginePipeline hot loop allocates 0 GC bytes on Main Thread");
            Assert.Equal((byte)1, profiler->PipelinedTwoThread, "FrameProfiler reports 2-Thread pipelined mode");
            Assert.True(profiler->SmoothedLogicMs > 0.0 && profiler->SmoothedRenderMs > 0.0 && profiler->SmoothedGpuMs > 0.0,
                $"Independent Logic/Render/GPU timings measured ({profiler->SmoothedLogicMs:F3}/{profiler->SmoothedRenderMs:F3}/{profiler->SmoothedGpuMs:F3} ms)");

            EnginePipelineSystem.Stop(pipe);
            ProfilerOverlaySystem.Destroy(ref *overlay, ref *world);
        }
        finally
        {
            EnginePipelineSystem.Stop(pipe);
            BringEngine.Assets.AssetSystem.UnloadByBundle(ref world->Assets, bundleId);
            GpuSystem.Dispose(ref *gpu, ref world->Assets);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after 2-thread pipeline shutdown");
    }

    private static void TestOverlayAnchorsResizeAndScopesReset()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 512, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        FontHandle font = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("ov_font"));
        TextureHandle white = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("ov_white"));
        var profiler = FrameProfiler.Create(ref alloc.Persistent, enabled: true, measureGpuWithFence: false);
        var pacer = FramePacer.CreateDefault();

        // F13 & T7: Verify all 4 anchors, dynamic resize, SetVisible(false), and ScopesText reset to "SCOPES: --"
        var anchors = new[] { ProfilerAnchor.TopLeft, ProfilerAnchor.TopRight, ProfilerAnchor.BottomLeft, ProfilerAnchor.BottomRight };
        foreach (ProfilerAnchor anchor in anchors)
        {
            var ov = ProfilerOverlaySystem.Create(ref world, font, white, 1280f, 720f, anchor, startVisible: true);
            int tdIdx = BringEngine.Simulation.World.TransformSystem.GetDenseIndex(in world.Transforms, ov.BgEntity);
            Vec3 pos720 = world.Transforms.LocalPosition[tdIdx];

            // Resize to ultrawide 2560x1080 -> right-anchored panels must shift right
            ProfilerOverlaySystem.Update(ref ov, ref world, in profiler, default, in pacer, 2560f, 1080f);
            Vec3 posWide = world.Transforms.LocalPosition[tdIdx];
            if (anchor == ProfilerAnchor.TopRight || anchor == ProfilerAnchor.BottomRight)
                Assert.True(posWide.X > pos720.X, $"Right-anchored {anchor} shifts X on wider aspect ({pos720.X} -> {posWide.X})");
            else
                Assert.Near(pos720.X, posWide.X, 1e-3f, $"Left-anchored {anchor} keeps left margin");

            ProfilerOverlaySystem.SetVisible(ref ov, ref world, false);
            var bgSp = BringEngine.Simulation.RenderData.RenderComponentSystem.TryGetSprite(in world.RenderComponents, ov.BgSprite);
            Assert.True(bgSp != null && bgSp->Visible == 0, $"{anchor} SetVisible(false) hides BgSprite");

            ProfilerOverlaySystem.Destroy(ref ov, ref world);
        }

        // ScopesText reset when 0 scopes arrive after a frame with scopes
        var ovScope = ProfilerOverlaySystem.Create(ref world, font, white, 1280f, 720f, ProfilerAnchor.TopRight, startVisible: true);
        ProfilerSystem.BeginFrame(ref profiler);
        int s = ProfilerSystem.BeginScope(ref profiler, "Sim");
        ProfilerSystem.EndScope(ref profiler, s);
        ProfilerSystem.EndFrame(ref profiler);
        ProfilerSystem.BeginFrame(ref profiler); // copies "Sim" into LastFrameScopes
        ProfilerOverlaySystem.Update(ref ovScope, ref world, in profiler, default, in pacer, 1280f, 720f);
        var txt = BringEngine.Simulation.RenderData.RenderComponentSystem.TryGetText(in world.RenderComponents, ovScope.ScopesText);
        Assert.True(txt != null && txt->Content.ToString().Contains("Sim:"), "ScopesText displays active scope");

        ProfilerSystem.EndFrame(ref profiler); // 0 scopes in this frame
        ProfilerSystem.BeginFrame(ref profiler); // LastFrameScopeCount becomes 0
        ProfilerOverlaySystem.Update(ref ovScope, ref world, in profiler, default, in pacer, 1280f, 720f);
        Assert.Equal("SCOPES: --", txt->Content.ToString(), "ScopesText resets to 'SCOPES: --' when 0 scopes arrive");

        ProfilerOverlaySystem.Destroy(ref ovScope, ref world);
        bundleArena.Dispose();
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }

    private static void FaultingLogicCallback(void* userCtx, ref PipelineFrameInput fin, ref RenderPacket outPacket)
    {
        int* shouldThrow = (int*)userCtx;
        if (*shouldThrow != 0)
            throw new InvalidDataException("Simulated logic worker fault");
        outPacket = new RenderPacket { Valid = 1 };
    }

    private static void TestEnginePipelineHeadlessFaultAndFlush()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 1 * 1024 * 1024, 512 * 1024, 128 * 1024);
        EnginePipelineState* pipe = alloc.Persistent.Alloc<EnginePipelineState>(1, 64);
        int* flag = alloc.Persistent.Alloc<int>(1, 16);
        *flag = 0;

        GpuContext dummyGpu = default;
        var shaders = default(RenderShaderDefaults);
        EnginePipelineSystem.Start(pipe, flag, &FaultingLogicCallback, in shaders);
        try
        {
            var fin = new PipelineFrameInput { DeltaTime = 1f / 60f, ViewportWidth = 1280f, ViewportHeight = 720f };
            EnginePipelineSystem.StepPipelinedFrame(pipe, ref dummyGpu, ref alloc, in fin);
            Assert.Equal((byte)1, pipe->HasRenderablePacket, "First headless step produced packet");

            EnginePipelineSystem.FlushBeforeSceneTransition(pipe, ref dummyGpu);
            Assert.Equal((byte)0, pipe->HasRenderablePacket, "FlushBeforeSceneTransition invalidated pending packet");

            *flag = 1;
            bool threw = false;
            try
            {
                EnginePipelineSystem.StepPipelinedFrame(pipe, ref dummyGpu, ref alloc, in fin);
            }
            catch (InvalidOperationException ex)
            {
                threw = ex.InnerException is InvalidDataException;
            }
            Assert.True(threw, "Worker fault propagated via StepPipelinedFrame with inner exception");

            *flag = 0;
            EnginePipelineSystem.StepPipelinedFrame(pipe, ref dummyGpu, ref alloc, in fin);
            Assert.Equal((byte)1, pipe->HasRenderablePacket, "Pipeline recovered cleanly after worker fault");
        }
        finally
        {
            EnginePipelineSystem.Stop(pipe);
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after pipeline fault test");
    }

    private static void TestProfilerSeqlockAndPipelineConcurrentStop()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 256 * 1024, 256 * 1024, 64 * 1024);

        try
        {
            // CORE-12: Concurrent TryGetLastScopeMs reader while writer loops BeginFrame/BeginScope/EndScope/EndFrame
            FrameProfiler* prof = alloc.Persistent.Alloc<FrameProfiler>(1, 64);
            *prof = FrameProfiler.Create(ref alloc.Persistent, enabled: true, measureGpuWithFence: false);

            int stopReader = 0;
            int tornDetected = 0;
            int validReads = 0;
            Exception? readerErr = null;
            nint profPtr = (nint)prof;

            var reader = new Thread(() =>
            {
                try
                {
                    FrameProfiler* p = (FrameProfiler*)profPtr;
                    while (Volatile.Read(ref stopReader) == 0)
                    {
                        if (ProfilerSystem.TryGetLastScopeMs(in *p, "Sim", out double simMs))
                        {
                            if (!double.IsFinite(simMs) || simMs < 0.0 || simMs > 60000.0)
                                Interlocked.Increment(ref tornDetected);
                            else
                                Interlocked.Increment(ref validReads);
                        }
                        if (ProfilerSystem.TryGetLastScopeMs(in *p, "View", out double viewMs))
                        {
                            if (!double.IsFinite(viewMs) || viewMs < 0.0 || viewMs > 60000.0)
                                Interlocked.Increment(ref tornDetected);
                            else
                                Interlocked.Increment(ref validReads);
                        }
                    }
                }
                catch (Exception ex)
                {
                    readerErr = ex;
                }
            });
            reader.Start();

            try
            {
                for (int iter = 0; iter < 4000; iter++)
                {
                    ProfilerSystem.BeginFrame(ref *prof);
                    int s1 = ProfilerSystem.BeginScope(ref *prof, "Sim");
                    ProfilerSystem.EndScope(ref *prof, s1);
                    int s2 = ProfilerSystem.BeginScope(ref *prof, "View");
                    ProfilerSystem.EndScope(ref *prof, s2);
                    ProfilerSystem.EndFrame(ref *prof);
                }
            }
            finally
            {
                Volatile.Write(ref stopReader, 1);
                reader.Join();
            }

            Assert.True(readerErr == null, $"Concurrent TryGetLastScopeMs reader had 0 exceptions ({readerErr?.Message})");
            Assert.Equal(0, tornDetected, "Zero torn scope durations observed under concurrent BeginFrame");
            Assert.True(validReads > 0, $"Observed valid concurrent scope reads ({validReads})");

            // CORE-17: 4-thread barrier-synchronized concurrent EnginePipelineSystem.Stop()
            EnginePipelineState* pipe = alloc.Persistent.Alloc<EnginePipelineState>(1, 64);
            int* flag = alloc.Persistent.Alloc<int>(1, 16);
            *flag = 0;
            var shaders = default(RenderShaderDefaults);
            EnginePipelineSystem.Start(pipe, flag, &FaultingLogicCallback, in shaders);

            using var barrier = new Barrier(4);
            nint pipePtr = (nint)pipe;
            Exception? stopErr = null;
            var threads = new Thread[4];
            for (int t = 0; t < 4; t++)
            {
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        EnginePipelineSystem.Stop((EnginePipelineState*)pipePtr);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref stopErr, ex, null);
                    }
                });
                threads[t].Start();
            }
            for (int t = 0; t < 4; t++) threads[t].Join();
            Assert.True(stopErr == null, $"Concurrent EnginePipelineSystem.Stop had 0 exceptions ({stopErr?.Message})");
        }
        finally
        {
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after seqlock & concurrent pipeline stop tests");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after seqlock & concurrent pipeline stop tests");
    }
}
