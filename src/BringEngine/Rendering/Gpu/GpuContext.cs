using System;
using System.IO;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using SDL3;
using StbImageWriteSharp;

namespace BringEngine.Rendering.Gpu;

public enum ShaderVertexLayout : byte
{
    Mesh3D = 0,
    InstancedQuad = 1,
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
    public ShaderVertexLayout Layout;
    public byte Alive;
}

/// <summary>
/// Per-frame GPU telemetry metrics for performance monitoring and AI-agent optimization.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderStats
{
    public int DrawCalls;
    public int PipelineBinds;
    public int MeshInstances;
    public int SpriteInstances;
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

    public nint UploadTransferBuffer;
    public uint UploadTransferBufferBytes;

    // Resource pools (allocated in Persistent arena)
    public GpuMeshSlot* Meshes;
    public int MaxMeshes;

    public GpuTextureSlot* Textures;
    public int MaxTextures;

    public GpuShaderSlot* Shaders;
    public uint* ShaderGens;
    public int MaxShaders;

    // Built-in engine shader handles
    public ShaderHandle BuiltinPbrShader;
    public ShaderHandle BuiltinSpriteShader;
    public ShaderHandle BuiltinSdfTextShader;
    public ShaderHandle BuiltinParticleShader;

    public RenderStats LastFrameStats;
    public byte Initialized;
}

public static unsafe class GpuSystem
{
    public static GpuContext Create(
        nint window,
        ref LinearArena persistentArena,
        ref AssetRegistry assets,
        bool debugMode = false,
        int maxMeshes = 1024,
        int maxTextures = 512,
        int maxShaders = 64)
    {
        if (!ShaderCross.Init())
            throw new InvalidOperationException($"SDL_ShaderCross_Init failed: {SDL.GetError()}");

        SDL.GPUShaderFormat formats = ShaderCross.GetSPIRVShaderFormats();
        if (formats == 0)
            formats = SDL.GPUShaderFormat.SPIRV | SDL.GPUShaderFormat.DXIL | SDL.GPUShaderFormat.MSL;

        nint device = SDL.CreateGPUDevice(formats, debugMode, null);
        if (device == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUDevice failed: {SDL.GetError()}");

        if (!SDL.ClaimWindowForGPUDevice(device, window))
            throw new InvalidOperationException($"SDL_ClaimWindowForGPUDevice failed: {SDL.GetError()}");

        SDL.GPUTextureFormat swapFmt = SDL.GetGPUSwapchainTextureFormat(device, window);

        uint* shaderGens = persistentArena.AllocUninitialized<uint>(maxShaders, 16);
        for (int i = 0; i < maxShaders; i++) shaderGens[i] = 1;

        var ctx = new GpuContext
        {
            Device = device,
            Window = window,
            SwapchainFormat = swapFmt,
            DepthFormat = SDL.GPUTextureFormat.D32Float,
            Meshes = persistentArena.Alloc<GpuMeshSlot>(maxMeshes, 64),
            MaxMeshes = maxMeshes,
            Textures = persistentArena.Alloc<GpuTextureSlot>(maxTextures, 64),
            MaxTextures = maxTextures,
            Shaders = persistentArena.Alloc<GpuShaderSlot>(maxShaders, 64),
            ShaderGens = shaderGens,
            MaxShaders = maxShaders,
            Initialized = 1,
        };

        CreateDefaultSamplersAndBuffers(ref ctx);
        return ctx;
    }

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
    /// Overload taking explicit stable GpuContext* (preferred when GpuContext lives in native/fixed memory).
    /// </summary>
    public static void BindAssetCleanupHooks(GpuContext* ctxPtr, ref AssetRegistry assets)
    {
        assets.GpuContextPtr = ctxPtr;
        assets.ReleaseGpuMesh = &ReleaseGpuMeshHook;
        assets.ReleaseGpuTexture = &ReleaseGpuTextureHook;
    }

    private static void ReleaseGpuMeshHook(void* ctxPtr, int slot)
    {
        if (ctxPtr == null) return;
        ReleaseMeshSlot(ref *(GpuContext*)ctxPtr, slot);
    }

    private static void ReleaseGpuTextureHook(void* ctxPtr, int slot)
    {
        if (ctxPtr == null) return;
        ReleaseTextureSlot(ref *(GpuContext*)ctxPtr, slot);
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

        var linClamp = linWrap;
        linClamp.AddressModeU = SDL.GPUSamplerAddressMode.ClampToEdge;
        linClamp.AddressModeV = SDL.GPUSamplerAddressMode.ClampToEdge;
        linClamp.AddressModeW = SDL.GPUSamplerAddressMode.ClampToEdge;
        ctx.SamplerLinearClamp = SDL.CreateGPUSampler(ctx.Device, in linClamp);

        var nearClamp = linClamp;
        nearClamp.MinFilter = SDL.GPUFilter.Nearest;
        nearClamp.MagFilter = SDL.GPUFilter.Nearest;
        nearClamp.MipmapMode = SDL.GPUSamplerMipmapMode.Nearest;
        ctx.SamplerNearestClamp = SDL.CreateGPUSampler(ctx.Device, in nearClamp);

        // Allocate reusable 16MB dynamic upload transfer buffer
        ctx.UploadTransferBufferBytes = 16 * 1024 * 1024;
        var tbInfo = new SDL.GPUTransferBufferCreateInfo
        {
            Usage = SDL.GPUTransferBufferUsage.Upload,
            Size = ctx.UploadTransferBufferBytes,
        };
        ctx.UploadTransferBuffer = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);

        // Allocate per-frame storage buffers
        ctx.Instance3DBufferBytes = 4 * 1024 * 1024;       // ~28k 3D instances
        ctx.JointPaletteBufferBytes = 2 * 1024 * 1024;     // 32k joint matrices
        ctx.SpriteInstanceBufferBytes = 2 * 1024 * 1024;   // ~18k sprites
        ctx.TextGlyphBufferBytes = 4 * 1024 * 1024;        // ~32k text glyphs
        ctx.ParticleInstanceBufferBytes = 2 * 1024 * 1024; // ~32k particles

        ctx.Instance3DBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.Instance3DBufferBytes);
        ctx.JointPaletteBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.JointPaletteBufferBytes);
        ctx.SpriteInstanceBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.SpriteInstanceBufferBytes);
        ctx.TextGlyphBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.TextGlyphBufferBytes);
        ctx.ParticleInstanceBuffer = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.GraphicsStorageRead, ctx.ParticleInstanceBufferBytes);

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

        // Upload QuadIndexBuffer + DefaultWhiteTexture in one startup copy pass
        uint* quadIndices = stackalloc uint[6] { 0, 1, 2, 0, 2, 3 };
        uint whitePixel = 0xFFFF_FFFFu;

        nint mapPtr = SDL.MapGPUTransferBuffer(ctx.Device, ctx.UploadTransferBuffer, false);
        Buffer.MemoryCopy(quadIndices, (void*)mapPtr, 24, 24);
        *(uint*)((byte*)mapPtr + 64) = whitePixel;
        SDL.UnmapGPUTransferBuffer(ctx.Device, ctx.UploadTransferBuffer);

        nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
        nint copy = SDL.BeginGPUCopyPass(cmd);

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
        if (width == 0 || height == 0) return;
        if (ctx.DepthTexture != nint.Zero && ctx.DepthWidth == width && ctx.DepthHeight == height)
            return;

        if (ctx.DepthTexture != nint.Zero)
            SDL.ReleaseGPUTexture(ctx.Device, ctx.DepthTexture);

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
        ctx.DepthTexture = SDL.CreateGPUTexture(ctx.Device, in info);
        ctx.DepthWidth = width;
        ctx.DepthHeight = height;
    }

    public static void EnsureCaptureTarget(ref GpuContext ctx, uint width, uint height)
    {
        if (ctx.CaptureColorTexture != nint.Zero && ctx.CaptureWidth == width && ctx.CaptureHeight == height)
            return;

        if (ctx.CaptureColorTexture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.CaptureColorTexture);
        if (ctx.CaptureTransferBuffer != nint.Zero) SDL.ReleaseGPUTransferBuffer(ctx.Device, ctx.CaptureTransferBuffer);

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
        ctx.CaptureColorTexture = SDL.CreateGPUTexture(ctx.Device, in texInfo);

        var tbInfo = new SDL.GPUTransferBufferCreateInfo
        {
            Usage = SDL.GPUTransferBufferUsage.Download,
            Size = width * height * 4,
        };
        ctx.CaptureTransferBuffer = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);
        ctx.CaptureWidth = width;
        ctx.CaptureHeight = height;
    }

    /// <summary>
    /// Downloads CaptureColorTexture from the GPU and writes a PNG file to disk (for visual verification!).
    /// </summary>
    public static void SaveCaptureTextureToPng(ref GpuContext ctx, string outputPath)
    {
        if (ctx.CaptureColorTexture == nint.Zero) return;

        nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
        nint copy = SDL.BeginGPUCopyPass(cmd);
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
        nint* fenceArr = stackalloc nint[1] { fence };
        SDL.WaitForGPUFences(ctx.Device, true, (nint)fenceArr, 1);
        SDL.ReleaseGPUFence(ctx.Device, fence);

        nint mapped = SDL.MapGPUTransferBuffer(ctx.Device, ctx.CaptureTransferBuffer, false);
        int w = (int)ctx.CaptureWidth;
        int h = (int)ctx.CaptureHeight;
        int byteCount = w * h * 4;
        byte[] rgba = new byte[byteCount];
        bool isBgra = ctx.SwapchainFormat == SDL.GPUTextureFormat.B8G8R8A8Unorm ||
                      ctx.SwapchainFormat == SDL.GPUTextureFormat.B8G8R8A8UnormSRGB;

        byte* srcPtr = (byte*)mapped;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            if (isBgra)
            {
                rgba[o + 0] = srcPtr[o + 2];
                rgba[o + 1] = srcPtr[o + 1];
                rgba[o + 2] = srcPtr[o + 0];
                rgba[o + 3] = 255;
            }
            else
            {
                rgba[o + 0] = srcPtr[o + 0];
                rgba[o + 1] = srcPtr[o + 1];
                rgba[o + 2] = srcPtr[o + 2];
                rgba[o + 3] = 255;
            }
        }
        SDL.UnmapGPUTransferBuffer(ctx.Device, ctx.CaptureTransferBuffer);

        using var fs = File.Create(outputPath);
        var writer = new ImageWriter();
        writer.WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha, fs);
    }

    public static int EnsureMeshUploaded(ref GpuContext ctx, ref MeshAssetData mesh)
    {
        if (mesh.GpuSlot >= 0) return mesh.GpuSlot;
        if (mesh.VertexCount <= 0 || mesh.IndexCount <= 0) return -1;

        int slot = -1;
        for (int i = 0; i < ctx.MaxMeshes; i++)
        {
            if (ctx.Meshes[i].Alive == 0) { slot = i; break; }
        }
        if (slot < 0) throw new OutOfMemoryException("GpuContext mesh slots exhausted.");

        uint vBytes = (uint)(mesh.VertexCount * sizeof(Vertex3D));
        uint iBytes = (uint)(mesh.IndexCount * sizeof(uint));
        nint vb = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.Vertex, vBytes);
        nint ib = CreateBuffer(ctx.Device, SDL.GPUBufferUsageFlags.Index, iBytes);

        // One-shot upload transfer
        var tbInfo = new SDL.GPUTransferBufferCreateInfo { Usage = SDL.GPUTransferBufferUsage.Upload, Size = vBytes + iBytes };
        nint tb = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);
        byte* mapped = (byte*)SDL.MapGPUTransferBuffer(ctx.Device, tb, false);
        Buffer.MemoryCopy(mesh.Vertices, mapped, vBytes, vBytes);
        Buffer.MemoryCopy(mesh.Indices, mapped + vBytes, iBytes, iBytes);
        SDL.UnmapGPUTransferBuffer(ctx.Device, tb);

        nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
        nint copy = SDL.BeginGPUCopyPass(cmd);
        var vSrc = new SDL.GPUTransferBufferLocation { TransferBuffer = tb, Offset = 0 };
        var vDst = new SDL.GPUBufferRegion { Buffer = vb, Offset = 0, Size = vBytes };
        SDL.UploadToGPUBuffer(copy, in vSrc, in vDst, false);

        var iSrc = new SDL.GPUTransferBufferLocation { TransferBuffer = tb, Offset = vBytes };
        var iDst = new SDL.GPUBufferRegion { Buffer = ib, Offset = 0, Size = iBytes };
        SDL.UploadToGPUBuffer(copy, in iSrc, in iDst, false);
        SDL.EndGPUCopyPass(copy);
        SDL.SubmitGPUCommandBuffer(cmd);
        SDL.ReleaseGPUTransferBuffer(ctx.Device, tb);

        ctx.Meshes[slot] = new GpuMeshSlot
        {
            VertexBuffer = vb,
            IndexBuffer = ib,
            IndexCount = (uint)mesh.IndexCount,
            Alive = 1,
        };
        mesh.GpuSlot = slot;
        return slot;
    }

    public static int EnsureTextureUploaded(ref GpuContext ctx, ref TextureAssetData tex)
    {
        if (tex.GpuSlot >= 0) return tex.GpuSlot;
        if (tex.Width <= 0 || tex.Height <= 0 || tex.Pixels == null) return -1;

        int slot = -1;
        for (int i = 0; i < ctx.MaxTextures; i++)
        {
            if (ctx.Textures[i].Alive == 0) { slot = i; break; }
        }
        if (slot < 0) throw new OutOfMemoryException("GpuContext texture slots exhausted.");

        SDL.GPUTextureFormat fmt = tex.Channels == 1
            ? SDL.GPUTextureFormat.R8Unorm
            : SDL.GPUTextureFormat.R8G8B8A8Unorm;

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
        nint gpuTex = SDL.CreateGPUTexture(ctx.Device, in info);
        if (gpuTex == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateGPUTexture failed: {SDL.GetError()}");

        uint byteSize = (uint)(tex.Width * tex.Height * tex.Channels);
        var tbInfo = new SDL.GPUTransferBufferCreateInfo { Usage = SDL.GPUTransferBufferUsage.Upload, Size = byteSize };
        nint tb = SDL.CreateGPUTransferBuffer(ctx.Device, in tbInfo);
        void* mapped = (void*)SDL.MapGPUTransferBuffer(ctx.Device, tb, false);
        Buffer.MemoryCopy(tex.Pixels, mapped, byteSize, byteSize);
        SDL.UnmapGPUTransferBuffer(ctx.Device, tb);

        nint cmd = SDL.AcquireGPUCommandBuffer(ctx.Device);
        nint copy = SDL.BeginGPUCopyPass(cmd);
        var src = new SDL.GPUTextureTransferInfo
        {
            TransferBuffer = tb,
            Offset = 0,
            PixelsPerRow = (uint)tex.Width,
            RowsPerLayer = (uint)tex.Height,
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
        return slot;
    }

    public static void ReleaseMeshSlot(ref GpuContext ctx, int slot)
    {
        if ((uint)slot >= (uint)ctx.MaxMeshes || ctx.Meshes[slot].Alive == 0) return;
        if (ctx.Meshes[slot].VertexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.Meshes[slot].VertexBuffer);
        if (ctx.Meshes[slot].IndexBuffer != nint.Zero) SDL.ReleaseGPUBuffer(ctx.Device, ctx.Meshes[slot].IndexBuffer);
        ctx.Meshes[slot] = default;
    }

    public static void ReleaseTextureSlot(ref GpuContext ctx, int slot)
    {
        if ((uint)slot >= (uint)ctx.MaxTextures || ctx.Textures[slot].Alive == 0) return;
        if (ctx.Textures[slot].Texture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.Textures[slot].Texture);
        ctx.Textures[slot] = default;
    }

    public static void Dispose(ref GpuContext ctx)
    {
        if (ctx.Initialized == 0 || ctx.Device == nint.Zero) return;
        SDL.WaitForGPUIdle(ctx.Device);

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
            if (s.VertexShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, s.VertexShader);
            if (s.FragmentShader != nint.Zero) SDL.ReleaseGPUShader(ctx.Device, s.FragmentShader);
            s = default;
        }

        if (ctx.DepthTexture != nint.Zero) SDL.ReleaseGPUTexture(ctx.Device, ctx.DepthTexture);
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
        if (ctx.UploadTransferBuffer != nint.Zero) SDL.ReleaseGPUTransferBuffer(ctx.Device, ctx.UploadTransferBuffer);

        if (ctx.Window != nint.Zero) SDL.ReleaseWindowFromGPUDevice(ctx.Device, ctx.Window);
        SDL.DestroyGPUDevice(ctx.Device);
        ShaderCross.Quit();
        ctx.Device = nint.Zero;
        ctx.Initialized = 0;
    }
}
