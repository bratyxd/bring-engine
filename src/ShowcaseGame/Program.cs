using System;
using System.IO;
using BringEngine;
using BringEngine.Assets;
using BringEngine.Assets.Bundles;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Rendering.Profiling;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Input;
using BringEngine.Simulation.Scenes;
using BringEngine.Simulation.World;
using SDL3;
using ShowcaseGame.Simulation;
using ShowcaseGame.View;

namespace ShowcaseGame;

/// <summary>
/// Bring Arena — Complete playable reference game built with Bring Engine (NativeAOT, Zero-OOP, Data + Systems).
///
/// Defaults:
///   VSync ON + FPS Cap 60 (runtime toggleable via F2 = VSync, F3 = Cycle FPS Cap, F1 = Profiler UI Overlay).
///
/// Modes:
///   (default)                  : Interactive windowed SDL3_gpu playable game
///   --headless [--ticks N]     : 100% headless deterministic simulation & zero-GC audit (ZERO SDL/GPU calls)
///   --verify-render [--out dir]: Renders Title scene + live Arena + Additive HUD + Common Profiler Overlay to PNGs
///   --fps N                    : Override initial FPS cap (0 = uncapped, default = 60)
///   --no-vsync                 : Start with VSync disabled (default = enabled)
/// </summary>
public static unsafe class Program
{
    public static int Main(string[] args)
    {
        bool headless = false;
        bool verifyRender = false;
        int ticks = 600;
        int targetFps = FramePacer.DefaultTargetFps; // 60 by default
        bool vsync = true;                           // ON by default
        string outDir = AppContext.BaseDirectory;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--headless") headless = true;
            else if (args[i] == "--verify-render") verifyRender = true;
            else if (args[i] == "--no-vsync") vsync = false;
            else if (args[i] == "--fps" && i + 1 < args.Length) int.TryParse(args[++i], out targetFps);
            else if (args[i] == "--ticks" && i + 1 < args.Length) int.TryParse(args[++i], out ticks);
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
        }

        if (headless)
            return RunHeadlessVerification(ticks);

        return RunWindowed(verifyRender, outDir, targetFps, vsync);
    }

    private static int RunHeadlessVerification(int ticks)
    {
        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - ShowcaseGame Headless Determinism & Zero-GC Audit");
        NativeMemoryTracker.ResetStatsForTest();

        ulong hash1 = RunSingleHeadlessPass(ticks, checkZeroGc: true, out int score1, out int wave1, out long gcBytes);
        ulong hash2 = RunSingleHeadlessPass(ticks, checkZeroGc: false, out int score2, out int wave2, out _);

        Console.WriteLine($"  Run 1: ticks={ticks}, score={score1}, wave={wave1}, hash=0x{hash1:X16}, hotLoopGcBytes={gcBytes}");
        Console.WriteLine($"  Run 2: ticks={ticks}, score={score2}, wave={wave2}, hash=0x{hash2:X16}");
        Console.WriteLine($"  Native leaks after teardown: {NativeMemoryTracker.LiveBytes} bytes ({NativeMemoryTracker.LiveBlocks} blocks)");

        if (hash1 != hash2) { Console.WriteLine("  [FAIL] Determinism mismatch!"); return 1; }
        if (gcBytes != 0) { Console.WriteLine($"  [FAIL] Hot loop allocated {gcBytes} GC bytes!"); return 1; }
        if (NativeMemoryTracker.LiveBytes != 0) { Console.WriteLine("  [FAIL] Native memory leak!"); return 1; }
        if (score1 <= 0) { Console.WriteLine("  [FAIL] Expected positive gameplay score during scripted play!"); return 1; }

        Console.WriteLine("  [PASS] Bit-exact headless determinism, zero GC hot loop, and zero native leaks verified!");
        return 0;
    }

    private static ulong RunSingleHeadlessPass(int ticks, bool checkZeroGc, out int finalScore, out int finalWave, out long gcAllocated)
    {
        var gameAlloc = GameAllocators.Create();
        var sim = GameSimState.Create(ref gameAlloc.Persistent, seed: 20260417UL);
        InputState input = default;
        Fix32 dt = Fix32.FromRatio(1, 60);

        // Warmup 1 tick before GC measurement
        GameSimSystems.StepFixed(ref sim, in input, dt);

        long gcBefore = checkZeroGc ? GC.GetAllocatedBytesForCurrentThread() : 0;
        for (int t = 0; t < ticks; t++)
        {
            gameAlloc.BeginFrame();
            FeedScriptedGameplayInput(ref input, t);
            GameSimSystems.StepFixed(ref sim, in input, dt);
        }
        long gcAfter = checkZeroGc ? GC.GetAllocatedBytesForCurrentThread() : 0;

        gcAllocated = gcAfter - gcBefore;
        finalScore = sim.Score;
        finalWave = sim.Wave;
        ulong hash = GameSimSystems.ComputeStateHash(in sim);

        gameAlloc.Dispose();
        return hash;
    }

    private static void FeedScriptedGameplayInput(ref InputState input, int tick)
    {
        input.BeginFrame();
        int phase = (tick / 45) % 4;
        input.SetKey(KeyCode.D, phase == 0);
        input.SetKey(KeyCode.W, phase == 1);
        input.SetKey(KeyCode.A, phase == 2);
        input.SetKey(KeyCode.S, phase == 3);
        input.SetKey(KeyCode.Space, tick % 70 == 15);
    }

    private static int RunWindowed(bool verifyRender, string outDir, int targetFps, bool vsync)
    {
        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - Bring Arena ({(verifyRender ? "Verify Render Mode" : "Interactive")}, VSync={(vsync ? "ON" : "OFF")}, FPS Cap={(targetFps > 0 ? targetFps.ToString() : "Uncapped")})");
        GameAssets.EnsureGlbFilesOnDisk();
        NativeMemoryTracker.ResetStatsForTest();

        var engAlloc = EngineAllocators.Create(
            persistentBytes: 12 * 1024 * 1024,
            frameBytes: 4 * 1024 * 1024,
            scratchBytes: 8 * 1024 * 1024,
            dynamicBytes: 4 * 1024 * 1024);
        var gameAlloc = GameAllocators.Create();

        var world = EngineWorld.Create(ref engAlloc.Persistent, maxEntities: 8192, maxParticles: 8192);
        var bundles = BundleManager.Create(ref engAlloc.Persistent, maxBundles: 16);
        var scenes = SceneManager.Create(ref engAlloc.Persistent, maxDefinitions: 16, maxActiveScenes: 8);

        // Engine Frame Profiler + Frame Pacer (defaults: VSync ON, FPS Cap 60)
        FrameProfiler* profiler = engAlloc.Persistent.Alloc<FrameProfiler>(1, 64);
        *profiler = FrameProfiler.Create(ref engAlloc.Persistent, enabled: true, measureGpuWithFence: true);
        FramePacer pacer = FramePacer.Create(targetFps, vsync);
        ProfilerOverlay profOverlay = default;

        GameSimState* sim = gameAlloc.Persistent.Alloc<GameSimState>(1, 64);
        *sim = GameSimState.Create(ref gameAlloc.Persistent, seed: 20260417UL);

        GameViewState* view = gameAlloc.Persistent.Alloc<GameViewState>(1, 64);
        *view = GameViewState.Create(ref gameAlloc.Persistent, sim);

        var window = PlatformSystem.InitWindow("Bring Engine — Bring Arena", 1280, 720, hidden: verifyRender);
        GpuContext* gpu = engAlloc.Persistent.Alloc<GpuContext>(1, 64);
        *gpu = GpuSystem.Create(window.Window, ref engAlloc.Persistent, ref world.Assets, debugMode: false);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);
        FramePacerSystem.ApplyVSync(ref pacer, ref *gpu, vsync);

        try
        {
            // Compile engine built-in HLSL shaders + ShowcaseGame's custom hologram shader via SDL_shadercross
            ShaderSystem.CompileBuiltinShaders(ref *gpu);
            string holoPath = Path.Combine(GameAssets.ShadersDir, "hologram_energy.hlsl");
            view->HologramShader = ShaderSystem.CompileFromFile(ref *gpu, holoPath, "hologram_energy", ShaderVertexLayout.Mesh3D, SDL.GPUCullMode.None);

            // Register Ref-Counted Asset Bundles
            var bCommon = BundleSystem.Register(ref bundles, "common", 4 * 1024 * 1024, &GameAssets.LoadCommonBundle);
            var bTitle = BundleSystem.Register(ref bundles, "title", 4 * 1024 * 1024, &GameAssets.LoadTitleBundle);
            var bArena = BundleSystem.Register(ref bundles, "arena", 6 * 1024 * 1024, &GameAssets.LoadArenaBundle);

            // Register Unity-Like Scenes
            Span<BundleId> titleDeps = stackalloc BundleId[] { bCommon, bTitle };
            Span<BundleId> arenaDeps = stackalloc BundleId[] { bCommon, bArena };
            Span<BundleId> hudDeps = stackalloc BundleId[] { bCommon };

            SceneSystem.RegisterScene(ref scenes, ref engAlloc.Persistent, "Title", titleDeps, 512 * 1024,
                onLoad: &GameViewSystems.OnTitleLoad, onViewSync: &GameViewSystems.OnTitleViewSync, userData: view);
            SceneSystem.RegisterScene(ref scenes, ref engAlloc.Persistent, "Arena", arenaDeps, 1024 * 1024,
                onLoad: &GameViewSystems.OnArenaLoad, onViewSync: &GameViewSystems.OnArenaViewSync, userData: view);
            SceneSystem.RegisterScene(ref scenes, ref engAlloc.Persistent, "HUD", hudDeps, 256 * 1024,
                onLoad: &GameViewSystems.OnHudLoad, onViewSync: &GameViewSystems.OnHudViewSync, userData: view);

            // Load Title Scene (Single) — acquires shared 'common' bundle
            SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("Title"), SceneLoadMode.Single);

            // Create persistent Common Profiler UI Overlay (survives all Single/Additive scene switches!)
            FontHandle mainFont = AssetSystem.FindFont(in world.Assets, StringId.Hash("main_font"));
            TextureHandle whiteTex = AssetSystem.FindTexture(in world.Assets, StringId.Hash("white"));
            profOverlay = ProfilerOverlaySystem.Create(ref world, mainFont, whiteTex, window.PixelWidth, window.PixelHeight, ProfilerAnchor.TopRight, startVisible: true);

            if (verifyRender)
            {
                Directory.CreateDirectory(outDir);
                GpuSystem.EnsureCaptureTarget(ref *gpu, 1280, 720);

                // 1. Render Title scene (40 fast-forward sim ticks, then 8 real 60 FPS paced & profiled GPU frames!)
                for (int f = 0; f < 48; f++)
                {
                    bool renderNow = f >= 40;
                    if (f == 40) ProfilerSystem.ResetHistory(ref *profiler);

                    if (renderNow)
                    {
                        FramePacerSystem.BeginFrame(ref pacer);
                        ProfilerSystem.BeginFrame(ref *profiler);
                    }
                    engAlloc.BeginFrame();
                    gameAlloc.BeginFrame();

                    int vsScope = renderNow ? ProfilerSystem.BeginScope(ref *profiler, "View") : -1;
                    SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, 1f / 60f);
                    if (vsScope >= 0) ProfilerSystem.EndScope(ref *profiler, vsScope);

                    int csScope = renderNow ? ProfilerSystem.BeginScope(ref *profiler, "Core") : -1;
                    ProfilerOverlaySystem.Update(ref profOverlay, ref world, in *profiler, in gpu->LastFrameStats, in pacer, 1280f, 720f);
                    EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, 1f / 60f, 1280f, 720f);
                    if (csScope >= 0) ProfilerSystem.EndScope(ref *profiler, csScope);

                    if (renderNow)
                    {
                        BatchRenderer.RenderFrame(ref *gpu, ref world, ref engAlloc, in view->Lighting, captureToOffscreen: true, profiler: profiler);
                        ProfilerSystem.EndCpuWork(ref *profiler);
                        FramePacerSystem.EndFrameAndPace(ref pacer, useSdlPreciseDelay: true);
                        ProfilerSystem.EndFrame(ref *profiler);
                    }
                }

                string titlePng = Path.Combine(outDir, "verify_title.png");
                GpuSystem.SaveCaptureTextureToPng(ref *gpu, titlePng);
                var ts = gpu->LastFrameStats;
                Console.WriteLine($"  [Title] Captured {titlePng} | FPS={profiler->CurrentFps:F1}, CPU={profiler->SmoothedCpuMs:F2}ms, GPU={profiler->SmoothedGpuMs:F2}ms, Draws={ts.DrawCalls}, SetPass={ts.SetPassCalls}, Passes={ts.RenderPasses + ts.CopyPasses}");
                Console.WriteLine($"  [Bundles] Loaded={bundles.LoadedBundleCount} (title={BundleSystem.IsLoaded(in bundles, bTitle)}, arena={BundleSystem.IsLoaded(in bundles, bArena)})");

                // 2. Transition Single -> Arena + Additive -> HUD (title bundle unloads, Profiler Overlay stays alive!)
                GameSimSystems.ResetMatch(ref *sim, 20260417UL);
                SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("Arena"), SceneLoadMode.Single);
                SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("HUD"), SceneLoadMode.Additive);
                Console.WriteLine($"  [Transition -> Arena+HUD] Loaded={bundles.LoadedBundleCount} (common={BundleSystem.GetRefCount(in bundles, bCommon)}, title={BundleSystem.IsLoaded(in bundles, bTitle)}, arena={BundleSystem.IsLoaded(in bundles, bArena)})");

                // 3. Step 88 scripted gameplay ticks (last 8 frames rendered & paced at 60 FPS for live profiler readings!)
                Fix32 fixedDt = Fix32.FromRatio(1, 60);
                for (int f = 0; f < 88; f++)
                {
                    bool renderNow = f >= 80;
                    if (f == 80) ProfilerSystem.ResetHistory(ref *profiler);

                    if (renderNow)
                    {
                        FramePacerSystem.BeginFrame(ref pacer);
                        ProfilerSystem.BeginFrame(ref *profiler);
                    }
                    engAlloc.BeginFrame();
                    gameAlloc.BeginFrame();

                    FeedScriptedGameplayInput(ref world.Input, f);

                    int simScope = renderNow ? ProfilerSystem.BeginScope(ref *profiler, "Sim") : -1;
                    GameSimSystems.StepFixed(ref *sim, in world.Input, fixedDt);
                    if (simScope >= 0) ProfilerSystem.EndScope(ref *profiler, simScope);

                    int viewScope = renderNow ? ProfilerSystem.BeginScope(ref *profiler, "View") : -1;
                    view->CachedRenderStats = gpu->LastFrameStats;
                    SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, 1f / 60f);
                    ProfilerOverlaySystem.Update(ref profOverlay, ref world, in *profiler, in gpu->LastFrameStats, in pacer, 1280f, 720f);
                    if (viewScope >= 0) ProfilerSystem.EndScope(ref *profiler, viewScope);

                    int coreScope = renderNow ? ProfilerSystem.BeginScope(ref *profiler, "Core") : -1;
                    EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, 1f / 60f, 1280f, 720f);
                    if (coreScope >= 0) ProfilerSystem.EndScope(ref *profiler, coreScope);

                    if (renderNow)
                    {
                        BatchRenderer.RenderFrame(ref *gpu, ref world, ref engAlloc, in view->Lighting, captureToOffscreen: true, profiler: profiler);
                        ProfilerSystem.EndCpuWork(ref *profiler);
                        FramePacerSystem.EndFrameAndPace(ref pacer, useSdlPreciseDelay: true);
                        ProfilerSystem.EndFrame(ref *profiler);
                    }
                }

                string arenaPng = Path.Combine(outDir, "verify_arena.png");
                GpuSystem.SaveCaptureTextureToPng(ref *gpu, arenaPng);
                var st = gpu->LastFrameStats;
                Console.WriteLine($"  [Arena+HUD] Captured {arenaPng} | Score={sim->Score} | FPS={profiler->CurrentFps:F1}, CPU={profiler->SmoothedCpuMs:F2}ms, GPU={profiler->SmoothedGpuMs:F2}ms, Draws={st.DrawCalls}, SetPass={st.SetPassCalls}, Passes={st.RenderPasses + st.CopyPasses}, FrameArena={st.FrameArenaUsedBytes / 1024}KB");
            }
            else
            {
                // Interactive Playable Loop (Fixed 60Hz Simulation + Profiled Render + VSync & FPS Cap Pacing)
                const float fixedStep = 1f / 60f;
                Fix32 fixedDt = Fix32.FromRatio(1, 60);
                float accumulator = 0f;

                while (window.Closed == 0 && world.Input.QuitRequested == 0)
                {
                    FramePacerSystem.BeginFrame(ref pacer);
                    ProfilerSystem.BeginFrame(ref *profiler);

                    float frameDt = PlatformSystem.PollDeltaSeconds(ref window);
                    PlatformSystem.PollEvents(ref window, ref world.Input);
                    if (world.Input.IsKeyPressed(KeyCode.Escape)) break;

                    // Profiler / VSync / FPS Cap Debug Hotkeys
                    if (world.Input.IsKeyPressed(KeyCode.F1))
                        ProfilerOverlaySystem.Toggle(ref profOverlay, ref world);
                    if (world.Input.IsKeyPressed(KeyCode.F2))
                        FramePacerSystem.ToggleVSync(ref pacer, ref *gpu);
                    if (world.Input.IsKeyPressed(KeyCode.F3))
                        FramePacerSystem.CycleFpsCapPreset(ref pacer);

                    // Handle scene transitions requested by view/input
                    if (view->RequestStartArena != 0)
                    {
                        view->RequestStartArena = 0;
                        GameSimSystems.ResetMatch(ref *sim, (ulong)(world.TickCount + 20260417UL));
                        SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("Arena"), SceneLoadMode.Single);
                        SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("HUD"), SceneLoadMode.Additive);
                    }
                    else if (view->RequestReturnTitle != 0)
                    {
                        view->RequestReturnTitle = 0;
                        SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("Title"), SceneLoadMode.Single);
                    }

                    engAlloc.BeginFrame();
                    gameAlloc.BeginFrame();

                    int simScope = ProfilerSystem.BeginScope(ref *profiler, "Sim");
                    accumulator += frameDt;
                    while (accumulator >= fixedStep)
                    {
                        if (view->Mode == 1)
                            GameSimSystems.StepFixed(ref *sim, in world.Input, fixedDt);
                        accumulator -= fixedStep;
                    }
                    ProfilerSystem.EndScope(ref *profiler, simScope);

                    int viewScope = ProfilerSystem.BeginScope(ref *profiler, "View");
                    view->CachedRenderStats = gpu->LastFrameStats;
                    SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, frameDt);
                    ProfilerOverlaySystem.Update(ref profOverlay, ref world, in *profiler, in gpu->LastFrameStats, in pacer, window.PixelWidth, window.PixelHeight);
                    ProfilerSystem.EndScope(ref *profiler, viewScope);

                    int coreScope = ProfilerSystem.BeginScope(ref *profiler, "Core");
                    EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, frameDt, window.PixelWidth, window.PixelHeight);
                    ProfilerSystem.EndScope(ref *profiler, coreScope);

                    BatchRenderer.RenderFrame(ref *gpu, ref world, ref engAlloc, in view->Lighting, captureToOffscreen: false, profiler: profiler);

                    ProfilerSystem.EndCpuWork(ref *profiler);
                    FramePacerSystem.EndFrameAndPace(ref pacer, useSdlPreciseDelay: true);
                    ProfilerSystem.EndFrame(ref *profiler);
                }
            }
        }
        finally
        {
            ProfilerOverlaySystem.Destroy(ref profOverlay, ref world);
            SceneSystem.UnloadAllScenes(ref scenes, ref world, ref engAlloc, ref bundles);
            BundleSystem.UnloadAll(ref bundles, ref world);
            GpuSystem.Dispose(ref *gpu);
            PlatformSystem.Shutdown(ref window);
            gameAlloc.Dispose();
            engAlloc.Dispose();
        }

        Console.WriteLine($"  [Shutdown] Native leaks: {NativeMemoryTracker.LiveBytes} bytes ({NativeMemoryTracker.LiveBlocks} blocks)");
        return NativeMemoryTracker.LiveBytes == 0 ? 0 : 1;
    }
}
