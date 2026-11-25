using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;
using SDL3;

namespace BringEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal struct Instance3DGpu
{
    public Mat4 Model;
    public ColorF BaseColor;
    public Vec4 EmissiveRough; // xyz = emissive, w = roughness
    public Vec4 Extra;         // x = metallic, y = alphaCutoff, z = jointOffset (<0 static), w = pad
    public Vec4 CustomParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FrameUniformsGpu
{
    public Mat4 ViewProj;
    public Vec4 CameraPosTime;
    public Vec4 SunDirIntensity;
    public ColorF SunColor;
    public ColorF AmbientSky;
    public ColorF AmbientGround;
    public Vec4 FogColorDensity;
    public Vec4 PointLightPos0, PointLightPos1, PointLightPos2, PointLightPos3;
    public Vec4 PointLightColor0, PointLightColor1, PointLightColor2, PointLightColor3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpriteGpuInstance
{
    public Mat4 Model;
    public Vec4 UvRect;
    public ColorF Color;
    public Vec4 CustomParams; // x = alphaCutoff, y = hitFlash, z = time, w = pad
}

[StructLayout(LayoutKind.Sequential)]
internal struct ParticleGpuInstance
{
    public Vec4 PosSize;
    public ColorF Color;
    public Vec4 UvRect;
    public Vec4 RotExtra;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SimpleCameraUniformsGpu
{
    public Mat4 ViewProj;
    public Vec4 CameraPosTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ParticleCameraUniformsGpu
{
    public Mat4 ViewProj;
    public Vec4 CameraRightTime;
    public Vec4 CameraUpPad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BatchUniformsGpu
{
    public uint BaseInstance;
    public uint P1, P2, P3;
}

/// <summary>
/// High-performance, cache-friendly SDL3_gpu Multi-Camera Instanced Batch Renderer.
/// - Frustum-culls 3D meshes on CPU.
/// - Sorts & batches 3D meshes (instanced + skeletal skinning), 2D sprites in 3D, SDF text glyphs, and SoA particles in FrameArena.
/// - Issues ONE SDL_GPUCopyPass per frame before RenderPasses (zero pass interleaving).
/// - Deduplicates GPU pipeline/mesh/texture binds across batches.
/// </summary>
public static unsafe class BatchRenderer
{
    private struct Draw3DSortItem
    {
        public ulong SortKey;
        public int MeshGpuSlot;
        public int TextureGpuSlot;
        public ShaderHandle Shader;
        public MaterialAlphaMode AlphaMode;
        public Instance3DGpu Instance;
    }

    private struct Batch3DCmd
    {
        public int MeshGpuSlot;
        public int TextureGpuSlot;
        public ShaderHandle Shader;
        public MaterialAlphaMode AlphaMode;
        public uint BaseInstance;
        public uint InstanceCount;
    }

    private struct SpriteSortItem
    {
        public ulong SortKey;
        public int TextureGpuSlot;
        public ShaderHandle Shader;
        public SpriteBlendMode BlendMode;
        public byte DepthTest;
        public SpriteGpuInstance Instance;
    }

    private struct SpriteBatchCmd
    {
        public int TextureGpuSlot;
        public ShaderHandle Shader;
        public SpriteBlendMode BlendMode;
        public byte DepthTest;
        public uint BaseInstance;
        public uint InstanceCount;
    }

    private struct TextBatchCmd
    {
        public int AtlasTexGpuSlot;
        public ShaderHandle Shader;
        public byte DepthTest;
        public uint BaseInstance;
        public uint InstanceCount;
    }

    private struct ParticleBatchCmd
    {
        public int TextureGpuSlot;
        public ShaderHandle Shader;
        public ParticleBlendMode BlendMode;
        public uint BaseInstance;
        public uint InstanceCount;
    }

    private struct CameraRenderSlice
    {
        public CameraHandle Camera;
        public int Batch3DStart, Batch3DCount;
        public int SpriteBatchStart, SpriteBatchCount;
        public int TextBatchStart, TextBatchCount;
        public int ParticleBatchStart, ParticleBatchCount;
    }

    /// <summary>
    /// Renders all active cameras in DepthOrder to the swapchain (and optionally to CaptureColorTexture for PNG export).
    /// Pass a FrameProfiler* to measure RenderGather / GpuUpload / GpuEncode CPU scopes and real GPU fence execution time (ms).
    /// </summary>
    public static void RenderFrame(
        ref GpuContext gpu,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        in SceneLighting lighting,
        bool captureToOffscreen = false,
        FrameProfiler* profiler = null)
    {
        if (gpu.Initialized == 0) return;

        int gatherScope = profiler != null ? ProfilerSystem.BeginScope(ref *profiler, "RenderGather") : -1;

        // 1. Ensure newly loaded assets are uploaded to GPU
        UploadPendingAssets(ref gpu, ref world);

        // 2. Acquire command buffer and render target (swapchain or offscreen capture)
        nint cmd = SDL.AcquireGPUCommandBuffer(gpu.Device);
        if (cmd == nint.Zero)
        {
            if (gatherScope >= 0) ProfilerSystem.EndScope(ref *profiler, gatherScope);
            return;
        }

        nint colorTarget;
        uint targetW, targetH;

        if (captureToOffscreen)
        {
            targetW = gpu.CaptureWidth > 0 ? gpu.CaptureWidth : 1280u;
            targetH = gpu.CaptureHeight > 0 ? gpu.CaptureHeight : 720u;
            GpuSystem.EnsureCaptureTarget(ref gpu, targetW, targetH);
            colorTarget = gpu.CaptureColorTexture;
        }
        else
        {
            if (!SDL.WaitAndAcquireGPUSwapchainTexture(cmd, gpu.Window, out colorTarget, out targetW, out targetH) || colorTarget == nint.Zero)
            {
                SDL.SubmitGPUCommandBuffer(cmd);
                if (gatherScope >= 0) ProfilerSystem.EndScope(ref *profiler, gatherScope);
                return;
            }
        }

        GpuSystem.EnsureDepthTexture(ref gpu, targetW, targetH);

        // 3. Gather, frustum-cull, sort, and batch ALL cameras into FrameArena (ZERO GC allocations)
        RenderStats stats = default;
        var sortedCameras = CameraSystem.GetSortedActiveCameras(in world.Cameras, ref alloc.Frame);
        if (sortedCameras.Length == 0)
        {
            // Clear screen even if no camera exists
            var ct = new SDL.GPUColorTargetInfo
            {
                Texture = colorTarget,
                ClearColor = new SDL.FColor { R = 0.05f, G = 0.07f, B = 0.12f, A = 1f },
                LoadOp = SDL.GPULoadOp.Clear,
                StoreOp = SDL.GPUStoreOp.Store,
            };
            nint rp = SDL.BeginGPURenderPass(cmd, (nint)(&ct), 1, nint.Zero);
            SDL.EndGPURenderPass(rp);
            SDL.SubmitGPUCommandBuffer(cmd);
            if (gatherScope >= 0) ProfilerSystem.EndScope(ref *profiler, gatherScope);
            return;
        }

        int camCount = sortedCameras.Length;
        int cap3D = Math.Clamp(world.RenderComponents.MeshRendererCount * camCount, 64, 16384);
        int capSprites = Math.Clamp(world.RenderComponents.SpriteCount * camCount, 64, 8192);
        int capGlyphs = Math.Clamp(world.RenderComponents.TextCount * 160 * camCount, 128, 16384);
        int capParticles = Math.Clamp(world.Particles.ParticleCount * camCount, 64, 16384);

        var camSlices = alloc.Frame.AllocSlice<CameraRenderSlice>(camCount);
        var allInstances3D = NativeList<Instance3DGpu>.CreateInArena(ref alloc.Frame, cap3D, 64);
        var allBatches3D = NativeList<Batch3DCmd>.CreateInArena(ref alloc.Frame, Math.Min(cap3D, 4096), 16);
        var allSprites = NativeList<SpriteGpuInstance>.CreateInArena(ref alloc.Frame, capSprites, 64);
        var allSpriteBatches = NativeList<SpriteBatchCmd>.CreateInArena(ref alloc.Frame, Math.Min(capSprites, 2048), 16);
        var allGlyphs = NativeList<GlyphGpuInstance>.CreateInArena(ref alloc.Frame, capGlyphs, 64);
        var allTextBatches = NativeList<TextBatchCmd>.CreateInArena(ref alloc.Frame, Math.Max(64, world.RenderComponents.TextCount * camCount), 16);
        var allParticles = NativeList<ParticleGpuInstance>.CreateInArena(ref alloc.Frame, capParticles, 64);
        var allParticleBatches = NativeList<ParticleBatchCmd>.CreateInArena(ref alloc.Frame, Math.Max(64, world.Particles.ActiveEmitters * camCount), 16);

        for (int ci = 0; ci < sortedCameras.Length; ci++)
        {
            CameraData* camPtr = CameraSystem.TryGetPtr(in world.Cameras, sortedCameras[ci]);
            if (camPtr == null) continue;
            ref CameraData cam = ref *camPtr;

            ref CameraRenderSlice slice = ref camSlices[ci];
            slice.Camera = cam.Handle;

            // --- 3A. Gather & Batch 3D Meshes ---
            slice.Batch3DStart = allBatches3D.Count;
            GatherAndBatch3D(in gpu, in world, in cam, ref alloc.Frame, ref allInstances3D, ref allBatches3D, ref stats);
            slice.Batch3DCount = allBatches3D.Count - slice.Batch3DStart;

            // --- 3B. Gather & Batch 2D Sprites in 3D/HUD ---
            slice.SpriteBatchStart = allSpriteBatches.Count;
            GatherAndBatchSprites(in gpu, in world, in cam, ref alloc.Frame, ref allSprites, ref allSpriteBatches, ref stats);
            slice.SpriteBatchCount = allSpriteBatches.Count - slice.SpriteBatchStart;

            // --- 3C. Gather, Layout & Batch Advanced SDF Text ---
            slice.TextBatchStart = allTextBatches.Count;
            GatherAndBatchText(in gpu, in world, in cam, ref alloc.Scratch, ref allGlyphs, ref allTextBatches, ref stats);
            slice.TextBatchCount = allTextBatches.Count - slice.TextBatchStart;

            // --- 3D. Gather & Batch SoA Particles ---
            slice.ParticleBatchStart = allParticleBatches.Count;
            GatherAndBatchParticles(in gpu, in world, in cam, ref allParticles, ref allParticleBatches, ref stats);
            slice.ParticleBatchCount = allParticleBatches.Count - slice.ParticleBatchStart;
        }

        if (gatherScope >= 0) ProfilerSystem.EndScope(ref *profiler, gatherScope);

        // 4. SINGLE SDL_GPUCopyPass uploading ALL dynamic buffers for the whole frame!
        int uploadScope = profiler != null ? ProfilerSystem.BeginScope(ref *profiler, "GpuUpload") : -1;
        if (UploadFrameBuffersInSingleCopyPass(
            ref gpu, cmd,
            in allInstances3D,
            in world.Animations.FrameJointPalette,
            in allSprites,
            in allGlyphs,
            in allParticles))
        {
            stats.CopyPasses++;
        }
        if (uploadScope >= 0) ProfilerSystem.EndScope(ref *profiler, uploadScope);

        // 5. Execute RenderPass per active camera in DepthOrder (with GPU state deduplication)
        int encodeScope = profiler != null ? ProfilerSystem.BeginScope(ref *profiler, "GpuEncode") : -1;
        nint* vStorage = stackalloc nint[2] { gpu.Instance3DBuffer, gpu.JointPaletteBuffer };
        nint quadIndexBuf = gpu.QuadIndexBuffer;

        for (int ci = 0; ci < sortedCameras.Length; ci++)
        {
            CameraData* camPtr = CameraSystem.TryGetPtr(in world.Cameras, camSlices[ci].Camera);
            if (camPtr == null) continue;
            ref CameraData cam = ref *camPtr;
            ref CameraRenderSlice slice = ref camSlices[ci];

            SDL.GPULoadOp colorLoad = (ci == 0 || cam.ClearFlags == CameraClearFlags.SolidColor)
                ? SDL.GPULoadOp.Clear : SDL.GPULoadOp.Load;
            SDL.GPULoadOp depthLoad = (ci == 0 || cam.ClearFlags == CameraClearFlags.SolidColor || cam.ClearFlags == CameraClearFlags.DepthOnly)
                ? SDL.GPULoadOp.Clear : SDL.GPULoadOp.Load;

            var colorInfo = new SDL.GPUColorTargetInfo
            {
                Texture = colorTarget,
                ClearColor = new SDL.FColor { R = cam.ClearColor.R, G = cam.ClearColor.G, B = cam.ClearColor.B, A = cam.ClearColor.A },
                LoadOp = colorLoad,
                StoreOp = SDL.GPUStoreOp.Store,
            };
            var depthInfo = new SDL.GPUDepthStencilTargetInfo
            {
                Texture = gpu.DepthTexture,
                ClearDepth = 1.0f,
                LoadOp = depthLoad,
                StoreOp = SDL.GPUStoreOp.Store,
                StencilLoadOp = SDL.GPULoadOp.DontCare,
                StencilStoreOp = SDL.GPUStoreOp.DontCare,
            };

            nint pass = SDL.BeginGPURenderPass(cmd, (nint)(&colorInfo), 1, in depthInfo);
            stats.RenderPasses++;

            var vp = new SDL.GPUViewport
            {
                X = cam.ViewportRect.X * targetW,
                Y = cam.ViewportRect.Y * targetH,
                W = cam.ViewportRect.Z * targetW,
                H = cam.ViewportRect.W * targetH,
                MinDepth = 0f,
                MaxDepth = 1f,
            };
            SDL.SetGPUViewport(pass, in vp);

            nint boundPipeline = nint.Zero;
            int boundMeshSlot = -1;
            int boundTexSlot = int.MinValue;

            // --- Draw 3D Instanced Batches ---
            if (slice.Batch3DCount > 0)
            {
                var fu = BuildFrameUniforms(in cam, in lighting, world.TotalTime);
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&fu), (uint)sizeof(FrameUniformsGpu));
                SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&fu), (uint)sizeof(FrameUniformsGpu));

                SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(vStorage, 2), 2);

                for (int bi = 0; bi < slice.Batch3DCount; bi++)
                {
                    Batch3DCmd b = allBatches3D[slice.Batch3DStart + bi];
                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinPbrShader;
                    GpuShaderSlot* sSlot = ShaderSystem.TryGetShader(in gpu, sh);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = b.AlphaMode == MaterialAlphaMode.Blend ? sSlot->PipelineAlphaBlend : sSlot->PipelineOpaque;
                    if (pipe != boundPipeline)
                    {
                        SDL.BindGPUGraphicsPipeline(pass, pipe);
                        boundPipeline = pipe;
                        stats.PipelineBinds++;
                        passChanged = true;
                    }

                    if (b.MeshGpuSlot != boundMeshSlot)
                    {
                        ref GpuMeshSlot gm = ref gpu.Meshes[b.MeshGpuSlot];
                        var vbBind = new SDL.GPUBufferBinding { Buffer = gm.VertexBuffer, Offset = 0 };
                        var ibBind = new SDL.GPUBufferBinding { Buffer = gm.IndexBuffer, Offset = 0 };
                        SDL.BindGPUVertexBuffers(pass, 0, (nint)(&vbBind), 1);
                        SDL.BindGPUIndexBuffer(pass, in ibBind, SDL.GPUIndexElementSize.IndexElementSize32Bit);
                        boundMeshSlot = b.MeshGpuSlot;
                    }

                    if (b.TextureGpuSlot != boundTexSlot || passChanged)
                    {
                        BindFragmentTexture(pass, in gpu, b.TextureGpuSlot);
                        boundTexSlot = b.TextureGpuSlot;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    uint idxCount = gpu.Meshes[b.MeshGpuSlot].IndexCount;
                    SDL.DrawGPUIndexedPrimitives(pass, idxCount, b.InstanceCount, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(idxCount / 3 * b.InstanceCount);
                }
            }

            // Shared index buffer for instanced quads (Sprites, Text, Particles)
            bool quadIndexBound = false;
            void EnsureQuadIndexBound()
            {
                if (quadIndexBound) return;
                var qib = new SDL.GPUBufferBinding { Buffer = quadIndexBuf, Offset = 0 };
                SDL.BindGPUIndexBuffer(pass, in qib, SDL.GPUIndexElementSize.IndexElementSize32Bit);
                quadIndexBound = true;
                boundMeshSlot = -1;
            }

            // --- Draw 2D Sprites (in 3D World or Screen HUD) ---
            if (slice.SpriteBatchCount > 0)
            {
                EnsureQuadIndexBound();
                var cu = new SimpleCameraUniformsGpu { ViewProj = cam.ViewProjMatrix, CameraPosTime = new Vec4(cam.ShakenPosition, world.TotalTime) };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&cu), (uint)sizeof(SimpleCameraUniformsGpu));
                nint sb = gpu.SpriteInstanceBuffer;
                SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(&sb, 1), 1);

                for (int bi = 0; bi < slice.SpriteBatchCount; bi++)
                {
                    SpriteBatchCmd b = allSpriteBatches[slice.SpriteBatchStart + bi];
                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinSpriteShader;
                    GpuShaderSlot* sSlot = ShaderSystem.TryGetShader(in gpu, sh);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = SelectQuadPipeline(sSlot, b.BlendMode == SpriteBlendMode.Additive ? 2 : (b.BlendMode == SpriteBlendMode.AlphaCutout ? 0 : 1), b.DepthTest != 0);
                    if (pipe != boundPipeline)
                    {
                        SDL.BindGPUGraphicsPipeline(pass, pipe);
                        boundPipeline = pipe;
                        stats.PipelineBinds++;
                        passChanged = true;
                    }

                    if (b.TextureGpuSlot != boundTexSlot || passChanged)
                    {
                        BindFragmentTexture(pass, in gpu, b.TextureGpuSlot);
                        boundTexSlot = b.TextureGpuSlot;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    SDL.DrawGPUIndexedPrimitives(pass, 6, b.InstanceCount, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(2 * b.InstanceCount);
                }
            }

            // --- Draw Batched Particles ---
            if (slice.ParticleBatchCount > 0)
            {
                EnsureQuadIndexBound();
                var pu = new ParticleCameraUniformsGpu
                {
                    ViewProj = cam.ViewProjMatrix,
                    CameraRightTime = new Vec4(cam.Right, world.TotalTime),
                    CameraUpPad = new Vec4(cam.CameraUp, 0f),
                };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&pu), (uint)sizeof(ParticleCameraUniformsGpu));
                nint pb = gpu.ParticleInstanceBuffer;
                SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(&pb, 1), 1);

                for (int bi = 0; bi < slice.ParticleBatchCount; bi++)
                {
                    ParticleBatchCmd b = allParticleBatches[slice.ParticleBatchStart + bi];
                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinParticleShader;
                    GpuShaderSlot* sSlot = ShaderSystem.TryGetShader(in gpu, sh);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = b.BlendMode == ParticleBlendMode.Additive ? sSlot->PipelineAdditive : sSlot->PipelineAlphaBlend;
                    if (pipe != boundPipeline)
                    {
                        SDL.BindGPUGraphicsPipeline(pass, pipe);
                        boundPipeline = pipe;
                        stats.PipelineBinds++;
                        passChanged = true;
                    }

                    if (b.TextureGpuSlot != boundTexSlot || passChanged)
                    {
                        BindFragmentTexture(pass, in gpu, b.TextureGpuSlot);
                        boundTexSlot = b.TextureGpuSlot;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    SDL.DrawGPUIndexedPrimitives(pass, 6, b.InstanceCount, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(2 * b.InstanceCount);
                }
            }

            // --- Draw Advanced Animated/Shadered SDF Text ---
            if (slice.TextBatchCount > 0)
            {
                EnsureQuadIndexBound();
                var cu = new SimpleCameraUniformsGpu { ViewProj = cam.ViewProjMatrix, CameraPosTime = new Vec4(cam.ShakenPosition, world.TotalTime) };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&cu), (uint)sizeof(SimpleCameraUniformsGpu));
                nint tb = gpu.TextGlyphBuffer;
                SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(&tb, 1), 1);

                for (int bi = 0; bi < slice.TextBatchCount; bi++)
                {
                    TextBatchCmd b = allTextBatches[slice.TextBatchStart + bi];
                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinSdfTextShader;
                    GpuShaderSlot* sSlot = ShaderSystem.TryGetShader(in gpu, sh);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = b.DepthTest != 0 ? sSlot->PipelineAlphaBlend : sSlot->PipelineOverlayNoDepth;
                    if (pipe != boundPipeline)
                    {
                        SDL.BindGPUGraphicsPipeline(pass, pipe);
                        boundPipeline = pipe;
                        stats.PipelineBinds++;
                        passChanged = true;
                    }

                    if (b.AtlasTexGpuSlot != boundTexSlot || passChanged)
                    {
                        BindFragmentTexture(pass, in gpu, b.AtlasTexGpuSlot);
                        boundTexSlot = b.AtlasTexGpuSlot;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    SDL.DrawGPUIndexedPrimitives(pass, 6, b.InstanceCount, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(2 * b.InstanceCount);
                }
            }

            SDL.EndGPURenderPass(pass);
        }

        if (encodeScope >= 0) ProfilerSystem.EndScope(ref *profiler, encodeScope);

        // 6. Submit command buffer (with fence GPU time measurement when profiler is enabled)
        if (profiler != null && profiler->Enabled != 0 && profiler->MeasureGpuWithFence != 0)
        {
            long gpuStart = Stopwatch.GetTimestamp();
            nint fence = SDL.SubmitGPUCommandBufferAndAcquireFence(cmd);
            if (fence != nint.Zero)
            {
                nint* fArr = stackalloc nint[1] { fence };
                SDL.WaitForGPUFences(gpu.Device, true, (nint)fArr, 1);
                SDL.ReleaseGPUFence(gpu.Device, fence);
            }
            long gpuEnd = Stopwatch.GetTimestamp();
            long gpuTicks = gpuEnd - gpuStart;
            double gpuMs = gpuTicks * (1000.0 / Stopwatch.Frequency);
            ProfilerSystem.RecordGpuTime(ref *profiler, gpuMs, gpuTicks);
        }
        else
        {
            SDL.SubmitGPUCommandBuffer(cmd);
        }

        stats.FrameArenaUsedBytes = alloc.Frame.Offset;
        gpu.LastFrameStats = stats;
    }

    private static void UploadPendingAssets(ref GpuContext gpu, ref EngineWorld world)
    {
        for (int i = 0; i < world.Assets.MaxMeshes; i++)
        {
            if (world.Assets.MeshAlive[i] != 0 && world.Assets.Meshes[i].GpuSlot < 0)
                GpuSystem.EnsureMeshUploaded(ref gpu, ref world.Assets.Meshes[i]);
        }
        for (int i = 0; i < world.Assets.MaxTextures; i++)
        {
            if (world.Assets.TextureAlive[i] != 0 && world.Assets.Textures[i].GpuSlot < 0)
                GpuSystem.EnsureTextureUploaded(ref gpu, ref world.Assets.Textures[i]);
        }
    }

    private static bool UploadFrameBuffersInSingleCopyPass(
        ref GpuContext gpu,
        nint cmd,
        in NativeList<Instance3DGpu> instances3D,
        in NativeSlice<Mat4> jointPalette,
        in NativeList<SpriteGpuInstance> sprites,
        in NativeList<GlyphGpuInstance> glyphs,
        in NativeList<ParticleGpuInstance> particles)
    {
        uint bytes3D = (uint)(instances3D.Count * sizeof(Instance3DGpu));
        uint bytesJoints = (uint)(jointPalette.Length * sizeof(Mat4));
        uint bytesSprites = (uint)(sprites.Count * sizeof(SpriteGpuInstance));
        uint bytesGlyphs = (uint)(glyphs.Count * sizeof(GlyphGpuInstance));
        uint bytesParticles = (uint)(particles.Count * sizeof(ParticleGpuInstance));

        // Clamp safely against GPU buffer capacities
        if (bytes3D > gpu.Instance3DBufferBytes) bytes3D = gpu.Instance3DBufferBytes;
        if (bytesJoints > gpu.JointPaletteBufferBytes) bytesJoints = gpu.JointPaletteBufferBytes;
        if (bytesSprites > gpu.SpriteInstanceBufferBytes) bytesSprites = gpu.SpriteInstanceBufferBytes;
        if (bytesGlyphs > gpu.TextGlyphBufferBytes) bytesGlyphs = gpu.TextGlyphBufferBytes;
        if (bytesParticles > gpu.ParticleInstanceBufferBytes) bytesParticles = gpu.ParticleInstanceBufferBytes;

        uint off3D = 0;
        uint offJoints = (uint)LinearArena.AlignUp(off3D + bytes3D, 64);
        uint offSprites = (uint)LinearArena.AlignUp(offJoints + bytesJoints, 64);
        uint offGlyphs = (uint)LinearArena.AlignUp(offSprites + bytesSprites, 64);
        uint offParticles = (uint)LinearArena.AlignUp(offGlyphs + bytesGlyphs, 64);
        uint totalNeeded = offParticles + bytesParticles;

        if (totalNeeded == 0) return false;

        byte* mapped = (byte*)SDL.MapGPUTransferBuffer(gpu.Device, gpu.UploadTransferBuffer, cycle: true);
        if (bytes3D > 0) Buffer.MemoryCopy(instances3D.Data, mapped + off3D, bytes3D, bytes3D);
        if (bytesJoints > 0) Buffer.MemoryCopy(jointPalette.Ptr, mapped + offJoints, bytesJoints, bytesJoints);
        if (bytesSprites > 0) Buffer.MemoryCopy(sprites.Data, mapped + offSprites, bytesSprites, bytesSprites);
        if (bytesGlyphs > 0) Buffer.MemoryCopy(glyphs.Data, mapped + offGlyphs, bytesGlyphs, bytesGlyphs);
        if (bytesParticles > 0) Buffer.MemoryCopy(particles.Data, mapped + offParticles, bytesParticles, bytesParticles);
        SDL.UnmapGPUTransferBuffer(gpu.Device, gpu.UploadTransferBuffer);

        nint uploadTb = gpu.UploadTransferBuffer;
        nint copy = SDL.BeginGPUCopyPass(cmd);
        void CopySub(nint dstBuf, uint srcOff, uint size)
        {
            if (size == 0) return;
            var src = new SDL.GPUTransferBufferLocation { TransferBuffer = uploadTb, Offset = srcOff };
            var dst = new SDL.GPUBufferRegion { Buffer = dstBuf, Offset = 0, Size = size };
            SDL.UploadToGPUBuffer(copy, in src, in dst, cycle: true);
        }
        CopySub(gpu.Instance3DBuffer, off3D, bytes3D);
        CopySub(gpu.JointPaletteBuffer, offJoints, bytesJoints);
        CopySub(gpu.SpriteInstanceBuffer, offSprites, bytesSprites);
        CopySub(gpu.TextGlyphBuffer, offGlyphs, bytesGlyphs);
        CopySub(gpu.ParticleInstanceBuffer, offParticles, bytesParticles);
        SDL.EndGPUCopyPass(copy);
        return true;
    }

    private static void GatherAndBatch3D(
        in GpuContext gpu,
        in EngineWorld world,
        in CameraData cam,
        ref LinearArena frameArena,
        ref NativeList<Instance3DGpu> outInstances,
        ref NativeList<Batch3DCmd> outBatches,
        ref RenderStats stats)
    {
        int count = world.RenderComponents.MeshRendererCount;
        if (count == 0) return;

        var items = NativeList<Draw3DSortItem>.CreateInArena(ref frameArena, count, 64);

        for (int i = 0; i < count; i++)
        {
            ref MeshRendererData mr = ref world.RenderComponents.MeshRenderers[i];
            if (mr.Visible == 0 || (mr.LayerMask & cam.LayerMask) == 0) continue;

            MeshAssetData* mesh = AssetSystem.TryGetMesh(in world.Assets, mr.Mesh);
            if (mesh == null || mesh->GpuSlot < 0) continue;

            int td = TransformSystem.GetDenseIndex(in world.Transforms, mr.Entity);
            if (td < 0) continue;
            Mat4 worldMat = world.Transforms.WorldMatrix[td];

            // CPU Frustum Culling
            Bounds3 worldBounds = mesh->LocalBounds.Transform(in worldMat);
            if (!cam.Frustum.IntersectsBounds(in worldBounds))
            {
                stats.CulledMeshes++;
                continue;
            }

            MaterialAssetData* mat = AssetSystem.TryGetMaterial(in world.Assets, mr.Material);
            ColorF baseCol = mat != null
                ? new ColorF(mat->BaseColorFactor.R * mr.Tint.R, mat->BaseColorFactor.G * mr.Tint.G, mat->BaseColorFactor.B * mr.Tint.B, mat->BaseColorFactor.A * mr.Tint.A)
                : mr.Tint;

            int texSlot = -1;
            if (mat != null && mat->BaseColorTexture.IsValid)
            {
                TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, mat->BaseColorTexture);
                if (tex != null) texSlot = tex->GpuSlot;
            }

            int jointOff = -1;
            if (mesh->HasSkinning != 0 && (uint)mr.AnimPlayerIndex < (uint)world.Animations.PlayerCount)
                jointOff = world.Animations.Players[mr.AnimPlayerIndex].FrameJointOffset;

            MaterialAlphaMode alphaMode = mat != null ? mat->AlphaMode : MaterialAlphaMode.Opaque;
            ShaderHandle shader = (mat != null && mat->CustomShader.IsValid) ? mat->CustomShader : gpu.BuiltinPbrShader;

            ulong key = ((ulong)alphaMode << 58)
                      | ((ulong)(shader.Raw & 0xFFFFu) << 40)
                      | ((ulong)((uint)(texSlot + 1) & 0xFFFFu) << 20)
                      | ((ulong)((uint)mesh->GpuSlot & 0xFFFFFu));

            items.Add(new Draw3DSortItem
            {
                SortKey = key,
                MeshGpuSlot = mesh->GpuSlot,
                TextureGpuSlot = texSlot,
                Shader = shader,
                AlphaMode = alphaMode,
                Instance = new Instance3DGpu
                {
                    Model = worldMat,
                    BaseColor = baseCol,
                    EmissiveRough = new Vec4(mat != null ? mat->EmissiveFactor : Vec3.Zero, mat != null ? mat->RoughnessFactor : 0.6f),
                    Extra = new Vec4(mat != null ? mat->MetallicFactor : 0.1f, alphaMode == MaterialAlphaMode.Mask && mat != null ? mat->AlphaCutoff : 0f, jointOff, 0f),
                    CustomParams = mat != null ? mat->CustomParams : Vec4.Zero,
                },
            });
        }

        if (items.Count == 0) return;

        // Sort items by SortKey (cache-friendly in-place quicksort)
        Sort3DItems(items.Data, 0, items.Count - 1);

        // Merge consecutive identical (AlphaMode, Shader, Texture, Mesh) into single instanced draw calls!
        int idx = 0;
        while (idx < items.Count)
        {
            ulong batchKey = items[idx].SortKey;
            uint baseInst = (uint)outInstances.Count;
            uint instCount = 0;

            while (idx < items.Count && items[idx].SortKey == batchKey && outInstances.Count < outInstances.Capacity)
            {
                outInstances.Add(items[idx].Instance);
                instCount++;
                idx++;
            }

            if (instCount > 0 && outBatches.Count < outBatches.Capacity)
            {
                ref Draw3DSortItem rep = ref items[idx - 1];
                outBatches.Add(new Batch3DCmd
                {
                    MeshGpuSlot = rep.MeshGpuSlot,
                    TextureGpuSlot = rep.TextureGpuSlot,
                    Shader = rep.Shader,
                    AlphaMode = rep.AlphaMode,
                    BaseInstance = baseInst,
                    InstanceCount = instCount,
                });
                stats.MeshInstances += (int)instCount;
            }
        }
    }

    private static void GatherAndBatchSprites(
        in GpuContext gpu,
        in EngineWorld world,
        in CameraData cam,
        ref LinearArena frameArena,
        ref NativeList<SpriteGpuInstance> outSprites,
        ref NativeList<SpriteBatchCmd> outBatches,
        ref RenderStats stats)
    {
        int count = world.RenderComponents.SpriteCount;
        if (count == 0) return;

        var items = NativeList<SpriteSortItem>.CreateInArena(ref frameArena, count, 64);

        for (int i = 0; i < count; i++)
        {
            ref SpriteData sp = ref world.RenderComponents.Sprites[i];
            if (sp.Visible == 0 || (sp.LayerMask & cam.LayerMask) == 0) continue;

            int td = TransformSystem.GetDenseIndex(in world.Transforms, sp.Entity);
            if (td < 0) continue;

            Mat4 worldMat = world.Transforms.WorldMatrix[td];
            Vec3 pos = world.Transforms.WorldPosition[td];
            Vec3 right, up, fwd;

            if (sp.Billboard == SpriteBillboardMode.Spherical)
            {
                Vec3 scl = world.Transforms.LocalScale[td];
                right = cam.Right * (sp.Size.X * scl.X);
                up = cam.CameraUp * (sp.Size.Y * scl.Y);
                fwd = -cam.Forward;
            }
            else if (sp.Billboard == SpriteBillboardMode.CylindricalY)
            {
                Vec3 scl = world.Transforms.LocalScale[td];
                Vec3 toCam = cam.ShakenPosition - pos;
                toCam.Y = 0f;
                fwd = toCam.LengthSq() > 1e-6f ? toCam.Normalized() : Vec3.UnitZ;
                right = Vec3.Cross(Vec3.Up, fwd).Normalized() * (sp.Size.X * scl.X);
                up = Vec3.Up * (sp.Size.Y * scl.Y);
            }
            else
            {
                right = new Vec3(worldMat.C0.X, worldMat.C0.Y, worldMat.C0.Z) * sp.Size.X;
                up = new Vec3(worldMat.C1.X, worldMat.C1.Y, worldMat.C1.Z) * sp.Size.Y;
                fwd = new Vec3(worldMat.C2.X, worldMat.C2.Y, worldMat.C2.Z);
            }

            Vec3 origin = pos - right * sp.Pivot.X - up * sp.Pivot.Y;
            Mat4 quadMat = new Mat4
            {
                C0 = new Vec4(right, 0f),
                C1 = new Vec4(up, 0f),
                C2 = new Vec4(fwd, 0f),
                C3 = new Vec4(origin, 1f),
            };

            Vec4 uv = sp.UvRect;
            if (sp.FlipX != 0) { uv.X += uv.Z; uv.Z = -uv.Z; }
            if (sp.FlipY != 0) { uv.Y += uv.W; uv.W = -uv.W; }

            int texSlot = -1;
            if (sp.Texture.IsValid)
            {
                TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, sp.Texture);
                if (tex != null) texSlot = tex->GpuSlot;
            }

            ShaderHandle sh = sp.CustomShader.IsValid ? sp.CustomShader : gpu.BuiltinSpriteShader;
            uint sortLayerU = (uint)(sp.SortingLayer + 32768);
            uint orderU = (uint)(sp.OrderInLayer + 32768);
            ulong key = ((ulong)sortLayerU << 48)
                      | ((ulong)orderU << 32)
                      | ((ulong)sp.DepthTest << 30)
                      | ((ulong)sp.BlendMode << 28)
                      | ((ulong)(sh.Raw & 0xFFFu) << 16)
                      | ((ulong)((uint)(texSlot + 1) & 0xFFFFu));

            Vec4 cp = sp.CustomParams;
            if (sp.BlendMode == SpriteBlendMode.AlphaCutout && cp.X <= 0f) cp.X = 0.5f;

            items.Add(new SpriteSortItem
            {
                SortKey = key,
                TextureGpuSlot = texSlot,
                Shader = sh,
                BlendMode = sp.BlendMode,
                DepthTest = sp.DepthTest,
                Instance = new SpriteGpuInstance
                {
                    Model = quadMat,
                    UvRect = uv,
                    Color = sp.Color,
                    CustomParams = cp,
                },
            });
        }

        if (items.Count == 0) return;
        SortSpriteItems(items.Data, 0, items.Count - 1);

        int idx = 0;
        while (idx < items.Count)
        {
            ref SpriteSortItem first = ref items[idx];
            uint baseInst = (uint)outSprites.Count;
            uint instCount = 0;

            while (idx < items.Count &&
                   items[idx].TextureGpuSlot == first.TextureGpuSlot &&
                   items[idx].Shader == first.Shader &&
                   items[idx].BlendMode == first.BlendMode &&
                   items[idx].DepthTest == first.DepthTest &&
                   outSprites.Count < outSprites.Capacity)
            {
                outSprites.Add(items[idx].Instance);
                instCount++;
                idx++;
            }

            if (instCount > 0 && outBatches.Count < outBatches.Capacity)
            {
                outBatches.Add(new SpriteBatchCmd
                {
                    TextureGpuSlot = first.TextureGpuSlot,
                    Shader = first.Shader,
                    BlendMode = first.BlendMode,
                    DepthTest = first.DepthTest,
                    BaseInstance = baseInst,
                    InstanceCount = instCount,
                });
                stats.SpriteInstances += (int)instCount;
            }
        }
    }

    private static void GatherAndBatchText(
        in GpuContext gpu,
        in EngineWorld world,
        in CameraData cam,
        ref LinearArena scratchArena,
        ref NativeList<GlyphGpuInstance> outGlyphs,
        ref NativeList<TextBatchCmd> outBatches,
        ref RenderStats stats)
    {
        int count = world.RenderComponents.TextCount;
        if (count == 0) return;
        int batchStart = outBatches.Count;

        for (int i = 0; i < count; i++)
        {
            ref TextData td = ref world.RenderComponents.Texts[i];
            if (td.Visible == 0 || (td.LayerMask & cam.LayerMask) == 0) continue;

            FontAssetData* font = AssetSystem.TryGetFont(in world.Assets, td.Font);
            if (font == null) continue;
            TextureAssetData* atlas = AssetSystem.TryGetTexture(in world.Assets, font->AtlasTexture);
            if (atlas == null || atlas->GpuSlot < 0) continue;

            uint baseGlyph = (uint)outGlyphs.Count;
            int emitted = TextLayoutSystem.LayoutTextIntoGlyphs(in td, in *font, in world.Transforms, in cam, ref scratchArena, ref outGlyphs);
            if (emitted <= 0) continue;

            ShaderHandle sh = td.CustomShader.IsValid ? td.CustomShader : gpu.BuiltinSdfTextShader;

            // Merge with previous batch within THIS camera if identical font atlas + shader + depthTest
            if (outBatches.Count > batchStart)
            {
                ref TextBatchCmd prev = ref outBatches[outBatches.Count - 1];
                if (prev.AtlasTexGpuSlot == atlas->GpuSlot && prev.Shader == sh && prev.DepthTest == td.DepthTest)
                {
                    prev.InstanceCount += (uint)emitted;
                    stats.GlyphInstances += emitted;
                    continue;
                }
            }

            if (outBatches.Count < outBatches.Capacity)
            {
                outBatches.Add(new TextBatchCmd
                {
                    AtlasTexGpuSlot = atlas->GpuSlot,
                    Shader = sh,
                    DepthTest = td.DepthTest,
                    BaseInstance = baseGlyph,
                    InstanceCount = (uint)emitted,
                });
                stats.GlyphInstances += emitted;
            }
        }
    }

    private static void GatherAndBatchParticles(
        in GpuContext gpu,
        in EngineWorld world,
        in CameraData cam,
        ref NativeList<ParticleGpuInstance> outParticles,
        ref NativeList<ParticleBatchCmd> outBatches,
        ref RenderStats stats)
    {
        ref readonly ParticleTable pt = ref world.Particles;
        if (pt.ParticleCount == 0) return;
        int batchStart = outBatches.Count;

        // Emit grouped per active emitter matching camera LayerMask (guarantees 1 draw call per emitter or merged group!)
        for (int es = 0; es < pt.EmitterCapacity; es++)
        {
            if (pt.EmitterAlive[es] == 0) continue;
            ref ParticleEmitterData em = ref pt.Emitters[es];
            if ((em.LayerMask & cam.LayerMask) == 0) continue;

            int texSlot = -1;
            if (em.Texture.IsValid)
            {
                TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, em.Texture);
                if (tex != null) texSlot = tex->GpuSlot;
            }

            uint baseInst = (uint)outParticles.Count;
            uint count = 0;
            int cols = em.AtlasColumns > 0 ? em.AtlasColumns : 1;
            int rows = em.AtlasRows > 0 ? em.AtlasRows : 1;
            float uW = 1f / cols;
            float vH = 1f / rows;

            for (int p = 0; p < pt.ParticleCount && outParticles.Count < outParticles.Capacity; p++)
            {
                if (pt.EmitterSlot[p] != (ushort)es) continue;
                int f = pt.FrameIndex[p];
                Vec4 uv = new Vec4((f % cols) * uW, (f / cols) * vH, uW, vH);

                outParticles.Add(new ParticleGpuInstance
                {
                    PosSize = new Vec4(pt.PosX[p], pt.PosY[p], pt.PosZ[p], pt.Size[p]),
                    Color = pt.Color[p],
                    UvRect = uv,
                    RotExtra = new Vec4(pt.Rotation[p], em.StretchedBillboard, 0f, 0f),
                });
                count++;
            }

            if (count == 0) continue;
            ShaderHandle sh = em.CustomShader.IsValid ? em.CustomShader : gpu.BuiltinParticleShader;

            if (outBatches.Count > batchStart)
            {
                ref ParticleBatchCmd prev = ref outBatches[outBatches.Count - 1];
                if (prev.TextureGpuSlot == texSlot && prev.Shader == sh && prev.BlendMode == em.BlendMode)
                {
                    prev.InstanceCount += count;
                    stats.ParticleInstances += (int)count;
                    continue;
                }
            }

            if (outBatches.Count < outBatches.Capacity)
            {
                outBatches.Add(new ParticleBatchCmd
                {
                    TextureGpuSlot = texSlot,
                    Shader = sh,
                    BlendMode = em.BlendMode,
                    BaseInstance = baseInst,
                    InstanceCount = count,
                });
                stats.ParticleInstances += (int)count;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BindFragmentTexture(nint pass, in GpuContext gpu, int texSlot)
    {
        nint tex = gpu.DefaultWhiteTexture;
        nint samp = gpu.SamplerLinearWrap;
        if (texSlot >= 0 && texSlot < gpu.MaxTextures && gpu.Textures[texSlot].Alive != 0)
        {
            ref GpuTextureSlot gt = ref gpu.Textures[texSlot];
            tex = gt.Texture;
            if (gt.NearestFilter != 0) samp = gpu.SamplerNearestClamp;
            else if (gt.ClampEdge != 0) samp = gpu.SamplerLinearClamp;
        }
        var binding = new SDL.GPUTextureSamplerBinding { Texture = tex, Sampler = samp };
        SDL.BindGPUFragmentSamplers(pass, 0, (nint)(&binding), 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint SelectQuadPipeline(GpuShaderSlot* s, int blendMode, bool depthTest)
    {
        if (!depthTest) return s->PipelineOverlayNoDepth;
        return blendMode switch
        {
            0 => s->PipelineOpaque,
            2 => s->PipelineAdditive,
            _ => s->PipelineAlphaBlend,
        };
    }

    private static FrameUniformsGpu BuildFrameUniforms(in CameraData cam, in SceneLighting l, float time) => new FrameUniformsGpu
    {
        ViewProj = cam.ViewProjMatrix,
        CameraPosTime = new Vec4(cam.ShakenPosition, time),
        SunDirIntensity = new Vec4(l.SunDirection, l.SunIntensity),
        SunColor = l.SunColor,
        AmbientSky = l.AmbientSky,
        AmbientGround = l.AmbientGround,
        FogColorDensity = new Vec4(l.FogColor.R, l.FogColor.G, l.FogColor.B, l.FogDensity),
        PointLightPos0 = l.PointLightPos0,
        PointLightPos1 = l.PointLightPos1,
        PointLightPos2 = l.PointLightPos2,
        PointLightPos3 = l.PointLightPos3,
        PointLightColor0 = l.PointLightColor0,
        PointLightColor1 = l.PointLightColor1,
        PointLightColor2 = l.PointLightColor2,
        PointLightColor3 = l.PointLightColor3,
    };

    private static void Sort3DItems(Draw3DSortItem* arr, int lo, int hi)
    {
        if (lo >= hi) return;
        ulong pivot = arr[(lo + hi) >> 1].SortKey;
        int i = lo, j = hi;
        while (i <= j)
        {
            while (arr[i].SortKey < pivot) i++;
            while (arr[j].SortKey > pivot) j--;
            if (i <= j)
            {
                Draw3DSortItem tmp = arr[i];
                arr[i] = arr[j];
                arr[j] = tmp;
                i++; j--;
            }
        }
        if (lo < j) Sort3DItems(arr, lo, j);
        if (i < hi) Sort3DItems(arr, i, hi);
    }

    private static void SortSpriteItems(SpriteSortItem* arr, int lo, int hi)
    {
        if (lo >= hi) return;
        ulong pivot = arr[(lo + hi) >> 1].SortKey;
        int i = lo, j = hi;
        while (i <= j)
        {
            while (arr[i].SortKey < pivot) i++;
            while (arr[j].SortKey > pivot) j--;
            if (i <= j)
            {
                SpriteSortItem tmp = arr[i];
                arr[i] = arr[j];
                arr[j] = tmp;
                i++; j--;
            }
        }
        if (lo < j) SortSpriteItems(arr, lo, j);
        if (i < hi) SortSpriteItems(arr, i, hi);
    }
}
