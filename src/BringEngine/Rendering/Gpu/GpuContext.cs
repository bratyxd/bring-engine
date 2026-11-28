using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Assets;
using BringEngine.Assets.Compression;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using SDL3;
using StbImageWriteSharp;

namespace BringEngine.Rendering.Gpu;

public enum ShaderVertexLayout : byte
{
    Mesh3D = 0,
    InstancedQuad = 1,
    Rig2DMesh = 2,
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuMeshSlot
{
    public nint VertexBuffer;
    public nint IndexBuffer;
    public uint IndexCount;
    public byte Alive;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuTextureSlot
{
    public nint Texture;
    public int Width;
    public int Height;
    public byte Channels;
    public byte ClampEdge;
    public byte NearestFilter;
    public byte Alive;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuShaderSlot
{
    public ShaderHandle Handle;
    public StringId Name;
    public nint VertexShader;
    public nint FragmentShader;
    public nint PipelineOpaque;
    public nint PipelineAlphaBlend;
    public nint PipelineAdditive;
    public nint PipelineOverlayNoDepth;
    public nint PipelineAdditiveNoDepth;
    public nint PipelineMultiply;
    public nint PipelineMultiplyNoDepth;
    public nint PipelineOpaqueCullNone;
    public nint PipelineAlphaBlendCullNone;
    public ShaderVertexLayout Layout;
    public byte FragmentSamplerCount;
    public byte WarnedLayoutMismatch;
    public byte Alive;
}

/// <summary>
/// Per-frame GPU telemetry metrics for performance monitoring and AI-agent optimization.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderStats
{
    public int DrawCalls;
    public int ShadowDrawCalls;  // Directional shadow pass instanced draw calls
    public int SetPassCalls;     // Unity-equivalent SetPass calls (pipeline or shader/material state transitions)
    public int PipelineBinds;
    public int RenderPasses;
    public int CopyPasses;
    public int MeshInstances;
    public int SpriteInstances;
    public int Rig2DInstances;
    public int GlyphInstances;
    public int ParticleInstances;
    public int Triangles;
    public int CulledMeshes;
    public nuint FrameArenaUsedBytes;
}

/// <summary>
/// SDL3_gpu device, swapchain, depth target, dynamic instancing storage buffers,
/// single-copy-pass transfer buffer, and resource pools.
/// 100% unmanaged struct, zero OOP.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct GpuContext
{
    public nint Device;
    public nint Window;
    public SDL.GPUTextureFormat SwapchainFormat;
    public SDL.GPUTextureFormat DepthFormat;

    public nint DepthTexture;
    public uint DepthWidth;
    public uint DepthHeight;

    // Directional sun shadow map resources (D32Float DepthStencilTarget | Sampler)
    public nint ShadowDepthTexture;
    public uint ShadowMapSize;
    public nint ShadowVertexShader;
    public nint ShadowFragmentShader;
    public nint ShadowPipeline;
    public nint ShadowPipelineCullNone;
    public nint ShadowMaskVertexShader;
    public nint ShadowMaskFragmentShader;
    public nint ShadowMaskPipeline;
    public nint ShadowMaskPipelineCullNone;
    public byte ShadowMapSupported;

    // Offscreen scene color target (decouples GPU render work from swapchain VSync Present wait)
    public nint SceneColorTexture;
    public uint SceneColorWidth;
    public uint SceneColorHeight;

    // Optional offscreen target for deterministic screenshot capture
    public nint CaptureColorTexture;
    public nint CaptureTransferBuffer;
    public uint CaptureWidth;
    public uint CaptureHeight;

    // Samplers & fallback texture
    public nint SamplerLinearWrap;
    public nint SamplerLinearClamp;
    public nint SamplerNearestClamp;
    public nint DefaultWhiteTexture;

    // Static unit quad index buffer (6 indices: 0,1,2, 0,2,3)
    public nint QuadIndexBuffer;

    // Reusable per-frame GPU storage buffers & single transfer buffer
    public nint Instance3DBuffer;
    public uint Instance3DBufferBytes;
    public nint JointPaletteBuffer;
    public uint JointPaletteBufferBytes;
    public nint SpriteInstanceBuffer;
    public uint SpriteInstanceBufferBytes;
    public nint TextGlyphBuffer;
    public uint TextGlyphBufferBytes;
    public nint ParticleInstanceBuffer;
    public uint ParticleInstanceBufferBytes;
    public nint Rig2DVertexBuffer;
    public uint Rig2DVertexBufferBytes;
    public nint Rig2DIndexBuffer;
    public uint Rig2DIndexBufferBytes;

    public nint UploadTransferBuffer;
    public uint UploadTransferBufferBytes;

    // Resource pools (allocated in Persistent arena)
    public GpuMeshSlot* Meshes;
    public uint* MeshSlotGens;
    public byte* MeshSlotPendingRelease;
    public int MaxMeshes;

    public GpuTextureSlot* Textures;
    public uint* TextureSlotGens;
    public byte* TextureSlotPendingRelease;
    public int MaxTextures;

    public GpuShaderSlot* Shaders;
    public uint* ShaderGens;
    public int MaxShaders;

    // Spinlock-protected deferred GPU release queues (Logic Thread enqueues; Main Thread drains via FlushDeferredReleases)
    public int* DeferredMeshSlots;
    public int DeferredMeshCount;
    public int DeferredMeshCap;

    public int* DeferredTextureSlots;
    public int DeferredTextureCount;
    public int DeferredTextureCap;

    public CachePaddedInt DeferredReleaseLock;

    // Built-in engine shader handles
    public ShaderHandle BuiltinPbrShader;
    public ShaderHandle BuiltinSpriteShader;
    public ShaderHandle BuiltinRig2DShader;
    public ShaderHandle BuiltinSdfTextShader;
    public ShaderHandle BuiltinParticleShader;

    public LogState* Log;
    public RenderStats LastFrameStats;
    public SDL.GPUPresentMode ActivePresentMode;
    public uint WarnedCapsMask;
    public byte VSyncEnabled;
    public byte Initialized;
}

public static unsafe class GpuSystem
{
    /// <summary>
    /// Initializes a pinned <see cref="GpuContext"/> in-place and automatically binds deferred GPU cleanup hooks onto
    /// <paramref name="assets"/>. Recommended canonical entry point for all games and tests.
    /// </summary>
    public static void Init(
        GpuContext* ctx,
        nint window,
        ref LinearArena persistentArena,
        ref AssetRegistry assets,
        bool debugMode = false,
        int maxMeshes = 1024,
        int maxTextures = 512,
        int maxShaders = 64,
        LogState* log = null)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        *ctx = CreateCore(window, ref persistentArena, debugMode, maxMeshes, maxTextures, maxShaders, log);
        BindAssetCleanupHooks(ctx, ref assets);
    }

    /// <summary>
    /// Creates a <see cref="GpuContext"/> value without binding <paramref name="assets"/> cleanup hooks automatically.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="Init(GpuContext*, nint, ref LinearArena, ref AssetRegistry, bool, int, int, int, LogState*)"/>
    /// with a pinned <see cref="GpuContext"/> pointer so asset cleanup hooks are always bound safely.
    /// If using <see cref="Create"/>, callers must pin the returned struct and call
    /// <see cref="BindAssetCleanupHooks(GpuContext*, ref AssetRegistry)"/>.
    /// </remarks>
    public static GpuContext Create(
        nint window,
        ref LinearArena persistentArena,
        ref AssetRegistry assets,
        bool debugMode = false,
        int maxMeshes = 1024,
        int maxTextures = 512,
        int maxShaders = 64,
        LogState* log = null)
    {
        _ = assets.MaxMeshes;
        return CreateCore(window, ref persistentArena, debugMode, maxMeshes, maxTextures, maxShaders, log);
    }

    private static GpuContext CreateCore(
        nint window,
        ref LinearArena persistentArena,
        bool debugMode,
        int maxMeshes,
        int maxTextures,
        int maxShaders,
        LogState* log)
    {
        if (maxMeshes < 1) throw new ArgumentOutOfRangeException(nameof(maxMeshes));
        if (maxTextures < 1) throw new ArgumentOutOfRangeException(nameof(maxTextures));
        if (maxShaders < 1) throw new ArgumentOutOfRangeException(nameof(maxShaders));

        if (!ShaderCross.Init())
        {
            string err = SDL.GetError();
            if (log != null) LogSystem.Error(log, $"SDL_ShaderCross_Init failed: {err}");
            throw new InvalidOperationException($"SDL_ShaderCross_Init failed: {err}");
        }

        nint device = nint.Zero;
        bool windowClaimed = false;
        var ctx = new GpuContext { Log = log };

        try
        {
            SDL.GPUShaderFormat formats = ShaderCross.GetSPIRVShaderFormats();
            if (formats == 0)
                formats = SDL.GPUShaderFormat.SPIRV | SDL.GPUShaderFormat.DXIL | SDL.GPUShaderFormat.MSL;

            device = SDL.CreateGPUDevice(formats, debugMode, null);
            if (device == nint.Zero)
            {
                string err = SDL.GetError();
                if (log != null) LogSystem.Error(log, $"SDL_CreateGPUDevice failed: {err}");
                throw new InvalidOperationException($"SDL_CreateGPUDevice failed: {err}");
            }

            if (!SDL.ClaimWindowForGPUDevice(device, window))
            {
                string err = SDL.GetError();
                if (log != null) LogSystem.Error(log, $"SDL_ClaimWindowForGPUDevice failed: {err}");
                throw new InvalidOperationException($"SDL_ClaimWindowForGPUDevice failed: {err}");
            }
            windowClaimed = true;

            SDL.GPUTextureFormat swapFmt = SDL.GetGPUSwapchainTextureFormat(device, window);

            uint* meshSlotGens = persistentArena.AllocUninitialized<uint>(maxMeshes, 16);
            for (int i = 0; i < maxMeshes; i++) meshSlotGens[i] = 1;
            byte* meshPending = persistentArena.Alloc<byte>(maxMeshes, 16);
            int* deferredMeshes = persistentArena.AllocUninitialized<int>(maxMeshes, 16);

            uint* texSlotGens = persistentArena.AllocUninitialized<uint>(maxTextures, 16);
            for (int i = 0; i < maxTextures; i++) texSlotGens[i] = 1;
            byte* texPending = persistentArena.Alloc<byte>(maxTextures, 16);
            int* deferredTextures = persistentArena.AllocUninitialized<int>(maxTextures, 16);

            uint* shaderGens = persistentArena.AllocUninitialized<uint>(maxShaders, 16);
            for (int i = 0; i < maxShaders; i++) shaderGens[i] = 1;

            bool shadowSupported = SDL.GPUTextureSupportsFormat(
                device,
                SDL.GPUTextureFormat.D32Float,
                SDL.GPUTextureType.TextureType2D,
                SDL.GPUTextureUsageFlags.DepthStencilTarget | SDL.GPUTextureUsageFlags.Sampler);

            ctx = new GpuContext
            {
                Device = device,
                Window = window,
                SwapchainFormat = swapFmt,
                DepthFormat = SDL.GPUTextureFormat.D32Float,
                ShadowMapSupported = (byte)(shadowSupported ? 1 : 0),
                Meshes = persistentArena.Alloc<GpuMeshSlot>(maxMeshes, 64),
                MeshSlotGens = meshSlotGens,
                MeshSlotPendingRelease = meshPending,
                MaxMeshes = maxMeshes,
                Textures = persistentArena.Alloc<GpuTextureSlot>(maxTextures, 64),
                TextureSlotGens = texSlotGens,
                TextureSlotPendingRelease = texPending,
                MaxTextures = maxTextures,
                Shaders = persistentArena.Alloc<GpuShaderSlot>(maxShaders, 64),
                ShaderGens = shaderGens,
                MaxShaders = maxShaders,
                DeferredMeshSlots = deferredMeshes,
                DeferredMeshCount = 0,
                DeferredMeshCap = maxMeshes,
                DeferredTextureSlots = deferredTextures,
                DeferredTextureCount = 0,
                DeferredTextureCap = maxTextures,
                DeferredReleaseLock = default,
                Log = log,
                Initialized = 1,
            };

            CreateDefaultSamplersAndBuffers(ref ctx);
            SDL.SetGPUAllowedFramesInFlight(ctx.Device, 2); // Pipelined double-buffered GPU execution
            SetVSync(ref ctx, enabled: true);               // VSync ON by default

            if (log != null)
            {
                string driver = SDL.GetGPUDeviceDriver(ctx.Device) ?? "unknown";
                LogSystem.Info(log, $"GpuContext created: driver={driver} debug={debugMode} swapFmt={swapFmt} shadowD32={shadowSupported} vsync={(ctx.VSyncEnabled != 0 ? "ON" : "OFF")} presentMode={ctx.ActivePresentMode}");
            }

            return ctx;
        }
        catch
        {
            if (device != nint.Zero)
            {
                if (ctx.DefaultWhiteTexture != nint.Zero) SDL.ReleaseGPUTexture(device, ctx.DefaultWhiteTexture);
                if (ctx.SamplerLinearWrap != nint.Zero) SDL.ReleaseGPUSampler(device, ctx.SamplerLinearWrap);
                if (ctx.SamplerLinearClamp != nint.Zero) SDL.ReleaseGPUSampler(device, ctx.SamplerLinearClamp);
                if (ctx.SamplerNearestClamp != nint.Zero) SDL.ReleaseGPUSampler(device, ctx.SamplerNearestClamp);
                if (ctx.QuadIndexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.QuadIndexBuffer);
                if (ctx.Instance3DBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.Instance3DBuffer);
                if (ctx.JointPaletteBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.JointPaletteBuffer);
                if (ctx.SpriteInstanceBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.SpriteInstanceBuffer);
                if (ctx.TextGlyphBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.TextGlyphBuffer);
                if (ctx.ParticleInstanceBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.ParticleInstanceBuffer);
                if (ctx.Rig2DVertexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.Rig2DVertexBuffer);
                if (ctx.Rig2DIndexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(device, ctx.Rig2DIndexBuffer);
                if (ctx.UploadTransferBuffer != nint.Zero) SDL.ReleaseGPUTransferBuffer(device, ctx.UploadTransferBuffer);
                if (windowClaimed && window != nint.Zero) SDL.ReleaseWindowFromGPUDevice(device, window);
                SDL.DestroyGPUDevice(device);
            }
            ShaderCross.Quit();
            throw;
        }
    }

    public static RenderShaderDefaults GetBuiltinShaders(in GpuContext ctx) => new RenderShaderDefaults
    {
        Pbr = ctx.BuiltinPbrShader,
        Sprite = ctx.BuiltinSpriteShader,
        Rig2D = ctx.BuiltinRig2DShader,
        SdfText = ctx.BuiltinSdfTextShader,
        Particle = ctx.BuiltinParticleShader,
    };

    /// <summary>
    /// Configures swapchain VSync mode on SDL3_gpu.
    /// - enabled = true  -> SDL_GPU_PRESENTMODE_VSYNC (always supported, tear-free)
    /// - enabled = false -> SDL_GPU_PRESENTMODE_IMMEDIATE if supported, else MAILBOX, else VSYNC
    /// </summary>
    public static bool SetVSync(ref GpuContext ctx, bool enabled)
    {
        if (ctx.Device == nint.Zero || ctx.Window == nint.Zero) return false;

        SDL.GPUPresentMode desired = SDL.GPUPresentMode.VSync;
        if (!enabled)
        {
            if (SDL.WindowSupportsGPUPresentMode(ctx.Device, ctx.Window, SDL.GPUPresentMode.Immediate))
                desired = SDL.GPUPresentMode.Immediate;
            else if (SDL.WindowSupportsGPUPresentMode(ctx.Device, ctx.Window, SDL.GPUPresentMode.Mailbox))
            {
                desired = SDL.GPUPresentMode.Mailbox;
                if (ctx.Log != null)
                    LogSystem.Warn(ctx.Log, "Immediate present mode unsupported; falling back to Mailbox");
            }
            else
            {
                desired = SDL.GPUPresentMode.VSync;
                if (ctx.Log != null)
                    LogSystem.Warn(ctx.Log, "Immediate/Mailbox present modes unsupported; staying on VSync");
            }
        }

        bool ok = SDL.SetGPUSwapchainParameters(ctx.Device, ctx.Window, SDL.GPUSwapchainComposition.SDR, desired);
        if (ok)
        {
            ctx.ActivePresentMode = desired;
            ctx.VSyncEnabled = (byte)(desired == SDL.GPUPresentMode.VSync ? 1 : 0);
        }
        else if (ctx.Log != null)
        {
            LogSystem.Warn(ctx.Log, $"SDL_SetGPUSwapchainParameters({desired}) failed: {SDL.GetError()}");
        }
        return ok;
    }

    [Obsolete("Pass a pinned GpuContext* instead of ref GpuContext to prevent dangling pointer capture.", error: true)]
    public static void BindAssetCleanupHooks(ref GpuContext ctx, ref AssetRegistry assets)
    {
        fixed (GpuContext* ptr = &ctx)
        {
            assets.GpuContextPtr = ptr;
            assets.ReleaseGpuMesh = &ReleaseGpuMeshHook;
            assets.ReleaseGpuTexture = &ReleaseGpuTextureHook;
        }
    }

    /// <summary>
    /// Binds GPU resource release hooks onto AssetRegistry using a stable pinned GpuContext*.
    /// </summary>
    public static void BindAssetCleanupHooks(GpuContext* ctxPtr, ref AssetRegistry assets)
    {
        assets.GpuContextPtr = ctxPtr;
        assets.ReleaseGpuMesh = ctxPtr != null ? &ReleaseGpuMeshHook : null;
        assets.ReleaseGpuTexture = ctxPtr != null ? &ReleaseGpuTextureHook : null;
    }

    public static void ClearAssetCleanupHooks(ref AssetRegistry assets)
    {
        assets.GpuContextPtr = null;
        assets.ReleaseGpuMesh = null;
        assets.ReleaseGpuTexture = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireDeferredLock(ref GpuContext ctx)
    {
        SpinWait sw = default;
        while (Interlocked.CompareExchange(ref ctx.DeferredReleaseLock.Value, 1, 0) != 0)
            sw.SpinOnce();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseDeferredLock(ref GpuContext ctx)
    {
        Volatile.Write(ref ctx.DeferredReleaseLock.Value, 0);
    }

    private static void ReleaseGpuMeshHook(void* ctxPtr, int slot)
    {
        if (ctxPtr == null) return;
        ref GpuContext ctx = ref *(GpuContext*)ctxPtr;
        if (ctx.Initialized == 0 || (uint)slot >= (uint)ctx.MaxMeshes) return;
        if (ctx.DeferredMeshSlots == null || ctx.MeshSlotPendingRelease == null) return;

        AcquireDeferredLock(ref ctx);
        try
        {
            if (ctx.MeshSlotPendingRelease[slot] == 0)
            {
                ctx.MeshSlotPendingRelease[slot] = 1;
                if (ctx.DeferredMeshCount < ctx.DeferredMeshCap)
                    ctx.DeferredMeshSlots[ctx.DeferredMeshCount++] = slot;
            }
        }
        finally
        {
            ReleaseDeferredLock(ref ctx);
        }
    }

    private static void ReleaseGpuTextureHook(void* ctxPtr, int slot)
    {
        if (ctxPtr == null) return;
        ref GpuContext ctx = ref *(GpuContext*)ctxPtr;
        if (ctx.Initialized == 0 || (uint)slot >= (uint)ctx.MaxTextures) return;
        if (ctx.DeferredTextureSlots == null || ctx.TextureSlotPendingRelease == null) return;

        AcquireDeferredLock(ref ctx);
        try
        {
            if (ctx.TextureSlotPendingRelease[slot] == 0)
            {
                ctx.TextureSlotPendingRelease[slot] = 1;
                if (ctx.DeferredTextureCount < ctx.DeferredTextureCap)
                    ctx.DeferredTextureSlots[ctx.DeferredTextureCount++] = slot;
            }
        }
        finally
        {
            ReleaseDeferredLock(ref ctx);
        }
    }

    /// <summary>
    /// Drains all deferred GPU mesh/texture release requests queued by <see cref="AssetSystem.UnloadByBundle"/>
    /// (safe to enqueue from the Logic Thread) and releases the underlying SDL3_gpu handles on the Main/Render Thread.
    /// Automatically invoked at the end of <see cref="BatchRenderer.SubmitRenderPacket"/> and inside <see cref="Dispose(ref GpuContext, ref AssetRegistry)"/>.
    /// </summary>
    public static void FlushDeferredReleases(ref GpuContext ctx)
    {
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero) return;

        int* batch = stackalloc int[64];

        if (ctx.DeferredMeshSlots != null)
        {
            while (true)
            {
                int count = 0;
                AcquireDeferredLock(ref ctx);
                try
                {
                    while (ctx.DeferredMeshCount > 0 && count < 64)
                        batch[count++] = ctx.DeferredMeshSlots[--ctx.DeferredMeshCount];
                }
                finally
                {
                    ReleaseDeferredLock(ref ctx);
                }

                if (count == 0) break;
                for (int i = 0; i < count; i++)
                    ReleaseMeshSlot(ref ctx, batch[i]);
            }
        }

        if (ctx.DeferredTextureSlots != null)
        {
            while (true)
            {
                int count = 0;
                AcquireDeferredLock(ref ctx);
                try
                {
                    while (ctx.DeferredTextureCount > 0 && count < 64)
                        batch[count++] = ctx.DeferredTextureSlots[--ctx.DeferredTextureCount];
                }
                finally
                {
                    ReleaseDeferredLock(ref ctx);
                }

                if (count == 0) break;
                for (int i = 0; i < count; i++)
                    ReleaseTextureSlot(ref ctx, batch[i]);
            }
        }
    }

    private static void CreateDefaultSamplersAndBuffers(ref GpuContext ctx)
    {
        var linWrap = new SDL.GPUSamplerCreateInfo
        {
            MinFilter = SDL.GPUFilter.Linear,
            MagFilter = SDL.GPUFilter.Linear,
            MipmapMode = SDL.GPUSamplerMipmapMode.Linear,
            AddressModeU = SDL.GPUSamplerAddressMode.Repeat,
            AddressModeV = SDL.GPUSamplerAddressMode.Repeat,
            AddressModeW = SDL.GPUSamplerAddressMode.Repeat,
        };
        ctx.SamplerLinearWrap = SDL.CreateGPUSampler(ctx.Device, in linWrap);
        if (ctx.SamplerLinearWrap == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUSampler(LinearWrap) failed: {SDL.GetError()}");

        var linClamp = linWrap;
        linClamp.AddressModeU = SDL.GPUSamplerAddressMode.ClampToEdge;
        linClamp.AddressModeV = SDL.GPUSamplerAddressMode.ClampToEdge;
        linClamp.AddressModeW = SDL.GPUSamplerAddressMode.ClampToEdge;
        ctx.SamplerLinearClamp = SDL.CreateGPUSampler(ctx.Device, in linClamp);
        if (ctx.SamplerLinearClamp == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUSampler(LinearClamp) failed: {SDL.GetError()}");

        var nearClamp = linClamp;
        nearClamp.MinFilter = SDL.GPUFilter.Nearest;
        nearClamp.MagFilter = SDL.GPUFilter.Nearest;
        nearClamp.MipmapMode = SDL.GPUSamplerMipmapMode.Nearest;
        ctx.SamplerNearestClamp = SDL.CreateGPUSampler(ctx.Device, in nearClamp);
        if (ctx.SamplerNearestClamp == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUSampler(NearestClamp) failed: {SDL.GetError()}");

        // Allocate reusable 16MB dynamic upload transfer buffer
        ctx.UploadTransferBufferBytes = 16 * 1024 * 1024;
        var tbInfo = new SDL.GPUTransferBufferCreateInfo
        {
            Usage = SDL.GPUTransferBufferUsage.Upload,
            Size = ctx.UploadTransferBufferBytes,
        };
        ctx.UploadTransferBuffer = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);
        if (ctx.UploadTransferBuffer == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUTransferBuffer({ctx.UploadTransferBufferBytes}) failed: {SDL.GetError()}");

        // Allocate per-frame storage buffers (grown dynamically on demand in BatchRenderer)
        ctx.Instance3DBufferBytes = 4 * 1024 * 1024;       // 32,768 3D instances
        ctx.JointPaletteBufferBytes = 2 * 1024 * 1024;     // 32,768 joint matrices
        ctx.SpriteInstanceBufferBytes = 2 * 1024 * 1024;   // ~18k sprites
        ctx.TextGlyphBufferBytes = 4 * 1024 * 1024;        // ~29,127 text glyphs
        ctx.ParticleInstanceBufferBytes = 2 * 1024 * 1024; // 32,768 particles
        ctx.Rig2DVertexBufferBytes = 2 * 1024 * 1024;      // 65,536 Rig2D vertices (32B each)
        ctx.Rig2DIndexBufferBytes = 1 * 1024 * 1024;       // 262,144 Rig2D indices (4B each)

        ctx.Instance3DBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.Instance3DBufferBytes);
        ctx.JointPaletteBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.JointPaletteBufferBytes);
        ctx.SpriteInstanceBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.SpriteInstanceBufferBytes);
        ctx.TextGlyphBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.TextGlyphBufferBytes);
        ctx.ParticleInstanceBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.ParticleInstanceBufferBytes);
        ctx.Rig2DVertexBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.Vertex, ctx.Rig2DVertexBufferBytes);
        ctx.Rig2DIndexBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.Index, ctx.Rig2DIndexBufferBytes);

        // Static unit quad index buffer (6 uint32: 0,1,2, 0,2,3)
        ctx.QuadIndexBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.Index, 6 * sizeof(uint));

        // 1x1 white fallback texture
        var whiteTexInfo = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = SDL.GPUTextureFormat.R8G8B8A8Unorm,
            Usage = SDL.GPUTextureUsageFlags.Sampler,
            Width = 1,
            Height = 1,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        ctx.DefaultWhiteTexture = SDL.CreateGPUTexture(ctx.Device, in whiteTexInfo);
        if (ctx.DefaultWhiteTexture == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUTexture(DefaultWhite) failed: {SDL.GetError()}");

        // Upload QuadIndexBuffer + DefaultWhiteTexture in one startup copy pass
        uint* quadIndices = stackalloc uint[6] { 0, 1, 2, 0, 2, 3 };
        uint whitePixel = 0xFFFF_FFFFu;

        nint mapPtr = SDL.MapGPUTransferBuffer(ctx.Device, ctx.UploadTransferBuffer, false);
        if (mapPtr == nint.Zero)
            throw new InvalidOperationException($"SDL_MapGPUTransferBuffer(Startup) failed: {SDL.GetError()}");
        Buffer.MemoryCopy(quadIndices, (void*)mapPtr, 24, 24);
        *(uint*)((byte*)mapPtr + 64) = whitePixel;
        SDL.UnmapGPUTransferBuffer(ctx.Device, ctx.UploadTransferBuffer);

        nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
        if (cmd == nint.Zero)
            throw new InvalidOperationException($"SDL_AcquireGPUCommandBuffer(Startup) failed: {SDL.GetError()}");
        nint copy = SDL.BeginGPUCopyPass(cmd);
        if (copy == nint.Zero)
        {
            SDL.SubmitGPUCommandBuffer(cmd);
            throw new InvalidOperationException($"SDL_BeginGPUCopyPass(Startup) failed: {SDL.GetError()}");
        }

        var bufSrc = new SDL.GPUTransferBufferLocation { TransferBuffer = ctx.UploadTransferBuffer, Offset = 0 };
        var bufDst = new SDL.GPUBufferRegion { Buffer = ctx.QuadIndexBuffer, Offset = 0, Size = 24 };
        SDL.UploadToGPUBuffer(copy, in bufSrc, in bufDst, false);

        var texSrc = new SDL.GPUTextureTransferInfo { TransferBuffer = ctx.UploadTransferBuffer, Offset = 64, PixelsPerRow = 1, RowsPerLayer = 1 };
        var texDst = new SDL.GPUTextureRegion { Texture = ctx.DefaultWhiteTexture, W = 1, H = 1, D = 1 };
        SDL.UploadToGPUTexture(copy, in texSrc, in texDst, false);

        SDL.EndGPUCopyPass(copy);
        SDL.SubmitGPUCommandBuffer(cmd);
    }

    public static nint CreateBuffer(nint device, SDL.GPUBufferUsageFlags usage, uint size)
    {
        var info = new SDL.GPUBufferCreateInfo { Usage = usage, Size = size };
        nint buf = SDL.CreateGPUBuffer(device, in info);
        if (buf == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUBuffer({size}) failed: {SDL.GetError()}");
        return buf;
    }

    public static void EnsureDepthTexture(ref GpuContext ctx, uint width, uint height)
    {
        if (width == 0 || height == 0 || ctx.Device == nint.Zero) return;
        if (ctx.DepthTexture != nint.Zero && ctx.DepthWidth == width && ctx.DepthHeight == height)
            return;

        if (ctx.DepthTexture != nint.Zero)
        {
            SDL.ReleaseGPUTexture(ctx.Device, ctx.DepthTexture);
            ctx.DepthTexture = nint.Zero;
            ctx.DepthWidth = 0;
            ctx.DepthHeight = 0;
        }

        var info = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = ctx.DepthFormat,
            Usage = SDL.GPUTextureUsageFlags.DepthStencilTarget,
            Width = width,
            Height = height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        nint tex = SDL.CreateGPUTexture(ctx.Device, in info);
        if (tex == nint.Zero)
        {
            string err = SDL.GetError();
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"EnsureDepthTexture({width}x{height}) failed: {err}");
            return;
        }
        ctx.DepthTexture = tex;
        ctx.DepthWidth = width;
        ctx.DepthHeight = height;
    }

    public static bool EnsureShadowMapTexture(ref GpuContext ctx, uint size)
    {
        if (ctx.ShadowMapSupported == 0 || ctx.Device == nint.Zero) return false;
        if (size < 256u) size = 256u;
        if (size > 4096u) size = 4096u;
        if (ctx.ShadowDepthTexture != nint.Zero && ctx.ShadowMapSize == size)
            return true;

        if (ctx.ShadowDepthTexture != nint.Zero)
        {
            SDL.ReleaseGPUTexture(ctx.Device, ctx.ShadowDepthTexture);
            ctx.ShadowDepthTexture = nint.Zero;
            ctx.ShadowMapSize = 0;
        }

        var info = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = ctx.DepthFormat,
            Usage = SDL.GPUTextureUsageFlags.DepthStencilTarget | SDL.GPUTextureUsageFlags.Sampler,
            Width = size,
            Height = size,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        nint tex = SDL.CreateGPUTexture(ctx.Device, in info);
        if (tex == nint.Zero)
        {
            if (ctx.Log != null) LogSystem.Warn(ctx.Log, $"EnsureShadowMapTexture({size}) failed: {SDL.GetError()}");
            return false;
        }
        ctx.ShadowDepthTexture = tex;
        ctx.ShadowMapSize = size;
        return true;
    }

    public static void EnsureSceneColorTarget(ref GpuContext ctx, uint width, uint height)
    {
        if (width == 0 || height == 0 || ctx.Device == nint.Zero) return;
        if (ctx.SceneColorTexture != nint.Zero && ctx.SceneColorWidth == width && ctx.SceneColorHeight == height)
            return;

        if (ctx.SceneColorTexture != nint.Zero)
        {
            SDL.ReleaseGPUTexture(ctx.Device, ctx.SceneColorTexture);
            ctx.SceneColorTexture = nint.Zero;
            ctx.SceneColorWidth = 0;
            ctx.SceneColorHeight = 0;
        }

        var info = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = ctx.SwapchainFormat,
            Usage = SDL.GPUTextureUsageFlags.ColorTarget | SDL.GPUTextureUsageFlags.Sampler,
            Width = width,
            Height = height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        nint tex = SDL.CreateGPUTexture(ctx.Device, in info);
        if (tex == nint.Zero)
        {
            string err = SDL.GetError();
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"EnsureSceneColorTarget({width}x{height}) failed: {err}");
            return;
        }
        ctx.SceneColorTexture = tex;
        ctx.SceneColorWidth = width;
        ctx.SceneColorHeight = height;
    }

    public static void EnsureCaptureTarget(ref GpuContext ctx, uint width, uint height)
    {
        if (width == 0 || height == 0 || ctx.Device == nint.Zero) return;
        if (ctx.CaptureColorTexture != nint.Zero && ctx.CaptureWidth == width && ctx.CaptureHeight == height)
            return;

        if (ctx.CaptureColorTexture != nint.Zero)
        {
            SDL.ReleaseGPUTexture(ctx.Device, ctx.CaptureColorTexture);
            ctx.CaptureColorTexture = nint.Zero;
        }
        if (ctx.CaptureTransferBuffer != nint.Zero)
        {
            SDL.ReleaseGPUTransferBuffer(ctx.Device, ctx.CaptureTransferBuffer);
            ctx.CaptureTransferBuffer = nint.Zero;
        }
        ctx.CaptureWidth = 0;
        ctx.CaptureHeight = 0;

        var texInfo = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = ctx.SwapchainFormat,
            Usage = SDL.GPUTextureUsageFlags.ColorTarget | SDL.GPUTextureUsageFlags.Sampler,
            Width = width,
            Height = height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        nint tex = SDL.CreateGPUTexture(ctx.Device, in texInfo);
        if (tex == nint.Zero)
        {
            string err = SDL.GetError();
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"EnsureCaptureTarget texture({width}x{height}) failed: {err}");
            return;
        }

        ulong tbBytes = (ulong)width * height * 4UL;
        if (tbBytes > uint.MaxValue)
        {
            SDL.ReleaseGPUTexture(ctx.Device, tex);
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"EnsureCaptureTarget({width}x{height}) exceeds 4GB transfer limit");
            return;
        }

        var tbInfo = new SDL.GPUTransferBufferCreateInfo
        {
            Usage = SDL.GPUTransferBufferUsage.Download,
            Size = (uint)tbBytes,
        };
        nint tb = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);
        if (tb == nint.Zero)
        {
            string err = SDL.GetError();
            SDL.ReleaseGPUTexture(ctx.Device, tex);
            if (ctx.Log != null) LogSystem.Error(ctx.Log, $"EnsureCaptureTarget transferBuffer({width}x{height}) failed: {err}");
            return;
        }

        ctx.CaptureColorTexture = tex;
        ctx.CaptureTransferBuffer = tb;
        ctx.CaptureWidth = width;
        ctx.CaptureHeight = height;
    }

    /// <summary>
    /// Downloads CaptureColorTexture from the GPU into a caller-provided RGBA8 byte buffer (length >= CaptureWidth * CaptureHeight * 4).
    /// Useful for deterministic visual verification & screenshot export.
    /// </summary>
    public static bool ReadCaptureTextureRgba(ref GpuContext ctx, byte[] dstRgba)
    {
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero ||
            ctx.CaptureColorTexture == nint.Zero || ctx.CaptureTransferBuffer == nint.Zero ||
            ctx.CaptureWidth == 0 || ctx.CaptureHeight == 0)
            return false;
        int w = (int)ctx.CaptureWidth;
        int h = (int)ctx.CaptureHeight;
        long neededBytes = (long)w * h * 4L;
        if (dstRgba == null || dstRgba.LongLength < neededBytes) return false;

        nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
        if (cmd == nint.Zero) return false;
        nint copy = SDL.BeginGPUCopyPass(cmd);
        if (copy == nint.Zero)
        {
            SDL.SubmitGPUCommandBuffer(cmd);
            return false;
        }
        var src = new SDL.GPUTextureRegion
        {
            Texture = ctx.CaptureColorTexture,
            W = ctx.CaptureWidth,
            H = ctx.CaptureHeight,
            D = 1,
        };
        var dst = new SDL.GPUTextureTransferInfo
        {
            TransferBuffer = ctx.CaptureTransferBuffer,
            Offset = 0,
            PixelsPerRow = ctx.CaptureWidth,
            RowsPerLayer = ctx.CaptureHeight,
        };
        SDL.DownloadFromGPUTexture(copy, in src, in dst);
        SDL.EndGPUCopyPass(copy);

        nint fence = SDL.SubmitGPUCommandBufferAndAcquireFence(cmd);
        if (fence == nint.Zero) return false;
        nint* fenceArr = stackalloc nint[1] { fence };
        SDL.WaitForGPUFences(ctx.Device, true, (nint)fenceArr, 1);
        SDL.ReleaseGPUFence(ctx.Device, fence);

        nint mapped = SDL.MapGPUTransferBuffer(ctx.Device, ctx.CaptureTransferBuffer, false);
        if (mapped == nint.Zero) return false;

        bool isBgra = ctx.SwapchainFormat == SDL.GPUTextureFormat.B8G8R8A8Unorm ||
                      ctx.SwapchainFormat == SDL.GPUTextureFormat.B8G8R8A8UnormSRGB;

        byte* srcPtr = (byte*)mapped;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            if (isBgra)
            {
                dstRgba[o + 0] = srcPtr[o + 2];
                dstRgba[o + 1] = srcPtr[o + 1];
                dstRgba[o + 2] = srcPtr[o + 0];
                dstRgba[o + 3] = 255;
            }
            else
            {
                dstRgba[o + 0] = srcPtr[o + 0];
                dstRgba[o + 1] = srcPtr[o + 1];
                dstRgba[o + 2] = srcPtr[o + 2];
                dstRgba[o + 3] = 255;
            }
        }
        SDL.UnmapGPUTransferBuffer(ctx.Device, ctx.CaptureTransferBuffer);
        return true;
    }

    /// <summary>
    /// Downloads CaptureColorTexture from the GPU and writes a PNG file to disk (for visual verification!).
    /// </summary>
    public static void SaveCaptureTextureToPng(ref GpuContext ctx, string outputPath)
    {
        if (ctx.CaptureColorTexture == nint.Zero) return;
        int w = (int)ctx.CaptureWidth;
        int h = (int)ctx.CaptureHeight;
        byte[] rgba = new byte[w * h * 4];
        if (!ReadCaptureTextureRgba(ref ctx, rgba)) return;

        using var fs = File.Create(outputPath);
        var writer = new ImageWriter();
        writer.WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha, fs);
    }

    public static int EnsureMeshUploaded(ref GpuContext ctx, ref MeshAssetData mesh)
    {
        if (mesh.GpuSlot >= 0) return mesh.GpuSlot;
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero || ctx.Meshes == null) return -1;
        if (mesh.VertexCount <= 0 || mesh.IndexCount <= 0 || mesh.Vertices == null || mesh.Indices == null) return -1;

        ulong vBytes64 = (ulong)mesh.VertexCount * (ulong)sizeof(Vertex3D);
        ulong iBytes64 = (ulong)mesh.IndexCount * (ulong)sizeof(uint);
        if (vBytes64 == 0 || iBytes64 == 0 || vBytes64 > uint.MaxValue || iBytes64 > uint.MaxValue || vBytes64 + iBytes64 > uint.MaxValue)
            return -1;

        int slot = -1;
        for (int i = 0; i < ctx.MaxMeshes; i++)
        {
            if (ctx.Meshes[i].Alive == 0 && (ctx.MeshSlotPendingRelease == null || Volatile.Read(ref ctx.MeshSlotPendingRelease[i]) == 0))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) throw new OutOfMemoryException("GpuContext mesh slots exhausted.");

        uint vBytes = (uint)vBytes64;
        uint iBytes = (uint)iBytes64;
        nint vb = nint.Zero;
        nint ib = nint.Zero;
        nint tb = nint.Zero;
        bool isMapped = false;

        try
        {
            vb = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.Vertex, vBytes);
            ib = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.Index, iBytes);

            // One-shot upload transfer
            var tbInfo = new SDL.GPUTransferBufferCreateInfo { Usage = SDL.GPUTransferBufferUsage.Upload, Size = vBytes + iBytes };
            tb = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);
            if (tb == nint.Zero)
                throw new InvalidOperationException($"SDL_CreateGPUTransferBuffer({vBytes + iBytes}) failed for mesh {mesh.Name}: {SDL.GetError()}");

            nint mapRaw = SDL.MapGPUTransferBuffer(ctx.Device, tb, false);
            if (mapRaw == nint.Zero)
                throw new InvalidOperationException($"SDL_MapGPUTransferBuffer failed for mesh {mesh.Name}: {SDL.GetError()}");
            isMapped = true;

            byte* mapped = (byte*)mapRaw;
            Buffer.MemoryCopy(mesh.Vertices, mapped, vBytes, vBytes);
            Buffer.MemoryCopy(mesh.Indices, mapped + vBytes, iBytes, iBytes);
            SDL.UnmapGPUTransferBuffer(ctx.Device, tb);
            isMapped = false;

            nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
            if (cmd == nint.Zero)
                throw new InvalidOperationException($"SDL_AcquireGPUCommandBuffer failed for mesh {mesh.Name}: {SDL.GetError()}");
            nint copy = SDL.BeginGPUCopyPass(cmd);
            if (copy == nint.Zero)
            {
                SDL.SubmitGPUCommandBuffer(cmd);
                throw new InvalidOperationException($"SDL_BeginGPUCopyPass failed for mesh {mesh.Name}: {SDL.GetError()}");
            }
            var vSrc = new SDL.GPUTransferBufferLocation { TransferBuffer = tb, Offset = 0 };
            var vDst = new SDL.GPUBufferRegion { Buffer = vb, Offset = 0, Size = vBytes };
            SDL.UploadToGPUBuffer(copy, in vSrc, in vDst, false);

            var iSrc = new SDL.GPUTransferBufferLocation { TransferBuffer = tb, Offset = vBytes };
            var iDst = new SDL.GPUBufferRegion { Buffer = ib, Offset = 0, Size = iBytes };
            SDL.UploadToGPUBuffer(copy, in iSrc, in iDst, false);
            SDL.EndGPUCopyPass(copy);
            SDL.SubmitGPUCommandBuffer(cmd);
            SDL.ReleaseGPUTransferBuffer(ctx.Device, tb);
            tb = nint.Zero;

            ctx.Meshes[slot] = new GpuMeshSlot
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                IndexCount = (uint)mesh.IndexCount,
                Alive = 1,
            };
            mesh.GpuSlot = slot;
            mesh.GpuSlotGen = ctx.MeshSlotGens != null ? ctx.MeshSlotGens[slot] : 1u;
            return slot;
        }
        catch
        {
            if (isMapped && tb != nint.Zero) SDL.UnmapGPUTransferBuffer(ctx.Device, tb);
            if (tb != nint.Zero) SDL.ReleaseGPUTransferBuffer(ctx.Device, tb);
            if (vb != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, vb);
            if (ib != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ib);
            throw;
        }
    }

    public static int EnsureTextureUploaded(ref GpuContext ctx, ref TextureAssetData tex)
    {
        if (tex.GpuSlot >= 0) return tex.GpuSlot;
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero || ctx.Textures == null) return -1;
        if (tex.Width <= 0 || tex.Height <= 0 || tex.Pixels == null) return -1;

        TexturePixelFormat srcFmt = tex.Format;
        if (srcFmt == TexturePixelFormat.Rgba8 && tex.Channels == 1)
            srcFmt = TexturePixelFormat.R8;

        ulong expectedSrcBytes64 = srcFmt switch
        {
            TexturePixelFormat.R8 => (ulong)tex.Width * (ulong)tex.Height,
            TexturePixelFormat.Bc1Rgba => (((ulong)tex.Width + 3UL) >> 2) * (((ulong)tex.Height + 3UL) >> 2) * 8UL,
            TexturePixelFormat.Bc3Rgba => (((ulong)tex.Width + 3UL) >> 2) * (((ulong)tex.Height + 3UL) >> 2) * 16UL,
            _ => (ulong)tex.Width * (ulong)tex.Height * 4UL,
        };
        if (expectedSrcBytes64 == 0 || expectedSrcBytes64 > uint.MaxValue) return -1;
        if (tex.DataBytes < 0 || (tex.DataBytes > 0 && (ulong)tex.DataBytes < expectedSrcBytes64))
        {
            if (ctx.Log != null)
                LogSystem.Error(ctx.Log, $"EnsureTextureUploaded rejected undersized texture {tex.Name} ({tex.Width}x{tex.Height}, {srcFmt}): DataBytes={tex.DataBytes} < expected={expectedSrcBytes64}");
            return -1;
        }

        int slot = -1;
        for (int i = 0; i < ctx.MaxTextures; i++)
        {
            if (ctx.Textures[i].Alive == 0 && (ctx.TextureSlotPendingRelease == null || Volatile.Read(ref ctx.TextureSlotPendingRelease[i]) == 0))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) throw new OutOfMemoryException("GpuContext texture slots exhausted.");

        SDL.GPUTextureFormat fmt;
        bool decodeFallbackToRgba = false;
        if (srcFmt == TexturePixelFormat.R8)
        {
            fmt = SDL.GPUTextureFormat.R8Unorm;
        }
        else if (srcFmt == TexturePixelFormat.Bc1Rgba || srcFmt == TexturePixelFormat.Bc3Rgba)
        {
            bool isBc3 = srcFmt == TexturePixelFormat.Bc3Rgba;
            fmt = isBc3 ? SDL.GPUTextureFormat.BC3RGBAUnorm : SDL.GPUTextureFormat.BC1RGBAUnorm;
            bool nonBlockAligned = (tex.Width & 3) != 0 || (tex.Height & 3) != 0;
            bool gpuSupports = !nonBlockAligned && SDL.GPUTextureSupportsFormat(ctx.Device, fmt, SDL.GPUTextureType.TextureType2D, SDL.GPUTextureUsageFlags.Sampler);
            if (!gpuSupports)
            {
                if (ctx.Log != null)
                {
                    LogSystem.Warn(ctx.Log, $"Texture {tex.Name} ({tex.Width}x{tex.Height}, {srcFmt}) falling back to RGBA8 software decode (non4x4={nonBlockAligned})");
                }
                fmt = SDL.GPUTextureFormat.R8G8B8A8Unorm;
                decodeFallbackToRgba = true;
            }
        }
        else
        {
            fmt = SDL.GPUTextureFormat.R8G8B8A8Unorm;
        }

        ulong uploadBytes64 = decodeFallbackToRgba
            ? (ulong)tex.Width * (ulong)tex.Height * 4UL
            : expectedSrcBytes64;
        if (uploadBytes64 == 0 || uploadBytes64 > uint.MaxValue) return -1;
        uint byteSize = (uint)uploadBytes64;

        nint gpuTex = nint.Zero;
        nint tb = nint.Zero;
        bool isMapped = false;

        try
        {
            var info = new SDL.GPUTextureCreateInfo
            {
                Type = SDL.GPUTextureType.TextureType2D,
                Format = fmt,
                Usage = SDL.GPUTextureUsageFlags.Sampler,
                Width = (uint)tex.Width,
                Height = (uint)tex.Height,
                LayerCountOrDepth = 1,
                NumLevels = 1,
                SampleCount = SDL.GPUSampleCount.SampleCount1,
            };
            gpuTex = SDL.CreateGPUTexture(ctx.Device, in info);
            if (gpuTex == nint.Zero)
            {
                string err = SDL.GetError();
                if (ctx.Log != null) LogSystem.Error(ctx.Log, $"SDL_CreateGPUTexture({tex.Width}x{tex.Height}, {fmt}) failed: {err}");
                throw new InvalidOperationException($"SDL_CreateGPUTexture failed: {err}");
            }

            var tbInfo = new SDL.GPUTransferBufferCreateInfo { Usage = SDL.GPUTransferBufferUsage.Upload, Size = byteSize };
            tb = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);
            if (tb == nint.Zero)
            {
                string err = SDL.GetError();
                if (ctx.Log != null) LogSystem.Error(ctx.Log, $"SDL_CreateGPUTransferBuffer({byteSize}) failed for texture {tex.Name}: {err}");
                throw new InvalidOperationException($"SDL_CreateGPUTransferBuffer failed: {err}");
            }

            nint mapRaw = SDL.MapGPUTransferBuffer(ctx.Device, tb, false);
            if (mapRaw == nint.Zero)
            {
                string err = SDL.GetError();
                if (ctx.Log != null) LogSystem.Error(ctx.Log, $"SDL_MapGPUTransferBuffer({byteSize}) failed for texture {tex.Name}: {err}");
                throw new InvalidOperationException($"SDL_MapGPUTransferBuffer failed: {err}");
            }
            isMapped = true;

            void* mapped = (void*)mapRaw;
            if (decodeFallbackToRgba)
            {
                if (srcFmt == TexturePixelFormat.Bc1Rgba)
                    TextureCompressionSystem.DecodeBc1ToRgba(tex.Pixels, tex.Width, tex.Height, (byte*)mapped);
                else
                    TextureCompressionSystem.DecodeBc3ToRgba(tex.Pixels, tex.Width, tex.Height, (byte*)mapped);
            }
            else
            {
                Buffer.MemoryCopy(tex.Pixels, mapped, byteSize, byteSize);
            }
            SDL.UnmapGPUTransferBuffer(ctx.Device, tb);
            isMapped = false;

            nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
            if (cmd == nint.Zero)
                throw new InvalidOperationException($"SDL_AcquireGPUCommandBuffer failed for texture {tex.Name}: {SDL.GetError()}");
            nint copy = SDL.BeginGPUCopyPass(cmd);
            if (copy == nint.Zero)
            {
                SDL.SubmitGPUCommandBuffer(cmd);
                throw new InvalidOperationException($"SDL_BeginGPUCopyPass failed for texture {tex.Name}: {SDL.GetError()}");
            }
            var src = new SDL.GPUTextureTransferInfo
            {
                TransferBuffer = tb,
                Offset = 0,
                PixelsPerRow = 0,
                RowsPerLayer = 0,
            };
            var dst = new SDL.GPUTextureRegion
            {
                Texture = gpuTex,
                W = (uint)tex.Width,
                H = (uint)tex.Height,
                D = 1,
            };
            SDL.UploadToGPUTexture(copy, in src, in dst, false);
            SDL.EndGPUCopyPass(copy);
            SDL.SubmitGPUCommandBuffer(cmd);
            SDL.ReleaseGPUTransferBuffer(ctx.Device, tb);
            tb = nint.Zero;

            ctx.Textures[slot] = new GpuTextureSlot
            {
                Texture = gpuTex,
                Width = tex.Width,
                Height = tex.Height,
                Channels = tex.Channels,
                ClampEdge = tex.ClampEdge,
                NearestFilter = tex.NearestFilter,
                Alive = 1,
            };
            tex.GpuSlot = slot;
            tex.GpuSlotGen = ctx.TextureSlotGens != null ? ctx.TextureSlotGens[slot] : 1u;
            return slot;
        }
        catch
        {
            if (isMapped && tb != nint.Zero) SDL.UnmapGPUTransferBuffer(ctx.Device, tb);
            if (tb != nint.Zero) SDL.ReleaseGPUTransferBuffer(ctx.Device, tb);
            if (gpuTex != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, gpuTex);
            throw;
        }
    }

    public static void ReleaseMeshSlot(ref GpuContext ctx, int slot)
    {
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero || ctx.Meshes == null) return;
        if ((uint)slot >= (uint)ctx.MaxMeshes) return;
        if (ctx.Meshes[slot].Alive != 0)
        {
            if (ctx.Meshes[slot].VertexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.Meshes[slot].VertexBuffer);
            if (ctx.Meshes[slot].IndexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.Meshes[slot].IndexBuffer);
            ctx.Meshes[slot] = default;
            if (ctx.MeshSlotGens != null)
                ctx.MeshSlotGens[slot] = HandlePacking.NextGeneration(ctx.MeshSlotGens[slot]);
        }
        if (ctx.MeshSlotPendingRelease != null)
            Volatile.Write(ref ctx.MeshSlotPendingRelease[slot], 0);
    }

    public static void ReleaseTextureSlot(ref GpuContext ctx, int slot)
    {
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero || ctx.Textures == null) return;
        if ((uint)slot >= (uint)ctx.MaxTextures) return;
        if (ctx.Textures[slot].Alive != 0)
        {
            if (ctx.Textures[slot].Texture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.Textures[slot].Texture);
            ctx.Textures[slot] = default;
            if (ctx.TextureSlotGens != null)
                ctx.TextureSlotGens[slot] = HandlePacking.NextGeneration(ctx.TextureSlotGens[slot]);
        }
        if (ctx.TextureSlotPendingRelease != null)
            Volatile.Write(ref ctx.TextureSlotPendingRelease[slot], 0);
    }

    /// <summary>
    /// Clears GPU cleanup hooks on <paramref name="assets"/>, resets all live asset GPU slots to -1,
    /// drains deferred releases, and disposes the SDL3_gpu device. Recommended canonical teardown overload.
    /// </summary>
    public static void Dispose(ref GpuContext ctx, ref AssetRegistry assets)
    {
        ClearAssetCleanupHooks(ref assets);
        AssetSystem.ResetAllGpuSlots(ref assets);
        DisposeCore(ref ctx);
    }

    /// <summary>
    /// Disposes the SDL3_gpu device without clearing <see cref="AssetRegistry"/> hooks or resetting asset GPU slots.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="Dispose(ref GpuContext, ref AssetRegistry)"/> so asset hooks are cleared and live asset GPU slots are reset cleanly.
    /// </remarks>
    public static void Dispose(ref GpuContext ctx) => DisposeCore(ref ctx);

    private static void DisposeCore(ref GpuContext ctx)
    {
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero) return;
        SDL.WaitForGPUIdle(ctx.Device);
        FlushDeferredReleases(ref ctx);

        for (int i = 0; i < ctx.MaxMeshes; i++) ReleaseMeshSlot(ref ctx, i);
        for (int i = 0; i < ctx.MaxTextures; i++) ReleaseTextureSlot(ref ctx, i);
        for (int i = 0; i < ctx.MaxShaders; i++)
        {
            if (ctx.Shaders[i].Alive == 0) continue;
            ref GpuShaderSlot s = ref ctx.Shaders[i];
            if (s.PipelineOpaque != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineOpaque);
            if (s.PipelineAlphaBlend != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineAlphaBlend);
            if (s.PipelineAdditive != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineAdditive);
            if (s.PipelineOverlayNoDepth != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineOverlayNoDepth);
            if (s.PipelineAdditiveNoDepth != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineAdditiveNoDepth);
            if (s.PipelineMultiply != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineMultiply);
            if (s.PipelineMultiplyNoDepth != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineMultiplyNoDepth);
            if (s.PipelineOpaqueCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineOpaqueCullNone);
            if (s.PipelineAlphaBlendCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, s.PipelineAlphaBlendCullNone);
            if (s.VertexShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, s.VertexShader);
            if (s.FragmentShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, s.FragmentShader);
            s = default;
        }

        if (ctx.ShadowPipeline != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, ctx.ShadowPipeline);
        if (ctx.ShadowPipelineCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, ctx.ShadowPipelineCullNone);
        if (ctx.ShadowVertexShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, ctx.ShadowVertexShader);
        if (ctx.ShadowFragmentShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, ctx.ShadowFragmentShader);
        if (ctx.ShadowMaskPipeline != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, ctx.ShadowMaskPipeline);
        if (ctx.ShadowMaskPipelineCullNone != nint.Zero) SDL.ReleaseGPUGraphicsPipeline(ctx.Device, ctx.ShadowMaskPipelineCullNone);
        if (ctx.ShadowMaskVertexShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, ctx.ShadowMaskVertexShader);
        if (ctx.ShadowMaskFragmentShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, ctx.ShadowMaskFragmentShader);
        if (ctx.ShadowDepthTexture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.ShadowDepthTexture);
        if (ctx.DepthTexture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.DepthTexture);
        if (ctx.SceneColorTexture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.SceneColorTexture);
        if (ctx.CaptureColorTexture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.CaptureColorTexture);
        if (ctx.CaptureTransferBuffer != nint.Zero) SDL.ReleaseGPUTransferBuffer(ctx.Device, ctx.CaptureTransferBuffer);
        if (ctx.DefaultWhiteTexture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.DefaultWhiteTexture);
        if (ctx.SamplerLinearWrap != nint.Zero) SDL.ReleaseGPUSampler(ctx.Device, ctx.SamplerLinearWrap);
        if (ctx.SamplerLinearClamp != nint.Zero) SDL.ReleaseGPUSampler(ctx.Device, ctx.SamplerLinearClamp);
        if (ctx.SamplerNearestClamp != nint.Zero) SDL.ReleaseGPUSampler(ctx.Device, ctx.SamplerNearestClamp);
        if (ctx.QuadIndexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.QuadIndexBuffer);
        if (ctx.Instance3DBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.Instance3DBuffer);
        if (ctx.JointPaletteBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.JointPaletteBuffer);
        if (ctx.SpriteInstanceBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.SpriteInstanceBuffer);
        if (ctx.TextGlyphBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.TextGlyphBuffer);
        if (ctx.ParticleInstanceBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.ParticleInstanceBuffer);
        if (ctx.Rig2DVertexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.Rig2DVertexBuffer);
        if (ctx.Rig2DIndexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.Rig2DIndexBuffer);
        if (ctx.UploadTransferBuffer != nint.Zero) SDL.ReleaseGPUTransferBuffer(ctx.Device, ctx.UploadTransferBuffer);

        if (ctx.Window != nint.Zero) SDL.ReleaseWindowFromGPUDevice(ctx.Device, ctx.Window);
        SDL.DestroyGPUDevice(ctx.Device);
        ShaderCross.Quit();
        ctx.Device = nint.Zero;
        ctx.Initialized = 0;
    }
}
