using System;
using System.IO;
using BringEngine;
using BringEngine.Assets.Bundles;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
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
/// Modes:
///   (default)                  : Interactive windowed SDL3_gpu playable game
///   --headless [--ticks N]     : 100% headless deterministic simulation & zero-GC audit (ZERO SDL/GPU calls)
///   --verify-render [--out dir]: Renders Title scene + live Arena + Additive HUD scenes and saves PNG screenshots
/// </summary>
public static unsafe class Program
{
    public static int Main(string[] args)
    {
        bool headless = false;
        bool verifyRender = false;
        int ticks = 600;
        string outDir = AppContext.BaseDirectory;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--headless") headless = true;
            else if (args[i] == "--verify-render") verifyRender = true;
            else if (args[i] == "--ticks" && i + 1 < args.Length) int.TryParse(args[++i], out ticks);
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
        }

        if (headless)
            return RunHeadlessVerification(ticks);

        return RunWindowed(verifyRender, outDir);
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
        // Orbit movement + periodic dash + auto-firing at enemies
        int phase = (tick / 45) % 4;
        input.SetKey(KeyCode.D, phase == 0);
        input.SetKey(KeyCode.W, phase == 1);
        input.SetKey(KeyCode.A, phase == 2);
        input.SetKey(KeyCode.S, phase == 3);
        input.SetKey(KeyCode.Space, tick % 70 == 15);
    }

    private static int RunWindowed(bool verifyRender, string outDir)
    {
        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - Bring Arena ({(verifyRender ? "Verify Render Mode" : "Interactive")})");
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

        GameSimState* sim = gameAlloc.Persistent.Alloc<GameSimState>(1, 64);
        *sim = GameSimState.Create(ref gameAlloc.Persistent, seed: 20260417UL);

        GameViewState* view = gameAlloc.Persistent.Alloc<GameViewState>(1, 64);
        *view = GameViewState.Create(ref gameAlloc.Persistent, sim);

        var window = PlatformSystem.InitWindow("Bring Engine — Bring Arena", 1280, 720, hidden: verifyRender);
        GpuContext* gpu = engAlloc.Persistent.Alloc<GpuContext>(1, 64);
        *gpu = GpuSystem.Create(window.Window, ref engAlloc.Persistent, ref world.Assets, debugMode: false);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

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

            // Load Title Scene (Single)
            SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("Title"), SceneLoadMode.Single);

            if (verifyRender)
            {
                Directory.CreateDirectory(outDir);
                GpuSystem.EnsureCaptureTarget(ref *gpu, 1280, 720);

                // 1. Render Title scene for 45 ticks (~0.75s) -> capture verify_title.png
                for (int f = 0; f < 45; f++)
                {
                    engAlloc.BeginFrame();
                    gameAlloc.BeginFrame();
                    SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, 1f / 60f);
                    EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, 1f / 60f, 1280f, 720f);
                }
                engAlloc.BeginFrame();
                SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, 1f / 60f);
                EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, 1f / 60f, 1280f, 720f);
                BatchRenderer.RenderFrame(ref *gpu, ref world, ref engAlloc, in view->Lighting, captureToOffscreen: true);
                string titlePng = Path.Combine(outDir, "verify_title.png");
                GpuSystem.SaveCaptureTextureToPng(ref *gpu, titlePng);
                Console.WriteLine($"  [Title] Captured {titlePng} | Draws={gpu->LastFrameStats.DrawCalls}, Meshes={gpu->LastFrameStats.MeshInstances}, Sprites={gpu->LastFrameStats.SpriteInstances}, Glyphs={gpu->LastFrameStats.GlyphInstances}, Particles={gpu->LastFrameStats.ParticleInstances}");
                Console.WriteLine($"  [Bundles] Loaded={bundles.LoadedBundleCount} (title={BundleSystem.IsLoaded(in bundles, bTitle)}, arena={BundleSystem.IsLoaded(in bundles, bArena)})");

                // 2. Transition Single -> Arena + Additive -> HUD (title bundle MUST unload automatically!)
                GameSimSystems.ResetMatch(ref *sim, 20260417UL);
                SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("Arena"), SceneLoadMode.Single);
                SceneSystem.LoadScene(ref scenes, ref world, ref engAlloc, ref bundles, StringId.Hash("HUD"), SceneLoadMode.Additive);
                Console.WriteLine($"  [Transition -> Arena+HUD] Loaded={bundles.LoadedBundleCount} (common={BundleSystem.GetRefCount(in bundles, bCommon)}, title={BundleSystem.IsLoaded(in bundles, bTitle)}, arena={BundleSystem.IsLoaded(in bundles, bArena)})");

                // 3. Step 85 scripted gameplay ticks (movement, firing, hits, kills, floating SDF combat text, bursts!)
                Fix32 fixedDt = Fix32.FromRatio(1, 60);
                for (int f = 0; f < 85; f++)
                {
                    engAlloc.BeginFrame();
                    gameAlloc.BeginFrame();
                    FeedScriptedGameplayInput(ref world.Input, f);
                    GameSimSystems.StepFixed(ref *sim, in world.Input, fixedDt);
                    view->CachedRenderStats = gpu->LastFrameStats;
                    SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, 1f / 60f);
                    EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, 1f / 60f, 1280f, 720f);
                    if (f == 83 || f == 84)
                        BatchRenderer.RenderFrame(ref *gpu, ref world, ref engAlloc, in view->Lighting, captureToOffscreen: true);
                }

                string arenaPng = Path.Combine(outDir, "verify_arena.png");
                GpuSystem.SaveCaptureTextureToPng(ref *gpu, arenaPng);
                var st = gpu->LastFrameStats;
                Console.WriteLine($"  [Arena+HUD] Captured {arenaPng} | Score={sim->Score} | Draws={st.DrawCalls}, PipelineBinds={st.PipelineBinds}, Meshes={st.MeshInstances}, Sprites={st.SpriteInstances}, Glyphs={st.GlyphInstances}, Particles={st.ParticleInstances}, FrameArena={st.FrameArenaUsedBytes / 1024}KB");
            }
            else
            {
                // Interactive Playable Loop (Fixed 60Hz Simulation + Variable View/Render)
                const float fixedStep = 1f / 60f;
                Fix32 fixedDt = Fix32.FromRatio(1, 60);
                float accumulator = 0f;

                while (window.Closed == 0 && world.Input.QuitRequested == 0)
                {
                    float frameDt = PlatformSystem.PollDeltaSeconds(ref window);
                    PlatformSystem.PollEvents(ref window, ref world.Input);
                    if (world.Input.IsKeyPressed(KeyCode.Escape)) break;

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

                    accumulator += frameDt;
                    while (accumulator >= fixedStep)
                    {
                        if (view->Mode == 1)
                            GameSimSystems.StepFixed(ref *sim, in world.Input, fixedDt);
                        accumulator -= fixedStep;
                    }

                    view->CachedRenderStats = gpu->LastFrameStats;
                    SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, frameDt);
                    EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, frameDt, window.PixelWidth, window.PixelHeight);
                    BatchRenderer.RenderFrame(ref *gpu, ref world, ref engAlloc, in view->Lighting, captureToOffscreen: false);
                }
            }
        }
        finally
        {
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
