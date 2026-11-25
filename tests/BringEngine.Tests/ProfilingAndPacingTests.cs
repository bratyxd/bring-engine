using System;
using System.Diagnostics;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
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
    public static void RunAll()
    {
        Console.WriteLine("=== Update: VSync, FPS Cap (FramePacer), FrameProfiler & ProfilerOverlay ===");
        Assert.Run("FramePacer_DefaultsToVSyncOnAnd60FpsCap", &TestFramePacerDefaultsAndCap);
        Assert.Run("FrameProfilerAndOverlay_ScopesStatsAndZeroGc", &TestProfilerAndOverlayZeroGc);
        Assert.Run("GpuVSyncToggle_SetPassCallsAndGpuFrameMs", &TestGpuVSyncAndProfilerMs);
    }

    private static void TestFramePacerDefaultsAndCap()
    {
        var pacer = FramePacer.CreateDefault();
        Assert.Equal(60, pacer.TargetFps, "Default FPS cap must be 60");
        Assert.Equal((byte)1, pacer.VSyncEnabled, "Default VSync must be ON");

        // Verify CycleFpsCapPreset cycles cleanly
        Assert.Equal(120, FramePacerSystem.CycleFpsCapPreset(ref pacer), "60 -> 120");
        Assert.Equal(144, FramePacerSystem.CycleFpsCapPreset(ref pacer), "120 -> 144");
        Assert.Equal(0, FramePacerSystem.CycleFpsCapPreset(ref pacer), "144 -> Uncapped(0)");
        Assert.Equal(30, FramePacerSystem.CycleFpsCapPreset(ref pacer), "0 -> 30");
        Assert.Equal(60, FramePacerSystem.CycleFpsCapPreset(ref pacer), "30 -> 60");

        // Verify FPS cap actually paces frames (5 frames at 120 FPS = ~41.6ms)
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

        var profiler = FrameProfiler.Create(ref alloc.Persistent, enabled: true, measureGpuWithFence: false);
        var pacer = FramePacer.CreateDefault();
        var overlay = ProfilerOverlaySystem.Create(ref world, font, white, 1280, 720, ProfilerAnchor.TopRight, startVisible: true);
        RenderStats fakeStats = new RenderStats { DrawCalls = 12, SetPassCalls = 9, RenderPasses = 2, CopyPasses = 1, MeshInstances = 40, Triangles = 1200, FrameArenaUsedBytes = 128 * 1024 };

        // Warmup 2 frames
        for (int f = 0; f < 2; f++)
        {
            ProfilerSystem.BeginFrame(ref profiler);
            int s = ProfilerSystem.BeginScope(ref profiler, "Sim");
            ProfilerSystem.EndScope(ref profiler, s);
            ProfilerOverlaySystem.Update(ref overlay, ref world, in profiler, in fakeStats, in pacer, 1280, 720);
            ProfilerSystem.EndCpuWork(ref profiler);
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
            ProfilerOverlaySystem.Update(ref overlay, ref world, in profiler, in fakeStats, in pacer, 1280, 720);
            ProfilerSystem.EndCpuWork(ref profiler);
            ProfilerSystem.EndFrame(ref profiler);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "FrameProfiler + ProfilerOverlaySystem.Update must allocate 0 GC bytes");
        Assert.True(ProfilerSystem.TryGetLastScopeMs(in profiler, "Sim", out _), "Last frame scope 'Sim' recorded");
        Assert.True(profiler.CurrentFps > 0.0, "Smoothed FPS computed");

        ProfilerOverlaySystem.Destroy(ref overlay, ref world);
        bundleArena.Dispose();
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }

    private static void TestGpuVSyncAndProfilerMs()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 2 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 512, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        var window = PlatformSystem.InitWindow("BringEngine Profiler GPU Test", 640, 360, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: false);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

        FrameProfiler* profiler = alloc.Persistent.Alloc<FrameProfiler>(1, 64);
        *profiler = FrameProfiler.Create(ref alloc.Persistent, enabled: true, measureGpuWithFence: true);
        var pacer = FramePacer.CreateDefault();

        try
        {
            // Default VSync must be ON!
            Assert.Equal((byte)1, gpu->VSyncEnabled, "GpuContext defaults to VSync ON");

            // Toggle VSync OFF then back ON
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

            for (int f = 0; f < 3; f++)
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
            Assert.True(profiler->CpuFrameMs >= 0.0, "CPU frame time measured cleanly");

            ProfilerOverlaySystem.Destroy(ref overlay, ref world);
        }
        finally
        {
            BringEngine.Assets.AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            GpuSystem.Dispose(ref *gpu);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }
}
