using System;
using System.IO;
using System.Runtime.InteropServices;
using BringEngine;
using BringEngine.Assets;
using BringEngine.Assets.Bundles;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
using BringEngine.Core.Storage;
using BringEngine.Core.Threading;
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
/// Unmanaged context struct passed to Bring Engine's dedicated Logic/Update Thread callback.
/// Zero-OOP, zero managed references.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GameLogicContext
{
    public EngineAllocators* EngAlloc;
    public GameAllocators* GameAlloc;
    public EngineWorld* World;
    public BundleManager* Bundles;
    public SceneManager* Scenes;
    public FrameProfiler* Profiler;
    public FramePacer* Pacer;
    public ProfilerOverlay* ProfOverlay;
    public GameSimState* Sim;
    public GameViewState* View;
    public float FixedAccumulator;
    public byte FixedSingleStepMode; // 1 in deterministic verify-render mode (1 exact 1/60s sim tick per frame)
    public byte ProfileScopesActive;

    // Input edge/delta/text/focus latch across 0-tick and multi-tick fixed-step frames
    public fixed ulong LatchedKeysPressed[8];
    public fixed ulong LatchedKeysReleased[8];
    public fixed ulong PrevTickKeysDown[8];
    public uint LatchedMousePressed;
    public uint LatchedMouseReleased;
    public uint PrevTickMouseDown;
    public fixed uint LatchedPadPressed[InputState.MaxGamepads];
    public fixed uint LatchedPadReleased[InputState.MaxGamepads];
    public fixed uint PrevTickPadDown[InputState.MaxGamepads];
    public float LatchedMouseDeltaX;
    public float LatchedMouseDeltaY;
    public float LatchedMouseWheelY;
    public fixed byte LatchedTextInputUtf8[InputState.MaxTextInputBytes];
    public byte LatchedTextInputLength;
    public byte PrevTickWindowUnfocused;
    public byte LatchedFocusLost;
    public byte LatchedFocusGained;
}

/// <summary>
/// Bring Arena — Complete playable reference game built with Bring Engine (CoreCLR .NET 10, Zero-OOP, Data + Systems).
///
/// Engine Defaults:
///   - Borderless Full Window by default (WindowMode.BorderlessFullscreen; toggle with F11 or pass --windowed)
///   - 2-Thread Pipelined Execution (Dedicated Logic Thread Frame N + Main/Render Thread Frame N-1)
///   - VSync ON + FPS Cap 60 (runtime toggleable: F1 Profiler, F2 VSync, F3 Cycle FPS Cap, F11 Borderless/Windowed)
///
/// Modes:
///   (default)                  : Interactive Borderless Full Window SDL3_gpu playable game (2-Thread Pipelined)
///   --windowed                 : Launch in resizable 1280x720 window instead of borderless full window
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
        WindowMode windowMode = WindowMode.BorderlessFullscreen; // Borderless Full Window by default!
        string outDir = AppContext.BaseDirectory;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--headless") headless = true;
            else if (args[i] == "--verify-render") verifyRender = true;
            else if (args[i] == "--no-vsync") vsync = false;
            else if (args[i] == "--windowed") windowMode = WindowMode.Windowed;
            else if (args[i] == "--fps" && i + 1 < args.Length) int.TryParse(args[++i], out targetFps);
            else if (args[i] == "--ticks" && i + 1 < args.Length) int.TryParse(args[++i], out ticks);
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
        }

        if (headless)
            return RunHeadlessVerification(ticks);

        return RunPipelinedGame(verifyRender, outDir, targetFps, vsync, windowMode);
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

    /// <summary>
    /// Logic/Update Thread entry point (runs Frame N concurrently with Main/Render Thread submitting Frame N-1!).
    /// ZERO SDL3 or GPU calls allowed here.
    /// </summary>
    private static void RunLogicFrame(void* userCtx, ref PipelineFrameInput frameIn, ref RenderPacket outPacket)
    {
        GameLogicContext* ctx = (GameLogicContext*)userCtx;
        ref var engAlloc = ref *ctx->EngAlloc;
        ref var gameAlloc = ref *ctx->GameAlloc;
        ref var world = ref *ctx->World;
        ref var bundles = ref *ctx->Bundles;
        ref var scenes = ref *ctx->Scenes;
        FrameProfiler* prof = ctx->ProfileScopesActive != 0 ? ctx->Profiler : null;

        gameAlloc.BeginFrame();
        world.Input = frameIn.Input;
        LatchInputEdges(ctx, in frameIn.Input);

        const float fixedStep = 1f / 60f;
        Fix32 fixedDt = Fix32.FromRatio(1, 60);

        int simScope = prof != null ? ProfilerSystem.BeginScope(ref *prof, "Sim") : -1;
        if (ctx->FixedSingleStepMode != 0)
        {
            InputState tickInput = ConsumeLatchedTickInput(ctx, in frameIn.Input);
            if (ctx->View->Mode == 1)
                GameSimSystems.StepFixed(ref *ctx->Sim, in tickInput, fixedDt);
        }
        else
        {
            ctx->FixedAccumulator += frameIn.DeltaTime;
            while (ctx->FixedAccumulator >= fixedStep)
            {
                InputState tickInput = ConsumeLatchedTickInput(ctx, in frameIn.Input);
                if (ctx->View->Mode == 1)
                    GameSimSystems.StepFixed(ref *ctx->Sim, in tickInput, fixedDt);
                ctx->FixedAccumulator -= fixedStep;
            }
        }
        if (simScope >= 0) ProfilerSystem.EndScope(ref *prof, simScope);

        int viewScope = prof != null ? ProfilerSystem.BeginScope(ref *prof, "View") : -1;
        ctx->View->CachedRenderStats = frameIn.PrevRenderStats;
        SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref engAlloc, ref bundles, frameIn.DeltaTime);
        ProfilerOverlaySystem.Update(ref *ctx->ProfOverlay, ref world, in *ctx->Profiler, in frameIn.PrevRenderStats, in *ctx->Pacer, frameIn.ViewportWidth, frameIn.ViewportHeight);
        if (viewScope >= 0) ProfilerSystem.EndScope(ref *prof, viewScope);

        int coreScope = prof != null ? ProfilerSystem.BeginScope(ref *prof, "Core") : -1;
        EngineWorldSystem.StepCoreSystems(ref world, ref engAlloc, frameIn.DeltaTime, frameIn.ViewportWidth, frameIn.ViewportHeight);
        if (coreScope >= 0) ProfilerSystem.EndScope(ref *prof, coreScope);

        // Extract immutable Frame N RenderPacket into engAlloc.Frame (pure CPU, zero GPU calls!)
        BatchRenderer.ExtractRenderPacket(
            in frameIn.BuiltinShaders, in world, in ctx->View->Lighting,
            ref engAlloc.Frame, ref engAlloc.Scratch, out outPacket, prof);
    }

    private static void LatchInputEdges(GameLogicContext* ctx, in InputState src)
    {
        for (int w = 0; w < 8; w++)
        {
            ulong down = src.KeysDown[w];
            ulong prev = src.KeysPrev[w];
            ctx->LatchedKeysPressed[w] |= down & ~prev;
            ctx->LatchedKeysReleased[w] |= ~down & prev;
        }
        ctx->LatchedMousePressed |= src.MouseButtonsDown & ~src.MouseButtonsPrev;
        ctx->LatchedMouseReleased |= ~src.MouseButtonsDown & src.MouseButtonsPrev;
        for (int g = 0; g < InputState.MaxGamepads; g++)
        {
            GamepadState gp = src.GetGamepad(g);
            ctx->LatchedPadPressed[g] |= gp.ButtonsDown & ~gp.ButtonsPrev;
            ctx->LatchedPadReleased[g] |= ~gp.ButtonsDown & gp.ButtonsPrev;
        }
        ctx->LatchedMouseDeltaX += src.MouseDeltaX;
        ctx->LatchedMouseDeltaY += src.MouseDeltaY;
        ctx->LatchedMouseWheelY += src.MouseWheelY;

        if (src.TextInputLength > 0)
        {
            int startLen = ctx->LatchedTextInputLength;
            int avail = InputState.MaxTextInputBytes - startLen;
            if (avail > 0)
            {
                int srcLen = src.TextInputLength;
                bool truncated = srcLen > avail;
                int copy = truncated ? avail : srcLen;
                for (int i = 0; i < copy; i++)
                    ctx->LatchedTextInputUtf8[startLen + i] = src.TextInputUtf8[i];
                int newLen = startLen + copy;
                if (truncated)
                    newLen = TrimToUtf8RuneBoundary(ctx->LatchedTextInputUtf8, startLen, newLen);
                ctx->LatchedTextInputLength = (byte)newLen;
            }
        }

        if (src.WasWindowFocusLost) ctx->LatchedFocusLost = 1;
        if (src.WasWindowFocusGained) ctx->LatchedFocusGained = 1;
    }

    private static int TrimToUtf8RuneBoundary(byte* buf, int start, int end)
    {
        if (end <= start) return start;
        int minSearch = end - 4;
        if (minSearch < start) minSearch = start;
        int idx = end - 1;
        while (idx >= minSearch && (buf[idx] & 0xC0) == 0x80)
            idx--;
        if (idx < minSearch) return minSearch;

        byte lead = buf[idx];
        int expected;
        if ((lead & 0x80) == 0) expected = 1;
        else if ((lead & 0xE0) == 0xC0) expected = 2;
        else if ((lead & 0xF0) == 0xE0) expected = 3;
        else if ((lead & 0xF8) == 0xF0) expected = 4;
        else return idx;

        int actual = end - idx;
        if (actual < expected) return idx;
        if (actual > expected) return idx + expected;
        return end;
    }

    private static InputState ConsumeLatchedTickInput(GameLogicContext* ctx, in InputState src)
    {
        InputState tick = src;
        for (int w = 0; w < 8; w++)
        {
            ulong pressed = ctx->LatchedKeysPressed[w];
            ulong released = ctx->LatchedKeysReleased[w] & ~pressed;
            ulong down = (tick.KeysDown[w] | pressed) & ~released;
            ulong prev = (ctx->PrevTickKeysDown[w] & ~pressed) | released;
            tick.KeysDown[w] = down;
            tick.KeysPrev[w] = prev;
            ctx->PrevTickKeysDown[w] = down;
            ctx->LatchedKeysPressed[w] = 0;
            ctx->LatchedKeysReleased[w] = (ctx->LatchedKeysReleased[w] & pressed) & ~src.KeysDown[w];
        }

        uint mPressed = ctx->LatchedMousePressed;
        uint mReleased = ctx->LatchedMouseReleased & ~mPressed;
        uint mDown = (tick.MouseButtonsDown | mPressed) & ~mReleased;
        uint mPrev = (ctx->PrevTickMouseDown & ~mPressed) | mReleased;
        tick.MouseButtonsDown = mDown;
        tick.MouseButtonsPrev = mPrev;
        ctx->PrevTickMouseDown = mDown;
        ctx->LatchedMousePressed = 0;
        ctx->LatchedMouseReleased = (ctx->LatchedMouseReleased & mPressed) & ~src.MouseButtonsDown;

        ConsumePadLatch(ref tick.Gamepad0, src.Gamepad0.ButtonsDown, ref ctx->LatchedPadPressed[0], ref ctx->LatchedPadReleased[0], ref ctx->PrevTickPadDown[0]);
        ConsumePadLatch(ref tick.Gamepad1, src.Gamepad1.ButtonsDown, ref ctx->LatchedPadPressed[1], ref ctx->LatchedPadReleased[1], ref ctx->PrevTickPadDown[1]);
        ConsumePadLatch(ref tick.Gamepad2, src.Gamepad2.ButtonsDown, ref ctx->LatchedPadPressed[2], ref ctx->LatchedPadReleased[2], ref ctx->PrevTickPadDown[2]);
        ConsumePadLatch(ref tick.Gamepad3, src.Gamepad3.ButtonsDown, ref ctx->LatchedPadPressed[3], ref ctx->LatchedPadReleased[3], ref ctx->PrevTickPadDown[3]);

        tick.MouseDeltaX = ctx->LatchedMouseDeltaX;
        tick.MouseDeltaY = ctx->LatchedMouseDeltaY;
        tick.MouseWheelY = ctx->LatchedMouseWheelY;
        ctx->LatchedMouseDeltaX = 0f;
        ctx->LatchedMouseDeltaY = 0f;
        ctx->LatchedMouseWheelY = 0f;

        byte textLen = ctx->LatchedTextInputLength;
        tick.TextInputLength = textLen;
        for (int i = 0; i < textLen; i++)
            tick.TextInputUtf8[i] = ctx->LatchedTextInputUtf8[i];
        ctx->LatchedTextInputLength = 0;

        if (ctx->PrevTickWindowUnfocused == 0 && ctx->LatchedFocusLost != 0)
        {
            tick.WindowUnfocused = 1;
            tick.WindowUnfocusedPrev = 0;
            ctx->LatchedFocusLost = 0;
        }
        else if (ctx->PrevTickWindowUnfocused != 0 && ctx->LatchedFocusGained != 0)
        {
            tick.WindowUnfocused = 0;
            tick.WindowUnfocusedPrev = 1;
            ctx->LatchedFocusGained = 0;
        }
        else if (ctx->LatchedFocusLost != 0)
        {
            tick.WindowUnfocused = 1;
            tick.WindowUnfocusedPrev = 0;
            ctx->LatchedFocusLost = 0;
        }
        else if (ctx->LatchedFocusGained != 0)
        {
            tick.WindowUnfocused = 0;
            tick.WindowUnfocusedPrev = 1;
            ctx->LatchedFocusGained = 0;
        }
        else
        {
            tick.WindowUnfocused = src.WindowUnfocused;
            tick.WindowUnfocusedPrev = ctx->PrevTickWindowUnfocused;
        }
        ctx->PrevTickWindowUnfocused = tick.WindowUnfocused;

        return tick;
    }

    private static void ConsumePadLatch(ref GamepadState pad, uint srcDown, ref uint pressedLatch, ref uint releasedLatch, ref uint prevDown)
    {
        uint pressed = pressedLatch;
        uint released = releasedLatch & ~pressed;
        uint down = (pad.ButtonsDown | pressed) & ~released;
        uint prev = (prevDown & ~pressed) | released;
        pad.ButtonsDown = down;
        pad.ButtonsPrev = prev;
        prevDown = down;
        pressedLatch = 0;
        releasedLatch = (releasedLatch & pressed) & ~srcDown;
    }

    private static int RunPipelinedGame(bool verifyRender, string outDir, int targetFps, bool vsync, WindowMode windowMode)
    {
        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - Bring Arena ({(verifyRender ? "Verify Render Mode" : windowMode.ToString())}, 2-Thread Pipelined, VSync={(vsync ? "ON" : "OFF")}, FPS Cap={(targetFps > 0 ? targetFps.ToString() : "Uncapped")})");
        GameAssets.EnsureGlbFilesOnDisk();
        NativeMemoryTracker.ResetStatsForTest();

        var engAlloc = EngineAllocators.Create(
            persistentBytes: 12 * 1024 * 1024,
            frameBytes: 4 * 1024 * 1024,
            scratchBytes: 8 * 1024 * 1024,
            dynamicBytes: 4 * 1024 * 1024);
        var gameAlloc = GameAllocators.Create();

        // Keep top-level structs in native Persistent memory so pointers passed to Logic Thread stay 100% pinned & stable!
        LogState* log = engAlloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref engAlloc.Persistent, appName: "BringArena", defaultFileName: "engine.log", minLevel: LogLevel.Info, mirrorToConsole: false);

        SaveSystemState* save = engAlloc.Persistent.Alloc<SaveSystemState>(1, 64);
        SaveSystem.Start(save, ref engAlloc.Persistent, appName: "BringArena", log: log);

        EngineWorld* world = engAlloc.Persistent.Alloc<EngineWorld>(1, 64);
        *world = EngineWorld.Create(ref engAlloc.Persistent, maxEntities: 8192, maxParticles: 8192);

        BundleManager* bundles = engAlloc.Persistent.Alloc<BundleManager>(1, 64);
        *bundles = BundleManager.Create(ref engAlloc.Persistent, maxBundles: 16, log: log);

        SceneManager* scenes = engAlloc.Persistent.Alloc<SceneManager>(1, 64);
        *scenes = SceneManager.Create(ref engAlloc.Persistent, maxDefinitions: 16, maxActiveScenes: 8, log: log);

        FrameProfiler* profiler = engAlloc.Persistent.Alloc<FrameProfiler>(1, 64);
        *profiler = FrameProfiler.Create(ref engAlloc.Persistent, enabled: true, measureGpuWithFence: true);

        FramePacer* pacer = engAlloc.Persistent.Alloc<FramePacer>(1, 64);
        *pacer = FramePacer.Create(targetFps, vsync);

        ProfilerOverlay* profOverlay = engAlloc.Persistent.Alloc<ProfilerOverlay>(1, 64);
        *profOverlay = default;

        GameSimState* sim = gameAlloc.Persistent.Alloc<GameSimState>(1, 64);
        *sim = GameSimState.Create(ref gameAlloc.Persistent, seed: 20260417UL);

        GameViewState* view = gameAlloc.Persistent.Alloc<GameViewState>(1, 64);
        *view = GameViewState.Create(ref gameAlloc.Persistent, sim);

        EnginePipelineState* pipeline = engAlloc.Persistent.Alloc<EnginePipelineState>(1, 64);
        *pipeline = default;

        GameLogicContext* logicCtx = engAlloc.Persistent.Alloc<GameLogicContext>(1, 64);

        var window = PlatformSystem.InitWindow("Bring Engine — Bring Arena", 1280, 720, hidden: verifyRender, mode: windowMode, log: log);
        GpuContext* gpu = engAlloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref engAlloc.Persistent, ref world->Assets, debugMode: false, log: log);
        FramePacerSystem.ApplyVSync(ref *pacer, ref *gpu, vsync);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu, log: log);
            string holoPath = Path.Combine(GameAssets.ShadersDir, "hologram_energy.hlsl");
            view->HologramShader = ShaderSystem.CompileFromFile(ref *gpu, holoPath, "hologram_energy", ShaderVertexLayout.Mesh3D, SDL.GPUCullMode.None);

            var bCommon = BundleSystem.Register(ref *bundles, "common", 4 * 1024 * 1024, &GameAssets.LoadCommonBundle);
            var bTitle = BundleSystem.Register(ref *bundles, "title", 4 * 1024 * 1024, &GameAssets.LoadTitleBundle);
            var bArena = BundleSystem.Register(ref *bundles, "arena", 6 * 1024 * 1024, &GameAssets.LoadArenaBundle);

            Span<BundleId> titleDeps = stackalloc BundleId[] { bCommon, bTitle };
            Span<BundleId> arenaDeps = stackalloc BundleId[] { bCommon, bArena };
            Span<BundleId> hudDeps = stackalloc BundleId[] { bCommon };

            SceneSystem.RegisterScene(ref *scenes, ref engAlloc.Persistent, "Title", titleDeps, 512 * 1024,
                onLoad: &GameViewSystems.OnTitleLoad, onViewSync: &GameViewSystems.OnTitleViewSync, userData: view);
            SceneSystem.RegisterScene(ref *scenes, ref engAlloc.Persistent, "Arena", arenaDeps, 1024 * 1024,
                onLoad: &GameViewSystems.OnArenaLoad, onViewSync: &GameViewSystems.OnArenaViewSync, userData: view);
            SceneSystem.RegisterScene(ref *scenes, ref engAlloc.Persistent, "HUD", hudDeps, 256 * 1024,
                onLoad: &GameViewSystems.OnHudLoad, onViewSync: &GameViewSystems.OnHudViewSync, userData: view);

            // Load Title Scene & immediately upload scene assets on Main Thread before starting 2-thread pipeline!
            SceneSystem.LoadScene(ref *scenes, ref *world, ref engAlloc, ref *bundles, StringId.Hash("Title"), SceneLoadMode.Single);
            BatchRenderer.UploadPendingAssets(ref *gpu, ref *world);

            FontHandle mainFont = AssetSystem.FindFont(in world->Assets, StringId.Hash("main_font"));
            TextureHandle whiteTex = AssetSystem.FindTexture(in world->Assets, StringId.Hash("white"));
            *profOverlay = ProfilerOverlaySystem.Create(ref *world, mainFont, whiteTex, window.PixelWidth, window.PixelHeight, ProfilerAnchor.TopRight, startVisible: true);

            *logicCtx = new GameLogicContext
            {
                EngAlloc = &engAlloc,
                GameAlloc = &gameAlloc,
                World = world,
                Bundles = bundles,
                Scenes = scenes,
                Profiler = profiler,
                Pacer = pacer,
                ProfOverlay = profOverlay,
                Sim = sim,
                View = view,
                FixedSingleStepMode = (byte)(verifyRender ? 1 : 0),
                ProfileScopesActive = 1,
            };

            RenderShaderDefaults builtinShaders = GpuSystem.GetBuiltinShaders(in *gpu);
            EnginePipelineSystem.Start(pipeline, logicCtx, &RunLogicFrame, in builtinShaders);

            if (verifyRender)
            {
                Directory.CreateDirectory(outDir);
                GpuSystem.EnsureCaptureTarget(ref *gpu, 1280, 720);

                // 1. Title Scene: 36 fast-forward ticks + 12 real 2-thread pipelined 60 FPS profiled frames!
                InputState scriptedInput = default;
                for (int f = 0; f < 48; f++)
                {
                    bool paced = f >= 36;
                    if (f == 36) ProfilerSystem.ResetHistory(ref *profiler);
                    logicCtx->ProfileScopesActive = (byte)(paced ? 1 : 0);

                    if (paced)
                    {
                        FramePacerSystem.BeginFrame(ref *pacer);
                        ProfilerSystem.BeginFrame(ref *profiler);
                    }

                    var fin = new PipelineFrameInput
                    {
                        Input = scriptedInput,
                        DeltaTime = 1f / 60f,
                        ViewportWidth = 1280f,
                        ViewportHeight = 720f,
                        PrevRenderStats = gpu->LastFrameStats,
                        PrevRenderCpuMs = pipeline->LastRenderCpuMs,
                        PrevGpuMs = pipeline->LastGpuMs,
                        BuiltinShaders = builtinShaders,
                    };

                    EnginePipelineSystem.StepPipelinedFrame(pipeline, ref *gpu, ref engAlloc, in fin, captureToOffscreen: true, profiler: paced ? profiler : null);

                    if (paced)
                    {
                        FramePacerSystem.EndFrameAndPace(ref *pacer, useSdlPreciseDelay: true);
                        ProfilerSystem.EndFrame(ref *profiler);
                    }
                }

                string titlePng = Path.Combine(outDir, "verify_title.png");
                GpuSystem.SaveCaptureTextureToPng(ref *gpu, titlePng);
                var ts = gpu->LastFrameStats;
                Console.WriteLine($"  [Title 2T] Captured {titlePng} | FPS={profiler->CurrentFps:F1}, CPU={profiler->SmoothedLogicMs:F2}ms, RND={profiler->SmoothedRenderMs:F2}ms, GPU={profiler->SmoothedGpuMs:F2}ms, Draws={ts.DrawCalls}, SetPass={ts.SetPassCalls}, Passes={ts.RenderPasses + ts.CopyPasses}");
                Console.WriteLine($"  [Bundles] Loaded={bundles->LoadedBundleCount} (title={BundleSystem.IsLoaded(in *bundles, bTitle)}, arena={BundleSystem.IsLoaded(in *bundles, bArena)})");

                // 2. Scene Transition at clean quiescent barrier -> Arena (Single) + HUD (Additive)
                EnginePipelineSystem.FlushBeforeSceneTransition(pipeline, ref *gpu);
                GpuSystem.FlushDeferredReleases(ref *gpu);
                GameSimSystems.ResetMatch(ref *sim, 20260417UL);
                SceneSystem.LoadScene(ref *scenes, ref *world, ref engAlloc, ref *bundles, StringId.Hash("Arena"), SceneLoadMode.Single);
                SceneSystem.LoadScene(ref *scenes, ref *world, ref engAlloc, ref *bundles, StringId.Hash("HUD"), SceneLoadMode.Additive);
                GpuSystem.FlushDeferredReleases(ref *gpu);
                BatchRenderer.UploadPendingAssets(ref *gpu, ref *world);
                Console.WriteLine($"  [Transition -> Arena+HUD] Loaded={bundles->LoadedBundleCount} (common={BundleSystem.GetRefCount(in *bundles, bCommon)}, title={BundleSystem.IsLoaded(in *bundles, bTitle)}, arena={BundleSystem.IsLoaded(in *bundles, bArena)})");

                // 3. Step 92 scripted gameplay frames on the 2-thread pipeline (last 12 frames paced at 60 FPS!)
                for (int f = 0; f < 92; f++)
                {
                    bool paced = f >= 80;
                    if (f == 80) ProfilerSystem.ResetHistory(ref *profiler);
                    logicCtx->ProfileScopesActive = (byte)(paced ? 1 : 0);

                    if (paced)
                    {
                        FramePacerSystem.BeginFrame(ref *pacer);
                        ProfilerSystem.BeginFrame(ref *profiler);
                    }

                    FeedScriptedGameplayInput(ref scriptedInput, f);
                    var fin = new PipelineFrameInput
                    {
                        Input = scriptedInput,
                        DeltaTime = 1f / 60f,
                        ViewportWidth = 1280f,
                        ViewportHeight = 720f,
                        PrevRenderStats = gpu->LastFrameStats,
                        PrevRenderCpuMs = pipeline->LastRenderCpuMs,
                        PrevGpuMs = pipeline->LastGpuMs,
                        BuiltinShaders = builtinShaders,
                    };

                    EnginePipelineSystem.StepPipelinedFrame(pipeline, ref *gpu, ref engAlloc, in fin, captureToOffscreen: true, profiler: paced ? profiler : null);

                    if (paced)
                    {
                        FramePacerSystem.EndFrameAndPace(ref *pacer, useSdlPreciseDelay: true);
                        ProfilerSystem.EndFrame(ref *profiler);
                    }
                }

                string arenaPng = Path.Combine(outDir, "verify_arena.png");
                GpuSystem.SaveCaptureTextureToPng(ref *gpu, arenaPng);
                var st = gpu->LastFrameStats;
                Console.WriteLine($"  [Arena+HUD 2T] Captured {arenaPng} | Score={sim->Score} | FPS={profiler->CurrentFps:F1}, CPU={profiler->SmoothedLogicMs:F2}ms, RND={profiler->SmoothedRenderMs:F2}ms, GPU={profiler->SmoothedGpuMs:F2}ms, Draws={st.DrawCalls}, SetPass={st.SetPassCalls}, Passes={st.RenderPasses + st.CopyPasses}, FrameArena={st.FrameArenaUsedBytes / 1024}KB");
            }
            else
            {
                // Interactive 2-Thread Pipelined Loop (Main/Render Thread + Dedicated Logic Thread)
                InputState mainInput = default;
                while (window.Closed == 0 && mainInput.QuitRequested == 0)
                {
                    FramePacerSystem.BeginFrame(ref *pacer);
                    ProfilerSystem.BeginFrame(ref *profiler);

                    float frameDt = PlatformSystem.PollDeltaSeconds(ref window);
                    PlatformSystem.PollEvents(ref window, ref mainInput);
                    if (mainInput.IsKeyPressed(KeyCode.Escape)) break;

                    // Standard Engine Hotkeys (safe here at the quiescent frame barrier!)
                    if (mainInput.IsKeyPressed(KeyCode.F1))
                        ProfilerOverlaySystem.Toggle(ref *profOverlay, ref *world);
                    if (mainInput.IsKeyPressed(KeyCode.F2))
                        FramePacerSystem.ToggleVSync(ref *pacer, ref *gpu);
                    if (mainInput.IsKeyPressed(KeyCode.F3))
                        FramePacerSystem.CycleFpsCapPreset(ref *pacer);
                    if (mainInput.IsKeyPressed(KeyCode.F11))
                        PlatformSystem.ToggleBorderlessFullscreen(ref window);

                    // Scene transitions executed cleanly at the frame barrier (zero race conditions!)
                    if (view->RequestStartArena != 0)
                    {
                        view->RequestStartArena = 0;
                        EnginePipelineSystem.FlushBeforeSceneTransition(pipeline, ref *gpu);
                        GpuSystem.FlushDeferredReleases(ref *gpu);
                        GameSimSystems.ResetMatch(ref *sim, (ulong)(world->TickCount + 20260417UL));
                        SceneSystem.LoadScene(ref *scenes, ref *world, ref engAlloc, ref *bundles, StringId.Hash("Arena"), SceneLoadMode.Single);
                        SceneSystem.LoadScene(ref *scenes, ref *world, ref engAlloc, ref *bundles, StringId.Hash("HUD"), SceneLoadMode.Additive);
                        GpuSystem.FlushDeferredReleases(ref *gpu);
                        BatchRenderer.UploadPendingAssets(ref *gpu, ref *world);
                    }
                    else if (view->RequestReturnTitle != 0)
                    {
                        view->RequestReturnTitle = 0;
                        EnginePipelineSystem.FlushBeforeSceneTransition(pipeline, ref *gpu);
                        GpuSystem.FlushDeferredReleases(ref *gpu);
                        SceneSystem.LoadScene(ref *scenes, ref *world, ref engAlloc, ref *bundles, StringId.Hash("Title"), SceneLoadMode.Single);
                        GpuSystem.FlushDeferredReleases(ref *gpu);
                        BatchRenderer.UploadPendingAssets(ref *gpu, ref *world);
                    }

                    var fin = new PipelineFrameInput
                    {
                        Input = mainInput,
                        DeltaTime = frameDt,
                        ViewportWidth = window.PixelWidth,
                        ViewportHeight = window.PixelHeight,
                        PrevRenderStats = gpu->LastFrameStats,
                        PrevRenderCpuMs = pipeline->LastRenderCpuMs,
                        PrevGpuMs = pipeline->LastGpuMs,
                        BuiltinShaders = builtinShaders,
                    };

                    // Runs Logic Thread (Frame N) concurrently with Main/Render Thread (Frame N-1) and swaps double-buffered arenas!
                    EnginePipelineSystem.StepPipelinedFrame(pipeline, ref *gpu, ref engAlloc, in fin, captureToOffscreen: false, profiler: profiler);

                    FramePacerSystem.EndFrameAndPace(ref *pacer, useSdlPreciseDelay: true);
                    ProfilerSystem.EndFrame(ref *profiler);
                }
            }
        }
        finally
        {
            EnginePipelineSystem.Stop(pipeline);
            ProfilerOverlaySystem.Destroy(ref *profOverlay, ref *world);
            SceneSystem.UnloadAllScenes(ref *scenes, ref *world, ref engAlloc, ref *bundles);
            BundleSystem.UnloadAll(ref *bundles, ref *world);
            GpuSystem.Dispose(ref *gpu, ref world->Assets);
            PlatformSystem.Shutdown(ref window);
            SaveSystem.Stop(save);
            LogSystem.Stop(log);
            gameAlloc.Dispose();
            engAlloc.Dispose();
        }

        Console.WriteLine($"  [Shutdown] Native leaks: {NativeMemoryTracker.LiveBytes} bytes ({NativeMemoryTracker.LiveBlocks} blocks)");
        return NativeMemoryTracker.LiveBytes == 0 ? 0 : 1;
    }
}
