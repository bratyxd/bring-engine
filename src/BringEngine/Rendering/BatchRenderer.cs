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
using BringEngine.Simulation.Rig2D;
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
    private const int MaxInstancesPerCategoryCap = 65536;

    // GPU warning latch bits stored in GpuContext.WarnedCapsMask
    private const uint GpuWarnGrowInst3D = 1u << 0;
    private const uint GpuWarnGrowJoints = 1u << 1;
    private const uint GpuWarnGrowSprites = 1u << 2;
    private const uint GpuWarnGrowGlyphs = 1u << 3;
    private const uint GpuWarnGrowParticles = 1u << 4;
    private const uint GpuWarnGrowTransfer = 1u << 5;
    private const uint GpuWarnMapTransfer = 1u << 6;
    private const uint GpuWarnGrowRig2DVerts = 1u << 7;
    private const uint GpuWarnGrowRig2DIndices = 1u << 8;

    // Extract warning latch bits (auto-resets whenever LogState* changes; resettable for tests)
    private const uint ExtractWarnCap3D = 1u << 0;
    private const uint ExtractWarnCapSprites = 1u << 1;
    private const uint ExtractWarnCapText = 1u << 2;
    private const uint ExtractWarnCapParticles = 1u << 3;
    private const uint ExtractWarnCapParticleBatches = 1u << 4;
    private const uint ExtractWarnCapShadows = 1u << 5;
    private const uint ExtractWarnCapRig2D = 1u << 6;

    private static long s_warnedExtractLogPtr;
    private static uint s_warnedExtractCapsMask;

    public static void ResetWarningLatchesForTest()
    {
        Volatile.Write(ref s_warnedExtractLogPtr, 0);
        Volatile.Write(ref s_warnedExtractCapsMask, 0u);
    }

    private static bool TryLatchExtractWarn(LogState* log, uint bit)
    {
        if (log == null) return false;
        long ptr = (long)(nint)log;
        if (Volatile.Read(ref s_warnedExtractLogPtr) != ptr)
        {
            if (Interlocked.Exchange(ref s_warnedExtractLogPtr, ptr) != ptr)
                Volatile.Write(ref s_warnedExtractCapsMask, 0u);
        }
        uint prev = Interlocked.Or(ref s_warnedExtractCapsMask, bit);
        return (prev & bit) == 0u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryLatchGpuWarn(ref GpuContext gpu, uint bit)
    {
        if ((gpu.WarnedCapsMask & bit) != 0u) return false;
        gpu.WarnedCapsMask |= bit;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UploadedFrameCounts
    {
        public uint Inst3D;
        public uint Joints;
        public uint Sprites;
        public uint Glyphs;
        public uint Particles;
        public uint Rig2DVerts;
        public uint Rig2DIndices;

        public uint Bytes3D;
        public uint BytesJoints;
        public uint BytesSprites;
        public uint BytesGlyphs;
        public uint BytesParticles;
        public uint BytesRig2DVerts;
        public uint BytesRig2DIndices;

        public uint Off3D;
        public uint OffJoints;
        public uint OffSprites;
        public uint OffGlyphs;
        public uint OffParticles;
        public uint OffRig2DVerts;
        public uint OffRig2DIndices;

        public uint TotalBytes;
    }

    private struct Rig2DSortItem
    {
        public ulong SortKey;
        public int OriginalIndex;
        public int InstanceIndex;
    }

    private struct Draw3DSortItem
    {
        public ulong SortKey;
        public int OriginalIndex;
        public int MeshGpuSlot;
        public uint MeshGpuGen;
        public int TextureGpuSlot;
        public uint TextureGpuGen;
        public ShaderHandle Shader;
        public MaterialAlphaMode AlphaMode;
        public byte DoubleSided;
        public Instance3DGpu Instance;
    }

    private struct TextSortItem
    {
        public int TextIndex;
        public int AtlasTexGpuSlot;
        public uint AtlasTexGpuGen;
        public ShaderHandle Shader;
        public short SortingOrder;
        public byte DepthTest;
    }

    private struct SpriteSortItem
    {
        public ulong SortKey;
        public int OriginalIndex;
        public int TextureGpuSlot;
        public uint TextureGpuGen;
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

        var camSnapshots = packetArena.AllocSlice<RenderCameraSnapshot>(camCount);

        // Snapshot skeletal joint palette into packetArena only when it belongs to this exact arena generation
        NativeSlice<Mat4> jointCopy = default;
        int jointLen = world.Animations.FrameJointPalette.Length;
        bool jointPaletteFresh = jointLen > 0 &&
                                 world.Animations.FrameJointPalette.Ptr != null &&
                                 world.Animations.FrameJointPaletteArenaBase == packetArena.BasePtr &&
                                 world.Animations.FrameJointPaletteArenaReset == packetArena.ResetCount;
        if (jointPaletteFresh)
        {
            ulong jBytes64 = (ulong)jointLen * (ulong)sizeof(Mat4);
            if (jBytes64 + 64UL <= packetArena.Remaining)
            {
                jointCopy = packetArena.AllocSlice<Mat4>(jointLen, 64, zeroed: false);
                Buffer.MemoryCopy(world.Animations.FrameJointPalette.Ptr, jointCopy.Ptr, jBytes64, jBytes64);
            }
        }

        bool wantShadows = lighting.ShadowsEnabled != 0 && lighting.SunIntensity > 0.001f && world.RenderComponents.MeshRendererCount > 0;
        int shadowDesired = wantShadows ? Math.Clamp(world.RenderComponents.MeshRendererCount, 0, MaxInstancesPerCategoryCap) : 0;
        int main3DDesired = (int)Math.Clamp((long)world.RenderComponents.MeshRendererCount * camCount, 0L, MaxInstancesPerCategoryCap);
        int cap3D = Math.Clamp(main3DDesired + shadowDesired, 64, MaxInstancesPerCategoryCap);
        int capSprites = (int)Math.Clamp((long)spriteSlotsPerCam * camCount, 64L, MaxInstancesPerCategoryCap);
        int minGlyphCap = world.RenderComponents.TextCount > 0 ? 512 : 128;
        int capGlyphs = (int)Math.Clamp((long)Math.Max(glyphSlotsPerCam, minGlyphCap) * camCount, 128L, MaxInstancesPerCategoryCap);
        int capParticles = (int)Math.Clamp((long)world.Particles.ParticleCount * camCount, 64L, MaxInstancesPerCategoryCap);

        int bat3D = Math.Min(cap3D, 4096);
        int batShadow = wantShadows ? Math.Clamp(world.RenderComponents.MeshRendererCount, 16, 2048) : 0;
        int batSprites = Math.Min(capSprites, 2048);
        int batText = Math.Clamp(Math.Max(64, world.RenderComponents.TextCount * camCount), 64, 4096);
        int batParticles = Math.Clamp(Math.Max(64, world.Particles.ActiveEmitters * camCount), 64, 4096);

        int capRig2DVerts = 0;
        int capRig2DIndices = 0;
        int batRig2D = 0;
        if (world.Rigs.InstanceCount > 0)
        {
            long estVerts = 0;
            long estIndices = 0;
            long estBatches = 0;
            for (int ri = 0; ri < world.Rigs.InstanceCount; ri++)
            {
                ref readonly Rig2DInstanceData inst = ref world.Rigs.Instances[ri];
                if (inst.Visible == 0) continue;
                Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, inst.Asset);
                if (asset == null) continue;
                estBatches += Math.Max(1, asset->SlotCount);
                for (int s = 0; s < inst.SlotSliceCount; s++)
                {
                    int attIdx = world.Rigs.SlotStates[inst.SlotSliceStart + s].ActiveAttachmentIndex;
                    if ((uint)attIdx >= (uint)asset->AttachmentCount) continue;
                    ref readonly Rig2DAttachmentData att = ref asset->Attachments[attIdx];
                    if (att.Kind == Rig2DAttachmentKind.WeightedMesh)
                    {
                        estVerts += att.VertexCount;
                        estIndices += att.IndexCount;
                    }
                    else if (att.Kind == Rig2DAttachmentKind.Region || att.Kind == Rig2DAttachmentKind.Sequence)
                    {
                        estVerts += 4;
                        estIndices += 6;
                    }
                }
            }
            if (estVerts > 0 && estIndices > 0)
            {
                capRig2DVerts = (int)Math.Clamp(estVerts * camCount, 64L, 131072L);
                capRig2DIndices = (int)Math.Clamp(estIndices * camCount, 96L, 393216L);
                batRig2D = (int)Math.Clamp(estBatches * camCount, 16L, 4096L);
            }
        }

        // Bound packet list allocations cleanly to remaining packetArena space so large scenes never throw LinearArena overflow
        FitPacketCapsToArenaBudget(
            packetArena.Remaining,
            wantShadows,
            ref cap3D, ref bat3D, ref batShadow,
            ref capSprites, ref batSprites,
            ref capRig2DVerts, ref capRig2DIndices, ref batRig2D,
            ref capGlyphs, ref batText,
            ref capParticles, ref batParticles);

        var allInstances3D = cap3D > 0 ? NativeList<Instance3DGpu>.CreateInArena(ref packetArena, cap3D, 64) : default;
        var allBatches3D = bat3D > 0 ? NativeList<Batch3DCmd>.CreateInArena(ref packetArena, bat3D, 16) : default;
        var shadowBatches3D = (wantShadows && batShadow > 0)
            ? NativeList<Batch3DCmd>.CreateInArena(ref packetArena, batShadow, 16)
            : default;
        var allSprites = capSprites > 0 ? NativeList<SpriteGpuInstance>.CreateInArena(ref packetArena, capSprites, 64) : default;
        var allSpriteBatches = batSprites > 0 ? NativeList<SpriteBatchCmd>.CreateInArena(ref packetArena, batSprites, 16) : default;
        var allRig2DVerts = capRig2DVerts > 0 ? NativeList<Rig2DVertexGpu>.CreateInArena(ref packetArena, capRig2DVerts, 64) : default;
        var allRig2DIndices = capRig2DIndices > 0 ? NativeList<uint>.CreateInArena(ref packetArena, capRig2DIndices, 64) : default;
        var allRig2DBatches = batRig2D > 0 ? NativeList<Rig2DBatchCmd>.CreateInArena(ref packetArena, batRig2D, 16) : default;
        var allGlyphs = capGlyphs > 0 ? NativeList<GlyphGpuInstance>.CreateInArena(ref packetArena, capGlyphs, 64) : default;
        var allTextBatches = batText > 0 ? NativeList<TextBatchCmd>.CreateInArena(ref packetArena, batText, 16) : default;
        var allParticles = capParticles > 0 ? NativeList<ParticleGpuInstance>.CreateInArena(ref packetArena, capParticles, 64) : default;
        var allParticleBatches = batParticles > 0 ? NativeList<ParticleBatchCmd>.CreateInArena(ref packetArena, batParticles, 16) : default;

        bool rig2DDeformFresh = world.Rigs.FrameDeformDeltas.Length > 0 &&
                                world.Rigs.FrameDeformDeltas.Ptr != null &&
                                world.Rigs.FrameDeformArenaBase == packetArena.BasePtr &&
                                world.Rigs.FrameDeformArenaReset == packetArena.ResetCount;

        int availableJointCount = jointCopy.Length;

        // Gather directional shadow casters FIRST with a dedicated reserved share so multi-camera gathering never starves shadows
        if (wantShadows && allInstances3D.Capacity > 0 && shadowBatches3D.Capacity > 0)
        {
            uint sSize = lighting.ShadowMapSize >= 256u ? Math.Min(lighting.ShadowMapSize, 4096u) : 2048u;
            packet.ShadowMapSize = sSize;
            packet.ShadowLightViewProj = ComputeDirectionalLightViewProj(in lighting, sSize);
            int fairShare = Math.Max(64, allInstances3D.Capacity / (camCount + 1));
            int shadowInstanceLimit = main3DDesired > 0
                ? Math.Min(allInstances3D.Capacity, Math.Min(shadowDesired, fairShare))
                : allInstances3D.Capacity;
            GatherShadowCasters3D(in world, in lighting, availableJointCount, shadowInstanceLimit, ref scratchArena, ref allInstances3D, ref shadowBatches3D, log);
            packet.ShadowsActive = (byte)(shadowBatches3D.Count > 0 ? 1 : 0);
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
            GatherAndBatch3D(in shaders, in world, in cam, availableJointCount, ref scratchArena, ref allInstances3D, ref allBatches3D, ref stats, log);
            snap.Batch3DCount = allBatches3D.Count - snap.Batch3DStart;

            // 1B. Gather & Batch 2D Sprites in 3D/HUD
            snap.SpriteBatchStart = allSpriteBatches.Count;
            GatherAndBatchSprites(in shaders, in world, in cam, ref scratchArena, ref allSprites, ref allSpriteBatches, ref stats, log);
            snap.SpriteBatchCount = allSpriteBatches.Count - snap.SpriteBatchStart;

            // 1B2. Gather & Batch Rig2D Skeletal Meshes
            snap.Rig2DBatchStart = allRig2DBatches.Count;
            if (world.Rigs.InstanceCount > 0 && allRig2DVerts.Capacity > 0 && allRig2DIndices.Capacity > 0 && allRig2DBatches.Capacity > 0)
                GatherAndBatchRig2D(in shaders, in world, in cam, rig2DDeformFresh, ref scratchArena, ref allRig2DVerts, ref allRig2DIndices, ref allRig2DBatches, ref stats, log);
            snap.Rig2DBatchCount = allRig2DBatches.Count - snap.Rig2DBatchStart;

            // 1C. Gather, Layout & Batch Advanced SDF Text
            snap.TextBatchStart = allTextBatches.Count;
            GatherAndBatchText(in shaders, in world, in cam, ref scratchArena, ref allGlyphs, ref allTextBatches, ref stats, log);
            snap.TextBatchCount = allTextBatches.Count - snap.TextBatchStart;

            // 1D. Gather & Batch SoA Particles
            snap.ParticleBatchStart = allParticleBatches.Count;
            GatherAndBatchParticles(in shaders, in world, in cam, ref scratchArena, ref allParticles, ref allParticleBatches, ref stats, log);
            snap.ParticleBatchCount = allParticleBatches.Count - snap.ParticleBatchStart;
        }

        packet.Cameras = camSnapshots;
        packet.Instances3D = allInstances3D;
        packet.Batches3D = allBatches3D;
        packet.ShadowBatches3D = shadowBatches3D;
        packet.JointPalette = jointCopy;
        packet.Sprites = allSprites;
        packet.SpriteBatches = allSpriteBatches;
        packet.Rig2DVertices = allRig2DVerts;
        packet.Rig2DIndices = allRig2DIndices;
        packet.Rig2DBatches = allRig2DBatches;
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
            GpuSystem.FlushDeferredReleases(ref gpu);
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
            in packet.Particles,
            in packet.Rig2DVertices,
            in packet.Rig2DIndices,
            out UploadedFrameCounts uploaded))
        {
            stats.CopyPasses++;
        }
        long uploadEnd = Stopwatch.GetTimestamp();
        timings.UploadMs = (uploadEnd - uploadStart) * s_ticksToMs;

        uint maxInst3D = uploaded.Inst3D;
        uint maxSprites = uploaded.Sprites;
        uint maxGlyphs = uploaded.Glyphs;
        uint maxParticles = uploaded.Particles;
        uint maxRig2DVerts = uploaded.Rig2DVerts;
        uint maxRig2DIndices = uploaded.Rig2DIndices;

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
                var sScissor = new SDL.Rect { X = 0, Y = 0, W = (int)sSize, H = (int)sSize };
                SDL.SetGPUScissor(spass, in sScissor);

                var sfu = new ShadowFrameUniformsGpu { LightViewProj = packet.ShadowLightViewProj };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&sfu), (uint)sizeof(ShadowFrameUniformsGpu));

                nint sBoundPipe = nint.Zero;
                int sBoundMesh = -1;
                int sBoundTex = int.MinValue;
                uint sBoundTexGen = 0u;
                for (int bi = 0; bi < packet.ShadowBatches3D.Count; bi++)
                {
                    Batch3DCmd b = packet.ShadowBatches3D[bi];
                    if (!IsMeshSlotValid(in gpu, b.MeshGpuSlot, b.MeshGpuGen)) continue;
                    if (b.BaseInstance >= maxInst3D) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxInst3D - b.BaseInstance);
                    if (drawInst == 0) continue;

                    bool isMasked = b.AlphaMode == MaterialAlphaMode.Mask && gpu.ShadowMaskPipeline != nint.Zero;
                    if (isMasked && b.TextureGpuSlot >= 0 && !IsTextureSlotValid(in gpu, b.TextureGpuSlot, b.TextureGpuGen)) continue;

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
                        sBoundTexGen = 0u;
                    }

                    if (isMasked && (b.TextureGpuSlot != sBoundTex || b.TextureGpuGen != sBoundTexGen || pipeChanged))
                    {
                        BindFragmentTexture(spass, in gpu, b.TextureGpuSlot, b.TextureGpuGen);
                        sBoundTex = b.TextureGpuSlot;
                        sBoundTexGen = b.TextureGpuGen;
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
            bool validViewport = TryComputeClampedViewport(cam.ViewportRect, targetW, targetH, out SDL.GPUViewport vp, out SDL.Rect scissor);
            bool isFullViewport = validViewport && scissor.X == 0 && scissor.Y == 0 && scissor.W == (int)targetW && scissor.H == (int)targetH;

            SDL.GPULoadOp colorLoad = (ci == 0 || (cam.ClearFlags == CameraClearFlags.SolidColor && isFullViewport))
                ? SDL.GPULoadOp.Clear : SDL.GPULoadOp.Load;
            SDL.GPULoadOp depthLoad = (ci == 0 || cam.ClearFlags == CameraClearFlags.SolidColor || cam.ClearFlags == CameraClearFlags.DepthOnly)
                ? SDL.GPULoadOp.Clear : SDL.GPULoadOp.Load;

            if (!validViewport && colorLoad == SDL.GPULoadOp.Load && depthLoad == SDL.GPULoadOp.Load)
                continue;

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

            if (!validViewport)
            {
                SDL.EndGPURenderPass(pass);
                continue;
            }

            SDL.SetGPUViewport(pass, in vp);
            SDL.SetGPUScissor(pass, in scissor);

            nint boundPipeline = nint.Zero;
            nint boundStorage0 = nint.Zero;
            int boundMeshSlot = -1;
            int boundTexSlot = int.MinValue;
            uint boundTexGen = 0u;
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
                    if (!IsMeshSlotValid(in gpu, b.MeshGpuSlot, b.MeshGpuGen)) continue;
                    if (b.TextureGpuSlot >= 0 && !IsTextureSlotValid(in gpu, b.TextureGpuSlot, b.TextureGpuGen)) continue;
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
                    if (pipe == nint.Zero) continue;

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
                    if (b.TextureGpuSlot != boundTexSlot || b.TextureGpuGen != boundTexGen || neededSamplerChanged(boundSamplerCount, neededSamplers) || passChanged)
                    {
                        if (neededSamplers >= 2)
                            BindFragmentTextureAndShadow(pass, in gpu, b.TextureGpuSlot, b.TextureGpuGen, shadowsRendered);
                        else if (neededSamplers == 1)
                            BindFragmentTexture(pass, in gpu, b.TextureGpuSlot, b.TextureGpuGen);
                        boundTexSlot = b.TextureGpuSlot;
                        boundTexGen = b.TextureGpuGen;
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
                    if (b.TextureGpuSlot >= 0 && !IsTextureSlotValid(in gpu, b.TextureGpuSlot, b.TextureGpuGen)) continue;
                    if (b.BaseInstance >= maxSprites) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxSprites - b.BaseInstance);
                    if (drawInst == 0) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinSpriteShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.InstancedQuad, gpu.BuiltinSpriteShader);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = SelectQuadPipeline(sSlot, b.BlendMode == SpriteBlendMode.Additive ? 2 : (b.BlendMode == SpriteBlendMode.AlphaCutout ? 0 : 1), b.DepthTest != 0);
                    if (pipe == nint.Zero) continue;

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
                        if (b.TextureGpuSlot != boundTexSlot || b.TextureGpuGen != boundTexGen || boundSamplerCount != neededSamplers || passChanged)
                        {
                            BindFragmentTexture(pass, in gpu, b.TextureGpuSlot, b.TextureGpuGen);
                            boundTexSlot = b.TextureGpuSlot;
                            boundTexGen = b.TextureGpuGen;
                            boundSamplerCount = neededSamplers;
                            passChanged = true;
                        }
                    }
                    else if (boundSamplerCount != 0)
                    {
                        boundTexSlot = int.MinValue;
                        boundTexGen = 0u;
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

            // --- Draw Batched Rig2D Skeletal Meshes ---
            if (cam.Rig2DBatchCount > 0 &&
                maxRig2DVerts > 0u &&
                maxRig2DVerts == (uint)packet.Rig2DVertices.Count &&
                maxRig2DIndices > 0u &&
                gpu.Rig2DVertexBuffer != nint.Zero &&
                gpu.Rig2DIndexBuffer != nint.Zero)
            {
                var cu = new SimpleCameraUniformsGpu { ViewProj = cam.ViewProjMatrix, CameraPosTime = new Vec4(cam.ShakenPosition, packet.WorldTotalTime) };
                SDL.PushGPUVertexUniformData(cmd, 0, (nint)(&cu), (uint)sizeof(SimpleCameraUniformsGpu));

                bool rig2DBuffersBound = false;
                boundSamplerCount = -1;

                for (int bi = 0; bi < cam.Rig2DBatchCount; bi++)
                {
                    Rig2DBatchCmd b = packet.Rig2DBatches[cam.Rig2DBatchStart + bi];
                    if (b.TextureGpuSlot >= 0 && !IsTextureSlotValid(in gpu, b.TextureGpuSlot, b.TextureGpuGen)) continue;
                    if (b.IndexOffset >= maxRig2DIndices) continue;
                    uint drawIndices = Math.Min(b.IndexCount, maxRig2DIndices - b.IndexOffset);
                    drawIndices -= drawIndices % 3u;
                    if (drawIndices == 0u) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinRig2DShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.Rig2DMesh, gpu.BuiltinRig2DShader);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = SelectRig2DPipeline(sSlot, b.BlendMode, b.DepthTest != 0);
                    if (pipe == nint.Zero) continue;

                    if (pipe != boundPipeline)
                    {
                        SDL.BindGPUGraphicsPipeline(pass, pipe);
                        boundPipeline = pipe;
                        stats.PipelineBinds++;
                        passChanged = true;
                    }

                    if (!rig2DBuffersBound || passChanged)
                    {
                        var rvb = new SDL.GPUBufferBinding { Buffer = gpu.Rig2DVertexBuffer, Offset = 0 };
                        var rib = new SDL.GPUBufferBinding { Buffer = gpu.Rig2DIndexBuffer, Offset = 0 };
                        SDL.BindGPUVertexBuffers(pass, 0, (nint)(&rvb), 1);
                        SDL.BindGPUIndexBuffer(pass, in rib, SDL.GPUIndexElementSize.IndexElementSize32Bit);
                        rig2DBuffersBound = true;
                        boundMeshSlot = -1;
                        quadIndexBound = false;
                    }

                    int neededSamplers = sSlot->FragmentSamplerCount;
                    if (neededSamplers >= 1)
                    {
                        if (b.TextureGpuSlot != boundTexSlot || b.TextureGpuGen != boundTexGen || boundSamplerCount != neededSamplers || passChanged)
                        {
                            BindFragmentTexture(pass, in gpu, b.TextureGpuSlot, b.TextureGpuGen);
                            boundTexSlot = b.TextureGpuSlot;
                            boundTexGen = b.TextureGpuGen;
                            boundSamplerCount = neededSamplers;
                            passChanged = true;
                        }
                    }
                    else if (boundSamplerCount != 0)
                    {
                        boundTexSlot = int.MinValue;
                        boundTexGen = 0u;
                        boundSamplerCount = 0;
                        passChanged = true;
                    }
                    if (passChanged) stats.SetPassCalls++;

                    SDL.DrawGPUIndexedPrimitives(pass, drawIndices, 1, b.IndexOffset, 0, 0);
                    stats.DrawCalls++;
                    stats.Triangles += (int)(drawIndices / 3u);
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
                    if (b.TextureGpuSlot >= 0 && !IsTextureSlotValid(in gpu, b.TextureGpuSlot, b.TextureGpuGen)) continue;
                    if (b.BaseInstance >= maxParticles) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxParticles - b.BaseInstance);
                    if (drawInst == 0) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinParticleShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.InstancedQuad, gpu.BuiltinParticleShader);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = b.BlendMode == ParticleBlendMode.Additive ? sSlot->PipelineAdditive : sSlot->PipelineAlphaBlend;
                    if (pipe == nint.Zero) continue;

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
                        if (b.TextureGpuSlot != boundTexSlot || b.TextureGpuGen != boundTexGen || boundSamplerCount != neededSamplers || passChanged)
                        {
                            BindFragmentTexture(pass, in gpu, b.TextureGpuSlot, b.TextureGpuGen);
                            boundTexSlot = b.TextureGpuSlot;
                            boundTexGen = b.TextureGpuGen;
                            boundSamplerCount = neededSamplers;
                            passChanged = true;
                        }
                    }
                    else if (boundSamplerCount != 0)
                    {
                        boundTexSlot = int.MinValue;
                        boundTexGen = 0u;
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
                    if (!IsTextureSlotValid(in gpu, b.AtlasTexGpuSlot, b.AtlasTexGpuGen)) continue;
                    if (b.BaseInstance >= maxGlyphs) continue;
                    uint drawInst = Math.Min(b.InstanceCount, maxGlyphs - b.BaseInstance);
                    if (drawInst == 0) continue;

                    ShaderHandle sh = b.Shader.IsValid ? b.Shader : gpu.BuiltinSdfTextShader;
                    GpuShaderSlot* sSlot = ResolveShaderSlot(ref gpu, sh, ShaderVertexLayout.InstancedQuad, gpu.BuiltinSdfTextShader);
                    if (sSlot == null) continue;

                    bool passChanged = false;
                    nint pipe = b.DepthTest != 0 ? sSlot->PipelineAlphaBlend : sSlot->PipelineOverlayNoDepth;
                    if (pipe == nint.Zero) continue;

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
                        if (b.AtlasTexGpuSlot != boundTexSlot || b.AtlasTexGpuGen != boundTexGen || boundSamplerCount != neededSamplers || passChanged)
                        {
                            BindFragmentTexture(pass, in gpu, b.AtlasTexGpuSlot, b.AtlasTexGpuGen);
                            boundTexSlot = b.AtlasTexGpuSlot;
                            boundTexGen = b.AtlasTexGpuGen;
                            boundSamplerCount = neededSamplers;
                            passChanged = true;
                        }
                    }
                    else if (boundSamplerCount != 0)
                    {
                        boundTexSlot = int.MinValue;
                        boundTexGen = 0u;
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

        // 6. Safely drain any deferred GPU releases queued during Logic Thread bundle unloads
        GpuSystem.FlushDeferredReleases(ref gpu);

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMeshSlotValid(in GpuContext gpu, int slot, uint expectedGen)
    {
        if ((uint)slot >= (uint)gpu.MaxMeshes || gpu.Meshes == null || gpu.Meshes[slot].Alive == 0)
            return false;
        return gpu.MeshSlotGens == null || gpu.MeshSlotGens[slot] == expectedGen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTextureSlotValid(in GpuContext gpu, int slot, uint expectedGen)
    {
        if ((uint)slot >= (uint)gpu.MaxTextures || gpu.Textures == null || gpu.Textures[slot].Alive == 0)
            return false;
        return gpu.TextureSlotGens == null || gpu.TextureSlotGens[slot] == expectedGen;
    }

    internal static bool TryComputeClampedViewport(
        Vec4 viewportRect,
        uint targetW,
        uint targetH,
        out SDL.GPUViewport vp,
        out SDL.Rect scissor)
    {
        vp = default;
        scissor = default;
        if (targetW == 0u || targetH == 0u) return false;
        if (!float.IsFinite(viewportRect.X) || !float.IsFinite(viewportRect.Y) ||
            !float.IsFinite(viewportRect.Z) || !float.IsFinite(viewportRect.W))
            return false;

        float x0 = Math.Clamp(viewportRect.X * targetW, 0f, (float)targetW);
        float y0 = Math.Clamp(viewportRect.Y * targetH, 0f, (float)targetH);
        float x1 = Math.Clamp((viewportRect.X + viewportRect.Z) * targetW, x0, (float)targetW);
        float y1 = Math.Clamp((viewportRect.Y + viewportRect.W) * targetH, y0, (float)targetH);

        float w = x1 - x0;
        float h = y1 - y0;
        if (w < 1f || h < 1f) return false;

        int sx = Math.Clamp((int)MathF.Floor(x0), 0, (int)targetW);
        int sy = Math.Clamp((int)MathF.Floor(y0), 0, (int)targetH);
        int sx1 = Math.Clamp((int)MathF.Ceiling(x1), sx, (int)targetW);
        int sy1 = Math.Clamp((int)MathF.Ceiling(y1), sy, (int)targetH);
        int sw = sx1 - sx;
        int sh = sy1 - sy;
        if (sw < 1 || sh < 1) return false;

        vp = new SDL.GPUViewport
        {
            X = x0,
            Y = y0,
            W = w,
            H = h,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        scissor = new SDL.Rect
        {
            X = sx,
            Y = sy,
            W = sw,
            H = sh,
        };
        return true;
    }

    private static void FitPacketCapsToArenaBudget(
        nuint availBytes,
        bool wantShadows,
        ref int cap3D, ref int bat3D, ref int batShadow,
        ref int capSprites, ref int batSprites,
        ref int capRig2DVerts, ref int capRig2DIndices, ref int batRig2D,
        ref int capGlyphs, ref int batText,
        ref int capParticles, ref int batParticles)
    {
        const ulong alignSlack = 1536UL;
        static ulong CalcBytes(int c3D, int b3D, int bSh, int cSp, int bSp, int cR2V, int cR2I, int bR2, int cGl, int bTx, int cPt, int bPt)
        {
            return (ulong)c3D * (ulong)sizeof(Instance3DGpu)
                 + (ulong)b3D * (ulong)sizeof(Batch3DCmd)
                 + (ulong)bSh * (ulong)sizeof(Batch3DCmd)
                 + (ulong)cSp * (ulong)sizeof(SpriteGpuInstance)
                 + (ulong)bSp * (ulong)sizeof(SpriteBatchCmd)
                 + (ulong)cR2V * (ulong)sizeof(Rig2DVertexGpu)
                 + (ulong)cR2I * (ulong)sizeof(uint)
                 + (ulong)bR2 * (ulong)sizeof(Rig2DBatchCmd)
                 + (ulong)cGl * (ulong)sizeof(GlyphGpuInstance)
                 + (ulong)bTx * (ulong)sizeof(TextBatchCmd)
                 + (ulong)cPt * (ulong)sizeof(ParticleGpuInstance)
                 + (ulong)bPt * (ulong)sizeof(ParticleBatchCmd)
                 + alignSlack;
        }

        ulong budget = availBytes;
        ulong needed = CalcBytes(cap3D, bat3D, batShadow, capSprites, batSprites, capRig2DVerts, capRig2DIndices, batRig2D, capGlyphs, batText, capParticles, batParticles);
        if (needed <= budget) return;

        if (budget <= alignSlack)
        {
            cap3D = bat3D = batShadow = capSprites = batSprites = capRig2DVerts = capRig2DIndices = batRig2D = capGlyphs = batText = capParticles = batParticles = 0;
            return;
        }

        double ratio = (double)(budget - alignSlack) / (double)(needed - alignSlack);
        for (int iter = 0; iter < 4 && needed > budget; iter++)
        {
            cap3D = Math.Max(0, (int)(cap3D * ratio));
            bat3D = Math.Min(cap3D, Math.Max(0, (int)(bat3D * ratio)));
            batShadow = wantShadows ? Math.Min(cap3D, Math.Max(0, (int)(batShadow * ratio))) : 0;
            capSprites = Math.Max(0, (int)(capSprites * ratio));
            batSprites = Math.Min(capSprites, Math.Max(0, (int)(batSprites * ratio)));
            capRig2DVerts = Math.Max(0, (int)(capRig2DVerts * ratio));
            capRig2DIndices = Math.Max(0, (int)(capRig2DIndices * ratio));
            batRig2D = Math.Max(0, (int)(batRig2D * ratio));
            capGlyphs = Math.Max(0, (int)(capGlyphs * ratio));
            batText = Math.Max(0, (int)(batText * ratio));
            capParticles = Math.Max(0, (int)(capParticles * ratio));
            batParticles = Math.Max(0, (int)(batParticles * ratio));

            needed = CalcBytes(cap3D, bat3D, batShadow, capSprites, batSprites, capRig2DVerts, capRig2DIndices, batRig2D, capGlyphs, batText, capParticles, batParticles);
            if (needed <= budget) break;
            ratio = 0.85;
        }

        if (needed > budget)
        {
            cap3D = bat3D = batShadow = capSprites = batSprites = capRig2DVerts = capRig2DIndices = batRig2D = capGlyphs = batText = capParticles = batParticles = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong AlignUpU64(ulong value, ulong alignment)
    {
        ulong mask = alignment - 1UL;
        return (value + mask) & ~mask;
    }

    /// <summary>
    /// Pure element-aligned upload budget calculation bounding all per-frame GPU streams by both
    /// their individual buffer capacities and the shared UploadTransferBuffer capacity.
    /// </summary>
    public static UploadedFrameCounts ComputeUploadBudget(
        int reqInst3D,
        int reqJoints,
        int reqSprites,
        int reqGlyphs,
        int reqParticles,
        uint inst3DBufBytes,
        uint jointBufBytes,
        uint spriteBufBytes,
        uint glyphBufBytes,
        uint particleBufBytes,
        uint transferBufBytes,
        int reqRig2DVerts = 0,
        int reqRig2DIndices = 0,
        uint rig2DVertBufBytes = 0,
        uint rig2DIdxBufBytes = 0)
    {
        uint sz3D = (uint)sizeof(Instance3DGpu);
        uint szJ = (uint)sizeof(Mat4);
        uint szS = (uint)sizeof(SpriteGpuInstance);
        uint szG = (uint)sizeof(GlyphGpuInstance);
        uint szP = (uint)sizeof(ParticleGpuInstance);
        uint szRV = (uint)sizeof(Rig2DVertexGpu);
        uint szRI = (uint)sizeof(uint);

        uint c3D = reqInst3D > 0 ? Math.Min((uint)reqInst3D, inst3DBufBytes / sz3D) : 0u;
        uint cJ = reqJoints > 0 ? Math.Min((uint)reqJoints, jointBufBytes / szJ) : 0u;
        uint cS = reqSprites > 0 ? Math.Min((uint)reqSprites, spriteBufBytes / szS) : 0u;
        uint cG = reqGlyphs > 0 ? Math.Min((uint)reqGlyphs, glyphBufBytes / szG) : 0u;
        uint cP = reqParticles > 0 ? Math.Min((uint)reqParticles, particleBufBytes / szP) : 0u;
        uint cRV = reqRig2DVerts > 0 ? Math.Min((uint)reqRig2DVerts, rig2DVertBufBytes / szRV) : 0u;
        uint cRI = reqRig2DIndices > 0 ? Math.Min((uint)reqRig2DIndices, rig2DIdxBufBytes / szRI) : 0u;

        ulong tbCap = transferBufBytes;
        ulong off3D = 0UL;
        ulong b3D = (ulong)c3D * sz3D;
        if (b3D > tbCap)
        {
            c3D = (uint)(tbCap / sz3D);
            b3D = (ulong)c3D * sz3D;
        }

        ulong offJ = AlignUpU64(off3D + b3D, 64UL);
        ulong remJ = tbCap > offJ ? tbCap - offJ : 0UL;
        ulong bJ = (ulong)cJ * szJ;
        if (bJ > remJ)
        {
            cJ = (uint)(remJ / szJ);
            bJ = (ulong)cJ * szJ;
        }

        ulong offS = AlignUpU64(offJ + bJ, 64UL);
        ulong remS = tbCap > offS ? tbCap - offS : 0UL;
        ulong bS = (ulong)cS * szS;
        if (bS > remS)
        {
            cS = (uint)(remS / szS);
            bS = (ulong)cS * szS;
        }

        ulong offG = AlignUpU64(offS + bS, 64UL);
        ulong remG = tbCap > offG ? tbCap - offG : 0UL;
        ulong bG = (ulong)cG * szG;
        if (bG > remG)
        {
            cG = (uint)(remG / szG);
            bG = (ulong)cG * szG;
        }

        ulong offP = AlignUpU64(offG + bG, 64UL);
        ulong remP = tbCap > offP ? tbCap - offP : 0UL;
        ulong bP = (ulong)cP * szP;
        if (bP > remP)
        {
            cP = (uint)(remP / szP);
            bP = (ulong)cP * szP;
        }

        ulong offRV = AlignUpU64(offP + bP, 64UL);
        ulong remRV = tbCap > offRV ? tbCap - offRV : 0UL;
        ulong bRV = (ulong)cRV * szRV;
        if (bRV > remRV)
        {
            cRV = (uint)(remRV / szRV);
            bRV = (ulong)cRV * szRV;
        }

        ulong offRI = AlignUpU64(offRV + bRV, 64UL);
        ulong remRI = tbCap > offRI ? tbCap - offRI : 0UL;
        ulong bRI = (ulong)cRI * szRI;
        if (bRI > remRI)
        {
            cRI = (uint)(remRI / szRI);
            bRI = (ulong)cRI * szRI;
        }

        ulong end = 0UL;
        if (b3D > 0UL) end = off3D + b3D;
        if (bJ > 0UL) end = offJ + bJ;
        if (bS > 0UL) end = offS + bS;
        if (bG > 0UL) end = offG + bG;
        if (bP > 0UL) end = offP + bP;
        if (bRV > 0UL) end = offRV + bRV;
        if (bRI > 0UL) end = offRI + bRI;

        return new UploadedFrameCounts
        {
            Inst3D = c3D,
            Joints = cJ,
            Sprites = cS,
            Glyphs = cG,
            Particles = cP,
            Rig2DVerts = cRV,
            Rig2DIndices = cRI,
            Bytes3D = (uint)b3D,
            BytesJoints = (uint)bJ,
            BytesSprites = (uint)bS,
            BytesGlyphs = (uint)bG,
            BytesParticles = (uint)bP,
            BytesRig2DVerts = (uint)bRV,
            BytesRig2DIndices = (uint)bRI,
            Off3D = (uint)off3D,
            OffJoints = (uint)offJ,
            OffSprites = (uint)offS,
            OffGlyphs = (uint)offG,
            OffParticles = (uint)offP,
            OffRig2DVerts = (uint)offRV,
            OffRig2DIndices = (uint)offRI,
            TotalBytes = (uint)end,
        };
    }

    private static void GrowBufferIfNeeded(ref GpuContext gpu, ref nint buffer, ref uint currentBytes, uint neededBytes, SDL.GPUBufferUsageFlags usage, uint failWarnBit, string label)
    {
        if (neededBytes <= currentBytes) return;
        uint grown = currentBytes > (uint.MaxValue / 2u) ? neededBytes : currentBytes * 2u;
        uint newBytes = Math.Max(neededBytes, grown);
        var info = new SDL.GPUBufferCreateInfo
        {
            Usage = usage,
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
        else if (gpu.Log != null && TryLatchGpuWarn(ref gpu, failWarnBit))
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
        in NativeList<ParticleGpuInstance> particles,
        in NativeList<Rig2DVertexGpu> rig2DVerts,
        in NativeList<uint> rig2DIndices,
        out UploadedFrameCounts uploaded)
    {
        uploaded = default;

        ulong req3D64 = (ulong)Math.Max(0, instances3D.Count) * (ulong)sizeof(Instance3DGpu);
        ulong reqJ64 = (ulong)Math.Max(0, jointPalette.Length) * (ulong)sizeof(Mat4);
        ulong reqS64 = (ulong)Math.Max(0, sprites.Count) * (ulong)sizeof(SpriteGpuInstance);
        ulong reqG64 = (ulong)Math.Max(0, glyphs.Count) * (ulong)sizeof(GlyphGpuInstance);
        ulong reqP64 = (ulong)Math.Max(0, particles.Count) * (ulong)sizeof(ParticleGpuInstance);
        ulong reqRV64 = (ulong)Math.Max(0, rig2DVerts.Count) * (ulong)sizeof(Rig2DVertexGpu);
        ulong reqRI64 = (ulong)Math.Max(0, rig2DIndices.Count) * (ulong)sizeof(uint);

        uint bytes3DReq = req3D64 > uint.MaxValue ? uint.MaxValue : (uint)req3D64;
        uint bytesJReq = reqJ64 > uint.MaxValue ? uint.MaxValue : (uint)reqJ64;
        uint bytesSReq = reqS64 > uint.MaxValue ? uint.MaxValue : (uint)reqS64;
        uint bytesGReq = reqG64 > uint.MaxValue ? uint.MaxValue : (uint)reqG64;
        uint bytesPReq = reqP64 > uint.MaxValue ? uint.MaxValue : (uint)reqP64;
        uint bytesRVReq = reqRV64 > uint.MaxValue ? uint.MaxValue : (uint)reqRV64;
        uint bytesRIReq = reqRI64 > uint.MaxValue ? uint.MaxValue : (uint)reqRI64;

        GrowBufferIfNeeded(ref gpu, ref gpu.Instance3DBuffer, ref gpu.Instance3DBufferBytes, bytes3DReq, SDL.GPUBufferUsageFlags.GraphicsStorageRead, GpuWarnGrowInst3D, "Instance3D");
        GrowBufferIfNeeded(ref gpu, ref gpu.JointPaletteBuffer, ref gpu.JointPaletteBufferBytes, bytesJReq, SDL.GPUBufferUsageFlags.GraphicsStorageRead, GpuWarnGrowJoints, "JointPalette");
        GrowBufferIfNeeded(ref gpu, ref gpu.SpriteInstanceBuffer, ref gpu.SpriteInstanceBufferBytes, bytesSReq, SDL.GPUBufferUsageFlags.GraphicsStorageRead, GpuWarnGrowSprites, "SpriteInstance");
        GrowBufferIfNeeded(ref gpu, ref gpu.TextGlyphBuffer, ref gpu.TextGlyphBufferBytes, bytesGReq, SDL.GPUBufferUsageFlags.GraphicsStorageRead, GpuWarnGrowGlyphs, "TextGlyph");
        GrowBufferIfNeeded(ref gpu, ref gpu.ParticleInstanceBuffer, ref gpu.ParticleInstanceBufferBytes, bytesPReq, SDL.GPUBufferUsageFlags.GraphicsStorageRead, GpuWarnGrowParticles, "ParticleInstance");
        GrowBufferIfNeeded(ref gpu, ref gpu.Rig2DVertexBuffer, ref gpu.Rig2DVertexBufferBytes, bytesRVReq, SDL.GPUBufferUsageFlags.Vertex, GpuWarnGrowRig2DVerts, "Rig2DVertex");
        GrowBufferIfNeeded(ref gpu, ref gpu.Rig2DIndexBuffer, ref gpu.Rig2DIndexBufferBytes, bytesRIReq, SDL.GPUBufferUsageFlags.Index, GpuWarnGrowRig2DIndices, "Rig2DIndex");

        UploadedFrameCounts desired = ComputeUploadBudget(
            instances3D.Count, jointPalette.Length, sprites.Count, glyphs.Count, particles.Count,
            gpu.Instance3DBufferBytes, gpu.JointPaletteBufferBytes, gpu.SpriteInstanceBufferBytes, gpu.TextGlyphBufferBytes, gpu.ParticleInstanceBufferBytes,
            uint.MaxValue,
            rig2DVerts.Count, rig2DIndices.Count, gpu.Rig2DVertexBufferBytes, gpu.Rig2DIndexBufferBytes);

        if (desired.TotalBytes == 0u) return false;

        if (desired.TotalBytes > gpu.UploadTransferBufferBytes)
        {
            uint grownTb = gpu.UploadTransferBufferBytes > (uint.MaxValue / 2u) ? desired.TotalBytes : gpu.UploadTransferBufferBytes * 2u;
            uint newTbBytes = Math.Max(desired.TotalBytes, grownTb);
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
            else if (gpu.Log != null && TryLatchGpuWarn(ref gpu, GpuWarnGrowTransfer))
            {
                LogSystem.Warn(gpu.Log, $"GpuContext failed growing UploadTransferBuffer to {newTbBytes} bytes ({SDL.GetError()}); clamping frame upload");
            }
        }

        UploadedFrameCounts finalBudget = ComputeUploadBudget(
            instances3D.Count, jointPalette.Length, sprites.Count, glyphs.Count, particles.Count,
            gpu.Instance3DBufferBytes, gpu.JointPaletteBufferBytes, gpu.SpriteInstanceBufferBytes, gpu.TextGlyphBufferBytes, gpu.ParticleInstanceBufferBytes,
            gpu.UploadTransferBufferBytes,
            rig2DVerts.Count, rig2DIndices.Count, gpu.Rig2DVertexBufferBytes, gpu.Rig2DIndexBufferBytes);

        if (finalBudget.TotalBytes == 0u) return false;

        nint mapRaw = SDL.MapGPUTransferBuffer(gpu.Device, gpu.UploadTransferBuffer, cycle: true);
        if (mapRaw == nint.Zero)
        {
            if (gpu.Log != null && TryLatchGpuWarn(ref gpu, GpuWarnMapTransfer))
                LogSystem.Error(gpu.Log, $"SDL_MapGPUTransferBuffer failed ({SDL.GetError()}); skipping frame buffer upload");
            return false;
        }

        byte* mapped = (byte*)mapRaw;
        if (finalBudget.Bytes3D > 0u)
        {
            Buffer.MemoryCopy(instances3D.Data, mapped + finalBudget.Off3D, finalBudget.Bytes3D, finalBudget.Bytes3D);
            // If joint palette upload was truncated/clamped, fall back skinned instances to static (-1) without mutating RenderPacket
            if (finalBudget.Joints < (uint)jointPalette.Length)
            {
                Instance3DGpu* dstInst = (Instance3DGpu*)(mapped + finalBudget.Off3D);
                for (uint i = 0; i < finalBudget.Inst3D; i++)
                {
                    if (dstInst[i].Extra.Z >= 0f)
                        dstInst[i].Extra.Z = -1f;
                }
            }
        }
        if (finalBudget.BytesJoints > 0u) Buffer.MemoryCopy(jointPalette.Ptr, mapped + finalBudget.OffJoints, finalBudget.BytesJoints, finalBudget.BytesJoints);
        if (finalBudget.BytesSprites > 0u) Buffer.MemoryCopy(sprites.Data, mapped + finalBudget.OffSprites, finalBudget.BytesSprites, finalBudget.BytesSprites);
        if (finalBudget.BytesGlyphs > 0u) Buffer.MemoryCopy(glyphs.Data, mapped + finalBudget.OffGlyphs, finalBudget.BytesGlyphs, finalBudget.BytesGlyphs);
        if (finalBudget.BytesParticles > 0u) Buffer.MemoryCopy(particles.Data, mapped + finalBudget.OffParticles, finalBudget.BytesParticles, finalBudget.BytesParticles);
        if (finalBudget.BytesRig2DVerts > 0u) Buffer.MemoryCopy(rig2DVerts.Data, mapped + finalBudget.OffRig2DVerts, finalBudget.BytesRig2DVerts, finalBudget.BytesRig2DVerts);
        if (finalBudget.BytesRig2DIndices > 0u) Buffer.MemoryCopy(rig2DIndices.Data, mapped + finalBudget.OffRig2DIndices, finalBudget.BytesRig2DIndices, finalBudget.BytesRig2DIndices);
        SDL.UnmapGPUTransferBuffer(gpu.Device, gpu.UploadTransferBuffer);

        nint uploadTb = gpu.UploadTransferBuffer;
        nint copy = SDL.BeginGPUCopyPass(cmd);
        if (copy == nint.Zero) return false;

        void CopySub(nint dstBuf, uint srcOff, uint size)
        {
            if (size == 0u) return;
            var src = new SDL.GPUTransferBufferLocation { TransferBuffer = uploadTb, Offset = srcOff };
            var dst = new SDL.GPUBufferRegion { Buffer = dstBuf, Offset = 0, Size = size };
            SDL.UploadToGPUBuffer(copy, in src, in dst, cycle: true);
        }
        CopySub(gpu.Instance3DBuffer, finalBudget.Off3D, finalBudget.Bytes3D);
        CopySub(gpu.JointPaletteBuffer, finalBudget.OffJoints, finalBudget.BytesJoints);
        CopySub(gpu.SpriteInstanceBuffer, finalBudget.OffSprites, finalBudget.BytesSprites);
        CopySub(gpu.TextGlyphBuffer, finalBudget.OffGlyphs, finalBudget.BytesGlyphs);
        CopySub(gpu.ParticleInstanceBuffer, finalBudget.OffParticles, finalBudget.BytesParticles);
        CopySub(gpu.Rig2DVertexBuffer, finalBudget.OffRig2DVerts, finalBudget.BytesRig2DVerts);
        CopySub(gpu.Rig2DIndexBuffer, finalBudget.OffRig2DIndices, finalBudget.BytesRig2DIndices);
        SDL.EndGPUCopyPass(copy);

        uploaded = finalBudget;
        return true;
    }

    private static void GatherAndBatch3D(
        in RenderShaderDefaults shaders,
        in EngineWorld world,
        in CameraData cam,
        int availableJointCount,
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
                uint texGen = 0u;
                if (mat != null && mat->BaseColorTexture.IsValid)
                {
                    TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, mat->BaseColorTexture);
                    if (tex != null)
                    {
                        texSlot = tex->GpuSlot;
                        texGen = tex->GpuSlotGen;
                    }
                }

                int jointOff = -1;
                if (mesh->HasSkinning != 0 && availableJointCount > 0)
                {
                    AnimPlayerData* ap = AnimationSystem.TryGetPlayer(in world.Animations, mr.AnimPlayer);
                    if (ap != null && ap->FrameJointCount > 0 &&
                        ap->FrameJointOffset >= 0 &&
                        ap->FrameJointOffset + ap->FrameJointCount <= availableJointCount)
                    {
                        jointOff = ap->FrameJointOffset;
                    }
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
                    MeshGpuGen = mesh->GpuSlotGen,
                    TextureGpuSlot = texSlot,
                    TextureGpuGen = texGen,
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
                    if (TryLatchExtractWarn(log, ExtractWarnCap3D))
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
                       items[idx].TextureGpuGen == first.TextureGpuGen &&
                       items[idx].MeshGpuSlot == first.MeshGpuSlot &&
                       items[idx].MeshGpuGen == first.MeshGpuGen &&
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
                        MeshGpuGen = first.MeshGpuGen,
                        TextureGpuSlot = first.TextureGpuSlot,
                        TextureGpuGen = first.TextureGpuGen,
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
                uint texGen = 0u;
                TextureAssetData* tex = null;
                if (sp.Texture.IsValid)
                {
                    tex = AssetSystem.TryGetTexture(in world.Assets, sp.Texture);
                    if (tex != null)
                    {
                        texSlot = tex->GpuSlot;
                        texGen = tex->GpuSlotGen;
                    }
                }

                ShaderHandle sh = sp.CustomShader.IsValid ? sp.CustomShader : shaders.Sprite;
                byte depthTest = sp.DepthTest != 0 ? (byte)1 : (byte)0;
                uint sortLayerU = (uint)(sp.SortingLayer + 32768);
                uint orderU = (uint)(sp.OrderInLayer + 32768);
                ulong key = ((ulong)sortLayerU << 48)
                          | ((ulong)orderU << 32)
                          | ((ulong)depthTest << 30)
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
                        TextureGpuGen = texGen,
                        Shader = sh,
                        BlendMode = sp.BlendMode,
                        DepthTest = depthTest,
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
                    EmitNineSliceSubQuads(in sp, tex, origin, right, up, fwd, uv, cp, key, i * 9, texSlot, texGen, depthTest, sh, ref items);
                }
            }

            if (items.Count == 0) return;
            SortSpriteItems(items.Data, 0, items.Count - 1);

            int idx = 0;
            while (idx < items.Count)
            {
                if (outSprites.Count >= outSprites.Capacity || outBatches.Count >= outBatches.Capacity)
                {
                    if (TryLatchExtractWarn(log, ExtractWarnCapSprites))
                        LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchSprites capacity cap reached (sprites={outSprites.Count}/{outSprites.Capacity}, batches={outBatches.Count}/{outBatches.Capacity})");
                    break;
                }

                SpriteSortItem first = items[idx];
                uint baseInst = (uint)outSprites.Count;
                uint instCount = 0;

                while (idx < items.Count &&
                       items[idx].TextureGpuSlot == first.TextureGpuSlot &&
                       items[idx].TextureGpuGen == first.TextureGpuGen &&
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
                        TextureGpuGen = first.TextureGpuGen,
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
        uint texGen,
        byte depthTest,
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
                    TextureGpuGen = texGen,
                    Shader = sh,
                    BlendMode = sp.BlendMode,
                    DepthTest = depthTest,
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

    private static void GatherAndBatchRig2D(
        in RenderShaderDefaults shaders,
        in EngineWorld world,
        in CameraData cam,
        bool deformFresh,
        ref LinearArena scratchArena,
        ref NativeList<Rig2DVertexGpu> outVerts,
        ref NativeList<uint> outIndices,
        ref NativeList<Rig2DBatchCmd> outBatches,
        ref RenderStats stats,
        LogState* log = null)
    {
        int instCount = world.Rigs.InstanceCount;
        if (instCount == 0) return;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            var items = NativeList<Rig2DSortItem>.CreateInArena(ref scratchArena, instCount, 16);
            for (int i = 0; i < instCount; i++)
            {
                ref readonly Rig2DInstanceData inst = ref world.Rigs.Instances[i];
                if (inst.Visible == 0 || (inst.LayerMask & cam.LayerMask) == 0) continue;
                Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, inst.Asset);
                if (asset == null || asset->SlotCount <= 0 || asset->BoneCount <= 0) continue;
                if (TransformSystem.GetDenseIndex(in world.Transforms, inst.Entity) < 0) continue;

                uint sortLayerU = (uint)(inst.SortingLayer + 32768);
                uint orderU = (uint)(inst.OrderInLayer + 32768);
                ulong key = ((ulong)sortLayerU << 32) | (ulong)orderU;
                items.Add(new Rig2DSortItem
                {
                    SortKey = key,
                    OriginalIndex = i,
                    InstanceIndex = i,
                });
            }

            if (items.Count == 0) return;
            SortRig2DItems(items.Data, 0, items.Count - 1);

            int batchStart = outBatches.Count;
            bool warnedCap = false;

            for (int ii = 0; ii < items.Count; ii++)
            {
                ref readonly Rig2DInstanceData inst = ref world.Rigs.Instances[items[ii].InstanceIndex];
                Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, inst.Asset);
                if (asset == null) continue;

                int td = TransformSystem.GetDenseIndex(in world.Transforms, inst.Entity);
                if (td < 0) continue;

                Mat4 worldMat = world.Transforms.WorldMatrix[td];
                Vec3 pos = world.Transforms.WorldPosition[td];
                Vec3 right, up;

                if (inst.Billboard == SpriteBillboardMode.Spherical)
                {
                    Vec3 scl = world.Transforms.LocalScale[td];
                    right = cam.Right * scl.X;
                    up = cam.CameraUp * scl.Y;
                }
                else if (inst.Billboard == SpriteBillboardMode.CylindricalY)
                {
                    Vec3 scl = world.Transforms.LocalScale[td];
                    Vec3 toCam = cam.ShakenPosition - pos;
                    toCam.Y = 0f;
                    Vec3 fwd = toCam.LengthSq() > 1e-6f ? toCam.Normalized() : Vec3.UnitZ;
                    right = Vec3.Cross(Vec3.Up, fwd).Normalized() * scl.X;
                    up = Vec3.Up * scl.Y;
                }
                else
                {
                    right = new Vec3(worldMat.C0.X, worldMat.C0.Y, worldMat.C0.Z);
                    up = new Vec3(worldMat.C1.X, worldMat.C1.Y, worldMat.C1.Z);
                }

                float invPpu = asset->PixelsPerUnit > 1e-5f ? (1f / asset->PixelsPerUnit) : 0.01f;
                float scaleX = invPpu * inst.Scale.X * (inst.FlipX != 0 ? -1f : 1f);
                float scaleY = invPpu * inst.Scale.Y * (inst.FlipY != 0 ? -1f : 1f);
                Vec2 originOff = asset->OriginOffset;

                Rig2DAffine2D* worldBones = world.Rigs.BoneWorld + inst.BoneSliceStart;
                Rig2DRuntimeSlotState* slotStates = world.Rigs.SlotStates + inst.SlotSliceStart;
                int* drawOrder = world.Rigs.DrawOrder + inst.SlotSliceStart;
                ShaderHandle sh = inst.CustomShader.IsValid ? inst.CustomShader : shaders.Rig2D;
                byte depthTest = inst.DepthTest != 0 ? (byte)1 : (byte)0;

                bool emittedAnyForRig = false;
                for (int di = 0; di < inst.SlotSliceCount; di++)
                {
                    int slotIdx = drawOrder[di];
                    if ((uint)slotIdx >= (uint)asset->SlotCount) continue;

                    ref readonly Rig2DSlotSetupData slotSetup = ref asset->Slots[slotIdx];
                    ref readonly Rig2DRuntimeSlotState st = ref slotStates[slotIdx];
                    int attIdx = st.ActiveAttachmentIndex;
                    if ((uint)attIdx >= (uint)asset->AttachmentCount) continue;

                    ref readonly Rig2DAttachmentData att = ref asset->Attachments[attIdx];
                    if (att.Kind == Rig2DAttachmentKind.None) continue;

                    float combinedAlpha = st.LightColor.A * inst.Tint.A;
                    if (combinedAlpha <= 0.002f) continue;

                    // Resolve sprite region (compressed BC3 atlas!) or fallback texture/UV
                    StringId regionKey = att.SpriteRegionName;
                    if (att.Kind == Rig2DAttachmentKind.Sequence && att.SeqFrameCount > 0 && asset->SequenceFrameNames != null)
                    {
                        int fi = Math.Clamp(st.SequenceFrame, 0, att.SeqFrameCount - 1);
                        regionKey = asset->SequenceFrameNames[att.SeqFrameStart + fi];
                    }

                    TextureHandle texH = att.FallbackTexture.IsValid ? att.FallbackTexture : asset->DefaultAtlasTexture;
                    Vec4 uvRect = (att.FallbackUvRect.Z != 0f || att.FallbackUvRect.W != 0f) ? att.FallbackUvRect : new Vec4(0f, 0f, 1f, 1f);
                    SpriteRegionData* reg = AssetSystem.TryGetSpriteRegion(in world.Assets, regionKey);
                    if (reg != null)
                    {
                        texH = reg->AtlasTexture;
                        uvRect = reg->UvRect;
                    }

                    int texSlot = -1;
                    uint texGen = 0u;
                    if (texH.IsValid)
                    {
                        TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, texH);
                        if (tex != null)
                        {
                            texSlot = tex->GpuSlot;
                            texGen = tex->GpuSlotGen;
                        }
                    }

                    Color32 lightCol = Color32.FromFloat(
                        st.LightColor.R * inst.Tint.R,
                        st.LightColor.G * inst.Tint.G,
                        st.LightColor.B * inst.Tint.B,
                        combinedAlpha);
                    Color32 darkCol = Color32.FromFloat(
                        st.DarkColor.R + inst.DarkTint.R,
                        st.DarkColor.G + inst.DarkTint.G,
                        st.DarkColor.B + inst.DarkTint.B,
                        inst.HitFlash);
                    float alphaCutoff = slotSetup.BlendMode == Rig2DBlendMode.Multiply ? -1f : 0f;

                    uint baseVert = (uint)outVerts.Count;
                    uint idxStart = (uint)outIndices.Count;
                    uint addedIndices = 0u;

                    if (att.Kind == Rig2DAttachmentKind.Region || att.Kind == Rig2DAttachmentKind.Sequence)
                    {
                        if (outVerts.Count + 4 > outVerts.Capacity || outIndices.Count + 6 > outIndices.Capacity)
                        {
                            warnedCap = true;
                            break;
                        }

                        Rig2DAffine2D bw = (uint)slotSetup.BoneIndex < (uint)inst.BoneSliceCount
                            ? worldBones[slotSetup.BoneIndex]
                            : Rig2DAffine2D.Identity;
                        Rig2DSystem.ComputeRegionQuadCorners(in att, in bw, out Vec2 bl, out Vec2 br, out Vec2 tr, out Vec2 tl);

                        Vec3 wBL = pos + right * ((bl.X - originOff.X) * scaleX) + up * ((bl.Y - originOff.Y) * scaleY);
                        Vec3 wBR = pos + right * ((br.X - originOff.X) * scaleX) + up * ((br.Y - originOff.Y) * scaleY);
                        Vec3 wTR = pos + right * ((tr.X - originOff.X) * scaleX) + up * ((tr.Y - originOff.Y) * scaleY);
                        Vec3 wTL = pos + right * ((tl.X - originOff.X) * scaleX) + up * ((tl.Y - originOff.Y) * scaleY);

                        outVerts.Add(new Rig2DVertexGpu { Position = wBL, Uv = new Vec2(uvRect.X, uvRect.Y + uvRect.W), LightColor = lightCol, DarkColor = darkCol, AlphaCutoff = alphaCutoff });
                        outVerts.Add(new Rig2DVertexGpu { Position = wBR, Uv = new Vec2(uvRect.X + uvRect.Z, uvRect.Y + uvRect.W), LightColor = lightCol, DarkColor = darkCol, AlphaCutoff = alphaCutoff });
                        outVerts.Add(new Rig2DVertexGpu { Position = wTR, Uv = new Vec2(uvRect.X + uvRect.Z, uvRect.Y), LightColor = lightCol, DarkColor = darkCol, AlphaCutoff = alphaCutoff });
                        outVerts.Add(new Rig2DVertexGpu { Position = wTL, Uv = new Vec2(uvRect.X, uvRect.Y), LightColor = lightCol, DarkColor = darkCol, AlphaCutoff = alphaCutoff });

                        outIndices.Add(baseVert + 0u);
                        outIndices.Add(baseVert + 1u);
                        outIndices.Add(baseVert + 2u);
                        outIndices.Add(baseVert + 0u);
                        outIndices.Add(baseVert + 2u);
                        outIndices.Add(baseVert + 3u);
                        addedIndices = 6u;
                    }
                    else if (att.Kind == Rig2DAttachmentKind.WeightedMesh)
                    {
                        int vc = att.VertexCount;
                        int ic = att.IndexCount;
                        if (vc <= 0 || ic <= 0) continue;
                        if (outVerts.Count + vc > outVerts.Capacity || outIndices.Count + ic > outIndices.Capacity)
                        {
                            warnedCap = true;
                            break;
                        }

                        bool hasDeform = deformFresh && st.DeformVertexCount == vc &&
                                         st.DeformVertexOffset >= 0 &&
                                         st.DeformVertexOffset + vc <= world.Rigs.FrameDeformDeltas.Length;
                        Vec2* deformPtr = hasDeform ? (world.Rigs.FrameDeformDeltas.Ptr + st.DeformVertexOffset) : null;

                        for (int v = 0; v < vc; v++)
                        {
                            ref readonly Rig2DMeshVertex mv = ref asset->MeshVertices[att.VertexStart + v];
                            Vec2 delta = deformPtr != null ? deformPtr[v] : Vec2.Zero;
                            Vec2 skelP = Rig2DSystem.SkinMeshVertex(asset, worldBones, slotSetup.BoneIndex, in mv, delta);
                            Vec3 wp = pos + right * ((skelP.X - originOff.X) * scaleX) + up * ((skelP.Y - originOff.Y) * scaleY);
                            Vec2 remappedUv = new Vec2(uvRect.X + mv.Uv.X * uvRect.Z, uvRect.Y + mv.Uv.Y * uvRect.W);
                            outVerts.Add(new Rig2DVertexGpu
                            {
                                Position = wp,
                                Uv = remappedUv,
                                LightColor = lightCol,
                                DarkColor = darkCol,
                                AlphaCutoff = alphaCutoff,
                            });
                        }

                        for (int k = 0; k < ic; k++)
                            outIndices.Add(baseVert + asset->MeshIndices[att.IndexStart + k]);
                        addedIndices = (uint)ic;
                    }

                    if (addedIndices == 0u) continue;

                    // Coalesce consecutive draws with identical GPU state into ONE batched draw call!
                    if (outBatches.Count > batchStart)
                    {
                        ref Rig2DBatchCmd prev = ref outBatches[outBatches.Count - 1];
                        if (prev.TextureGpuSlot == texSlot &&
                            prev.TextureGpuGen == texGen &&
                            prev.Shader == sh &&
                            prev.BlendMode == slotSetup.BlendMode &&
                            prev.DepthTest == depthTest &&
                            prev.IndexOffset + prev.IndexCount == idxStart)
                        {
                            prev.IndexCount += addedIndices;
                            emittedAnyForRig = true;
                            continue;
                        }
                    }

                    if (outBatches.Count >= outBatches.Capacity)
                    {
                        // Roll back vertices & indices emitted for this unbatchable slot so no orphans remain
                        outVerts.Count = (int)baseVert;
                        outIndices.Count = (int)idxStart;
                        warnedCap = true;
                        break;
                    }

                    emittedAnyForRig = true;
                    outBatches.Add(new Rig2DBatchCmd
                    {
                        TextureGpuSlot = texSlot,
                        TextureGpuGen = texGen,
                        Shader = sh,
                        BlendMode = slotSetup.BlendMode,
                        DepthTest = depthTest,
                        IndexOffset = idxStart,
                        IndexCount = addedIndices,
                    });
                }

                if (emittedAnyForRig) stats.Rig2DInstances++;
                if (warnedCap)
                {
                    if (TryLatchExtractWarn(log, ExtractWarnCapRig2D))
                        LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchRig2D capacity cap reached (verts={outVerts.Count}/{outVerts.Capacity}, indices={outIndices.Count}/{outIndices.Capacity}, batches={outBatches.Count}/{outBatches.Capacity})");
                    break;
                }
            }
        }
        finally
        {
            scratchArena.Rollback(mark);
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
                    AtlasTexGpuGen = atlas->GpuSlotGen,
                    Shader = sh,
                    SortingOrder = td.SortingOrder,
                    DepthTest = td.DepthTest != 0 ? (byte)1 : (byte)0,
                });
            }

            if (items.Count == 0) return;
            SortTextItems(items.Data, 0, items.Count - 1);

            int batchStart = outBatches.Count;
            for (int k = 0; k < items.Count; k++)
            {
                if (outGlyphs.Count >= outGlyphs.Capacity || outBatches.Count >= outBatches.Capacity)
                {
                    if (TryLatchExtractWarn(log, ExtractWarnCapText))
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
                    if (prev.AtlasTexGpuSlot == item.AtlasTexGpuSlot &&
                        prev.AtlasTexGpuGen == item.AtlasTexGpuGen &&
                        prev.Shader == item.Shader &&
                        prev.DepthTest == item.DepthTest)
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
                        AtlasTexGpuGen = item.AtlasTexGpuGen,
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
            Vec4* emBaseCellUv = scratchArena.AllocUninitialized<Vec4>(emCap, 16);
            int* emCols = scratchArena.AllocUninitialized<int>(emCap, 16);
            int* emRows = scratchArena.AllocUninitialized<int>(emCap, 16);

            bool anyVisible = false;
            for (int es = 0; es < emCap; es++)
            {
                if (pt.EmitterAlive[es] != 0 && (pt.Emitters[es].LayerMask & cam.LayerMask) != 0)
                {
                    visibleEmitter[es] = 1;
                    anyVisible = true;

                    ref readonly ParticleEmitterData em = ref pt.Emitters[es];
                    int cols = em.AtlasColumns > 0 ? em.AtlasColumns : 1;
                    int rows = em.AtlasRows > 0 ? em.AtlasRows : 1;
                    Vec4 baseUv = (float.IsFinite(em.BaseUvRect.X) && float.IsFinite(em.BaseUvRect.Y) &&
                                   float.IsFinite(em.BaseUvRect.Z) && float.IsFinite(em.BaseUvRect.W) &&
                                   em.BaseUvRect.Z > 0f && em.BaseUvRect.W > 0f)
                        ? em.BaseUvRect
                        : new Vec4(0f, 0f, 1f, 1f);
                    emCols[es] = cols;
                    emRows[es] = rows;
                    emBaseCellUv[es] = new Vec4(baseUv.X, baseUv.Y, baseUv.Z / cols, baseUv.W / rows);
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
                if (TryLatchExtractWarn(log, ExtractWarnCapParticles))
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

            if (running < totalVisible && TryLatchExtractWarn(log, ExtractWarnCapParticles))
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

                int cols = emCols[es];
                int rows = emRows[es];
                Vec4 bcUv = emBaseCellUv[es];

                int f0 = pt.FrameIndex[p];
                int col0 = f0 % cols;
                int row0 = f0 / cols;
                if (row0 >= rows) row0 = rows - 1;
                Vec4 uv0 = new Vec4(bcUv.X + col0 * bcUv.Z, bcUv.Y + row0 * bcUv.W, bcUv.Z, bcUv.W);

                int f1 = pt.NextFrameIndex[p];
                int col1 = f1 % cols;
                int row1 = f1 / cols;
                if (row1 >= rows) row1 = rows - 1;
                Vec4 uv1 = new Vec4(bcUv.X + col1 * bcUv.Z, bcUv.Y + row1 * bcUv.W, bcUv.Z, bcUv.W);

                float blend = DetMath.Clamp01(pt.FrameBlend[p]);

                outParticles.Data[writeAt] = new ParticleGpuInstance
                {
                    PosSize = new Vec4(pt.PosX[p], pt.PosY[p], pt.PosZ[p], pt.Size[p]),
                    Color = pt.Color[p],
                    UvRect = uv0,
                    RotExtra = new Vec4(pt.Rotation[p], pt.Emitters[es].StretchedBillboard, blend, 0f),
                    UvRectNext = uv1,
                };
            }

            int batchStart = outBatches.Count;
            for (int es = 0; es < emCap; es++)
            {
                int count = emitCounts[es];
                if (count <= 0) continue;
                if (outBatches.Count >= outBatches.Capacity)
                {
                    if (TryLatchExtractWarn(log, ExtractWarnCapParticleBatches))
                        LogSystem.Warn(log, $"BatchRenderer.GatherAndBatchParticles batch cap reached ({outBatches.Count}/{outBatches.Capacity})");
                    break;
                }

                ref readonly ParticleEmitterData em = ref pt.Emitters[es];
                int texSlot = -1;
                uint texGen = 0u;
                if (em.Texture.IsValid)
                {
                    TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, em.Texture);
                    if (tex != null)
                    {
                        texSlot = tex->GpuSlot;
                        texGen = tex->GpuSlotGen;
                    }
                }

                ShaderHandle sh = em.CustomShader.IsValid ? em.CustomShader : shaders.Particle;
                if (outBatches.Count > batchStart)
                {
                    ref ParticleBatchCmd prev = ref outBatches[outBatches.Count - 1];
                    if (prev.TextureGpuSlot == texSlot && prev.TextureGpuGen == texGen && prev.Shader == sh && prev.BlendMode == em.BlendMode)
                    {
                        prev.InstanceCount += (uint)count;
                        stats.ParticleInstances += count;
                        continue;
                    }
                }

                outBatches.Add(new ParticleBatchCmd
                {
                    TextureGpuSlot = texSlot,
                    TextureGpuGen = texGen,
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
        if (!(sunDir.LengthSq() >= 1e-6f) || !float.IsFinite(sunDir.X) || !float.IsFinite(sunDir.Y) || !float.IsFinite(sunDir.Z))
            sunDir = new Vec3(0.45f, 0.85f, 0.35f).Normalized();

        float halfExtent = (float.IsFinite(lighting.ShadowDistance) && lighting.ShadowDistance > 0.001f)
            ? MathF.Max(lighting.ShadowDistance, 0.25f)
            : 28f;
        uint mapSize = shadowMapSize >= 256u ? shadowMapSize : 2048u;

        Vec3 refUp = DetMath.Abs(Vec3.Dot(sunDir, Vec3.UnitY)) > 0.98f ? Vec3.UnitZ : Vec3.UnitY;
        Vec3 forward = -sunDir;
        Vec3 right = Vec3.Cross(forward, refUp).Normalized();
        Vec3 lightUp = Vec3.Cross(right, forward);

        // Snap center along light right/up axes to exact shadow texel increments to prevent shimmer
        float texelWorld = (2f * halfExtent) / mapSize;
        Vec3 center = lighting.ShadowCenter;
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z))
            center = Vec3.Zero;
        float sx = MathF.Round(Vec3.Dot(center, right) / texelWorld) * texelWorld;
        float sy = MathF.Round(Vec3.Dot(center, lightUp) / texelWorld) * texelWorld;
        float sz = Vec3.Dot(center, forward);
        Vec3 snappedCenter = right * sx + lightUp * sy + forward * sz;

        float pullBack = halfExtent * 2.0f;
        float nearPlane = MathF.Min(0.5f, pullBack * 0.1f);
        Vec3 eye = snappedCenter + sunDir * pullBack;
        Mat4 lightView = Mat4.CreateLookAt(eye, snappedCenter, lightUp);
        Mat4 lightProj = Mat4.CreateOrtho(-halfExtent, halfExtent, -halfExtent, halfExtent, nearPlane, pullBack * 2.25f);
        return Mat4.Multiply(in lightProj, in lightView);
    }

    private static void GatherShadowCasters3D(
        in EngineWorld world,
        in SceneLighting lighting,
        int availableJointCount,
        int instanceLimit,
        ref LinearArena scratchArena,
        ref NativeList<Instance3DGpu> outInstances,
        ref NativeList<Batch3DCmd> outShadowBatches,
        LogState* log = null)
    {
        int count = world.RenderComponents.MeshRendererCount;
        if (count == 0 || instanceLimit <= 0) return;

        int maxOutCount = Math.Min(outInstances.Capacity, outInstances.Count + instanceLimit);
        float halfExtent = (float.IsFinite(lighting.ShadowDistance) && lighting.ShadowDistance > 0.001f)
            ? MathF.Max(lighting.ShadowDistance, 0.25f)
            : 28f;
        float maxRadius = halfExtent * 1.85f;
        Vec3 shadowCenter = lighting.ShadowCenter;
        if (!float.IsFinite(shadowCenter.X) || !float.IsFinite(shadowCenter.Y) || !float.IsFinite(shadowCenter.Z))
            shadowCenter = Vec3.Zero;

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
                if (mesh->HasSkinning != 0 && availableJointCount > 0)
                {
                    AnimPlayerData* ap = AnimationSystem.TryGetPlayer(in world.Animations, mr.AnimPlayer);
                    if (ap != null && ap->FrameJointCount > 0 &&
                        ap->FrameJointOffset >= 0 &&
                        ap->FrameJointOffset + ap->FrameJointCount <= availableJointCount)
                    {
                        jointOff = ap->FrameJointOffset;
                    }
                }

                MaterialAlphaMode shadowAlphaMode = (mat != null && mat->AlphaMode == MaterialAlphaMode.Mask)
                    ? MaterialAlphaMode.Mask : MaterialAlphaMode.Opaque;
                byte doubleSided = (mat != null && mat->DoubleSided != 0) ? (byte)1 : (byte)0;
                int texSlot = -1;
                uint texGen = 0u;
                if (shadowAlphaMode == MaterialAlphaMode.Mask && mat != null && mat->BaseColorTexture.IsValid)
                {
                    TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, mat->BaseColorTexture);
                    if (tex != null)
                    {
                        texSlot = tex->GpuSlot;
                        texGen = tex->GpuSlotGen;
                    }
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
                    MeshGpuGen = mesh->GpuSlotGen,
                    TextureGpuSlot = texSlot,
                    TextureGpuGen = texGen,
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
                if (outInstances.Count >= maxOutCount || outShadowBatches.Count >= outShadowBatches.Capacity)
                {
                    if (TryLatchExtractWarn(log, ExtractWarnCapShadows))
                        LogSystem.Warn(log, $"BatchRenderer.GatherShadowCasters3D capacity cap reached (instances={outInstances.Count}/{maxOutCount}, shadowBatches={outShadowBatches.Count}/{outShadowBatches.Capacity})");
                    break;
                }

                Draw3DSortItem first = items[idx];
                uint baseInst = (uint)outInstances.Count;
                uint instCount = 0;

                while (idx < items.Count &&
                       items[idx].AlphaMode == first.AlphaMode &&
                       items[idx].DoubleSided == first.DoubleSided &&
                       items[idx].TextureGpuSlot == first.TextureGpuSlot &&
                       items[idx].TextureGpuGen == first.TextureGpuGen &&
                       items[idx].MeshGpuSlot == first.MeshGpuSlot &&
                       items[idx].MeshGpuGen == first.MeshGpuGen &&
                       outInstances.Count < maxOutCount)
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
                        MeshGpuGen = first.MeshGpuGen,
                        TextureGpuSlot = first.TextureGpuSlot,
                        TextureGpuGen = first.TextureGpuGen,
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
    private static void BindFragmentTexture(nint pass, in GpuContext gpu, int texSlot, uint texGen)
    {
        nint tex = gpu.DefaultWhiteTexture;
        nint samp = gpu.SamplerLinearWrap;
        if (IsTextureSlotValid(in gpu, texSlot, texGen))
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
    private static void BindFragmentTextureAndShadow(nint pass, in GpuContext gpu, int texSlot, uint texGen, bool shadowsRendered)
    {
        nint tex = gpu.DefaultWhiteTexture;
        nint samp = gpu.SamplerLinearWrap;
        if (IsTextureSlotValid(in gpu, texSlot, texGen))
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint SelectRig2DPipeline(GpuShaderSlot* s, Rig2DBlendMode blendMode, bool depthTest)
    {
        if (!depthTest)
        {
            return blendMode switch
            {
                Rig2DBlendMode.Additive => s->PipelineAdditiveNoDepth != nint.Zero ? s->PipelineAdditiveNoDepth : s->PipelineOverlayNoDepth,
                Rig2DBlendMode.Multiply => s->PipelineMultiplyNoDepth != nint.Zero ? s->PipelineMultiplyNoDepth : s->PipelineOverlayNoDepth,
                _ => s->PipelineOverlayNoDepth,
            };
        }
        return blendMode switch
        {
            Rig2DBlendMode.Additive => s->PipelineAdditive,
            Rig2DBlendMode.Multiply => s->PipelineMultiply != nint.Zero ? s->PipelineMultiply : s->PipelineAlphaBlend,
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MedianOfThree3D(Draw3DSortItem* arr, int a, int b, int c)
    {
        if (Less3D(in arr[a], arr[b].SortKey, arr[b].OriginalIndex))
        {
            if (Less3D(in arr[b], arr[c].SortKey, arr[c].OriginalIndex)) return b;
            return Less3D(in arr[a], arr[c].SortKey, arr[c].OriginalIndex) ? c : a;
        }
        if (Less3D(in arr[a], arr[c].SortKey, arr[c].OriginalIndex)) return a;
        return Less3D(in arr[b], arr[c].SortKey, arr[c].OriginalIndex) ? c : b;
    }

    private static void Sort3DItems(Draw3DSortItem* arr, int lo, int hi)
    {
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int pPos = MedianOfThree3D(arr, lo, mid, hi);
            ulong pKey = arr[pPos].SortKey;
            int pIdx = arr[pPos].OriginalIndex;
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
                    i++;
                    j--;
                }
            }
            if (j - lo < hi - i)
            {
                if (lo < j) Sort3DItems(arr, lo, j);
                lo = i;
            }
            else
            {
                if (i < hi) Sort3DItems(arr, i, hi);
                hi = j;
            }
        }
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
        c = a.AtlasTexGpuGen.CompareTo(b.AtlasTexGpuGen);
        if (c != 0) return c;
        return a.TextIndex.CompareTo(b.TextIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MedianOfThreeText(TextSortItem* arr, int a, int b, int c)
    {
        if (CompareTextItem(in arr[a], in arr[b]) < 0)
        {
            if (CompareTextItem(in arr[b], in arr[c]) < 0) return b;
            return CompareTextItem(in arr[a], in arr[c]) < 0 ? c : a;
        }
        if (CompareTextItem(in arr[a], in arr[c]) < 0) return a;
        return CompareTextItem(in arr[b], in arr[c]) < 0 ? c : b;
    }

    private static void SortTextItems(TextSortItem* arr, int lo, int hi)
    {
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int pPos = MedianOfThreeText(arr, lo, mid, hi);
            TextSortItem pivot = arr[pPos];
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
                    i++;
                    j--;
                }
            }
            if (j - lo < hi - i)
            {
                if (lo < j) SortTextItems(arr, lo, j);
                lo = i;
            }
            else
            {
                if (i < hi) SortTextItems(arr, i, hi);
                hi = j;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LessSprite(in SpriteSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey < pivotKey || (a.SortKey == pivotKey && a.OriginalIndex < pivotIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GreaterSprite(in SpriteSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey > pivotKey || (a.SortKey == pivotKey && a.OriginalIndex > pivotIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MedianOfThreeSprite(SpriteSortItem* arr, int a, int b, int c)
    {
        if (LessSprite(in arr[a], arr[b].SortKey, arr[b].OriginalIndex))
        {
            if (LessSprite(in arr[b], arr[c].SortKey, arr[c].OriginalIndex)) return b;
            return LessSprite(in arr[a], arr[c].SortKey, arr[c].OriginalIndex) ? c : a;
        }
        if (LessSprite(in arr[a], arr[c].SortKey, arr[c].OriginalIndex)) return a;
        return LessSprite(in arr[b], arr[c].SortKey, arr[c].OriginalIndex) ? c : b;
    }

    private static void SortSpriteItems(SpriteSortItem* arr, int lo, int hi)
    {
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int pPos = MedianOfThreeSprite(arr, lo, mid, hi);
            ulong pKey = arr[pPos].SortKey;
            int pIdx = arr[pPos].OriginalIndex;
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
                    i++;
                    j--;
                }
            }
            if (j - lo < hi - i)
            {
                if (lo < j) SortSpriteItems(arr, lo, j);
                lo = i;
            }
            else
            {
                if (i < hi) SortSpriteItems(arr, i, hi);
                hi = j;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LessRig2D(in Rig2DSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey < pivotKey || (a.SortKey == pivotKey && a.OriginalIndex < pivotIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GreaterRig2D(in Rig2DSortItem a, ulong pivotKey, int pivotIdx)
        => a.SortKey > pivotKey || (a.SortKey == pivotKey && a.OriginalIndex > pivotIdx);

    private static void SortRig2DItems(Rig2DSortItem* arr, int lo, int hi)
    {
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ulong pKey = arr[mid].SortKey;
            int pIdx = arr[mid].OriginalIndex;
            int i = lo, j = hi;
            while (i <= j)
            {
                while (LessRig2D(in arr[i], pKey, pIdx)) i++;
                while (GreaterRig2D(in arr[j], pKey, pIdx)) j--;
                if (i <= j)
                {
                    Rig2DSortItem tmp = arr[i];
                    arr[i] = arr[j];
                    arr[j] = tmp;
                    i++;
                    j--;
                }
            }
            if (j - lo < hi - i)
            {
                if (lo < j) SortRig2DItems(arr, lo, j);
                lo = i;
            }
            else
            {
                if (i < hi) SortRig2DItems(arr, i, hi);
                hi = j;
            }
        }
    }
}
