using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;
using SDL3;

namespace BringEngine.Rendering;

/// <summary>
/// High-performance, cache-friendly SDL3_gpu Multi-Camera Instanced Batch Renderer.
/// Split cleanly into two pipelined phases for 2-thread execution:
/// 1. ExtractRenderPacket (Logic Thread / pure CPU, ZERO SDL/GPU calls):
///    Frustum-culls 3D meshes, deterministically sorts & batches 3D meshes, 2D sprites in 3D/HUD,
///    SDF text glyphs, SoA particles, and skeletal joint palettes into an immutable RenderPacket in FrameArena.
/// 2. SubmitRenderPacket (Main / Render Thread ONLY):
///    Uploads all dynamic buffers in ONE SDL_GPUCopyPass (cycle: true), cycles depth target cleanly
///    on first clear pass, binds pipelines BEFORE storage buffers/resources, deduplicates GPU state binds,
///    and optionally measures real GPU execution time via SDL3_gpu fence.
/// </summary>
public static unsafe class BatchRenderer
{
    private static readonly double s_ticksToMs = 1000.0 / Stopwatch.Frequency;

    private struct Draw3DSortItem
    {
        public ulong SortKey;
        public int OriginalIndex;
        public int MeshGpuSlot;
        public int TextureGpuSlot;
        public ShaderHandle Shader;
        public MaterialAlphaMode AlphaMode;
        public byte DoubleSided;
        public Instance3DGpu Instance;
    }

    private struct TextSortItem
    {
        public int TextIndex;
        public int AtlasTexGpuSlot;
        public ShaderHandle Shader;
        public short SortingOrder;
        public byte DepthTest;
    }

    private struct SpriteSortItem
    {
        public ulong SortKey;
        public int OriginalIndex;
        public int TextureGpuSlot;
        public ShaderHandle Shader;
        public SpriteBlendMode BlendMode;
        public byte DepthTest;
        public SpriteGpuInstance Instance;
    }

    /// <summary>
    /// Uploads any newly loaded meshes & textures to GPU VRAM.
    /// Must be called on the Main / Render Thread (at startup, after SceneSystem.LoadScene, or in single-threaded RenderFrame).
    /// </summary>
    public static void UploadPendingAssets(ref GpuContext gpu, ref EngineWorld world)
    {
        if (gpu.Initialized == 0) return;
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

    /// <summary>
    /// Phase 1 (Logic Thread — pure CPU, ZERO SDL/GPU calls, ZERO GC allocations):
    /// Frustum-culls, deterministically sorts, lays out SDF text, and packs all cameras into a
    /// self-contained immutable RenderPacket backed by packetArena (EngineAllocators.Frame).
    /// </summary>
    public static void ExtractRenderPacket(
        in RenderShaderDefaults shaders,
        in EngineWorld world,
        in SceneLighting lighting,
        ref LinearArena packetArena,
        ref LinearArena scratchArena,
        out RenderPacket packet,
        FrameProfiler* profiler = null,
        LogState* log = null)
    {
        int extractScope = profiler != null ? ProfilerSystem.BeginScope(ref *profiler, "Extract") : -1;

        packet = new RenderPacket
        {
            WorldTotalTime = world.TotalTime,
            Lighting = lighting,
            Valid = 1,
        };

        RenderStats stats = default;
        var sortedCameras = CameraSystem.GetSortedActiveCameras(in world.Cameras, ref packetArena);
        int camCount = sortedCameras.Length;

        if (camCount == 0)
        {
            packet.PacketArenaUsedBytes = packetArena.Offset;
            packet.ExtractedStats = stats;
            if (extractScope >= 0) ProfilerSystem.EndScope(ref *profiler, extractScope);
            return;
        }

        int spriteSlotsPerCam = 0;
        for (int si = 0; si < world.RenderComponents.SpriteCount; si++)
            spriteSlotsPerCam += world.RenderComponents.Sprites[si].DrawMode == SpriteDrawMode.Simple ? 1 : 9;

        int glyphSlotsPerCam = 0;
        for (int ti = 0; ti < world.RenderComponents.TextCount; ti++)
        {
            ref TextData td = ref world.RenderComponents.Texts[ti];
            if (td.Visible == 0 || td.Content.Length <= 0 || td.TypewriterProgress <= 0f) continue;
            bool hasShadow = td.ShadowColor.A > 0.01f && (DetMath.Abs(td.ShadowOffset.X) > 1e-4f || DetMath.Abs(td.ShadowOffset.Y) > 1e-4f);
            glyphSlotsPerCam += td.Content.Length * (hasShadow ? 2 : 1);
        }

        bool wantShadows = lighting.ShadowsEnabled != 0 && lighting.SunIntensity > 0.001f && world.RenderComponents.MeshRendererCount > 0;
        int cap3D = Math.Clamp(world.RenderComponents.MeshRendererCount * (camCount + (wantShadows ? 1 : 0)), 64, 16384);
        int capSprites = Math.Clamp(spriteSlotsPerCam * camCount, 64, 16384);
        int minGlyphCap = world.RenderComponents.TextCount > 0 ? 512 : 128;
        int capGlyphs = Math.Clamp(Math.Max(glyphSlotsPerCam, minGlyphCap) * camCount, 128, 16384);
        int capParticles = Math.Clamp(world.Particles.ParticleCount * camCount, 64, 16384);

        var camSnapshots = packetArena.AllocSlice<RenderCameraSnapshot>(camCount);
        var allInstances3D = NativeList<Instance3DGpu>.CreateInArena(ref packetArena, cap3D, 64);
        var allBatches3D = NativeList<Batch3DCmd>.CreateInArena(ref packetArena, Math.Min(cap3D, 4096), 16);
        var shadowBatches3D = wantShadows
            ? NativeList<Batch3DCmd>.CreateInArena(ref packetArena, Math.Clamp(world.RenderComponents.MeshRendererCount, 16, 2048), 16)
            : default;
        var allSprites = NativeList<SpriteGpuInstance>.CreateInArena(ref packetArena, capSprites, 64);
        var allSpriteBatches = NativeList<SpriteBatchCmd>.CreateInArena(ref packetArena, Math.Min(capSprites, 2048), 16);
        var allGlyphs = NativeList<GlyphGpuInstance>.CreateInArena(ref packetArena, capGlyphs, 64);
        var allTextBatches = NativeList<TextBatchCmd>.CreateInArena(ref packetArena, Math.Max(64, world.RenderComponents.TextCount * camCount), 16);
        var allParticles = NativeList<ParticleGpuInstance>.CreateInArena(ref packetArena, capParticles, 64);
        var allParticleBatches = NativeList<ParticleBatchCmd>.CreateInArena(ref packetArena, Math.Max(64, world.Particles.ActiveEmitters * camCount), 16);

        // Snapshot skeletal joint palette into packetArena so Render Thread never aliases Logic Thread's animation buffer
        NativeSlice<Mat4> jointCopy = default;
        int jointLen = world.Animations.FrameJointPalette.Length;
        if (jointLen > 0 && world.Animations.FrameJointPalette.Ptr != null)
        {
            jointCopy = packetArena.AllocSlice<Mat4>(jointLen, 64, zeroed: false);
            nuint jBytes = (nuint)(jointLen * sizeof(Mat4));
            Buffer.MemoryCopy(world.Animations.FrameJointPalette.Ptr, jointCopy.Ptr, jBytes, jBytes);
        }

        for (int ci = 0; ci < camCount; ci++)
        {
            CameraData* camPtr = CameraSystem.TryGetPtr(in world.Cameras, sortedCameras[ci]);
            if (camPtr == null) continue;
            ref CameraData cam = ref *camPtr;

            ref RenderCameraSnapshot snap = ref camSnapshots[ci];
            snap.ViewportRect = cam.ViewportRect;
            snap.ClearColor = cam.ClearColor;
            snap.ClearFlags = cam.ClearFlags;
            snap.ShakenPosition = cam.ShakenPosition;
            snap.Right = cam.Right;
            snap.CameraUp = cam.CameraUp;
            snap.ViewProjMatrix = cam.ViewProjMatrix;

            // 1A. Gather & Batch 3D Meshes
            snap.Batch3DStart = allBatches3D.Count;
            GatherAndBatch3D(in shaders, in world, in cam, ref scratchArena, ref allInstances3D, ref allBatches3D, ref stats, log);
            snap.Batch3DCount = allBatches3D.Count - snap.Batch3DStart;

            // 1B. Gather & Batch 2D Sprites in 3D/HUD
            snap.SpriteBatchStart = allSpriteBatches.Count;
            GatherAndBatchSprites(in shaders, in world, in cam, ref scratchArena, ref allSprites, ref allSpriteBatches, ref stats, log);
            snap.SpriteBatchCount = allSpriteBatches.Count - snap.SpriteBatchStart;

            // 1C. Gather, Layout & Batch Advanced SDF Text
            snap.TextBatchStart = allTextBatches.Count;
            GatherAndBatchText(in shaders, in world, in cam, ref scratchArena, ref allGlyphs, ref allTextBatches, ref stats, log);
            snap.TextBatchCount = allTextBatches.Count - snap.TextBatchStart;

            // 1D. Gather & Batch SoA Particles
            snap.ParticleBatchStart = allParticleBatches.Count;
            GatherAndBatchParticles(in shaders, in world, in cam, ref scratchArena, ref allParticles, ref allParticleBatches, ref stats, log);
            snap.ParticleBatchCount = allParticleBatches.Count - snap.ParticleBatchStart;
        }

        // Gather directional shadow map casters (grouped by AlphaMode, DoubleSided, Texture, MeshGpuSlot)
        if (wantShadows)
        {
            uint sSize = lighting.ShadowMapSize >= 256u ? Math.Min(lighting.ShadowMapSize, 4096u) : 2048u;
            packet.ShadowMapSize = sSize;
            packet.ShadowLightViewProj = ComputeDirectionalLightViewProj(in lighting, sSize);
            GatherShadowCasters3D(in world, in lighting, ref scratchArena, ref allInstances3D, ref shadowBatches3D, log);
            packet.ShadowsActive = (byte)(shadowBatches3D.Count > 0 ? 1 : 0);
        }

        packet.Cameras = camSnapshots;
        packet.Instances3D = allInstances3D;
        packet.Batches3D = allBatches3D;
        packet.ShadowBatches3D = shadowBatches3D;
        packet.JointPalette = jointCopy;
        packet.Sprites = allSprites;
        packet.SpriteBatches = allSpriteBatches;
        packet.Glyphs = allGlyphs;
        packet.TextBatches = allTextBatches;
        packet.Particles = allParticles;
        packet.ParticleBatches = allParticleBatches;
        packet.ExtractedStats = stats;
        packet.PacketArenaUsedBytes = packetArena.Offset;

        if (extractScope >= 0) ProfilerSystem.EndScope(ref *profiler, extractScope);
    }

    /// <summary>
    /// Phase 2 (Main / Render Thread ONLY):
    /// Consumes an immutable RenderPacket (zero reads from EngineWorld!), uploads dynamic buffers in ONE CopyPass,
    /// records state-deduplicated multi-camera RenderPasses with proper depth target cycling & pipeline-first binds,
    /// submits to SDL3_gpu, and returns exact Render Thread CPU & GPU timings.
    /// </summary>
    public static RenderSubmitTimings SubmitRenderPacket(
        ref GpuContext gpu,
        in RenderPacket packet,
        bool captureToOffscreen = false,
        bool measureGpuWithFence = true)
    {
        RenderSubmitTimings timings = default;
        if (gpu.Initialized == 0 || packet.Valid == 0) return timings;

        // 1. Determine render target dimensions & offscreen color target.
        //    Rendering to an offscreen SceneColorTexture on cmdRender and blitting to the swapchain on a separate
        //    cmdPresent completely decouples pure GPU execution time from swapchain VSync Present wait (~16ms)!
        uint targetW, targetH;
        nint colorTarget;

        if (captureToOffscreen)
        {
            targetW = gpu.CaptureWidth > 0 ? gpu.CaptureWidth : 1280u;
            targetH = gpu.CaptureHeight > 0 ? gpu.CaptureHeight : 720u;
            GpuSystem.EnsureCaptureTarget(ref gpu, targetW, targetH);
            colorTarget = gpu.CaptureColorTexture;
        }
        else
        {
            if (gpu.Window != nint.Zero && (SDL.GetWindowFlags(gpu.Window) & SDL.WindowFlags.Minimized) != 0)
                return timings;
            SDL.GetWindowSizeInPixels(gpu.Window, out int pw, out int ph);
            if (pw <= 0 || ph <= 0) return timings; // Minimized window
            targetW = (uint)pw;
            targetH = (uint)ph;
            GpuSystem.EnsureSceneColorTarget(ref gpu, targetW, targetH);
            colorTarget = gpu.SceneColorTexture;
        }

        if (colorTarget == nint.Zero) return timings;

        long renderCpuStart = Stopwatch.GetTimestamp();
        GpuSystem.EnsureDepthTexture(ref gpu, targetW, targetH);
        if (gpu.DepthTexture == nint.Zero) return timings;

        nint cmd = SDL.AcquireGPUCommandBuffer(gpu.Device);
        if (cmd == nint.Zero) return timings;

        RenderStats stats = packet.ExtractedStats;
        stats.FrameArenaUsedBytes = packet.PacketArenaUsedBytes;

        if (packet.Cameras.Length == 0)
        {
            var ct = new SDL.GPUColorTargetInfo
            {
                Texture = colorTarget,
                ClearColor = new SDL.FColor { R = 0.05f, G = 0.07f, B = 0.12f, A = 1f },
                LoadOp = SDL.GPULoadOp.Clear,
                StoreOp = SDL.GPUStoreOp.Store,
                Cycle = true,
            };
            nint rp = SDL.BeginGPURenderPass(cmd, (nint)(&ct), 1, nint.Zero);
            SDL.EndGPURenderPass(rp);
            stats.RenderPasses = 1;
            timings.TotalRenderCpuMs = (Stopwatch.GetTimestamp() - renderCpuStart) * s_ticksToMs;
            SDL.SubmitGPUCommandBuffer(cmd);
            if (!captureToOffscreen)
            {
                long presentStart = Stopwatch.GetTimestamp();
                PresentSceneColorToSwapchain(in gpu, targetW, targetH);
                timings.GpuWaitTicks += Stopwatch.GetTimestamp() - presentStart;
            }
            gpu.LastFrameStats = stats;
            return timings;
        }

        // 2. SINGLE SDL_GPUCopyPass uploading ALL dynamic buffers for the whole frame (cycle: true)!
        long uploadStart = Stopwatch.GetTimestamp();
        if (UploadFrameBuffersInSingleCopyPass(
            ref gpu, cmd,
            in packet.Instances3D,
            in packet.JointPalette,
            in packet.Sprites,
            in packet.Glyphs,
            in packet.Particles))
        {
            stats.CopyPasses++;
        }
        long uploadEnd = Stopwatch.GetTimestamp();
        timings.UploadMs = (uploadEnd - uploadStart) * s_ticksToMs;

        uint maxInst3D = gpu.Instance3DBufferBytes / (uint)sizeof(Instance3DGpu);
        uint maxSprites = gpu.SpriteInstanceBufferBytes / (uint)sizeof(SpriteGpuInstance);
        uint maxGlyphs = gpu.TextGlyphBufferBytes / (uint)sizeof(GlyphGpuInstance);
        uint maxParticles = gpu.ParticleInstanceBufferBytes / (uint)sizeof(ParticleGpuInstance);

        // 3. Execute Directional Sun Shadow Depth Pass (before main scene passes)
        long encodeStart = uploadEnd;
        nint* vStorage3D = stackalloc nint[2] { gpu.Instance3DBuffer, gpu.JointPaletteBuffer };
        nint quadIndexBuf = gpu.QuadIndexBuffer;

        bool shadowsRendered = false;
        if (packet.ShadowsActive != 0 &&
            packet.ShadowBatches3D.Count > 0 &&
            gpu.ShadowMapSupported != 0 &&
            gpu.ShadowPipeline != nint.Zero)
        {
            uint sSize = packet.ShadowMapSize >= 256u ? packet.ShadowMapSize : 2048u;
            if (GpuSystem.EnsureShadowMapTexture(ref gpu, sSize))
            {
                var shadowDepthInfo = new SDL.GPUDepthStencilTargetInfo
                {
                    Texture = gpu.ShadowDepthTexture,
                    ClearDepth = 1.0f,
                    LoadOp = SDL.GPULoadOp.Clear,
                    StoreOp = SDL.GPUStoreOp.Store,
                    StencilLoadOp = SDL.GPULoadOp.DontCare,
                    StencilStoreOp = SDL.GPUStoreOp.DontCare,
                    Cycle = 1,
                };

                nint spass = SDL.BeginGPURenderPass(cmd, nint.Zero, 0, in shadowDepthInfo);
                stats.RenderPasses++;

                var svp = new SDL.GPUViewport
                {
                    X = 0f,
                    Y = 0f,
                    W = sSize,
                    H = sSize,
                    MinDepth = 0f,
                    MaxDepth = 1f,
                };
                SDL.SetGPUViewport(spass, in svp);

                var sfu = new ShadowFrameUniformsGpu { LightViewProj = packet.ShadowLightViewProj };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&sfu), (uint)sizeof(ShadowFrameUniformsGpu));

                nint sBoundPipe = nint.Zero;
                int sBoundMesh = -1;
                int sBoundTex = int.MinValue;
                for (int bi = 0; bi < packet.ShadowBatches3D.Count; bi++)
                {
                    Batch3DCmd b = packet.ShadowBatches3D[bi];
                    if ((uint)b.MeshGpuSlot >= (uint)gpu.MaxMeshes || gpu.Meshes[b.MeshGpuSlot].Alive == 0) continue;
                    if (b.BaseInstance >= maxInst3D) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxInst3D - b.BaseInstance);
                    if (drawInst == 0) continue;

                    bool isMasked = b.AlphaMode == MaterialAlphaMode.Mask && gpu.ShadowMaskPipeline != nint.Zero;
                    nint sPipe = isMasked
                        ? ((b.DoubleSided != 0 && gpu.ShadowMaskPipelineCullNone != nint.Zero) ? gpu.ShadowMaskPipelineCullNone : gpu.ShadowMaskPipeline)
                        : ((b.DoubleSided != 0 && gpu.ShadowPipelineCullNone != nint.Zero) ? gpu.ShadowPipelineCullNone : gpu.ShadowPipeline);
                    if (sPipe == nint.Zero) continue;

                    bool pipeChanged = sPipe != sBoundPipe;
                    if (pipeChanged)
                    {
                        SDL.BindGPUGraphicsPipeline(spass, sPipe);
                        SDL.BindGPUVertexStorageBuffers(spass, 0, new ReadOnlySpan<nint>(vStorage3D, 2), 2);
                        sBoundPipe = sPipe;
                        stats.PipelineBinds++;
                        sBoundTex = int.MinValue;
                    }

                    if (isMasked && (b.TextureGpuSlot != sBoundTex || pipeChanged))
                    {
                        BindFragmentTexture(spass, in gpu, b.TextureGpuSlot);
                        sBoundTex = b.TextureGpuSlot;
                    }

                    if (b.MeshGpuSlot != sBoundMesh)
                    {
                        ref GpuMeshSlot gm = ref gpu.Meshes[b.MeshGpuSlot];
                        var vbBind = new SDL.GPUBufferBinding { Buffer = gm.VertexBuffer, Offset = 0 };
                        var ibBind = new SDL.GPUBufferBinding { Buffer = gm.IndexBuffer, Offset = 0 };
                        SDL.BindGPUVertexBuffers(spass, 0, (nint)(&vbBind), 1);
                        SDL.BindGPUIndexBuffer(spass, in ibBind, SDL.GPUIndexElementSize.IndexElementSize32Bit);
                        sBoundMesh = b.MeshGpuSlot;
                    }

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    uint idxCount = gpu.Meshes[b.MeshGpuSlot].IndexCount;
                    SDL.DrawGPUIndexedPrimitives(spass, idxCount, drawInst, 0, 0, 0);
                    stats.ShadowDrawCalls++;
                }

                SDL.EndGPURenderPass(spass);
                shadowsRendered = true;
            }
        }

        // 4. Execute RenderPass per camera in DepthOrder (pipeline bound BEFORE storage/texture resources!)
        for (int ci = 0; ci < packet.Cameras.Length; ci++)
        {
            ref readonly RenderCameraSnapshot cam = ref packet.Cameras[ci];
            bool isFullViewport = cam.ViewportRect.X <= 0.001f && cam.ViewportRect.Y <= 0.001f &&
                                  cam.ViewportRect.Z >= 0.999f && cam.ViewportRect.W >= 0.999f;

            SDL.GPULoadOp colorLoad = (ci == 0 || (cam.ClearFlags == CameraClearFlags.SolidColor && isFullViewport))
                ? SDL.GPULoadOp.Clear : SDL.GPULoadOp.Load;
            SDL.GPULoadOp depthLoad = (ci == 0 || cam.ClearFlags == CameraClearFlags.SolidColor || cam.ClearFlags == CameraClearFlags.DepthOnly)
                ? SDL.GPULoadOp.Clear : SDL.GPULoadOp.Load;

            var colorInfo = new SDL.GPUColorTargetInfo
            {
                Texture = colorTarget,
                ClearColor = new SDL.FColor { R = cam.ClearColor.R, G = cam.ClearColor.G, B = cam.ClearColor.B, A = cam.ClearColor.A },
                LoadOp = colorLoad,
                StoreOp = SDL.GPUStoreOp.Store,
                Cycle = (ci == 0) && (colorLoad == SDL.GPULoadOp.Clear),
            };
            var depthInfo = new SDL.GPUDepthStencilTargetInfo
            {
                Texture = gpu.DepthTexture,
                ClearDepth = 1.0f,
                LoadOp = depthLoad,
                StoreOp = SDL.GPUStoreOp.Store,
                StencilLoadOp = SDL.GPULoadOp.DontCare,
                StencilStoreOp = SDL.GPUStoreOp.DontCare,
                // Cycle depth texture on the initial frame clear so pipelined frames never hazard on prior depth reads!
                Cycle = (byte)(((ci == 0) && (depthLoad == SDL.GPULoadOp.Clear)) ? 1 : 0),
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
            nint boundStorage0 = nint.Zero;
            int boundMeshSlot = -1;
            int boundTexSlot = int.MinValue;
            int boundSamplerCount = -1;
            bool quadIndexBound = false;

            void EnsureQuadIndexBound()
            {
                if (quadIndexBound) return;
                var qib = new SDL.GPUBufferBinding { Buffer = quadIndexBuf, Offset = 0 };
                SDL.BindGPUIndexBuffer(pass, in qib, SDL.GPUIndexElementSize.IndexElementSize32Bit);
                quadIndexBound = true;
                boundMeshSlot = -1;
            }

            // --- Draw 3D Instanced Batches ---
            if (cam.Batch3DCount > 0)
            {
                var fu = BuildFrameUniforms(in cam, in packet, shadowsRendered ? gpu.ShadowMapSize : 0u);
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&fu), (uint)sizeof(FrameUniformsGpu));
                SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&fu), (uint)sizeof(FrameUniformsGpu));

                boundSamplerCount = -1;
                for (int bi = 0; bi < cam.Batch3DCount; bi++)
                {
                    Batch3DCmd b = packet.Batches3D[cam.Batch3DStart + bi];
                    if ((uint)b.MeshGpuSlot >= (uint)gpu.MaxMeshes || gpu.Meshes[b.MeshGpuSlot].Alive == 0) continue;
                    if (b.BaseInstance >= maxInst3D) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxInst3D - b.BaseInstance);
                    if (drawInst == 0) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinPbrShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.Mesh3D, gpu.BuiltinPbrShader);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe;
                    if (b.AlphaMode == MaterialAlphaMode.Blend)
                        pipe = (b.DoubleSided != 0 && sSlot->PipelineAlphaBlendCullNone != nint.Zero) ? sSlot->PipelineAlphaBlendCullNone : sSlot->PipelineAlphaBlend;
                    else
                        pipe = (b.DoubleSided != 0 && sSlot->PipelineOpaqueCullNone != nint.Zero) ? sSlot->PipelineOpaqueCullNone : sSlot->PipelineOpaque;

                    if (pipe != boundPipeline)
                    {
                        SDL.BindGPUGraphicsPipeline(pass, pipe);
                        boundPipeline = pipe;
                        stats.PipelineBinds++;
                        passChanged = true;
                    }

                    if (boundStorage0 != gpu.Instance3DBuffer || passChanged)
                    {
                        SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(vStorage3D, 2), 2);
                        boundStorage0 = gpu.Instance3DBuffer;
                    }

                    if (b.MeshGpuSlot != boundMeshSlot)
                    {
                        ref GpuMeshSlot gm = ref gpu.Meshes[b.MeshGpuSlot];
                        var vbBind = new SDL.GPUBufferBinding { Buffer = gm.VertexBuffer, Offset = 0 };
                        var ibBind = new SDL.GPUBufferBinding { Buffer = gm.IndexBuffer, Offset = 0 };
                        SDL.BindGPUVertexBuffers(pass, 0, (nint)(&vbBind), 1);
                        SDL.BindGPUIndexBuffer(pass, in ibBind, SDL.GPUIndexElementSize.IndexElementSize32Bit);
                        boundMeshSlot = b.MeshGpuSlot;
                        quadIndexBound = false;
                    }

                    int neededSamplers = sSlot->FragmentSamplerCount;
                    if (b.TextureGpuSlot != boundTexSlot || neededSamplerChanged(boundSamplerCount, neededSamplers) || passChanged)
                    {
                        if (neededSamplers >= 2)
                            BindFragmentTextureAndShadow(pass, in gpu, b.TextureGpuSlot, shadowsRendered);
                        else if (neededSamplers == 1)
                            BindFragmentTexture(pass, in gpu, b.TextureGpuSlot);
                        boundTexSlot = b.TextureGpuSlot;
                        boundSamplerCount = neededSamplers;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    uint idxCount = gpu.Meshes[b.MeshGpuSlot].IndexCount;
                    SDL.DrawGPUIndexedPrimitives(pass, idxCount, drawInst, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(idxCount / 3 * drawInst);
                }
            }

            // --- Draw 2D Sprites (in 3D World or Screen HUD) ---
            if (cam.SpriteBatchCount > 0)
            {
                EnsureQuadIndexBound();
                var cu = new SimpleCameraUniformsGpu { ViewProj = cam.ViewProjMatrix, CameraPosTime = new Vec4(cam.ShakenPosition, packet.WorldTotalTime) };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&cu), (uint)sizeof(SimpleCameraUniformsGpu));
                SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&cu), (uint)sizeof(SimpleCameraUniformsGpu));
                nint sb = gpu.SpriteInstanceBuffer;
                boundSamplerCount = -1;

                for (int bi = 0; bi < cam.SpriteBatchCount; bi++)
                {
                    SpriteBatchCmd b = packet.SpriteBatches[cam.SpriteBatchStart + bi];
                    if (b.BaseInstance >= maxSprites) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxSprites - b.BaseInstance);
                    if (drawInst == 0) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinSpriteShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.InstancedQuad, gpu.BuiltinSpriteShader);
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

                    if (boundStorage0 != sb || passChanged)
                    {
                        SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(&sb, 1), 1);
                        boundStorage0 = sb;
                    }

                    int neededSamplers = sSlot->FragmentSamplerCount;
                    if (neededSamplers >= 1)
                    {
                        if (b.TextureGpuSlot != boundTexSlot || boundSamplerCount != neededSamplers || passChanged)
                        {
                            BindFragmentTexture(pass, in gpu, b.TextureGpuSlot);
                            boundTexSlot = b.TextureGpuSlot;
                            boundSamplerCount = neededSamplers;
                            passChanged = true;
                        }
                    }
                    else if (boundSamplerCount != 0)
                    {
                        boundTexSlot = int.MinValue;
                        boundSamplerCount = 0;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    SDL.DrawGPUIndexedPrimitives(pass, 6, drawInst, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(2 * drawInst);
                }
            }

            // --- Draw Batched Particles ---
            if (cam.ParticleBatchCount > 0)
            {
                EnsureQuadIndexBound();
                var pu = new ParticleCameraUniformsGpu
                {
                    ViewProj = cam.ViewProjMatrix,
                    CameraRightTime = new Vec4(cam.Right, packet.WorldTotalTime),
                    CameraUpPad = new Vec4(cam.CameraUp, 0f),
                };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&pu), (uint)sizeof(ParticleCameraUniformsGpu));
                SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&pu), (uint)sizeof(ParticleCameraUniformsGpu));
                nint pb = gpu.ParticleInstanceBuffer;
                boundSamplerCount = -1;

                for (int bi = 0; bi < cam.ParticleBatchCount; bi++)
                {
                    ParticleBatchCmd b = packet.ParticleBatches[cam.ParticleBatchStart + bi];
                    if (b.BaseInstance >= maxParticles) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxParticles - b.BaseInstance);
                    if (drawInst == 0) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinParticleShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.InstancedQuad, gpu.BuiltinParticleShader);
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

                    if (boundStorage0 != pb || passChanged)
                    {
                        SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(&pb, 1), 1);
                        boundStorage0 = pb;
                    }

                    int neededSamplers = sSlot->FragmentSamplerCount;
                    if (neededSamplers >= 1)
                    {
                        if (b.TextureGpuSlot != boundTexSlot || boundSamplerCount != neededSamplers || passChanged)
                        {
                            BindFragmentTexture(pass, in gpu, b.TextureGpuSlot);
                            boundTexSlot = b.TextureGpuSlot;
                            boundSamplerCount = neededSamplers;
                            passChanged = true;
                        }
                    }
                    else if (boundSamplerCount != 0)
                    {
                        boundTexSlot = int.MinValue;
                        boundSamplerCount = 0;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    SDL.DrawGPUIndexedPrimitives(pass, 6, drawInst, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(2 * drawInst);
                }
            }

            // --- Draw Advanced Animated/Shadered SDF Text ---
            if (cam.TextBatchCount > 0)
            {
                EnsureQuadIndexBound();
                var cu = new SimpleCameraUniformsGpu { ViewProj = cam.ViewProjMatrix, CameraPosTime = new Vec4(cam.ShakenPosition, packet.WorldTotalTime) };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&cu), (uint)sizeof(SimpleCameraUniformsGpu));
                SDL.PushGPUFragmentUniformData(cmd, 0, (nint)(&cu), (uint)sizeof(SimpleCameraUniformsGpu));
                nint tb = gpu.TextGlyphBuffer;
                boundSamplerCount = -1;

                for (int bi = 0; bi < cam.TextBatchCount; bi++)
                {
                    TextBatchCmd b = packet.TextBatches[cam.TextBatchStart + bi];
                    if (b.BaseInstance >= maxGlyphs) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxGlyphs - b.BaseInstance);
                    if (drawInst == 0) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinSdfTextShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.InstancedQuad, gpu.BuiltinSdfTextShader);
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

                    if (boundStorage0 != tb || passChanged)
                    {
                        SDL.BindGPUVertexStorageBuffers(pass, 0, new ReadOnlySpan<nint>(&tb, 1), 1);
                        boundStorage0 = tb;
                    }

                    int neededSamplers = sSlot->FragmentSamplerCount;
                    if (neededSamplers >= 1)
                    {
                        if (b.AtlasTexGpuSlot != boundTexSlot || boundSamplerCount != neededSamplers || passChanged)
                        {
                            BindFragmentTexture(pass, in gpu, b.AtlasTexGpuSlot);
                            boundTexSlot = b.AtlasTexGpuSlot;
                            boundSamplerCount = neededSamplers;
                            passChanged = true;
                        }
                    }
                    else if (boundSamplerCount != 0)
                    {
                        boundTexSlot = int.MinValue;
                        boundSamplerCount = 0;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    var bu = new BatchUniformsGpu { BaseInstance = b.BaseInstance };
                    SDL.PushGPUVertexUniformData(cmd, 1, (nint)(&bu), (uint)sizeof(BatchUniformsGpu));

                    SDL.DrawGPUIndexedPrimitives(pass, 6, drawInst, 0, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(2 * drawInst);
                }
            }

            SDL.EndGPURenderPass(pass);
        }

        long encodeEnd = Stopwatch.GetTimestamp();
        timings.EncodeMs = (encodeEnd - encodeStart) * s_ticksToMs;
        timings.TotalRenderCpuMs = (encodeEnd - renderCpuStart) * s_ticksToMs;

        // 4. Submit render command buffer (NO swapchain attached -> measures PURE GPU execution time, zero VSync wait!).
        //    Wait on a leading queue barrier fence BEFORE starting the GPU timer so any prior frame's VSync Present
        //    on the command queue finishes outside the bracket, leaving (gpuEnd - gpuStart) = pure Copy + Shader GPU ms!
        if (measureGpuWithFence)
        {
            long waitBegin = Stopwatch.GetTimestamp();
            if (!captureToOffscreen)
            {
                nint barrierCmd = SDL.AcquireGPUCommandBuffer(gpu.Device);
                if (barrierCmd != nint.Zero)
                {
                    nint barrierFence = SDL.SubmitGPUCommandBufferAndAcquireFence(barrierCmd);
                    if (barrierFence != nint.Zero)
                    {
                        nint* bf = stackalloc nint[1] { barrierFence };
                        SDL.WaitForGPUFences(gpu.Device, true, (nint)bf, 1);
                        SDL.ReleaseGPUFence(gpu.Device, barrierFence);
                    }
                }
            }

            long gpuStart = Stopwatch.GetTimestamp();
            nint fence = SDL.SubmitGPUCommandBufferAndAcquireFence(cmd);
            if (fence != nint.Zero)
            {
                nint* fArr = stackalloc nint[1] { fence };
                SDL.WaitForGPUFences(gpu.Device, true, (nint)fArr, 1);
                SDL.ReleaseGPUFence(gpu.Device, fence);
            }
            long gpuEnd = Stopwatch.GetTimestamp();
            timings.GpuWaitTicks = gpuEnd - waitBegin;
            timings.GpuMs = (gpuEnd - gpuStart) * s_ticksToMs;
        }
        else
        {
            SDL.SubmitGPUCommandBuffer(cmd);
        }

        // 5. Blit offscreen SceneColorTexture -> swapchain & Present on a separate command buffer
        //    so VSync / monitor refresh wait NEVER pollutes GPU or Render Thread work measurements!
        if (!captureToOffscreen)
        {
            long presentStart = Stopwatch.GetTimestamp();
            PresentSceneColorToSwapchain(in gpu, targetW, targetH);
            timings.GpuWaitTicks += Stopwatch.GetTimestamp() - presentStart;
        }

        gpu.LastFrameStats = stats;
        return timings;
    }

    private static GpuShaderSlot* ResolveShaderSlot(
        ref GpuContext gpu,
        ShaderHandle handle,
        ShaderVertexLayout expectedLayout,
        ShaderHandle fallbackHandle)
    {
        GpuShaderSlot* sSlot = ShaderSystem.TryGetShader(in gpu, handle);
        if (sSlot == null)
        {
            return ShaderSystem.TryGetShader(in gpu, fallbackHandle);
        }
        if (sSlot->Layout != expectedLayout)
        {
            if (sSlot->WarnedLayoutMismatch == 0)
            {
                sSlot->WarnedLayoutMismatch = 1;
                if (gpu.Log != null)
                {
                    LogSystem.Warn(gpu.Log, $"Shader {handle} layout mismatch ({sSlot->Layout} != {expectedLayout}); falling back to built-in shader");
                }
            }
            return ShaderSystem.TryGetShader(in gpu, fallbackHandle);
        }
        return sSlot;
    }

    private static void PresentSceneColorToSwapchain(in GpuContext gpu, uint srcW, uint srcH)
    {
        nint presentCmd = SDL.AcquireGPUCommandBuffer(gpu.Device);
        if (presentCmd == nint.Zero) return;

        if (SDL.WaitAndAcquireGPUSwapchainTexture(presentCmd, gpu.Window, out nint swapTex, out uint swapW, out uint swapH) && swapTex != nint.Zero)
        {
            var blit = new SDL.GPUBlitInfo
            {
                Source = new SDL.GPUBlitRegion
                {
                    Texture = gpu.SceneColorTexture,
                    X = 0,
                    Y = 0,
                    W = srcW,
                    H = srcH,
                },
                Destination = new SDL.GPUBlitRegion
                {
                    Texture = swapTex,
                    X = 0,
                    Y = 0,
                    W = swapW,
                    H = swapH,
                },
                LoadOp = SDL.GPULoadOp.DontCare,
                FlipMode = SDL.FlipMode.None,
                Filter = (srcW == swapW && srcH == swapH) ? SDL.GPUFilter.Nearest : SDL.GPUFilter.Linear,
                Cycle = false,
            };
            SDL.BlitGPUTexture(presentCmd, in blit);
        }

        SDL.SubmitGPUCommandBuffer(presentCmd);
    }

    /// <summary>
    /// Single-threaded convenience entry point combining UploadPendingAssets + ExtractRenderPacket + SubmitRenderPacket.
    /// Useful for offscreen verification (--verify-render) and unit tests; interactive games should use EnginePipelineSystem.
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

        UploadPendingAssets(ref gpu, ref world);
        var shaders = GpuSystem.GetBuiltinShaders(in gpu);
        ExtractRenderPacket(in shaders, in world, in lighting, ref alloc.Frame, ref alloc.Scratch, out RenderPacket packet, profiler, gpu.Log);

        bool measureGpu = profiler != null && profiler->Enabled != 0 && profiler->MeasureGpuWithFence != 0;
        RenderSubmitTimings t = SubmitRenderPacket(ref gpu, in packet, captureToOffscreen, measureGpu);

        if (profiler != null && profiler->Enabled != 0)
        {
            ProfilerSystem.RecordRenderTime(ref *profiler, t.TotalRenderCpuMs);
            if (measureGpu)
                ProfilerSystem.RecordGpuTime(ref *profiler, t.GpuMs, t.GpuWaitTicks);
            else if (t.GpuWaitTicks > 0)
                Interlocked.Add(ref profiler->GpuWaitTicks, t.GpuWaitTicks);
        }
    }

    private static void GrowStorageBufferIfNeeded(ref GpuContext gpu, ref nint buffer, ref uint currentBytes, uint neededBytes, string label)
    {
        if (neededBytes <= currentBytes) return;
        uint grown = currentBytes > (uint.MaxValue / 2u) ? neededBytes : currentBytes * 2u;
        uint newBytes = Math.Max(neededBytes, grown);
        var info = new SDL.GPUBufferCreateInfo
        {
            Usage = SDL.GPUBufferUsageFlags.GraphicsStorageRead,
            Size = newBytes,
        };
        nint newBuf = SDL.CreateGPUBuffer(gpu.Device, in info);
        if (newBuf != nint.Zero)
        {
            if (buffer != nint.Zero) SDL.ReleaseGPUBuffer(gpu.Device, buffer);
            buffer = newBuf;
            currentBytes = newBytes;
            if (gpu.Log != null)
                LogSystem.Warn(gpu.Log, $"GpuContext dynamically grew {label} buffer to {newBytes} bytes");
        }
        else if (gpu.Log != null)
        {
            LogSystem.Warn(gpu.Log, $"GpuContext failed growing {label} buffer to {newBytes} bytes ({SDL.GetError()}); clamping upload to {currentBytes}");
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

        GrowStorageBufferIfNeeded(ref gpu, ref gpu.Instance3DBuffer, ref gpu.Instance3DBufferBytes, bytes3D, "Instance3D");
        GrowStorageBufferIfNeeded(ref gpu, ref gpu.JointPaletteBuffer, ref gpu.JointPaletteBufferBytes, bytesJoints, "JointPalette");
        GrowStorageBufferIfNeeded(ref gpu, ref gpu.SpriteInstanceBuffer, ref gpu.SpriteInstanceBufferBytes, bytesSprites, "SpriteInstance");
        GrowStorageBufferIfNeeded(ref gpu, ref gpu.TextGlyphBuffer, ref gpu.TextGlyphBufferBytes, bytesGlyphs, "TextGlyph");
        GrowStorageBufferIfNeeded(ref gpu, ref gpu.ParticleInstanceBuffer, ref gpu.ParticleInstanceBufferBytes, bytesParticles, "ParticleInstance");

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

        if (totalNeeded > gpu.UploadTransferBufferBytes)
        {
            uint grownTb = gpu.UploadTransferBufferBytes > (uint.MaxValue / 2u) ? totalNeeded : gpu.UploadTransferBufferBytes * 2u;
            uint newTbBytes = Math.Max(totalNeeded, grownTb);
            var tbInfo = new SDL.GPUTransferBufferCreateInfo
            {
                Usage = SDL.GPUTransferBufferUsage.Upload,
                Size = newTbBytes,
            };
            nint newTb = SDL.CreateGPUTransferBuffer(gpu.Device, in tbInfo);
            if (newTb != nint.Zero)
            {
                if (gpu.UploadTransferBuffer != nint.Zero) SDL.ReleaseGPUTransferBuffer(gpu.Device, gpu.UploadTransferBuffer);
                gpu.UploadTransferBuffer = newTb;
                gpu.UploadTransferBufferBytes = newTbBytes;
                if (gpu.Log != null)
                    LogSystem.Warn(gpu.Log, $"GpuContext dynamically grew UploadTransferBuffer to {newTbBytes} bytes");
            }
            else
            {
                if (gpu.Log != null)
                    LogSystem.Warn(gpu.Log, $"GpuContext failed growing UploadTransferBuffer to {newTbBytes} bytes ({SDL.GetError()}); clamping frame upload");
                uint rem = gpu.UploadTransferBufferBytes;
                if (bytes3D > rem) bytes3D = (rem / (uint)sizeof(Instance3DGpu)) * (uint)sizeof(Instance3DGpu);
                offJoints = (uint)LinearArena.AlignUp(off3D + bytes3D, 64);
                uint remJ = rem > offJoints ? rem - offJoints : 0u;
                if (bytesJoints > remJ) bytesJoints = (remJ / (uint)sizeof(Mat4)) * (uint)sizeof(Mat4);
                offSprites = (uint)LinearArena.AlignUp(offJoints + bytesJoints, 64);
                uint remS = rem > offSprites ? rem - offSprites : 0u;
                if (bytesSprites > remS) bytesSprites = (remS / (uint)sizeof(SpriteGpuInstance)) * (uint)sizeof(SpriteGpuInstance);
                offGlyphs = (uint)LinearArena.AlignUp(offSprites + bytesSprites, 64);
                uint remG = rem > offGlyphs ? rem - offGlyphs : 0u;
                if (bytesGlyphs > remG) bytesGlyphs = (remG / (uint)sizeof(GlyphGpuInstance)) * (uint)sizeof(GlyphGpuInstance);
                offParticles = (uint)LinearArena.AlignUp(offGlyphs + bytesGlyphs, 64);
                uint remP = rem > offParticles ? rem - offParticles : 0u;
                if (bytesParticles > remP) bytesParticles = (remP / (uint)sizeof(ParticleGpuInstance)) * (uint)sizeof(ParticleGpuInstance);
                totalNeeded = offParticles + bytesParticles;
                if (totalNeeded == 0) return false;
            }
        }

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
        in RenderShaderDefaults shaders,
        in EngineWorld world,
        in CameraData cam,
        ref LinearArena scratchArena,
        ref NativeList<Instance3DGpu> outInstances,
        ref NativeList<Batch3DCmd> outBatches,
        ref RenderStats stats,
        LogState* log = null)
    {
        int count = world.RenderComponents.MeshRendererCount;
        if (count == 0) return;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            var items = NativeList<Draw3DSortItem>.CreateInArena(ref scratchArena, count, 64);

            for (int i = 0; i < count; i++)
            {
                ref MeshRendererData mr = ref world.RenderComponents.MeshRenderers[i];
                if (mr.Visible == 0 || (mr.LayerMask & cam.LayerMask) == 0) continue;

                MeshAssetData* mesh = AssetSystem.TryGetMesh(in world.Assets, mr.Mesh);
                if (mesh == null || mesh->GpuSlot < 0) continue;

                int td = TransformSystem.GetDenseIndex(in world.Transforms, mr.Entity);
                if (td < 0) continue;
                Mat4 worldMat = world.Transforms.WorldMatrix[td];

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
                if (mesh->HasSkinning != 0)
                {
                    AnimPlayerData* ap = AnimationSystem.TryGetPlayer(in world.Animations, mr.AnimPlayer);
                    if (ap != null && ap->FrameJointCount > 0) jointOff = ap->FrameJointOffset;
                }

                MaterialAlphaMode alphaMode = mat != null ? mat->AlphaMode : MaterialAlphaMode.Opaque;
                byte doubleSided = mat != null && mat->DoubleSided != 0 ? (byte)1 : (byte)0;
                ShaderHandle shader = (mat != null && mat->CustomShader.IsValid) ? mat->CustomShader : shaders.Pbr;

                ulong key = ((ulong)alphaMode << 58)
                          | ((ulong)doubleSided << 57)
                          | ((ulong)(shader.Raw & 0xFFFFu) << 40)
                          | ((ulong)((uint)(texSlot + 1) & 0xFFFFu) << 20)
                          | ((ulong)((uint)mesh->GpuSlot & 0xFFFFFu));

                items.Add(new Draw3DSortItem
                {
                    SortKey = key,
                    OriginalIndex = i,
                    MeshGpuSlot = mesh->GpuSlot,
                    TextureGpuSlot = texSlot,
                    Shader = shader,
                    AlphaMode = alphaMode,
                    DoubleSided = doubleSided,
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

            Sort3DItems(items.Data, 0, items.Count - 1);

            int idx = 0;
            while (idx < items.Count)
            {
                if (outInstances.Count >= outInstances.Capacity || outBatches.Count >= outBatches.Capacity)
                {
                    if (log != null)
                        LogSystem.Warn(log, $"BatchRenderer.GatherAndBatch3D capacity cap reached (instances={outInstances.Count}/{outInstances.Capacity}, batches={outBatches.Count}/{outBatches.Capacity})");
                    break;
                }

                Draw3DSortItem first = items[idx];
                uint baseInst = (uint)outInstances.Count;
                uint instCount = 0;

                while (idx < items.Count &&
                       items[idx].AlphaMode == first.AlphaMode &&
                       items[idx].DoubleSided == first.DoubleSided &&
                       items[idx].Shader == first.Shader &&
                       items[idx].TextureGpuSlot == first.TextureGpuSlot &&
                       items[idx].MeshGpuSlot == first.MeshGpuSlot &&
                       outInstances.Count < outInstances.Capacity)
                {
                    outInstances.Add(items[idx].Instance);
                    instCount++;
                    idx++;
                }

                if (instCount > 0 && outBatches.Count < outBatches.Capacity)
                {
                    outBatches.Add(new Batch3DCmd
                    {
                        MeshGpuSlot = first.MeshGpuSlot,
                        TextureGpuSlot = first.TextureGpuSlot,
                        Shader = first.Shader,
                        AlphaMode = first.AlphaMode,
                        DoubleSided = first.DoubleSided,
                        BaseInstance = baseInst,
                        InstanceCount = instCount,
                    });
                    stats.MeshInstances += (int)instCount;
                }
            }
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    private static void GatherAndBatchSprites(
        in RenderShaderDefaults shaders,
        in EngineWorld world,
        in CameraData cam,
        ref LinearArena scratchArena,
        ref NativeList<SpriteGpuInstance> outSprites,
        ref NativeList<SpriteBatchCmd> outBatches,
        ref RenderStats stats,
        LogState* log = null)
    {
        int count = world.RenderComponents.SpriteCount;
        if (count == 0) return;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            // Count exact sort items needed (1 per simple sprite, up to 9 per 9-sliced sprite)
            int maxItems = 0;
            for (int i = 0; i < count; i++)
            {
                ref SpriteData sp = ref world.RenderComponents.Sprites[i];
                if (sp.Visible == 0 || (sp.LayerMask & cam.LayerMask) == 0) continue;
                maxItems += sp.DrawMode == SpriteDrawMode.Simple ? 1 : 9;
            }
            if (maxItems == 0) return;

            var items = NativeList<SpriteSortItem>.CreateInArena(ref scratchArena, maxItems, 64);

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

                Vec4 uv = sp.UvRect;
                if (sp.FlipX != 0) { uv.X += uv.Z; uv.Z = -uv.Z; }
                if (sp.FlipY != 0) { uv.Y += uv.W; uv.W = -uv.W; }

                int texSlot = -1;
                TextureAssetData* tex = null;
                if (sp.Texture.IsValid)
                {
                    tex = AssetSystem.TryGetTexture(in world.Assets, sp.Texture);
                    if (tex != null) texSlot = tex->GpuSlot;
                }

                ShaderHandle sh = sp.CustomShader.IsValid ? sp.CustomShader : shaders.Sprite;
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

                bool isSliced = sp.DrawMode != SpriteDrawMode.Simple &&
                                (sp.SliceBorders.X > 0f || sp.SliceBorders.Y > 0f || sp.SliceBorders.Z > 0f || sp.SliceBorders.W > 0f) &&
                                sp.Size.X > 1e-4f && sp.Size.Y > 1e-4f;

                if (!isSliced)
                {
                    Mat4 quadMat = new Mat4
                    {
                        C0 = new Vec4(right, 0f),
                        C1 = new Vec4(up, 0f),
                        C2 = new Vec4(fwd, 0f),
                        C3 = new Vec4(origin, 1f),
                    };

                    items.Add(new SpriteSortItem
                    {
                        SortKey = key,
                        OriginalIndex = i * 9,
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
                else
                {
                    EmitNineSliceSubQuads(in sp, tex, origin, right, up, fwd, uv, cp, key, i * 9, texSlot, sh, ref items);
                }
            }

            if (items.Count == 0) return;
            SortSpriteItems(items.Data, 0, items.Count - 1);

            int idx = 0;
            while (idx < items.Count)
            {
                if (outSprites.Count >= outSprites.Capacity || outBatches.Count >= outBatches.Capacity)
                {
                    if (log != null)
                        LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchSprites capacity cap reached (sprites={outSprites.Count}/{outSprites.Capacity}, batches={outBatches.Count}/{outBatches.Capacity})");
                    break;
                }

                SpriteSortItem first = items[idx];
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
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    /// <summary>
    /// Expands a 9-sliced SpriteData into up to 9 sub-quads sharing the exact same SortKey/Texture/Pipeline
    /// so all 9-slice panels using an atlas batch into ONE single instanced GPU draw call (0 GC allocations).
    /// </summary>
    private static void EmitNineSliceSubQuads(
        in SpriteData sp,
        TextureAssetData* tex,
        Vec3 origin,
        Vec3 right,
        Vec3 up,
        Vec3 fwd,
        Vec4 uv,
        Vec4 customParams,
        ulong sortKey,
        int baseOrigIdx,
        int texSlot,
        ShaderHandle sh,
        ref NativeList<SpriteSortItem> items)
    {
        float srcW = sp.SourcePixelSize.X;
        float srcH = sp.SourcePixelSize.Y;
        if (srcW <= 0f || srcH <= 0f)
        {
            if (tex != null && tex->Width > 0 && tex->Height > 0)
            {
                srcW = tex->Width * DetMath.Abs(uv.Z);
                srcH = tex->Height * DetMath.Abs(uv.W);
            }
            if (srcW <= 0f) srcW = sp.Size.X;
            if (srcH <= 0f) srcH = sp.Size.Y;
        }

        float scale = sp.SliceScale > 0f ? sp.SliceScale : 1f;
        float bL = DetMath.Max(0f, sp.SliceBorders.X) * scale;
        float bB = DetMath.Max(0f, sp.SliceBorders.Y) * scale;
        float bR = DetMath.Max(0f, sp.SliceBorders.Z) * scale;
        float bT = DetMath.Max(0f, sp.SliceBorders.W) * scale;

        // Graceful proportional shrink when target size is smaller than combined borders (matches Unity UI)
        if (bL + bR > sp.Size.X && (bL + bR) > 1e-5f)
        {
            float s = sp.Size.X / (bL + bR);
            bL *= s;
            bR *= s;
        }
        if (bB + bT > sp.Size.Y && (bB + bT) > 1e-5f)
        {
            float s = sp.Size.Y / (bB + bT);
            bB *= s;
            bT *= s;
        }

        float* xCut = stackalloc float[4];
        xCut[0] = 0f;
        xCut[1] = bL / sp.Size.X;
        xCut[2] = 1f - (bR / sp.Size.X);
        xCut[3] = 1f;

        float* yCut = stackalloc float[4];
        yCut[0] = 0f;
        yCut[1] = bB / sp.Size.Y;
        yCut[2] = 1f - (bT / sp.Size.Y);
        yCut[3] = 1f;

        // Source UV cuts (top-down PNG v=0 at top, v=1 at bottom; screen +Y goes bottom row=0 -> middle row=1 -> top row=2)
        float uL = DetMath.Clamp01(DetMath.Max(0f, sp.SliceBorders.X) / srcW);
        float uR = DetMath.Clamp01(DetMath.Max(0f, sp.SliceBorders.Z) / srcW);
        if (uL + uR > 1f) { float inv = 1f / (uL + uR); uL *= inv; uR *= inv; }

        float vB = DetMath.Clamp01(DetMath.Max(0f, sp.SliceBorders.Y) / srcH);
        float vT = DetMath.Clamp01(DetMath.Max(0f, sp.SliceBorders.W) / srcH);
        if (vB + vT > 1f) { float inv = 1f / (vB + vT); vB *= inv; vT *= inv; }

        float* uStart = stackalloc float[3];
        float* uLen = stackalloc float[3];
        uStart[0] = 0f;        uLen[0] = uL;
        uStart[1] = uL;        uLen[1] = 1f - uL - uR;
        uStart[2] = 1f - uR;   uLen[2] = uR;

        // Row 0 = bottom (v in [1-vB, 1]), Row 1 = middle (v in [vT, 1-vB]), Row 2 = top (v in [0, vT])
        float* vStart = stackalloc float[3];
        float* vLen = stackalloc float[3];
        vStart[0] = 1f - vB;   vLen[0] = vB;
        vStart[1] = vT;        vLen[1] = 1f - vT - vB;
        vStart[2] = 0f;        vLen[2] = vT;

        int subIdx = 0;
        for (int row = 0; row < 3; row++)
        {
            float dy = yCut[row + 1] - yCut[row];
            if (dy <= 1e-5f || vLen[row] <= 1e-6f) continue;

            for (int col = 0; col < 3; col++)
            {
                if (sp.DrawMode == SpriteDrawMode.SlicedNoCenter && row == 1 && col == 1) continue;
                float dx = xCut[col + 1] - xCut[col];
                if (dx <= 1e-5f || uLen[col] <= 1e-6f) continue;

                Vec3 subOrigin = origin + right * xCut[col] + up * yCut[row];
                Vec3 subRight = right * dx;
                Vec3 subUp = up * dy;

                Mat4 subMat = new Mat4
                {
                    C0 = new Vec4(subRight, 0f),
                    C1 = new Vec4(subUp, 0f),
                    C2 = new Vec4(fwd, 0f),
                    C3 = new Vec4(subOrigin, 1f),
                };

                Vec4 subUv = new Vec4(
                    uv.X + uStart[col] * uv.Z,
                    uv.Y + vStart[row] * uv.W,
                    uLen[col] * uv.Z,
                    vLen[row] * uv.W);

                items.Add(new SpriteSortItem
                {
                    SortKey = sortKey,
                    OriginalIndex = baseOrigIdx + subIdx++,
                    TextureGpuSlot = texSlot,
                    Shader = sh,
                    BlendMode = sp.BlendMode,
                    DepthTest = sp.DepthTest,
                    Instance = new SpriteGpuInstance
                    {
                        Model = subMat,
                        UvRect = subUv,
                        Color = sp.Color,
                        CustomParams = customParams,
                    },
                });
            }
        }
    }

    private static void GatherAndBatchText(
        in RenderShaderDefaults shaders,
        in EngineWorld world,
        in CameraData cam,
        ref LinearArena scratchArena,
        ref NativeList<GlyphGpuInstance> outGlyphs,
        ref NativeList<TextBatchCmd> outBatches,
        ref RenderStats stats,
        LogState* log = null)
    {
        int count = world.RenderComponents.TextCount;
        if (count == 0) return;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            var items = NativeList<TextSortItem>.CreateInArena(ref scratchArena, count, 16);
            for (int i = 0; i < count; i++)
            {
                ref TextData td = ref world.RenderComponents.Texts[i];
                if (td.Visible == 0 || (td.LayerMask & cam.LayerMask) == 0) continue;
                if (td.Content.Length <= 0 || td.TypewriterProgress <= 0f) continue;

                FontAssetData* font = AssetSystem.TryGetFont(in world.Assets, td.Font);
                if (font == null) continue;
                TextureAssetData* atlas = AssetSystem.TryGetTexture(in world.Assets, font->AtlasTexture);
                if (atlas == null || atlas->GpuSlot < 0) continue;

                ShaderHandle sh = td.CustomShader.IsValid ? td.CustomShader : shaders.SdfText;
                items.Add(new TextSortItem
                {
                    TextIndex = i,
                    AtlasTexGpuSlot = atlas->GpuSlot,
                    Shader = sh,
                    SortingOrder = td.SortingOrder,
                    DepthTest = td.DepthTest,
                });
            }

            if (items.Count == 0) return;
            SortTextItems(items.Data, 0, items.Count - 1);

            int batchStart = outBatches.Count;
            for (int k = 0; k < items.Count; k++)
            {
                if (outGlyphs.Count >= outGlyphs.Capacity || outBatches.Count >= outBatches.Capacity)
                {
                    if (log != null)
                        LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchText capacity cap reached (glyphs={outGlyphs.Count}/{outGlyphs.Capacity}, batches={outBatches.Count}/{outBatches.Capacity})");
                    break;
                }

                TextSortItem item = items[k];
                ref TextData td = ref world.RenderComponents.Texts[item.TextIndex];
                FontAssetData* font = AssetSystem.TryGetFont(in world.Assets, td.Font);
                if (font == null) continue;

                uint baseGlyph = (uint)outGlyphs.Count;
                int emitted = TextLayoutSystem.LayoutTextIntoGlyphs(in td, in *font, in world.Transforms, in cam, ref scratchArena, ref outGlyphs);
                if (emitted <= 0) continue;

                if (outBatches.Count > batchStart)
                {
                    ref TextBatchCmd prev = ref outBatches[outBatches.Count - 1];
                    if (prev.AtlasTexGpuSlot == item.AtlasTexGpuSlot && prev.Shader == item.Shader && prev.DepthTest == item.DepthTest)
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
                        AtlasTexGpuSlot = item.AtlasTexGpuSlot,
                        Shader = item.Shader,
                        DepthTest = item.DepthTest,
                        BaseInstance = baseGlyph,
                        InstanceCount = (uint)emitted,
                    });
                    stats.GlyphInstances += emitted;
                }
            }
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    private static void GatherAndBatchParticles(
        in RenderShaderDefaults shaders,
        in EngineWorld world,
        in CameraData cam,
        ref LinearArena scratchArena,
        ref NativeList<ParticleGpuInstance> outParticles,
        ref NativeList<ParticleBatchCmd> outBatches,
        ref RenderStats stats,
        LogState* log = null)
    {
        ref readonly ParticleTable pt = ref world.Particles;
        int emCap = pt.EmitterCapacity;
        if (pt.ParticleCount <= 0 || emCap <= 0) return;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            byte* visibleEmitter = scratchArena.Alloc<byte>(emCap, 16);
            int* counts = scratchArena.Alloc<int>(emCap, 16);
            int* starts = scratchArena.AllocUninitialized<int>(emCap, 16);
            int* cursors = scratchArena.AllocUninitialized<int>(emCap, 16);
            int* emitCounts = scratchArena.Alloc<int>(emCap, 16);

            bool anyVisible = false;
            for (int es = 0; es < emCap; es++)
            {
                if (pt.EmitterAlive[es] != 0 && (pt.Emitters[es].LayerMask & cam.LayerMask) != 0)
                {
                    visibleEmitter[es] = 1;
                    anyVisible = true;
                }
            }
            if (!anyVisible) return;

            int totalVisible = 0;
            for (int p = 0; p < pt.ParticleCount; p++)
            {
                ushort es = pt.EmitterSlot[p];
                if (es < emCap && visibleEmitter[es] != 0)
                {
                    counts[es]++;
                    totalVisible++;
                }
            }
            if (totalVisible == 0) return;

            int avail = outParticles.Capacity - outParticles.Count;
            if (avail <= 0)
            {
                if (log != null)
                    LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchParticles capacity cap reached ({outParticles.Count}/{outParticles.Capacity})");
                return;
            }

            int baseOut = outParticles.Count;
            int running = 0;
            for (int es = 0; es < emCap; es++)
            {
                starts[es] = baseOut + running;
                cursors[es] = baseOut + running;
                int c = counts[es];
                if (c == 0) continue;
                int take = (running + c <= avail) ? c : (avail - running);
                if (take < 0) take = 0;
                emitCounts[es] = take;
                running += take;
            }

            if (running < totalVisible && log != null)
                LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchParticles capacity cap reached ({baseOut + running}/{outParticles.Capacity})");
            if (running == 0) return;

            outParticles.Count = baseOut + running;

            for (int p = 0; p < pt.ParticleCount; p++)
            {
                ushort es = pt.EmitterSlot[p];
                if (es >= emCap || visibleEmitter[es] == 0) continue;
                int writeAt = cursors[es];
                if (writeAt >= starts[es] + emitCounts[es]) continue;
                cursors[es] = writeAt + 1;

                ref readonly ParticleEmitterData em = ref pt.Emitters[es];
                int cols = em.AtlasColumns > 0 ? em.AtlasColumns : 1;
                int rows = em.AtlasRows > 0 ? em.AtlasRows : 1;
                float uW = 1f / cols;
                float vH = 1f / rows;
                int f = pt.FrameIndex[p];
                Vec4 uv = new Vec4((f % cols) * uW, (f / cols) * vH, uW, vH);

                outParticles.Data[writeAt] = new ParticleGpuInstance
                {
                    PosSize = new Vec4(pt.PosX[p], pt.PosY[p], pt.PosZ[p], pt.Size[p]),
                    Color = pt.Color[p],
                    UvRect = uv,
                    RotExtra = new Vec4(pt.Rotation[p], em.StretchedBillboard, 0f, 0f),
                };
            }

            int batchStart = outBatches.Count;
            for (int es = 0; es < emCap; es++)
            {
                int count = emitCounts[es];
                if (count <= 0) continue;
                if (outBatches.Count >= outBatches.Capacity)
                {
                    if (log != null)
                        LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchParticles batch cap reached ({outBatches.Count}/{outBatches.Capacity})");
                    break;
                }

                ref readonly ParticleEmitterData em = ref pt.Emitters[es];
                int texSlot = -1;
                if (em.Texture.IsValid)
                {
                    TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, em.Texture);
                    if (tex != null) texSlot = tex->GpuSlot;
                }

                ShaderHandle sh = em.CustomShader.IsValid ? em.CustomShader : shaders.Particle;
                if (outBatches.Count > batchStart)
                {
                    ref ParticleBatchCmd prev = ref outBatches[outBatches.Count - 1];
                    if (prev.TextureGpuSlot == texSlot && prev.Shader == sh && prev.BlendMode == em.BlendMode)
                    {
                        prev.InstanceCount += (uint)count;
                        stats.ParticleInstances += count;
                        continue;
                    }
                }

                outBatches.Add(new ParticleBatchCmd
                {
                    TextureGpuSlot = texSlot,
                    Shader = sh,
                    BlendMode = em.BlendMode,
                    BaseInstance = (uint)starts[es],
                    InstanceCount = (uint)count,
                });
                stats.ParticleInstances += count;
            }
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool neededSamplerChanged(int prevCount, int nextCount) => prevCount != nextCount;

    /// <summary>
    /// Computes a texel-snapped right-handed orthographic LightViewProj matrix ([0, 1] clip Z)
    /// centered around lighting.ShadowCenter along lighting.SunDirection.
    /// </summary>
    public static Mat4 ComputeDirectionalLightViewProj(in SceneLighting lighting, uint shadowMapSize)
    {
        Vec3 sunDir = lighting.SunDirection.Normalized();
        if (sunDir.LengthSq() < 1e-6f) sunDir = new Vec3(0.45f, 0.85f, 0.35f).Normalized();

        float halfExtent = lighting.ShadowDistance > 0.001f ? MathF.Max(lighting.ShadowDistance, 0.25f) : 28f;
        uint mapSize = shadowMapSize >= 256u ? shadowMapSize : 2048u;

        Vec3 refUp = DetMath.Abs(Vec3.Dot(sunDir, Vec3.UnitY)) > 0.98f ? Vec3.UnitZ : Vec3.UnitY;
        Vec3 forward = -sunDir;
        Vec3 right = Vec3.Cross(forward, refUp).Normalized();
        Vec3 lightUp = Vec3.Cross(right, forward);

        // Snap center along light right/up axes to exact shadow texel increments to prevent shimmer
        float texelWorld = (2f * halfExtent) / mapSize;
        Vec3 center = lighting.ShadowCenter;
        float sx = MathF.Round(Vec3.Dot(center, right) / texelWorld) * texelWorld;
        float sy = MathF.Round(Vec3.Dot(center, lightUp) / texelWorld) * texelWorld;
        float sz = Vec3.Dot(center, forward);
        Vec3 snappedCenter = right * sx + lightUp * sy + forward * sz;

        float pullBack = halfExtent * 2.0f;
        Vec3 eye = snappedCenter + sunDir * pullBack;
        Mat4 lightView = Mat4.CreateLookAt(eye, snappedCenter, lightUp);
        Mat4 lightProj = Mat4.CreateOrtho(-halfExtent, halfExtent, -halfExtent, halfExtent, 0.5f, pullBack * 2.25f);
        return Mat4.Multiply(in lightProj, in lightView);
    }

    private static void GatherShadowCasters3D(
        in EngineWorld world,
        in SceneLighting lighting,
        ref LinearArena scratchArena,
        ref NativeList<Instance3DGpu> outInstances,
        ref NativeList<Batch3DCmd> outShadowBatches,
        LogState* log = null)
    {
        int count = world.RenderComponents.MeshRendererCount;
        if (count == 0) return;

        float halfExtent = lighting.ShadowDistance > 0.001f ? MathF.Max(lighting.ShadowDistance, 0.25f) : 28f;
        float maxRadius = halfExtent * 1.85f;
        Vec3 shadowCenter = lighting.ShadowCenter;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            var items = NativeList<Draw3DSortItem>.CreateInArena(ref scratchArena, count, 64);

            for (int i = 0; i < count; i++)
            {
                ref MeshRendererData mr = ref world.RenderComponents.MeshRenderers[i];
                if (mr.Visible == 0 || mr.CastShadow == 0) continue;

                MeshAssetData* mesh = AssetSystem.TryGetMesh(in world.Assets, mr.Mesh);
                if (mesh == null || mesh->GpuSlot < 0) continue;

                MaterialAssetData* mat = AssetSystem.TryGetMaterial(in world.Assets, mr.Material);
                if (mat != null && mat->AlphaMode == MaterialAlphaMode.Blend) continue; // Transparent meshes do not cast opaque depth shadows

                int td = TransformSystem.GetDenseIndex(in world.Transforms, mr.Entity);
                if (td < 0) continue;
                Mat4 worldMat = world.Transforms.WorldMatrix[td];

                Bounds3 wb = mesh->LocalBounds.Transform(in worldMat);
                Vec3 objCenter = (wb.Min + wb.Max) * 0.5f;
                float objRadius = (wb.Max - wb.Min).Length() * 0.5f;
                float distSq = Vec3.DistanceSq(objCenter, shadowCenter);
                float cullR = maxRadius + objRadius;
                if (distSq > cullR * cullR) continue;

                int jointOff = -1;
                if (mesh->HasSkinning != 0)
                {
                    AnimPlayerData* ap = AnimationSystem.TryGetPlayer(in world.Animations, mr.AnimPlayer);
                    if (ap != null && ap->FrameJointCount > 0) jointOff = ap->FrameJointOffset;
                }

                MaterialAlphaMode shadowAlphaMode = (mat != null && mat->AlphaMode == MaterialAlphaMode.Mask)
                    ? MaterialAlphaMode.Mask : MaterialAlphaMode.Opaque;
                byte doubleSided = (mat != null && mat->DoubleSided != 0) ? (byte)1 : (byte)0;
                int texSlot = -1;
                if (shadowAlphaMode == MaterialAlphaMode.Mask && mat != null && mat->BaseColorTexture.IsValid)
                {
                    TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, mat->BaseColorTexture);
                    if (tex != null) texSlot = tex->GpuSlot;
                }
                float alphaCutoff = shadowAlphaMode == MaterialAlphaMode.Mask && mat != null ? mat->AlphaCutoff : 0f;
                float baseAlpha = mat != null ? mat->BaseColorFactor.A * mr.Tint.A : mr.Tint.A;

                ulong key = ((ulong)shadowAlphaMode << 58)
                          | ((ulong)doubleSided << 57)
                          | ((ulong)((uint)(texSlot + 1) & 0xFFFFu) << 20)
                          | ((ulong)((uint)mesh->GpuSlot & 0xFFFFFu));

                items.Add(new Draw3DSortItem
                {
                    SortKey = key,
                    OriginalIndex = i,
                    MeshGpuSlot = mesh->GpuSlot,
                    TextureGpuSlot = texSlot,
                    AlphaMode = shadowAlphaMode,
                    DoubleSided = doubleSided,
                    Instance = new Instance3DGpu
                    {
                        Model = worldMat,
                        BaseColor = new ColorF(1f, 1f, 1f, baseAlpha),
                        Extra = new Vec4(0f, alphaCutoff, jointOff, 0f),
                    },
                });
            }

            if (items.Count == 0) return;

            Sort3DItems(items.Data, 0, items.Count - 1);

            int idx = 0;
            while (idx < items.Count)
            {
                if (outInstances.Count >= outInstances.Capacity || outShadowBatches.Count >= outShadowBatches.Capacity)
                {
                    if (log != null)
                        LogSystem.Warn(log, $"BatchRenderer.GatherShadowCasters3D capacity cap reached (instances={outInstances.Count}/{outInstances.Capacity}, shadowBatches={outShadowBatches.Count}/{outShadowBatches.Capacity})");
                    break;
                }

                Draw3DSortItem first = items[idx];
                uint baseInst = (uint)outInstances.Count;
                uint instCount = 0;

                while (idx < items.Count &&
                       items[idx].AlphaMode == first.AlphaMode &&
                       items[idx].DoubleSided == first.DoubleSided &&
                       items[idx].TextureGpuSlot == first.TextureGpuSlot &&
                       items[idx].MeshGpuSlot == first.MeshGpuSlot &&
                       outInstances.Count < outInstances.Capacity)
                {
                    outInstances.Add(items[idx].Instance);
                    instCount++;
                    idx++;
                }

                if (instCount > 0 && outShadowBatches.Count < outShadowBatches.Capacity)
                {
                    outShadowBatches.Add(new Batch3DCmd
                    {
                        MeshGpuSlot = first.MeshGpuSlot,
                        TextureGpuSlot = first.TextureGpuSlot,
                        Shader = ShaderHandle.Invalid,
                        AlphaMode = first.AlphaMode,
                        DoubleSided = first.DoubleSided,
                        BaseInstance = baseInst,
                        InstanceCount = instCount,
                    });
                }
            }
        }
        finally
        {
            scratchArena.Rollback(mark);
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
    private static void BindFragmentTextureAndShadow(nint pass, in GpuContext gpu, int texSlot, bool shadowsRendered)
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

        SDL.GPUTextureSamplerBinding* bindings = stackalloc SDL.GPUTextureSamplerBinding[2];
        bindings[0] = new SDL.GPUTextureSamplerBinding { Texture = tex, Sampler = samp };
        bindings[1] = new SDL.GPUTextureSamplerBinding
        {
            Texture = (shadowsRendered && gpu.ShadowDepthTexture != nint.Zero) ? gpu.ShadowDepthTexture : gpu.DefaultWhiteTexture,
            Sampler = gpu.SamplerNearestClamp,
        };
        SDL.BindGPUFragmentSamplers(pass, 0, (nint)bindings, 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint SelectQuadPipeline(GpuShaderSlot* s, int blendMode, bool depthTest)
    {
        if (!depthTest)
        {
            if (blendMode == 2 && s->PipelineAdditiveNoDepth != nint.Zero)
                return s->PipelineAdditiveNoDepth;
            return s->PipelineOverlayNoDepth;
        }
        return blendMode switch
        {
            0 => s->PipelineOpaque,
            2 => s->PipelineAdditive,
            _ => s->PipelineAlphaBlend,
        };
    }

    private static FrameUniformsGpu BuildFrameUniforms(in RenderCameraSnapshot cam, in RenderPacket packet, uint activeShadowMapSize)
    {
        ref readonly SceneLighting l = ref packet.Lighting;
        bool hasShadow = activeShadowMapSize > 0u && l.ShadowsEnabled != 0;
        float bias = l.ShadowBias > 0f ? l.ShadowBias : 0.0018f;
        float strength = hasShadow ? DetMath.Clamp01(l.ShadowStrength > 0f ? l.ShadowStrength : 0.72f) : 0f;
        float invMap = hasShadow ? (1f / activeShadowMapSize) : 0f;
        float normalBias = l.ShadowNormalBias >= 0f ? l.ShadowNormalBias : 0.04f;

        return new FrameUniformsGpu
        {
            ViewProj = cam.ViewProjMatrix,
            CameraPosTime = new Vec4(cam.ShakenPosition, packet.WorldTotalTime),
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
            LightViewProj = hasShadow ? packet.ShadowLightViewProj : Mat4.Identity,
            ShadowParams = new Vec4(bias, strength, invMap, normalBias),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Less3D(in Draw3DSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey < pivotKey || (a.SortKey == pivotKey && a.OriginalIndex < pivotIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Greater3D(in Draw3DSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey > pivotKey || (a.SortKey == pivotKey && a.OriginalIndex > pivotIdx);

    private static void Sort3DItems(Draw3DSortItem* arr, int lo, int hi)
    {
        if (lo >= hi) return;
        int mid = (lo + hi) >> 1;
        ulong pKey = arr[mid].SortKey;
        int pIdx = arr[mid].OriginalIndex;
        int i = lo, j = hi;
        while (i <= j)
        {
            while (Less3D(in arr[i], pKey, pIdx)) i++;
            while (Greater3D(in arr[j], pKey, pIdx)) j--;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareTextItem(in TextSortItem a, in TextSortItem b)
    {
        int c = a.SortingOrder.CompareTo(b.SortingOrder);
        if (c != 0) return c;
        c = a.DepthTest.CompareTo(b.DepthTest);
        if (c != 0) return c;
        c = a.Shader.Raw.CompareTo(b.Shader.Raw);
        if (c != 0) return c;
        c = a.AtlasTexGpuSlot.CompareTo(b.AtlasTexGpuSlot);
        if (c != 0) return c;
        return a.TextIndex.CompareTo(b.TextIndex);
    }

    private static void SortTextItems(TextSortItem* arr, int lo, int hi)
    {
        if (lo >= hi) return;
        int mid = (lo + hi) >> 1;
        TextSortItem pivot = arr[mid];
        int i = lo, j = hi;
        while (i <= j)
        {
            while (CompareTextItem(in arr[i], in pivot) < 0) i++;
            while (CompareTextItem(in arr[j], in pivot) > 0) j--;
            if (i <= j)
            {
                TextSortItem tmp = arr[i];
                arr[i] = arr[j];
                arr[j] = tmp;
                i++; j--;
            }
        }
        if (lo < j) SortTextItems(arr, lo, j);
        if (i < hi) SortTextItems(arr, i, hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LessSprite(in SpriteSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey < pivotKey || (a.SortKey == pivotKey && a.OriginalIndex < pivotIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GreaterSprite(in SpriteSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey > pivotKey || (a.SortKey == pivotKey && a.OriginalIndex > pivotIdx);

    private static void SortSpriteItems(SpriteSortItem* arr, int lo, int hi)
    {
        if (lo >= hi) return;
        int mid = (lo + hi) >> 1;
        ulong pKey = arr[mid].SortKey;
        int pIdx = arr[mid].OriginalIndex;
        int i = lo, j = hi;
        while (i <= j)
        {
            while (LessSprite(in arr[i], pKey, pIdx)) i++;
            while (GreaterSprite(in arr[j], pKey, pIdx)) j--;
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
