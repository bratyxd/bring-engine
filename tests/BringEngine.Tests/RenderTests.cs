using System;
using System.IO;
using System.Text;
using System.Threading;
using BringEngine.Assets;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Textures;
using BringEngine.Audio;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Input;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;
using SDL3;

namespace BringEngine.Tests;

public static unsafe class RenderTests
{
    public static void RunAll(bool headlessOnly = false)
    {
        Console.WriteLine("=== Phases 6-7: SDL3_gpu Renderer, Instancing, 3D Sprites, SDF Text & Shaders ===");
        Assert.Run("TextLayoutSystem_WordWrapShadowTypewriterAndAnimZeroGc", &TestTextLayoutHeadless);
        Assert.Run("TextLayoutSystem_WordWrapSkipsLeadingSpace_And_SingleText512Glyphs", &TestTextWordWrapAndGlyphBudget);
        Assert.Run("TextLayoutSystem_TrailingSpaceWrapAndMultiByteUtf8Typewriter", &TestTextTrailingSpaceAndUtf8Typewriter);
        Assert.Run("BatchRenderer_CapacityCap_CastShadow0_SortingOrder_ShaderGen_DoubleSided", &TestBatchRendererRulesAndCapacityCap);
        Assert.Run("BatchRenderer_ShadowTexelSnapping_And_SmallShadowDistance", &TestShadowSnappingAndDistance);
        Assert.Run("BatchRenderer_DirectionalLightViewProj_NaNAndSmallShadowDistance", &TestDirectionalLightNaNAndSmallDistance);
        Assert.Run("BatchRenderer_ComputeUploadBudget_ElementAlignedClamping", &TestComputeUploadBudgetClamping);
        Assert.Run("BatchRenderer_MultiCamera8200Meshes_NeverStarvesShadows", &TestMultiCameraShadowReservation);
        Assert.Run("BatchRenderer_WorstCaseSort_256KBStackZeroGc", &TestAntiQuicksort256KBStack);
        Assert.Run("BatchRenderer_SpriteDepthTestNonBoolean_NormalizedAndMerged", &TestSpriteDepthTestNormalization);
        Assert.Run("BatchRenderer_WarningLatch_TenFramesLogOnce", &TestExtractWarningLatchLogOnce);
        Assert.Run("BatchRenderer_StaleFrameJointPalette_FallbackToStaticZeroCopy", &TestStaleFrameJointPaletteGuard);
        Assert.Run("PlatformSystem_ScaleLogicalToPixel_RetinaMatchesScreenToWorldRay", &TestHiDpiMouseScalingAndRayCorners);
        Assert.Run("GpuSystem_NullPointersAndInvalidCapture_RejectedCleanly", &TestGpuSystemValidationHeadless);
        Assert.Run("ShaderCross_SpirvAndMslTranspileSmoke_AllBuiltinShaders", &TestSpirvAndMslSmoke);
        Assert.Run("InputState_FocusLostClearsHeldKeysMouseAndGamepadsAndFiresRelease", &TestFocusLostInputRelease);
        if (!headlessOnly)
        {
            Assert.Run("GpuBatchRenderer_Sdl3GpuInstancingSpritesSdfTextParticlesAndPngCapture", &TestGpuRenderEndToEnd);
            Assert.Run("GpuDirectionalShadows_OccluderDarkensFloorAndCustom1SamplerShaderCompatible", &TestGpuDirectionalShadowsAndCustomShader);
            Assert.Run("GpuResources_Non4x4Bc1Bc3Fallback_BundleUnloadReuse_And_DisposeOrder", &TestGpuNon4x4AndBundleLifecycle);
            Assert.Run("ShaderSystem_ErrorPaths_ReleaseShader_CustomQuadAnd2SamplerShaders", &TestShaderSystemErrorsAndCustomQuadShaders);
            Assert.Run("GpuBuiltinShaders_MaskShadow_DoubleSided_SdfNoBleed_AdditiveNoDepth_BufferGrow", &TestBuiltinShaderFeaturesAndDynamicGrow);
            Assert.Run("GpuLifecycle_InitHooks_DeferredRelease_StaleGenSkipped_And_DisposeReset", &TestGpuDeferredReleaseAndLifecycle);
            Assert.Run("GpuSdfText_TranslucentFillNoOutlineDarkening_And_ZeroAlphaOutlineNoFringe", &TestGpuSdfTextTranslucentCompositing);
            Assert.Run("PlatformSystem_ShutdownDoesNotKillAudioSubsystem", &TestPlatformShutdownPreservesAudio);
            Assert.Run("GpuShaders_RefuseReleaseBuiltins_And_ViewportClampingScissor", &TestBuiltinShaderProtectionAndViewportScissor);
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static void TestTextLayoutHeadless()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("test_font"), 48f, 512, 6);
        FontAssetData* font = AssetSystem.TryGetFont(in world.Assets, fontH);

        var e = EntitySystem.Spawn(ref world.Entities);
        TransformSystem.Add(ref world.Transforms, e, new Vec3(0, 2, 0));
        TransformSystem.PropagateHierarchy(ref world.Transforms);

        var camH = CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0, 2, 8), new Vec3(0, 2, 0));
        CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);
        CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, camH);

        var td = RenderComponentSystem.DefaultWorldText(e, fontH, "BRING ENGINE SDF TEXT", default);
        td.AnimFlags = TextAnimFlags.Wave | TextAnimFlags.Rainbow | TextAnimFlags.ShineSweep;
        td.TypewriterProgress = 1f;

        alloc.BeginFrame();
        var glyphs = NativeList<GlyphGpuInstance>.CreateInArena(ref alloc.Frame, 256);

        // Warmup
        int count = TextLayoutSystem.LayoutTextIntoGlyphs(in td, in *font, in world.Transforms, in *cam, ref alloc.Scratch, ref glyphs);
        Assert.True(count >= 30, $"Expected >=30 glyphs (chars+shadows), got {count}");

        // Zero-GC hot layout check
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            glyphs.Clear();
            TextLayoutSystem.LayoutTextIntoGlyphs(in td, in *font, in world.Transforms, in *cam, ref alloc.Scratch, ref glyphs);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "TextLayoutSystem hot loop allocates 0 GC bytes");

        // Typewriter half progress cuts glyph count in half
        td.TypewriterProgress = 0.5f;
        glyphs.Clear();
        int halfCount = TextLayoutSystem.LayoutTextIntoGlyphs(in td, in *font, in world.Transforms, in *cam, ref alloc.Scratch, ref glyphs);
        Assert.True(halfCount > 0 && halfCount < count, $"Typewriter progress reduced glyphs ({halfCount} < {count})");

        bundleArena.Dispose();
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }

    private static void TestGpuRenderEndToEnd()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 512 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 1024, 1024);
        var bundleArena = LinearArena.Create(4 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        var window = PlatformSystem.InitWindow("BringEngine GPU Test", 640, 360, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: false);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu);
            Assert.True(gpu->BuiltinPbrShader.IsValid, "Builtin PBR shader compiled via SDL_shadercross");
            Assert.True(gpu->BuiltinSpriteShader.IsValid, "Builtin Sprite shader compiled");
            Assert.True(gpu->BuiltinSdfTextShader.IsValid, "Builtin SDF Text shader compiled");
            Assert.True(gpu->BuiltinParticleShader.IsValid, "Builtin Particle shader compiled");

            // Load 1 glTF skinned model and instantiate 12 copies -> MUST batch into 1 instanced draw call!
            var heroSpec = GlbBuilder.CreateHeroMechSpec(ColorF.Blue, ColorF.Cyan);
            byte[] heroGlb = GlbBuilder.BuildGlb(in heroSpec, includeDemoAnimation: true);
            GltfHandle heroGltf = GltfLoader.LoadFromMemory(heroGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("hero"));

            for (int i = 0; i < 12; i++)
            {
                float x = (i % 4 - 1.5f) * 2.2f;
                float z = (i / 4 - 1.0f) * -2.2f;
                GltfLoader.Instantiate(ref world, ref alloc.Scratch, heroGltf, new Vec3(x, 0f, z), Quat.Identity, Vec3.One);
            }

            // Spawn 6 2D sprites in 3D world sharing one sprite sheet -> MUST batch into 1 draw call!
            TextureHandle sheet = TextureLoader.CreateArcadeSpriteSheet(64, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("sheet"));
            for (int i = 0; i < 6; i++)
            {
                var se = EntitySystem.Spawn(ref world.Entities);
                TransformSystem.Add(ref world.Transforms, se, new Vec3((i - 2.5f) * 1.4f, 2.3f, 0f));
                RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
                {
                    Entity = se,
                    Texture = sheet,
                    Size = new Vec2(0.9f, 0.9f),
                    Pivot = new Vec2(0.5f, 0.5f),
                    UvRect = new Vec4((i % 4) * 0.25f, 0f, 0.25f, 0.5f),
                    Color = ColorF.Gold,
                    LayerMask = 1u,
                    Billboard = SpriteBillboardMode.Spherical,
                    BlendMode = SpriteBlendMode.Additive,
                    DepthTest = 1,
                    Visible = 1,
                });
            }

            // Spawn 3D SDF animated text
            FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("font"));
            var te = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, te, new Vec3(0, 3.6f, 0));
            var td = RenderComponentSystem.DefaultWorldText(te, fontH, "BRING ENGINE", default);
            td.FontScale = 0.025f;
            td.Color = ColorF.Cyan;
            td.GlowIntensity = 0.8f;
            td.AnimFlags = TextAnimFlags.Wave | TextAnimFlags.Rainbow;
            RenderComponentSystem.AddText(ref world.RenderComponents, in td);

            // Spawn particle burst
            TextureHandle glow = TextureLoader.CreateRadialGlow(64, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("glow"));
            var emCfg = ParticleSystem.DefaultEmitter(new Vec3(0, 1.5f, 0), 42);
            emCfg.Texture = glow;
            var emH = ParticleSystem.CreateEmitter(ref world.Particles, in emCfg);
            ParticleSystem.Burst(ref world.Particles, emH, 80);

            // Main 3D camera
            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0, 4.5f, 10f), new Vec3(0, 1.2f, 0f), 55f);

            // Step simulation and render offscreen capture frame
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 0.25f, 640, 360);
            GpuSystem.EnsureCaptureTarget(ref *gpu, 640, 360);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);

            RenderStats st = gpu->LastFrameStats;
            Assert.Equal(12, st.MeshInstances, "All 12 glTF instances rendered");
            Assert.Equal(6, st.SpriteInstances, "All 6 3D sprites rendered");
            Assert.True(st.GlyphInstances >= 20, "SDF text glyphs rendered");
            Assert.True(st.ParticleInstances >= 80, $"Expected >=80 particles rendered, got {st.ParticleInstances}");
            // 12 meshes + 6 sprites + 80 particles + 22 text glyphs = 120 objects in JUST 4 draw calls!!
            Assert.Equal(4, st.DrawCalls, $"Expected exactly 4 batched draw calls for 120+ objects, got {st.DrawCalls}");
            if (gpu->ShadowMapSupported != 0)
                Assert.Equal(1, st.ShadowDrawCalls, $"Expected 1 instanced shadow draw call for all 12 glTF heroes, got {st.ShadowDrawCalls}");

            string pngPath = Path.Combine(AppContext.BaseDirectory, "verify_engine_test.png");
            GpuSystem.SaveCaptureTextureToPng(ref *gpu, pngPath);
            Assert.True(File.Exists(pngPath) && new FileInfo(pngPath).Length > 1024, "Captured GPU PNG screenshot");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after full GPU render & teardown");
    }

    private static void TestGpuDirectionalShadowsAndCustomShader()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 512 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 256);
        var bundleArena = LinearArena.Create(4 * 1024 * 1024);
        var bundleId = BundleId.Create(2, 1);

        var window = PlatformSystem.InitWindow("BringEngine Shadow Test", 320, 180, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        // Enable SDL3_gpu debug validation layer to prove zero D3D12/Vulkan/Metal barrier or binding errors!
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu);
            Assert.True(gpu->ShadowMapSupported != 0 && gpu->ShadowPipeline != nint.Zero, "Directional shadow pipeline compiled");

            // Compile a legacy 1-sampler custom 3D shader (original FrameUniforms prefix, 1 fragment sampler s0/t0)
            // to guarantee 100% backwards compatibility with user shaders like hologram_energy.hlsl!
            const string legacyCustomShader = """
                #pragma pack_matrix(column_major)
                struct Instance3D { float4x4 Model; float4 BaseColor; float4 EmissiveMeta; float4 Extra; float4 CustomParams; };
                StructuredBuffer<Instance3D> Instances : register(t0, space0);
                StructuredBuffer<float4x4> JointPalette : register(t1, space0);
                cbuffer FrameUniformsVS : register(b0, space1) {
                    float4x4 ViewProj; float4 CameraPosTime; float4 SunDirIntensity; float4 SunColor;
                    float4 AmbientSky; float4 AmbientGround; float4 FogColorDensity;
                    float4 PointLightPos[4]; float4 PointLightColor[4];
                };
                cbuffer BatchUniformsVS : register(b1, space1) { uint BaseInstance; uint3 _Pad; };
                struct VSIn { float3 Pos : TEXCOORD0; float3 Norm : TEXCOORD1; float2 Uv : TEXCOORD2; float4 Col : TEXCOORD3; uint Ji : TEXCOORD4; float4 Jw : TEXCOORD5; float2 Th : TEXCOORD6; uint Iid : SV_InstanceID; };
                struct PSIn { float4 Clip : SV_Position; float2 Uv : TEXCOORD0; float4 BaseCol : TEXCOORD1; };
                PSIn VSMain(VSIn i) {
                    Instance3D inst = Instances[BaseInstance + i.Iid];
                    float4 lp = float4(i.Pos, 1.0);
                    int jo = (int)inst.Extra.z;
                    if (jo >= 0) lp = mul(JointPalette[jo + (i.Ji & 0xFFu)], lp);
                    PSIn o;
                    o.Clip = mul(ViewProj, mul(inst.Model, lp));
                    o.Uv = i.Uv;
                    o.BaseCol = inst.BaseColor * i.Col;
                    return o;
                }
                SamplerState AlbedoSamp : register(s0, space2);
                Texture2D AlbedoTex : register(t0, space2);
                cbuffer FrameUniformsPS : register(b0, space3) {
                    float4x4 PS_ViewProj; float4 PS_CameraPosTime; float4 PS_SunDirIntensity; float4 PS_SunColor;
                    float4 PS_AmbientSky; float4 PS_AmbientGround; float4 PS_FogColorDensity;
                    float4 PS_PointLightPos[4]; float4 PS_PointLightColor[4];
                };
                float4 PSMain(PSIn i) : SV_Target0 { return AlbedoTex.Sample(AlbedoSamp, i.Uv) * i.BaseCol; }
                """;
            ShaderHandle custom1Sampler = ShaderSystem.CompileFromSource(ref *gpu, legacyCustomShader, "legacy_custom_1samp", ShaderVertexLayout.Mesh3D);
            GpuShaderSlot* customSlot = ShaderSystem.TryGetShader(in *gpu, custom1Sampler);
            GpuShaderSlot* pbrSlot = ShaderSystem.TryGetShader(in *gpu, gpu->BuiltinPbrShader);
            Assert.True(customSlot != null && customSlot->FragmentSamplerCount == 1, "Legacy custom shader reflected 1 fragment sampler");
            Assert.True(pbrSlot != null && pbrSlot->FragmentSamplerCount == 2, "Builtin PBR shader reflected 2 fragment samplers (Albedo + ShadowMap)");

            // Build unit cube mesh via GlbBuilder (1 primitive)
            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
            GltfHandle boxGltf = GltfLoader.LoadFromMemory(boxGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("shadow_cube"));
            GltfModelAssetData* boxModel = AssetSystem.TryGetGltf(in world.Assets, boxGltf);
            MeshHandle cubeMesh = boxModel->Primitives[0].Mesh;

            MaterialHandle floorMat = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                Name = StringId.Hash("floor_mat"),
                OwnerBundle = bundleId,
                BaseColorFactor = ColorF.White,
                RoughnessFactor = 0.9f,
                MetallicFactor = 0f,
                AlphaMode = MaterialAlphaMode.Opaque,
            });
            MaterialHandle customMat = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                Name = StringId.Hash("custom_mat"),
                OwnerBundle = bundleId,
                BaseColorFactor = ColorF.Cyan,
                AlphaMode = MaterialAlphaMode.Opaque,
                CustomShader = custom1Sampler,
            });

            // Floor at y = -0.25 (top surface at y = 0.0), spanning [-10, 10] in X and Z
            EntityId floorEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, floorEnt, new Vec3(0f, -0.25f, 0f), Quat.Identity, new Vec3(20f, 0.5f, 20f));
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, floorEnt, cubeMesh, floorMat, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);

            // Elevated occluder cube above center (0, 4.5, 0), scale (2.8, 0.6, 2.8): casts a clear shadow onto (0, 0, 0)
            // Placed above the camera frustum so off-camera shadow casting is verified too!
            EntityId occEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, occEnt, new Vec3(0f, 4.5f, 0f), Quat.Identity, new Vec3(2.8f, 0.6f, 2.8f));
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, occEnt, cubeMesh, floorMat, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);

            // Custom 1-sampler shader cube off to the side at (5, 0.5, 0)
            EntityId customEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, customEnt, new Vec3(5f, 0.5f, 0f), Quat.Identity, Vec3.One);
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, customEnt, cubeMesh, customMat, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);

            // Camera looks straight at (0, 0, 0) on the floor from (0, 2.4, 5.5) -> screen center = shadowed floor, far-left = sunlit floor!
            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 2.4f, 5.5f), new Vec3(0f, 0f, 0f), 55f);

            SceneLighting lit = SceneLighting.Default;
            lit.SunDirection = new Vec3(0f, 1f, 0.02f).Normalized(); // Nearly straight down so shadow lands directly on (0,0,0)
            lit.SunIntensity = 2.0f;
            lit.AmbientSky = new ColorF(0.15f, 0.15f, 0.15f, 1f);
            lit.AmbientGround = new ColorF(0.05f, 0.05f, 0.05f, 1f);
            lit.FogDensity = 0f;
            lit.ShadowDistance = 12f;
            lit.ShadowMapSize = 1024u;
            lit.ShadowStrength = 0.85f;
            lit.ShadowsEnabled = 1;

            const int W = 320, H = 180;
            byte[] rgbaShadowed = new byte[W * H * 4];
            byte[] rgbaUnshadowed = new byte[W * H * 4];

            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, W, H);
            GpuSystem.EnsureCaptureTarget(ref *gpu, W, H);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, in lit, captureToOffscreen: true);
            RenderStats stOn = gpu->LastFrameStats;
            Assert.True(stOn.ShadowDrawCalls >= 1, $"Expected >=1 shadow draw call with shadows ON, got {stOn.ShadowDrawCalls}");
            Assert.True(GpuSystem.ReadCaptureTextureRgba(ref *gpu, rgbaShadowed), "Read shadowed capture pixels");

            // Center pixel (W/2, H/2) is floor under occluder; left-mid pixel (W/8, H/2 + 20) is sunlit floor
            int centerOff = ((H / 2) * W + (W / 2)) * 4;
            int sunlitOff = ((H / 2 + 15) * W + (W / 8)) * 4;
            int shadowCenterLuma = (rgbaShadowed[centerOff] + rgbaShadowed[centerOff + 1] + rgbaShadowed[centerOff + 2]) / 3;
            int sunlitFloorLuma = (rgbaShadowed[sunlitOff] + rgbaShadowed[sunlitOff + 1] + rgbaShadowed[sunlitOff + 2]) / 3;
            Assert.True(sunlitFloorLuma - shadowCenterLuma >= 35,
                $"Directional shadow must darken floor under occluder (sunlit={sunlitFloorLuma}, shadowed={shadowCenterLuma})");

            // Render same scene with ShadowsEnabled = 0 -> center floor becomes brightly lit!
            lit.ShadowsEnabled = 0;
            alloc.BeginFrame();
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, in lit, captureToOffscreen: true);
            RenderStats stOff = gpu->LastFrameStats;
            Assert.Equal(0, stOff.ShadowDrawCalls, "0 shadow draw calls when ShadowsEnabled = 0");
            Assert.True(GpuSystem.ReadCaptureTextureRgba(ref *gpu, rgbaUnshadowed), "Read unshadowed capture pixels");

            int unshadowedCenterLuma = (rgbaUnshadowed[centerOff] + rgbaUnshadowed[centerOff + 1] + rgbaUnshadowed[centerOff + 2]) / 3;
            Assert.True(unshadowedCenterLuma - shadowCenterLuma >= 35,
                $"Disabling shadows must restore sunlight at floor center (off={unshadowedCenterLuma}, on={shadowCenterLuma})");

            // Zero-GC verification for ExtractRenderPacket with directional shadows ON
            lit.ShadowsEnabled = 1;
            var shaders = GpuSystem.GetBuiltinShaders(in *gpu);
            alloc.BeginFrame();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, in lit, ref alloc.Frame, ref alloc.Scratch, out _); // warmup
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 30; f++)
            {
                alloc.BeginFrame();
                BatchRenderer.ExtractRenderPacket(in shaders, in world, in lit, ref alloc.Frame, ref alloc.Scratch, out _);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "ExtractRenderPacket with directional shadows allocates 0 GC bytes");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after directional shadow GPU test");
    }

    private static void TestTextWordWrapAndGlyphBudget()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 2 * 1024 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("wrap_font"), 48f, 512, 6);
        FontAssetData* font = AssetSystem.TryGetFont(in world.Assets, fontH);
        TextureAssetData* atlas = AssetSystem.TryGetTexture(in world.Assets, font->AtlasTexture);
        atlas->GpuSlot = 0; // Headless GPU slot simulation

        var e = EntitySystem.Spawn(ref world.Entities);
        TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
        TransformSystem.PropagateHierarchy(ref world.Transforms);

        var camH = CameraSystem.CreateOrthographicUI(ref world.Cameras, 1280f, 720f);
        CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);
        CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, camH);

        // F9: Word-wrapped line 2 must start flush at X = 0 (no leading space indent)
        ref GlyphMetrics gmA = ref font->AsciiGlyphs['A' - SdfFontLoader.FirstAscii];
        ref GlyphMetrics gmSpace = ref font->AsciiGlyphs[0];
        var td = RenderComponentSystem.DefaultHudText(e, fontH, "AAA BBB", default);
        td.FontScale = 1f;
        td.AlignH = TextAlignmentH.Left;
        td.ShadowColor = ColorF.Transparent;
        td.ShadowOffset = Vec2.Zero;
        td.MaxWidth = (gmA.Advance * 3f + gmSpace.Advance * 0.5f);

        alloc.BeginFrame();
        var glyphs = NativeList<GlyphGpuInstance>.CreateInArena(ref alloc.Frame, 64);
        int count = TextLayoutSystem.LayoutTextIntoGlyphs(in td, in *font, in world.Transforms, in *cam, ref alloc.Scratch, ref glyphs);
        Assert.Equal(6, count, "Word-wrap emits 6 visible glyphs ('AAA' + 'BBB')");
        float firstLineX = glyphs[0].OriginAndPad.X; // 'A'
        float secondLineX = glyphs[3].OriginAndPad.X; // 'B'
        // Account for BearingX difference between 'A' and 'B': OriginAndPad.X = lc.X + gm.BearingX
        ref GlyphMetrics gmB = ref font->AsciiGlyphs['B' - SdfFontLoader.FirstAscii];
        float lcX1 = firstLineX - gmA.BearingX;
        float lcX2 = secondLineX - gmB.BearingX;
        Assert.Near(lcX1, lcX2, 1e-3f, "Wrapped line starts at exact same local X as line 1 (no leading space indent)");

        // F6 & SHD-07: Single FixedString256-max (252-char) shadowed TextData must emit 504 glyphs (not truncated at 192) & forward CustomParams
        var longEnt = EntitySystem.Spawn(ref world.Entities);
        TransformSystem.Add(ref world.Transforms, longEnt, Vec3.Zero);
        TransformSystem.PropagateHierarchy(ref world.Transforms);
        string longStr = new string('A', FixedString256.MaxBytes);
        var longTd = RenderComponentSystem.DefaultHudText(longEnt, fontH, longStr, default);
        longTd.CustomParams = new Vec4(1.5f, 2.5f, 3.5f, 4.5f);
        RenderComponentSystem.AddText(ref world.RenderComponents, in longTd);

        alloc.BeginFrame();
        var shaders = new RenderShaderDefaults { SdfText = ShaderHandle.Create(1, 1) };
        BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket packet);
        Assert.Equal(FixedString256.MaxBytes * 2, packet.Glyphs.Count, "Single FixedString256.MaxBytes shadowed TextData emits all 504 glyphs");
        Assert.Near(1.5f, packet.Glyphs[0].CustomParams.X, 1e-4f, "GlyphGpuInstance forwards TextData.CustomParams.X");
        Assert.Near(4.5f, packet.Glyphs[FixedString256.MaxBytes * 2 - 1].CustomParams.W, 1e-4f, "GlyphGpuInstance forwards TextData.CustomParams.W");

        bundleArena.Dispose();
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }

    private static void TestBatchRendererRulesAndCapacityCap()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string logDir = Path.Combine(AppContext.BaseDirectory, "test_render_logs_cap");
        if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);

        var alloc = EngineAllocators.Create(20 * 1024 * 1024, 12 * 1024 * 1024, 8 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, maxEntities: 17000, maxParticles: 64);
        world.RenderComponents = RenderComponentTables.Create(ref alloc.Persistent, maxMeshRenderers: 17000, maxSprites: 17000, maxTexts: 64);

        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, directoryOverride: logDir, minLevel: LogLevel.Warn, hookExceptions: false);

        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        try
        {
            // Register dummy mesh & materials & font with simulated GpuSlots
            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
            GltfHandle boxGltf = GltfLoader.LoadFromMemory(boxGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("cap_box"));
            GltfModelAssetData* boxModel = AssetSystem.TryGetGltf(in world.Assets, boxGltf);
            MeshHandle meshH = boxModel->Primitives[0].Mesh;
            MeshAssetData* meshPtr = AssetSystem.TryGetMesh(in world.Assets, meshH);
            meshPtr->GpuSlot = 0;

            TextureHandle whiteH = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("cap_white"));
            TextureAssetData* whitePtr = AssetSystem.TryGetTexture(in world.Assets, whiteH);
            whitePtr->GpuSlot = 0;

            FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("cap_font"));
            FontAssetData* fontPtr = AssetSystem.TryGetFont(in world.Assets, fontH);
            AssetSystem.TryGetTexture(in world.Assets, fontPtr->AtlasTexture)->GpuSlot = 1;

            // F4: CastShadow = 0 must exclude mesh from ShadowBatches3D
            MaterialHandle matA = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                OwnerBundle = bundleId, BaseColorFactor = ColorF.White, AlphaMode = MaterialAlphaMode.Opaque,
                CustomShader = ShaderHandle.Create(1, 1),
            });
            MaterialHandle matB = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                OwnerBundle = bundleId, BaseColorFactor = ColorF.White, AlphaMode = MaterialAlphaMode.Opaque,
                CustomShader = ShaderHandle.Create(1, 2), // Same index 1, different generation!
            });
            MaterialHandle matDouble = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                OwnerBundle = bundleId, BaseColorFactor = ColorF.White, AlphaMode = MaterialAlphaMode.Opaque,
                CustomShader = ShaderHandle.Create(1, 1), DoubleSided = 1,
            });

            EntityId e1 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e1, new Vec3(-2f, 0f, 0f));
            int mr1 = RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, e1, meshH, matA, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
            world.RenderComponents.MeshRenderers[mr1].CastShadow = 0;

            EntityId e2 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e2, new Vec3(0f, 0f, 0f));
            int mr2 = RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, e2, meshH, matB, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
            world.RenderComponents.MeshRenderers[mr2].CastShadow = 0;

            EntityId e3 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e3, new Vec3(2f, 0f, 0f));
            int mr3 = RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, e3, meshH, matDouble, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
            world.RenderComponents.MeshRenderers[mr3].CastShadow = 0;

            // F5: SortingOrder test with RemoveText swap-back
            EntityId te = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, te, Vec3.Zero);
            var td10 = RenderComponentSystem.DefaultHudText(te, fontH, "Z", default); td10.SortingOrder = 10; td10.ShadowColor = ColorF.Transparent; td10.ShadowOffset = Vec2.Zero;
            var td5 = RenderComponentSystem.DefaultHudText(te, fontH, "MM", default); td5.SortingOrder = 5; td5.ShadowColor = ColorF.Transparent; td5.ShadowOffset = Vec2.Zero;
            var td1 = RenderComponentSystem.DefaultHudText(te, fontH, "AAA", default); td1.SortingOrder = 1; td1.ShadowColor = ColorF.Transparent; td1.ShadowOffset = Vec2.Zero;
            RenderComponentSystem.AddText(ref world.RenderComponents, in td10);
            TextHandle hMid = RenderComponentSystem.AddText(ref world.RenderComponents, in td5);
            RenderComponentSystem.AddText(ref world.RenderComponents, in td1);
            RenderComponentSystem.RemoveText(ref world.RenderComponents, hMid);
            var td2 = RenderComponentSystem.DefaultHudText(te, fontH, "BBBB", default); td2.SortingOrder = 2; td2.ShadowColor = ColorF.Transparent; td2.ShadowOffset = Vec2.Zero;
            RenderComponentSystem.AddText(ref world.RenderComponents, in td2);

            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 2f, 12f), Vec3.Zero);
            CameraSystem.CreateOrthographicUI(ref world.Cameras, 1280f, 720f, depthOrder: 10);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);

            var shaders = new RenderShaderDefaults { Pbr = ShaderHandle.Create(0, 1), Sprite = ShaderHandle.Create(2, 1), SdfText = ShaderHandle.Create(3, 1) };
            alloc.BeginFrame();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket packetSmall);

            Assert.Equal(0, packetSmall.ShadowBatches3D.Count, "CastShadow = 0 excludes all meshes from ShadowBatches3D");
            Assert.Equal(3, packetSmall.Batches3D.Count, "Distinct ShaderHandle generations and DoubleSided do not merge batches");
            // Text order must be SortingOrder 1 ("AAA", 3 glyphs), 2 ("BBBB", 4 glyphs), 10 ("Z", 1 glyph) -> UVRect checks or glyph count 8
            Assert.Equal(8, packetSmall.Glyphs.Count, "Sorted texts emit 8 glyphs total");
            Assert.True(packetSmall.Glyphs[0].UvRect.Equals(fontPtr->AsciiGlyphs['A' - 32].UvRect), "First text glyphs match SortingOrder=1 ('A')");
            Assert.True(packetSmall.Glyphs[3].UvRect.Equals(fontPtr->AsciiGlyphs['B' - 32].UvRect), "Second text glyphs match SortingOrder=2 ('B')");
            Assert.True(packetSmall.Glyphs[7].UvRect.Equals(fontPtr->AsciiGlyphs['Z' - 32].UvRect), "Last text glyph matches SortingOrder=10 ('Z')");

            // 6.1: Capacity cap overflow on a tight packetArena must truncate cleanly without hanging or throwing!
            for (int i = 3; i < 16600; i++)
            {
                EntityId me = EntitySystem.Spawn(ref world.Entities);
                TransformSystem.Add(ref world.Transforms, me, Vec3.Zero);
                RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, me, meshH, matA, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
                RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
                {
                    Entity = me, Texture = whiteH, Size = Vec2.One, Color = ColorF.White, LayerMask = 1u, Visible = 1,
                });
            }
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);

            BatchRenderer.ResetWarningLatchesForTest();
            var tightFrame = LinearArena.Create(512 * 1024);
            try
            {
                BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref tightFrame, ref alloc.Scratch, out RenderPacket packetCap, log: log);
                Assert.True(packetCap.Instances3D.Count > 0 && packetCap.Instances3D.Count < 16600, $"3D instances truncated cleanly to arena budget ({packetCap.Instances3D.Count})");
                Assert.True(packetCap.Sprites.Count > 0 && packetCap.Sprites.Count < 16600, $"Sprites truncated cleanly to arena budget ({packetCap.Sprites.Count})");
            }
            finally
            {
                tightFrame.Dispose();
            }

            LogSystem.Flush(log);
            string logText = ReadAllTextShared(LogSystem.GetFilePath(log));
            Assert.True(logText.Contains("[WARN ]") && logText.Contains("capacity cap reached"), "Capacity cap truncation emitted [WARN] log");
        }
        finally
        {
            LogSystem.Stop(log);
            bundleArena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after capacity cap test");
    }

    private static void TestShadowSnappingAndDistance()
    {
        SceneLighting lit = SceneLighting.Default;
        lit.ShadowDistance = 1f;
        lit.ShadowMapSize = 1024u;
        float texelWorld = (2f * 1f) / 1024u;

        lit.ShadowCenter = new Vec3(0f, 0f, 0f);
        Mat4 m0 = BatchRenderer.ComputeDirectionalLightViewProj(in lit, 1024u);

        // Shift by less than 0.25 texels -> snapped matrix must remain identical
        lit.ShadowCenter = new Vec3(texelWorld * 0.1f, 0f, texelWorld * 0.1f);
        Mat4 m1 = BatchRenderer.ComputeDirectionalLightViewProj(in lit, 1024u);
        Assert.Near(m0.C3.X, m1.C3.X, 1e-4f, "Sub-texel ShadowCenter shift snaps to identical LightViewProj X");
        Assert.Near(m0.C3.Y, m1.C3.Y, 1e-4f, "Sub-texel ShadowCenter shift snaps to identical LightViewProj Y");

        // Verify ShadowDistance = 1f was NOT overridden to 28f (ortho width = 2 -> C0.X = 1.0f approx)
        float scaleX = MathF.Sqrt(m0.C0.X * m0.C0.X + m0.C1.X * m0.C1.X + m0.C2.X * m0.C2.X);
        Assert.True(scaleX > 0.5f, $"ShadowDistance=1f honored (ortho scaleX={scaleX:F3} >> 1/28)");
    }

    private static void TestSpirvAndMslSmoke()
    {
        NativeMemoryTracker.ResetStatsForTest();
        Assert.True(ShaderCross.Init(), "ShaderCross.Init succeeded");
        try
        {
            string shadersDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
            string[] files = new[]
            {
                "pbr_mesh.hlsl",
                "shadow_depth.hlsl",
                "shadow_depth_mask.hlsl",
                "sprite_3d.hlsl",
                "sdf_text.hlsl",
                "particle.hlsl",
            };

            foreach (string file in files)
            {
                string fullPath = Path.Combine(shadersDir, file);
                Assert.True(File.Exists(fullPath), $"Shader file exists: {fullPath}");
                string src = File.ReadAllText(fullPath);

                foreach (var (entry, stage) in new[] { ("VSMain", ShaderCross.ShaderStage.Vertex), ("PSMain", ShaderCross.ShaderStage.Fragment) })
                {
                    nint spirvPtr = ShaderCross.CompileSPIRVFromHLSL(src, entry, stage, out nuint spirvSize);
                    Assert.True(spirvPtr != nint.Zero && spirvSize > 0, $"SPIR-V compiled: {file} ({entry})");
                    try
                    {
                        nint metaPtr = ShaderCross.ReflectGraphicsSPIRV(spirvPtr, spirvSize, 0);
                        Assert.True(metaPtr != nint.Zero, $"SPIR-V reflected: {file} ({entry})");
                        SDL.Free(metaPtr);

                        var info = new ShaderCross.SPIRVInfo
                        {
                            ByteCode = spirvPtr,
                            ByteCodeSize = spirvSize,
                            ManagedEntrypoint = entry,
                            ShaderStage = stage,
                            Props = 0,
                        };
                        nint mslPtr = ShaderCross.TranspileMSLFromSPIRV(in info);
                        Assert.True(mslPtr != nint.Zero, $"MSL transpiled: {file} ({entry})");
                        try
                        {
                            string mslCode = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(mslPtr) ?? string.Empty;
                            string expectedToken = stage == ShaderCross.ShaderStage.Vertex ? "vertex" : "fragment";
                            Assert.True(mslCode.Contains(expectedToken), $"MSL contains '{expectedToken}' for {file} ({entry})");
                        }
                        finally
                        {
                            SDL.Free(mslPtr);
                        }
                    }
                    finally
                    {
                        SDL.Free(spirvPtr);
                    }
                }
            }
        }
        finally
        {
            ShaderCross.Quit();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after SPIR-V/MSL smoke test");
    }

    private static void TestFocusLostInputRelease()
    {
        InputState input = default;
        input.SetKey(KeyCode.W, true);
        input.MouseButtonsDown = 1u;
        input.SetGamepadConnected(0, true, 1001u);
        input.SetGamepadButton(0, GamepadButton.South, true);
        input.SetGamepadAxis(0, GamepadAxis.LeftX, 0.8f);
        input.SetGamepadAxis(0, GamepadAxis.LeftY, -0.6f);
        input.SetGamepadAxis(0, GamepadAxis.RightTrigger, 0.9f);
        input.BeginFrame(); // Key, mouse, and gamepad states now in Prev & Down

        Assert.True(input.IsKeyDown(KeyCode.W), "W is initially held");
        Assert.True(input.IsMouseDown(0), "Mouse 0 is initially held");
        Assert.True(input.IsGamepadButtonDown(GamepadButton.South, 0), "Gamepad South is initially held");

        // Invoke production helper used by PlatformSystem.PollEvents on WindowFocusLost
        InputSystem.ApplyFocusLost(ref input);

        Assert.True(!input.IsWindowFocused && input.WasWindowFocusLost, "Window focus lost recorded");
        Assert.True(!input.IsKeyDown(KeyCode.W) && input.IsKeyReleased(KeyCode.W), "Held key released cleanly on focus lost");
        Assert.True(!input.IsMouseDown(0) && input.IsMouseReleased(0), "Held mouse button released cleanly on focus lost");
        Assert.True(!input.IsGamepadButtonDown(GamepadButton.South, 0) && input.IsGamepadButtonReleased(GamepadButton.South, 0),
            "Held gamepad button released cleanly on focus lost");
        Vec2 stick = input.GetGamepadLeftStick(0, 0f);
        Assert.Near(0f, stick.X, 1e-5f, "Gamepad left stick X cleared on focus lost");
        Assert.Near(0f, stick.Y, 1e-5f, "Gamepad left stick Y cleared on focus lost");
        Assert.Near(0f, input.GetGamepadRightTrigger(0), 1e-5f, "Gamepad right trigger cleared on focus lost");
    }

    private static void TestGpuNon4x4AndBundleLifecycle()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string logDir = Path.Combine(AppContext.BaseDirectory, "test_render_logs_non4x4");
        if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);

        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, directoryOverride: logDir, minLevel: LogLevel.Info, hookExceptions: false);

        var bundle1Arena = LinearArena.Create(2 * 1024 * 1024);
        var bundle2Arena = LinearArena.Create(2 * 1024 * 1024);
        var b1 = BundleId.Create(1, 1);
        var b2 = BundleId.Create(2, 1);

        var window = PlatformSystem.InitWindow("Non4x4 & Lifecycle Test", 320, 180, hidden: true, log: log);
        Assert.True(window.Mode == WindowMode.Windowed, "Hidden window defaults to Windowed mode");
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true, log: log);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu, log: log);

            // Upload non-4x4 (33x19) BC1 & BC3 BRTX textures under debugMode: true
            int w = 33, h = 19;
            byte[] rgbaOpaque = new byte[w * h * 4];
            byte[] rgbaAlpha = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                rgbaOpaque[i * 4 + 0] = 200; rgbaOpaque[i * 4 + 1] = 120; rgbaOpaque[i * 4 + 2] = 40; rgbaOpaque[i * 4 + 3] = 255;
                rgbaAlpha[i * 4 + 0] = 40; rgbaAlpha[i * 4 + 1] = 200; rgbaAlpha[i * 4 + 2] = 220; rgbaAlpha[i * 4 + 3] = 180;
            }
            byte[] brtxBc1;
            byte[] brtxBc3;
            fixed (byte* p1 = rgbaOpaque)
                brtxBc1 = TextureCompressionSystem.EncodeBrtxBytes(p1, w, h, TextureCompressionPolicy.Bc1);
            fixed (byte* p3 = rgbaAlpha)
                brtxBc3 = TextureCompressionSystem.EncodeBrtxBytes(p3, w, h, TextureCompressionPolicy.Bc3);

            TextureHandle tBc1 = TextureLoader.LoadBrtxFromMemory(brtxBc1, ref bundle1Arena, ref world.Assets, b1, StringId.Hash("non4_bc1"));
            TextureHandle tBc3 = TextureLoader.LoadBrtxFromMemory(brtxBc3, ref bundle1Arena, ref world.Assets, b1, StringId.Hash("non4_bc3"));

            CameraSystem.CreateOrthographicUI(ref world.Cameras, 320f, 180f);
            EntityId se1 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, se1, new Vec3(40f, 40f, 0f));
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = se1, Texture = tBc1, Size = new Vec2(33f, 19f), LayerMask = 1u << 31, DepthTest = 0, Visible = 1 });

            EntityId se2 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, se2, new Vec3(100f, 40f, 0f));
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = se2, Texture = tBc3, Size = new Vec2(33f, 19f), LayerMask = 1u << 31, DepthTest = 0, Visible = 1 });

            GpuSystem.EnsureCaptureTarget(ref *gpu, 320, 180);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 320, 180);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);

            int slot1 = AssetSystem.TryGetTexture(in world.Assets, tBc1)->GpuSlot;
            int slot2 = AssetSystem.TryGetTexture(in world.Assets, tBc3)->GpuSlot;
            Assert.True(slot1 >= 0 && slot2 >= 0, "Non-4x4 BC1 & BC3 uploaded cleanly via RGBA8 fallback");

            // Unload bundle 1 -> deferred release enqueues GPU texture slots until FlushDeferredReleases
            AssetSystem.UnloadByBundle(ref world.Assets, b1);
            Assert.Equal((byte)1, gpu->Textures[slot1].Alive, "Bundle unload defers GPU texture slot 1 release until FlushDeferredReleases");
            Assert.Equal((byte)1, gpu->TextureSlotPendingRelease[slot1], "Bundle unload marks GPU texture slot 1 pending release");
            GpuSystem.FlushDeferredReleases(ref *gpu);
            Assert.Equal((byte)0, gpu->Textures[slot1].Alive, "FlushDeferredReleases released GPU texture slot 1");
            Assert.Equal((byte)0, gpu->Textures[slot2].Alive, "FlushDeferredReleases released GPU texture slot 2");

            // Reuse slots with bundle 2 and render cleanly
            TextureHandle tReuse = TextureLoader.CreateSolidColor(new Color32(0, 255, 255, 255), ref bundle2Arena, ref world.Assets, b2, StringId.Hash("reuse_tex"));
            EntityId se3 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, se3, new Vec3(60f, 60f, 0f));
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = se3, Texture = tReuse, Size = new Vec2(32f, 32f), LayerMask = 1u << 31, DepthTest = 0, Visible = 1 });

            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 320, 180);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);
            Assert.Equal(slot1, AssetSystem.TryGetTexture(in world.Assets, tReuse)->GpuSlot, "Reused freed GPU texture slot 0");

            LogSystem.Flush(log);
            string logText = ReadAllTextShared(LogSystem.GetFilePath(log));
            Assert.True(logText.Contains("falling back to RGBA8 software decode"), "Non-4x4 BC1/BC3 fallback logged warning");

            // Verify Dispose(ref gpu, ref world.Assets) clears hooks safely even before UnloadByBundle
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            Assert.True(world.Assets.GpuContextPtr == null && world.Assets.ReleaseGpuTexture == null, "GpuSystem.Dispose cleared AssetRegistry hooks");
            AssetSystem.UnloadByBundle(ref world.Assets, b2);
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, b1);
            AssetSystem.UnloadByBundle(ref world.Assets, b2);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            LogSystem.Stop(log);
            bundle1Arena.Dispose();
            bundle2Arena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after non-4x4 and bundle lifecycle test");
    }

    private static void TestShaderSystemErrorsAndCustomQuadShaders()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string logDir = Path.Combine(AppContext.BaseDirectory, "test_render_logs_shaders");
        if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);

        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, directoryOverride: logDir, minLevel: LogLevel.Info, hookExceptions: false);

        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bId = BundleId.Create(1, 1);

        var window = PlatformSystem.InitWindow("Shader Errors & Custom Quads", 320, 180, hidden: true, log: log);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true, maxShaders: 16, log: log);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu, log: log);

            // SHD-01 / SHD-10: Missing file error path
            bool threwMissing = false;
            try { ShaderSystem.CompileFromFile(ref *gpu, Path.Combine(AppContext.BaseDirectory, "nonexistent_shader_xyz.hlsl"), "missing"); }
            catch (FileNotFoundException) { threwMissing = true; }
            Assert.True(threwMissing, "Missing shader file throws FileNotFoundException");

            // SHD-01 / SHD-10: Valid VS + broken PS must release VS cleanly and throw
            const string brokenPs = """
                #pragma pack_matrix(column_major)
                struct VSIn { float3 Pos : TEXCOORD0; };
                struct PSIn { float4 Clip : SV_Position; };
                PSIn VSMain(VSIn i) { PSIn o; o.Clip = float4(i.Pos, 1.0); return o; }
                float4 PSMain(PSIn i) : SV_Target0 { return THIS_IS_NOT_VALID_HLSL; }
                """;
            bool threwBroken = false;
            try { ShaderSystem.CompileFromSource(ref *gpu, brokenPs, "broken_ps", ShaderVertexLayout.InstancedQuad); }
            catch (InvalidOperationException) { threwBroken = true; }
            Assert.True(threwBroken, "Invalid PSMain throws InvalidOperationException and cleans up VS");

            // F8 / SHD-10 / SHD-12: Custom 0-sampler InstancedQuad sprite shader reading b0, space3 fragment uniform
            const string customQuad0Samp = """
                #pragma pack_matrix(column_major)
                struct SpriteGpuInstance { float4x4 Model; float4 UvRect; float4 Color; float4 CustomParams; };
                StructuredBuffer<SpriteGpuInstance> Sprites : register(t0, space0);
                cbuffer CamVS : register(b0, space1) { float4x4 ViewProj; float4 CameraPosTime; };
                cbuffer BatchVS : register(b1, space1) { uint BaseInstance; uint3 _Pad; };
                struct PSIn { float4 Clip : SV_Position; float4 Col : TEXCOORD0; };
                static const float2 Q[4] = { float2(0,0), float2(1,0), float2(1,1), float2(0,1) };
                PSIn VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
                    SpriteGpuInstance sp = Sprites[BaseInstance + iid];
                    float2 p = Q[vid & 3u];
                    PSIn o; o.Clip = mul(ViewProj, mul(sp.Model, float4(p, 0, 1))); o.Col = sp.Color; return o;
                }
                cbuffer CamPS : register(b0, space3) { float4x4 PS_ViewProj; float4 PS_CameraPosTime; };
                float4 PSMain(PSIn i) : SV_Target0 { return float4(i.Col.rgb, 1.0) + PS_CameraPosTime * 0.0; }
                """;
            ShaderHandle customSprite0 = ShaderSystem.CompileFromSource(ref *gpu, customQuad0Samp, "custom_sprite_0samp", ShaderVertexLayout.InstancedQuad);
            Assert.Equal((byte)0, ShaderSystem.TryGetShader(in *gpu, customSprite0)->FragmentSamplerCount, "Custom sprite shader reflected 0 samplers");

            // Custom 2-sampler 3D shader (Albedo + ShadowMap)
            const string custom3D2Samp = """
                #pragma pack_matrix(column_major)
                struct Instance3D { float4x4 Model; float4 BaseColor; float4 EmissiveMeta; float4 Extra; float4 CustomParams; };
                StructuredBuffer<Instance3D> Instances : register(t0, space0);
                StructuredBuffer<float4x4> JointPalette : register(t1, space0);
                cbuffer FrameVS : register(b0, space1) {
                    float4x4 ViewProj; float4 CameraPosTime; float4 SunDirIntensity; float4 SunColor;
                    float4 AmbientSky; float4 AmbientGround; float4 FogColorDensity;
                    float4 PointLightPos[4]; float4 PointLightColor[4]; float4x4 LightViewProj; float4 ShadowParams;
                };
                cbuffer BatchVS : register(b1, space1) { uint BaseInstance; uint3 _Pad; };
                struct VSIn { float3 P : TEXCOORD0; float3 N : TEXCOORD1; float2 Uv : TEXCOORD2; float4 C : TEXCOORD3; uint Ji : TEXCOORD4; float4 Jw : TEXCOORD5; float2 Th : TEXCOORD6; uint Iid : SV_InstanceID; };
                struct PSIn { float4 Clip : SV_Position; float2 Uv : TEXCOORD0; float4 Col : TEXCOORD1; };
                PSIn VSMain(VSIn i) {
                    Instance3D inst = Instances[BaseInstance + i.Iid];
                    float4 lp = float4(i.P, 1.0) + float4(i.N, 0.0) * 0.0 + float4(i.Th, 0, 0) * 0.0;
                    if ((int)inst.Extra.z >= 0) lp = mul(JointPalette[(int)inst.Extra.z + (i.Ji & 0xFFu)], lp);
                    PSIn o; o.Clip = mul(ViewProj, mul(inst.Model, lp)); o.Uv = i.Uv; o.Col = inst.BaseColor * i.C; return o;
                }
                SamplerState S0 : register(s0, space2); Texture2D T0 : register(t0, space2);
                SamplerState S1 : register(s1, space2); Texture2D T1 : register(t1, space2);
                float4 PSMain(PSIn i) : SV_Target0 { return T0.Sample(S0, i.Uv) * i.Col * (0.99 + 0.01 * T1.SampleLevel(S1, float2(0.5, 0.5), 0).r); }
                """;
            ShaderHandle custom3d2 = ShaderSystem.CompileFromSource(ref *gpu, custom3D2Samp, "custom_3d_2samp", ShaderVertexLayout.Mesh3D);
            Assert.Equal((byte)2, ShaderSystem.TryGetShader(in *gpu, custom3d2)->FragmentSamplerCount, "Custom 3D shader reflected 2 samplers");

            // Custom Text & Particle shaders + StretchedBillboard particle emitter
            FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bId, StringId.Hash("cq_font"));
            TextureHandle whiteH = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bId, StringId.Hash("cq_white"));
            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
            GltfHandle boxGltf = GltfLoader.LoadFromMemory(boxGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bId, StringId.Hash("cq_box"));
            MeshHandle cubeMesh = AssetSystem.TryGetGltf(in world.Assets, boxGltf)->Primitives[0].Mesh;

            // Layout mismatch test: assign InstancedQuad shader to 3D Material (must fallback to builtin PBR + warn once)
            MaterialHandle mat2Samp = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData { OwnerBundle = bId, BaseColorFactor = ColorF.Green, CustomShader = custom3d2 });
            MaterialHandle matMismatch = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData { OwnerBundle = bId, BaseColorFactor = ColorF.Red, CustomShader = customSprite0 });

            EntityId eGood = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, eGood, new Vec3(-1.5f, 0f, 0f));
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, eGood, cubeMesh, mat2Samp, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);

            EntityId eBadLayout = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, eBadLayout, new Vec3(1.5f, 0f, 0f));
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, eBadLayout, cubeMesh, matMismatch, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);

            EntityId se = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, se, new Vec3(40f, 40f, 0f));
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = se, Texture = whiteH, Size = new Vec2(64f, 64f), Color = ColorF.Cyan, CustomShader = customSprite0, LayerMask = 1u << 31, DepthTest = 0, Visible = 1 });

            EntityId te = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, te, new Vec3(100f, 100f, 0f));
            var td = RenderComponentSystem.DefaultHudText(te, fontH, "CUSTOM", default);
            td.CustomShader = gpu->BuiltinSdfTextShader;
            RenderComponentSystem.AddText(ref world.RenderComponents, in td);

            var emCfg = ParticleSystem.DefaultEmitter(Vec3.Zero, 77);
            emCfg.Texture = whiteH;
            emCfg.StretchedBillboard = 1;
            emCfg.BlendMode = ParticleBlendMode.Alpha;
            EmitterHandle emH = ParticleSystem.CreateEmitter(ref world.Particles, in emCfg);
            ParticleSystem.Burst(ref world.Particles, emH, 16);

            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 2f, 7f), Vec3.Zero);
            CameraSystem.CreateOrthographicUI(ref world.Cameras, 320f, 180f, depthOrder: 10);

            GpuSystem.EnsureCaptureTarget(ref *gpu, 320, 180);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 320, 180);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);
            Assert.True(gpu->LastFrameStats.DrawCalls >= 4, "Custom 0-sampler, 2-sampler, stretched particle & layout mismatch fallback rendered cleanly");

            // Verify ReleaseShader frees slot & slot exhaustion throws cleanly
            Assert.True(ShaderSystem.ReleaseShader(ref *gpu, customSprite0), "ReleaseShader freed slot");
            Assert.True(ShaderSystem.TryGetShader(in *gpu, customSprite0) == null, "Released shader handle is now invalid");

            int freeRemaining = 0;
            for (int i = 0; i < gpu->MaxShaders; i++) if (gpu->Shaders[i].Alive == 0) freeRemaining++;
            for (int i = 0; i < freeRemaining; i++)
                ShaderSystem.CompileFromSource(ref *gpu, customQuad0Samp, $"fill_{i}", ShaderVertexLayout.InstancedQuad);
            bool threwExhaust = false;
            try { ShaderSystem.CompileFromSource(ref *gpu, customQuad0Samp, "overflow", ShaderVertexLayout.InstancedQuad); }
            catch (OutOfMemoryException) { threwExhaust = true; }
            Assert.True(threwExhaust, "Shader slot exhaustion throws OutOfMemoryException");

            LogSystem.Flush(log);
            string logText = ReadAllTextShared(LogSystem.GetFilePath(log));
            Assert.True(logText.Contains("layout mismatch"), "Layout mismatch logged [WARN]");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            LogSystem.Stop(log);
            bundleArena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after shader error & custom shader tests");
    }

    private static void TestBuiltinShaderFeaturesAndDynamicGrow()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string logDir = Path.Combine(AppContext.BaseDirectory, "test_render_logs_features");
        if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);

        var alloc = EngineAllocators.Create(16 * 1024 * 1024, 12 * 1024 * 1024, 4 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, directoryOverride: logDir, minLevel: LogLevel.Info, hookExceptions: false);

        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bId = BundleId.Create(1, 1);

        var window = PlatformSystem.InitWindow("Builtin Features & Buffer Grow", 320, 180, hidden: true, log: log);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true, log: log);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu, log: log);

            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
            GltfHandle boxGltf = GltfLoader.LoadFromMemory(boxGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bId, StringId.Hash("feat_box"));
            MeshHandle cubeMesh = AssetSystem.TryGetGltf(in world.Assets, boxGltf)->Primitives[0].Mesh;

            // Create 64x64 split cutout texture: left half (x < 32) alpha=255, right half (x >= 32) alpha=0
            int texSz = 64;
            byte* maskPx = bundleArena.AllocUninitialized<byte>(texSz * texSz * 4, 16);
            for (int y = 0; y < texSz; y++)
            {
                for (int x = 0; x < texSz; x++)
                {
                    int o = (y * texSz + x) * 4;
                    maskPx[o + 0] = 255; maskPx[o + 1] = 255; maskPx[o + 2] = 255;
                    maskPx[o + 3] = (byte)(x < 32 ? 255 : 0);
                }
            }
            TextureHandle maskTex = AssetSystem.RegisterTexture(ref world.Assets, new TextureAssetData
            {
                OwnerBundle = bId, Name = StringId.Hash("mask_tex"), Pixels = maskPx,
                Width = texSz, Height = texSz, Format = TexturePixelFormat.Rgba8, Channels = 4, ClampEdge = 1, NearestFilter = 1,
            });

            MaterialHandle floorMat = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                OwnerBundle = bId, BaseColorFactor = ColorF.White, RoughnessFactor = 0.9f, MetallicFactor = 0f, AlphaMode = MaterialAlphaMode.Opaque,
            });
            MaterialHandle maskMat = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                OwnerBundle = bId, BaseColorFactor = ColorF.White, BaseColorTexture = maskTex,
                AlphaMode = MaterialAlphaMode.Mask, AlphaCutoff = 0.5f, DoubleSided = 1,
            });

            // Floor at y = -0.25
            EntityId floorEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, floorEnt, new Vec3(0f, -0.25f, 0f), Quat.Identity, new Vec3(20f, 0.5f, 20f));
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, floorEnt, cubeMesh, floorMat, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);

            // Alpha-masked occluder spanning X=[-3, 3] at Y=4.5: left half (X<0) opaque, right half (X>0) transparent
            EntityId maskOcc = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, maskOcc, new Vec3(0f, 4.5f, 0f), Quat.Identity, new Vec3(6f, 0.4f, 6f));
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, maskOcc, cubeMesh, maskMat, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);

            // Additive No-Depth sprite + SDF text with OutlineColor.a == 0 (no red bleed!)
            TextureHandle whiteH = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bId, StringId.Hash("feat_white"));
            FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bId, StringId.Hash("feat_font"));

            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 2.5f, 6f), new Vec3(0f, 0f, 0f), 55f);
            CameraSystem.CreateOrthographicUI(ref world.Cameras, 320f, 180f, depthOrder: 10);

            EntityId spEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, spEnt, new Vec3(10f, 10f, 0f));
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
            {
                Entity = spEnt, Texture = whiteH, Size = new Vec2(20f, 20f), Color = new ColorF(0f, 0.5f, 0.5f, 1f),
                BlendMode = SpriteBlendMode.Additive, DepthTest = 0, LayerMask = 1u << 31, Visible = 1,
            });

            EntityId txtEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, txtEnt, new Vec3(20f, 140f, 0f));
            var td = RenderComponentSystem.DefaultHudText(txtEnt, fontH, "CLEAN", default);
            td.Color = ColorF.White;
            td.OutlineColor = new ColorF(1f, 0f, 0f, 0f); // Transparent red outline must NOT bleed!
            td.OutlineWidth = 0.25f;
            td.ShadowColor = ColorF.Transparent;
            td.ShadowOffset = Vec2.Zero;
            RenderComponentSystem.AddText(ref world.RenderComponents, in td);

            SceneLighting lit = SceneLighting.Default;
            lit.SunDirection = new Vec3(0f, 1f, 0.01f).Normalized();
            lit.SunIntensity = 2.0f;
            lit.FogDensity = 0f;
            lit.ShadowDistance = 12f;
            lit.ShadowMapSize = 1024u;
            lit.ShadowStrength = 0.85f;

            const int W = 320, H = 180;
            byte[] px = new byte[W * H * 4];
            GpuSystem.EnsureCaptureTarget(ref *gpu, W, H);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, W, H);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, in lit, captureToOffscreen: true);
            Assert.True(GpuSystem.ReadCaptureTextureRgba(ref *gpu, px), "Read feature verification pixels");

            // SHD-02: Verify cutout shadow — left floor pixel (X = W/2 - 30) is shadowed, right floor pixel (X = W/2 + 30) is sunlit!
            int leftOff = ((H / 2 + 10) * W + (W / 2 - 30)) * 4;
            int rightOff = ((H / 2 + 10) * W + (W / 2 + 30)) * 4;
            int leftLuma = (px[leftOff] + px[leftOff + 1] + px[leftOff + 2]) / 3;
            int rightLuma = (px[rightOff] + px[rightOff + 1] + px[rightOff + 2]) / 3;
            Assert.True(rightLuma - leftLuma >= 30, $"Masked shadow cutout darkens opaque side ({leftLuma}) vs transparent side ({rightLuma})");

            // F1 & T8: Dynamic JointPalette buffer growth (> 32,768 Mat4s) + dynamic resize (320x180 -> 480x270)
            alloc.BeginFrame();
            var shaders = GpuSystem.GetBuiltinShaders(in *gpu);
            BatchRenderer.ExtractRenderPacket(in shaders, in world, in lit, ref alloc.Frame, ref alloc.Scratch, out RenderPacket bigPacket);
            bigPacket.JointPalette = alloc.Frame.AllocSlice<Mat4>(36000, 64, zeroed: true); // > 2MB initial JointPaletteBuffer!
            GpuSystem.EnsureCaptureTarget(ref *gpu, 480, 270);
            BatchRenderer.SubmitRenderPacket(ref *gpu, in bigPacket, captureToOffscreen: true, measureGpuWithFence: true);
            Assert.True(gpu->JointPaletteBufferBytes >= 36000u * (uint)sizeof(Mat4), "JointPaletteBuffer grew dynamically past 2MB");

            LogSystem.Flush(log);
            string logText = ReadAllTextShared(LogSystem.GetFilePath(log));
            Assert.True(logText.Contains("dynamically grew JointPalette buffer"), "Dynamic JointPalette buffer growth logged [WARN]");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            LogSystem.Stop(log);
            bundleArena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after builtin features & dynamic grow test");
    }

    // ==================== WP-B5 Regression Tests ====================

    private static void TestTextTrailingSpaceAndUtf8Typewriter()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 1 * 1024 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        try
        {
            FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("r08_font"), 48f, 512, 6);
            FontAssetData* font = AssetSystem.TryGetFont(in world.Assets, fontH);

            var e = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            var camH = CameraSystem.CreateOrthographicUI(ref world.Cameras, 1280f, 720f);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);
            CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, camH);

            ref GlyphMetrics gmA = ref font->AsciiGlyphs['A' - SdfFontLoader.FirstAscii];
            ref GlyphMetrics gmB = ref font->AsciiGlyphs['B' - SdfFontLoader.FirstAscii];
            ref GlyphMetrics gmSp = ref font->AsciiGlyphs[0];
            float kernASp = font->AsciiKerning != null ? font->AsciiKerning[('A' - 32) * SdfFontLoader.AsciiCount + 0] : 0f;
            float kernSpB = font->AsciiKerning != null ? font->AsciiKerning[0 * SdfFontLoader.AsciiCount + ('B' - 32)] : 0f;
            float widthAB = gmA.Advance + kernASp + gmSp.Advance + kernSpB + gmB.Advance;

            // MaxWidth fits "A B" comfortably, but overflows on the trailing space in "A B "
            float maxW = widthAB + gmSp.Advance * 0.25f;

            var tdNoTrail = RenderComponentSystem.DefaultHudText(e, fontH, "A B", default);
            tdNoTrail.FontScale = 1f;
            tdNoTrail.AlignV = TextAlignmentV.Middle;
            tdNoTrail.ShadowColor = ColorF.Transparent;
            tdNoTrail.ShadowOffset = Vec2.Zero;
            tdNoTrail.MaxWidth = maxW;

            var tdTrail = RenderComponentSystem.DefaultHudText(e, fontH, "A B ", default);
            tdTrail.FontScale = 1f;
            tdTrail.AlignV = TextAlignmentV.Middle;
            tdTrail.ShadowColor = ColorF.Transparent;
            tdTrail.ShadowOffset = Vec2.Zero;
            tdTrail.MaxWidth = maxW;

            alloc.BeginFrame();
            var glyphs1 = NativeList<GlyphGpuInstance>.CreateInArena(ref alloc.Frame, 32);
            var glyphs2 = NativeList<GlyphGpuInstance>.CreateInArena(ref alloc.Frame, 32);
            int c1 = TextLayoutSystem.LayoutTextIntoGlyphs(in tdNoTrail, in *font, in world.Transforms, in *cam, ref alloc.Scratch, ref glyphs1);
            int c2 = TextLayoutSystem.LayoutTextIntoGlyphs(in tdTrail, in *font, in world.Transforms, in *cam, ref alloc.Scratch, ref glyphs2);

            Assert.Equal(2, c1, "\"A B\" emits 2 visible glyphs");
            Assert.Equal(2, c2, "\"A B \" emits 2 visible glyphs");
            // Both 'A' and 'B' must stay on the exact same single line (matching baseline Y of "A B")
            Assert.Near(glyphs1[0].OriginAndPad.Y, glyphs2[0].OriginAndPad.Y, 1e-3f, "\"A B \" keeps 'A' on line 1");
            Assert.Near(glyphs1[1].OriginAndPad.Y, glyphs2[1].OriginAndPad.Y, 1e-3f, "\"A B \" keeps 'B' on line 1 (no backtrack split)");

            // Multi-byte UTF-8 ("é" = 2 bytes) with TypewriterProgress = 0.5f must emit 1 logical glyph, not 0
            var tdUtf8 = RenderComponentSystem.DefaultHudText(e, fontH, "\u00E9", default);
            tdUtf8.ShadowColor = ColorF.Transparent;
            tdUtf8.ShadowOffset = Vec2.Zero;
            tdUtf8.TypewriterProgress = 0.5f;
            var glyphs3 = NativeList<GlyphGpuInstance>.CreateInArena(ref alloc.Frame, 16);
            int c3 = TextLayoutSystem.LayoutTextIntoGlyphs(in tdUtf8, in *font, in world.Transforms, in *cam, ref alloc.Scratch, ref glyphs3);
            Assert.Equal(1, c3, "Multi-byte UTF-8 \"é\" with TypewriterProgress=0.5f emits 1 glyph");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after RENDER-08 text test");
    }

    private static void TestDirectionalLightNaNAndSmallDistance()
    {
        SceneLighting lit = SceneLighting.Default;
        lit.SunDirection = new Vec3(float.NaN, float.NaN, float.NaN);
        lit.ShadowCenter = new Vec3(float.NaN, 0f, float.PositiveInfinity);
        lit.ShadowDistance = 0.25f;

        Mat4 mNan = BatchRenderer.ComputeDirectionalLightViewProj(in lit, 1024u);
        Assert.True(
            float.IsFinite(mNan.C0.X) && float.IsFinite(mNan.C1.Y) && float.IsFinite(mNan.C2.Z) && float.IsFinite(mNan.C3.W) &&
            float.IsFinite(mNan.C3.X) && float.IsFinite(mNan.C3.Y) && float.IsFinite(mNan.C3.Z),
            "NaN SunDirection and ShadowCenter still yield a finite LightViewProj matrix");

        lit.SunDirection = new Vec3(0.3f, 0.9f, 0.2f).Normalized();
        lit.ShadowCenter = new Vec3(1f, 2f, 3f);
        lit.ShadowDistance = 0.25f;
        Mat4 mSmall = BatchRenderer.ComputeDirectionalLightViewProj(in lit, 1024u);
        Vec4 clipCenter = Mat4.TransformVec4(in mSmall, new Vec4(lit.ShadowCenter, 1f));
        float clipZ = clipCenter.Z / clipCenter.W;
        Assert.True(float.IsFinite(clipZ) && clipZ > 0.1f && clipZ < 0.9f,
            $"Small ShadowDistance=0.25f places ShadowCenter clip Z inside (0.1, 0.9), got {clipZ:F4}");
    }

    private static void TestComputeUploadBudgetClamping()
    {
        uint sz3D = (uint)sizeof(Instance3DGpu);       // 128
        uint szJ = (uint)sizeof(Mat4);                 // 64
        uint szS = (uint)sizeof(SpriteGpuInstance);    // 112
        uint szG = (uint)sizeof(GlyphGpuInstance);     // 144
        uint szP = (uint)sizeof(ParticleGpuInstance);  // 64

        // Transfer buffer fits 3 Instance3D (384 B), 2 Mat4 (128 B, off=384), and 1 Sprite (112 B, off=512 -> end=624)
        // with 40 extra bytes (< 1 sprite, < 1 glyph) -> trailing glyphs & particles must clamp to 0!
        uint smallTb = 3u * sz3D + 2u * szJ + 1u * szS + 40u;
        var b = BatchRenderer.ComputeUploadBudget(
            reqInst3D: 3, reqJoints: 2, reqSprites: 5, reqGlyphs: 4, reqParticles: 8,
            inst3DBufBytes: 1024 * 1024, jointBufBytes: 1024 * 1024, spriteBufBytes: 1024 * 1024,
            glyphBufBytes: 1024 * 1024, particleBufBytes: 1024 * 1024,
            transferBufBytes: smallTb);

        Assert.Equal(3u, b.Inst3D, "All 3 Instance3D fit");
        Assert.Equal(2u, b.Joints, "All 2 Joints fit");
        Assert.Equal(1u, b.Sprites, "Sprites clamped element-aligned to 1");
        Assert.Equal(0u, b.Glyphs, "Glyphs clamped cleanly to 0 when transfer buffer exhausted");
        Assert.Equal(0u, b.Particles, "Particles clamped cleanly to 0 when transfer buffer exhausted");
        Assert.Equal(0u, b.BytesSprites % szS, "Sprite upload bytes strictly element-aligned");
        Assert.True(b.TotalBytes <= smallTb, $"TotalBytes ({b.TotalBytes}) <= transferBufBytes ({smallTb})");
        Assert.Equal(0u, b.OffJoints & 63u, "Joint offset 64-byte aligned");
        Assert.Equal(0u, b.OffSprites & 63u, "Sprite offset 64-byte aligned");

        // Also test clamping purely by storage buffer capacity smaller than request
        var b2 = BatchRenderer.ComputeUploadBudget(
            reqInst3D: 100, reqJoints: 50, reqSprites: 20, reqGlyphs: 10, reqParticles: 30,
            inst3DBufBytes: sz3D * 4 + 17, jointBufBytes: szJ * 3 + 9, spriteBufBytes: szS * 2 + 5,
            glyphBufBytes: szG * 1 + 11, particleBufBytes: szP * 5 + 3,
            transferBufBytes: 1024 * 1024);
        Assert.Equal(4u, b2.Inst3D, "Instance3D clamped to storage buffer element capacity");
        Assert.Equal(3u, b2.Joints, "Joints clamped to storage buffer element capacity");
        Assert.Equal(2u, b2.Sprites, "Sprites clamped to storage buffer element capacity");
        Assert.Equal(1u, b2.Glyphs, "Glyphs clamped to storage buffer element capacity");
        Assert.Equal(5u, b2.Particles, "Particles clamped to storage buffer element capacity");
    }

    private static void TestMultiCameraShadowReservation()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(24 * 1024 * 1024, 16 * 1024 * 1024, 8 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, maxEntities: 9000, maxParticles: 64);
        world.RenderComponents = RenderComponentTables.Create(ref alloc.Persistent, maxMeshRenderers: 9000, maxSprites: 64, maxTexts: 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        try
        {
            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
            GltfHandle boxGltf = GltfLoader.LoadFromMemory(boxGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("r09_box"));
            MeshHandle meshH = AssetSystem.TryGetGltf(in world.Assets, boxGltf)->Primitives[0].Mesh;
            MeshAssetData* meshPtr = AssetSystem.TryGetMesh(in world.Assets, meshH);
            meshPtr->GpuSlot = 0;
            meshPtr->GpuSlotGen = 1;

            MaterialHandle matH = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                OwnerBundle = bundleId, BaseColorFactor = ColorF.White, AlphaMode = MaterialAlphaMode.Opaque,
            });

            // 8,200 visible shadow-casting meshes * 2 cameras = 16,400 main instances (previously filled the 16,384 cap and starved shadows!)
            for (int i = 0; i < 8200; i++)
            {
                EntityId e = EntitySystem.Spawn(ref world.Entities);
                TransformSystem.Add(ref world.Transforms, e, new Vec3((i % 20 - 10) * 0.2f, 0f, (i % 10 - 5) * 0.2f));
                RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, e, meshH, matH, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
            }

            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 3f, 12f), Vec3.Zero, depthOrder: 0);
            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 4f, 14f), Vec3.Zero, depthOrder: 1);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);

            var shaders = new RenderShaderDefaults { Pbr = ShaderHandle.Create(1, 1) };
            alloc.BeginFrame();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket packet);

            Assert.Equal((byte)1, packet.ShadowsActive, "Shadows remain active with 2 cameras + 8,200 meshes");
            Assert.True(packet.ShadowBatches3D.Count > 0, $"ShadowBatches3D populated ({packet.ShadowBatches3D.Count})");
            Assert.Equal(2, packet.Cameras.Length, "Both cameras extracted");
            Assert.True(packet.Cameras[0].Batch3DCount > 0 && packet.Cameras[1].Batch3DCount > 0, "Both cameras rendered 3D batches");
            Assert.Equal(8200 * 3, packet.Instances3D.Count, "All 8,200 shadow casters + 2x 8,200 camera instances fit under raised 65,536 cap");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after RENDER-09 multi-camera shadow test");
    }

    private static void TestAntiQuicksort256KBStack()
    {
        Exception? threadEx = null;
        var t = new Thread(() =>
        {
            try
            {
                NativeMemoryTracker.ResetStatsForTest();
                var alloc = EngineAllocators.Create(28 * 1024 * 1024, 16 * 1024 * 1024, 8 * 1024 * 1024, 256 * 1024);
                var world = EngineWorld.Create(ref alloc.Persistent, maxEntities: 17000, maxParticles: 64);
                world.RenderComponents = RenderComponentTables.Create(ref alloc.Persistent, maxMeshRenderers: 17000, maxSprites: 17000, maxTexts: 64);
                var bundleArena = LinearArena.Create(2 * 1024 * 1024);
                var bundleId = BundleId.Create(1, 1);

                try
                {
                    var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
                    byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
                    GltfHandle boxGltf = GltfLoader.LoadFromMemory(boxGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("qsort_box"));
                    MeshHandle meshH = AssetSystem.TryGetGltf(in world.Assets, boxGltf)->Primitives[0].Mesh;
                    AssetSystem.TryGetMesh(in world.Assets, meshH)->GpuSlot = 0;

                    TextureHandle texH = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("qsort_tex"));
                    AssetSystem.TryGetTexture(in world.Assets, texH)->GpuSlot = 0;

                    MaterialHandle mat0 = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData { OwnerBundle = bundleId, AlphaMode = MaterialAlphaMode.Opaque });
                    MaterialHandle mat1 = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData { OwnerBundle = bundleId, AlphaMode = MaterialAlphaMode.Blend });

                    const int N = 16384;
                    for (int i = 0; i < N; i++)
                    {
                        EntityId e = EntitySystem.Spawn(ref world.Entities);
                        TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
                        // Alternating / anti-quicksort pattern across keys
                        MaterialHandle mh = ((i & 1) == 0) ? mat1 : mat0;
                        RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, e, meshH, mh, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
                        short layer = (short)((i & 1) == 0 ? (N - i) % 1024 : (i % 1024));
                        RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
                        {
                            Entity = e, Texture = texH, Size = Vec2.One, Color = ColorF.White,
                            SortingLayer = layer, OrderInLayer = (short)(N - i), LayerMask = 1u, Visible = 1,
                        });
                    }

                    CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 2f, 10f), Vec3.Zero);
                    TransformSystem.PropagateHierarchy(ref world.Transforms);
                    CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);

                    var shaders = new RenderShaderDefaults { Pbr = ShaderHandle.Create(1, 1), Sprite = ShaderHandle.Create(2, 1) };
                    SceneLighting lit = SceneLighting.Default;
                    lit.ShadowsEnabled = 0;

                    // Warmup + Zero-GC check on 256KB stack thread
                    alloc.BeginFrame();
                    BatchRenderer.ExtractRenderPacket(in shaders, in world, in lit, ref alloc.Frame, ref alloc.Scratch, out RenderPacket pWarm);
                    Assert.Equal(N, pWarm.Instances3D.Count, "All 16,384 meshes sorted and extracted on 256KB stack");
                    Assert.Equal(N, pWarm.Sprites.Count, "All 16,384 sprites sorted and extracted on 256KB stack");
                    Assert.Equal(2, pWarm.Batches3D.Count, "Sorted opaque + blend meshes merge into exactly 2 batches");

                    long gcBefore = GC.GetAllocatedBytesForCurrentThread();
                    alloc.BeginFrame();
                    BatchRenderer.ExtractRenderPacket(in shaders, in world, in lit, ref alloc.Frame, ref alloc.Scratch, out RenderPacket p2);
                    long gcAfter = GC.GetAllocatedBytesForCurrentThread();
                    Assert.Equal(0L, gcAfter - gcBefore, "Worst-case 16,384 sort allocates 0 GC bytes");
                    Assert.Equal(2, p2.Batches3D.Count, "Second pass sorted cleanly");
                }
                finally
                {
                    bundleArena.Dispose();
                    alloc.Dispose();
                }
                Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after 256KB stack sort test");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        }, maxStackSize: 256 * 1024);

        t.Start();
        t.Join();
        if (threadEx != null)
            throw new Exception($"Anti-quicksort 256KB stack thread failed: {threadEx.Message}", threadEx);
    }

    private static void TestSpriteDepthTestNormalization()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 2 * 1024 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 64);
        var bundleArena = LinearArena.Create(512 * 1024);
        var bundleId = BundleId.Create(1, 1);

        try
        {
            TextureHandle texH = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("dt_tex"));
            AssetSystem.TryGetTexture(in world.Assets, texH)->GpuSlot = 0;
            AssetSystem.TryGetTexture(in world.Assets, texH)->GpuSlotGen = 1;

            EntityId e1 = EntitySystem.Spawn(ref world.Entities);
            EntityId e2 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e1, new Vec3(-1f, 0f, 0f));
            TransformSystem.Add(ref world.Transforms, e2, new Vec3(1f, 0f, 0f));

            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
            {
                Entity = e1, Texture = texH, Size = Vec2.One, Color = ColorF.White,
                SortingLayer = 0, OrderInLayer = 0, BlendMode = SpriteBlendMode.AlphaBlend, DepthTest = 1, LayerMask = 1u, Visible = 1,
            });
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
            {
                Entity = e2, Texture = texH, Size = Vec2.One, Color = ColorF.White,
                SortingLayer = 0, OrderInLayer = 0, BlendMode = SpriteBlendMode.AlphaBlend, DepthTest = 255, LayerMask = 1u, Visible = 1,
            });

            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 0f, 5f), Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);

            var shaders = new RenderShaderDefaults { Sprite = ShaderHandle.Create(2, 1) };
            alloc.BeginFrame();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket pkt);

            Assert.Equal(1, pkt.SpriteBatches.Count, "DepthTest=1 and DepthTest=255 merge into 1 SpriteBatchCmd");
            Assert.Equal(2u, pkt.SpriteBatches[0].InstanceCount, "Merged batch contains both sprites");
            Assert.Equal((byte)1, pkt.SpriteBatches[0].DepthTest, "Normalized DepthTest is 1");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after RENDER-13 test");
    }

    private static void TestExtractWarningLatchLogOnce()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string logDir = Path.Combine(AppContext.BaseDirectory, "test_render_logs_latch");
        if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);

        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 1 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, maxEntities: 2048, maxParticles: 64);
        world.RenderComponents = RenderComponentTables.Create(ref alloc.Persistent, maxMeshRenderers: 2048, maxSprites: 64, maxTexts: 64);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, directoryOverride: logDir, minLevel: LogLevel.Warn, hookExceptions: false);
        var bundleArena = LinearArena.Create(1 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);
        var tinyFrame = LinearArena.Create(32 * 1024);

        try
        {
            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
            GltfHandle boxGltf = GltfLoader.LoadFromMemory(boxGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("latch_box"));
            MeshHandle meshH = AssetSystem.TryGetGltf(in world.Assets, boxGltf)->Primitives[0].Mesh;
            AssetSystem.TryGetMesh(in world.Assets, meshH)->GpuSlot = 0;

            MaterialHandle matH = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData { OwnerBundle = bundleId });
            for (int i = 0; i < 1000; i++)
            {
                EntityId e = EntitySystem.Spawn(ref world.Entities);
                TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
                int mr = RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, e, meshH, matH, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
                world.RenderComponents.MeshRenderers[mr].CastShadow = 0;
            }
            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 2f, 10f), Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);

            var shaders = new RenderShaderDefaults { Pbr = ShaderHandle.Create(1, 1) };
            SceneLighting lit = SceneLighting.Default;
            lit.ShadowsEnabled = 0;

            BatchRenderer.ResetWarningLatchesForTest();
            long loggedBefore = Volatile.Read(ref log->TotalLogged.Value);
            for (int f = 0; f < 10; f++)
            {
                tinyFrame.Reset();
                BatchRenderer.ExtractRenderPacket(in shaders, in world, in lit, ref tinyFrame, ref alloc.Scratch, out _, log: log);
            }
            long loggedDelta = Volatile.Read(ref log->TotalLogged.Value) - loggedBefore;
            Assert.Equal(1L, loggedDelta, $"10 consecutive cap-hit frames must log [WARN] exactly once, got {loggedDelta}");
        }
        finally
        {
            LogSystem.Stop(log);
            tinyFrame.Dispose();
            bundleArena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after XMOD-07 warning latch test");
    }

    private static void TestStaleFrameJointPaletteGuard()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        try
        {
            var heroSpec = GlbBuilder.CreateHeroMechSpec(ColorF.Blue, ColorF.Cyan);
            byte[] heroGlb = GlbBuilder.BuildGlb(in heroSpec, includeDemoAnimation: true);
            GltfHandle heroGltf = GltfLoader.LoadFromMemory(heroGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("xmod10_hero"));
            GltfModelAssetData* model = AssetSystem.TryGetGltf(in world.Assets, heroGltf);
            for (int p = 0; p < model->PrimitiveCount; p++)
            {
                MeshAssetData* mp = AssetSystem.TryGetMesh(in world.Assets, model->Primitives[p].Mesh);
                if (mp != null) { mp->GpuSlot = p; mp->GpuSlotGen = 1; }
            }

            GltfLoader.Instantiate(ref world, ref alloc.Scratch, heroGltf, Vec3.Zero, Quat.Identity, Vec3.One);
            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 2f, 8f), Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280, 720);

            var shaders = new RenderShaderDefaults { Pbr = ShaderHandle.Create(1, 1) };

            // Frame 1: Step animations into alloc.Frame -> JointPalette must be fresh and copied
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref world.Animations, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket pFresh);
            Assert.True(pFresh.JointPalette.Length > 0, "Frame 1 with StepAll copies fresh JointPalette");

            // Frame 2: Reset alloc.Frame WITHOUT stepping AnimationSystem -> stale palette MUST NOT be copied!
            alloc.Frame.Reset();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket pStale);
            Assert.Equal(0, pStale.JointPalette.Length, "Frame 2 without StepAll returns empty JointPalette (no stale alias)");
            Assert.True(pStale.Instances3D.Count > 0, "Skinned meshes still extract cleanly as static fallback");
            for (int i = 0; i < pStale.Instances3D.Count; i++)
                Assert.True(pStale.Instances3D[i].Extra.Z < 0f, "Stale joint palette forces jointOffset = -1 static fallback");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after XMOD-10 stale joint palette test");
    }

    private static void TestHiDpiMouseScalingAndRayCorners()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 512 * 1024, 64 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 32, 32);

        try
        {
            CameraHandle camH = CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 3f, 8f), Vec3.Zero, 60f);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 1280f, 720f);
            CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, camH);

            int logicalW = 1280, logicalH = 720;
            (float lx, float ly)[] samplePoints =
            {
                (0f, 0f),
                (logicalW, 0f),
                (0f, logicalH),
                (logicalW, logicalH),
                (logicalW * 0.5f, logicalH * 0.5f),
                (320f, 180f),
            };

            (int pw, int ph, string label)[] scales =
            {
                (1280, 720, "1x"),
                (1920, 1080, "1.5x"),
                (2560, 1440, "2x Retina"),
            };

            foreach (var (lx, ly) in samplePoints)
            {
                Ray refRay = CameraSystem.ScreenToWorldRay(in *cam, lx, ly, logicalW, logicalH);
                foreach (var (pw, ph, label) in scales)
                {
                    PlatformSystem.ScaleLogicalToPixel(lx, ly, logicalW, logicalH, pw, ph, out float px, out float py);
                    Ray scaledRay = CameraSystem.ScreenToWorldRay(in *cam, px, py, pw, ph);
                    Assert.Near(refRay.Direction.X, scaledRay.Direction.X, 1e-4f, $"{label} ray dir.X matches at ({lx},{ly})");
                    Assert.Near(refRay.Direction.Y, scaledRay.Direction.Y, 1e-4f, $"{label} ray dir.Y matches at ({lx},{ly})");
                    Assert.Near(refRay.Direction.Z, scaledRay.Direction.Z, 1e-4f, $"{label} ray dir.Z matches at ({lx},{ly})");
                }
            }
        }
        finally
        {
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after RENDER-04 HiDPI mouse scaling test");
    }

    private static void TestGpuSystemValidationHeadless()
    {
        GpuContext dummyCtx = default;
        dummyCtx.Initialized = 1;
        dummyCtx.Device = (nint)1; // non-zero sentinel for validation checks (returns -1 / false before any SDL call)
        dummyCtx.MaxMeshes = 4;
        dummyCtx.MaxTextures = 4;
        GpuMeshSlot* mSlots = stackalloc GpuMeshSlot[4];
        GpuTextureSlot* tSlots = stackalloc GpuTextureSlot[4];
        dummyCtx.Meshes = mSlots;
        dummyCtx.Textures = tSlots;

        var nullMesh = new MeshAssetData { GpuSlot = -1, VertexCount = 3, IndexCount = 3, Vertices = null, Indices = null };
        Assert.Equal(-1, GpuSystem.EnsureMeshUploaded(ref dummyCtx, ref nullMesh), "Null vertex/index pointers rejected with -1");

        byte* tinyBuf = stackalloc byte[4];
        var underTex = new TextureAssetData
        {
            GpuSlot = -1, Width = 16, Height = 16, Format = TexturePixelFormat.Rgba8, Channels = 4,
            Pixels = tinyBuf, DataBytes = 4, // needs 16*16*4 = 1024
        };
        Assert.Equal(-1, GpuSystem.EnsureTextureUploaded(ref dummyCtx, ref underTex), "Undersized TextureAssetData.DataBytes rejected with -1");

        dummyCtx.CaptureColorTexture = (nint)1;
        dummyCtx.CaptureTransferBuffer = nint.Zero;
        dummyCtx.CaptureWidth = 4;
        dummyCtx.CaptureHeight = 4;
        byte[] dst = new byte[4 * 4 * 4];
        Assert.True(!GpuSystem.ReadCaptureTextureRgba(ref dummyCtx, dst), "ReadCaptureTextureRgba with CaptureTransferBuffer==0 returns false");
    }

    private static void TestGpuDeferredReleaseAndLifecycle()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 128, 64);
        var b1Arena = LinearArena.Create(2 * 1024 * 1024);
        var b2Arena = LinearArena.Create(2 * 1024 * 1024);
        var b1 = BundleId.Create(1, 1);
        var b2 = BundleId.Create(2, 1);

        var window = PlatformSystem.InitWindow("Deferred Release & Lifecycle Test", 320, 180, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);

        try
        {
            // RENDER-05: GpuSystem.Init binds non-null cleanup hooks automatically
            Assert.True(world.Assets.GpuContextPtr == gpu && world.Assets.ReleaseGpuMesh != null && world.Assets.ReleaseGpuTexture != null,
                "GpuSystem.Init automatically binds non-null AssetRegistry cleanup hooks");

            // RENDER-06: Live GPU validation checks
            var nullMesh = new MeshAssetData { GpuSlot = -1, VertexCount = 3, IndexCount = 3, Vertices = null, Indices = null };
            Assert.Equal(-1, GpuSystem.EnsureMeshUploaded(ref *gpu, ref nullMesh), "Live GPU rejects null mesh pointers");
            byte* smallBuf = stackalloc byte[8];
            var underTex = new TextureAssetData { GpuSlot = -1, Width = 8, Height = 8, Format = TexturePixelFormat.Rgba8, Channels = 4, Pixels = smallBuf, DataBytes = 8 };
            Assert.Equal(-1, GpuSystem.EnsureTextureUploaded(ref *gpu, ref underTex), "Live GPU rejects undersized tex.DataBytes");
            Assert.True(!GpuSystem.ReadCaptureTextureRgba(ref *gpu, new byte[64]), "ReadCaptureTextureRgba returns false before EnsureCaptureTarget");

            ShaderSystem.CompileBuiltinShaders(ref *gpu);

            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] boxGlb = GlbBuilder.BuildGlb(in boxSpec, includeDemoAnimation: false);
            GltfHandle gltf1 = GltfLoader.LoadFromMemory(boxGlb, null, ref b1Arena, ref alloc.Scratch, ref world.Assets, ref world.Animations, b1, StringId.Hash("def_box1"));
            MeshHandle mesh1 = AssetSystem.TryGetGltf(in world.Assets, gltf1)->Primitives[0].Mesh;
            TextureHandle tex1 = TextureLoader.CreateSolidColor(Color32.White, ref b1Arena, ref world.Assets, b1, StringId.Hash("def_tex1"));
            MaterialHandle mat1 = AssetSystem.RegisterMaterial(ref world.Assets, new MaterialAssetData
            {
                OwnerBundle = b1, BaseColorFactor = ColorF.White, BaseColorTexture = tex1, AlphaMode = MaterialAlphaMode.Opaque,
            });

            EntityId e1 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e1, Vec3.Zero);
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, e1, mesh1, mat1, default, ColorF.White, 1u, AnimPlayerHandle.Invalid);
            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 1.5f, 4f), Vec3.Zero);

            BatchRenderer.UploadPendingAssets(ref *gpu, ref world);
            int meshSlot = AssetSystem.TryGetMesh(in world.Assets, mesh1)->GpuSlot;
            uint meshGen = AssetSystem.TryGetMesh(in world.Assets, mesh1)->GpuSlotGen;
            int texSlot = AssetSystem.TryGetTexture(in world.Assets, tex1)->GpuSlot;
            uint texGen = AssetSystem.TryGetTexture(in world.Assets, tex1)->GpuSlotGen;
            Assert.True(meshSlot >= 0 && meshGen > 0u && texSlot >= 0 && texGen > 0u, "Mesh and texture uploaded with valid slot generations");

            // Extract RenderPacket holding (meshSlot, meshGen) and (texSlot, texGen)
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 320, 180);
            var shaders = GpuSystem.GetBuiltinShaders(in *gpu);
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket packet);

            // Unload bundle 1 WHILE packet is still pending submission!
            AssetSystem.UnloadByBundle(ref world.Assets, b1);
            Assert.Equal((byte)1, gpu->Meshes[meshSlot].Alive, "Mesh GPU slot stays intact until FlushDeferredReleases");
            Assert.Equal((byte)1, gpu->Textures[texSlot].Alive, "Texture GPU slot stays intact until FlushDeferredReleases");
            Assert.Equal((byte)1, gpu->MeshSlotPendingRelease[meshSlot], "Mesh slot marked pending release");
            Assert.Equal((byte)1, gpu->TextureSlotPendingRelease[texSlot], "Texture slot marked pending release");

            // Submitting the in-flight packet renders safely AND flushes deferred releases at the end!
            GpuSystem.EnsureCaptureTarget(ref *gpu, 320, 180);
            BatchRenderer.SubmitRenderPacket(ref *gpu, in packet, captureToOffscreen: true, measureGpuWithFence: true);
            Assert.True(gpu->LastFrameStats.DrawCalls >= 1, "In-flight RenderPacket rendered cleanly before deferred flush");
            Assert.Equal((byte)0, gpu->Meshes[meshSlot].Alive, "SubmitRenderPacket flushed deferred mesh release");
            Assert.Equal((byte)0, gpu->Textures[texSlot].Alive, "SubmitRenderPacket flushed deferred texture release");
            Assert.True(gpu->MeshSlotGens[meshSlot] != meshGen, "Mesh slot generation bumped after release");
            Assert.True(gpu->TextureSlotGens[texSlot] != texGen, "Texture slot generation bumped after release");

            // Re-submitting the stale packet MUST skip stale MeshGpuGen/TextureGpuGen safely (0 draw calls, 0 validation errors)
            BatchRenderer.SubmitRenderPacket(ref *gpu, in packet, captureToOffscreen: true, measureGpuWithFence: true);
            Assert.Equal(0, gpu->LastFrameStats.DrawCalls, "Stale MeshGpuGen/TextureGpuGen batches skipped safely");
            Assert.Equal(0, gpu->LastFrameStats.ShadowDrawCalls, "Stale shadow batches skipped safely");

            // Upload bundle 2 asset, then call 2-arg Dispose -> verifies hooks cleared + GpuSlot reset to -1 + safe post-zero UnloadByBundle
            TextureHandle tex2 = TextureLoader.CreateSolidColor(Color32.White, ref b2Arena, ref world.Assets, b2, StringId.Hash("def_tex2"));
            BatchRenderer.UploadPendingAssets(ref *gpu, ref world);
            Assert.True(AssetSystem.TryGetTexture(in world.Assets, tex2)->GpuSlot >= 0, "Bundle 2 texture uploaded");

            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            Assert.True(world.Assets.GpuContextPtr == null && world.Assets.ReleaseGpuMesh == null && world.Assets.ReleaseGpuTexture == null,
                "2-arg Dispose cleared AssetRegistry hooks");
            Assert.Equal(-1, AssetSystem.TryGetTexture(in world.Assets, tex2)->GpuSlot, "2-arg Dispose reset live asset GpuSlot to -1");
            Assert.Equal(0u, AssetSystem.TryGetTexture(in world.Assets, tex2)->GpuSlotGen, "2-arg Dispose reset live asset GpuSlotGen to 0");

            // Zeroing GpuContext memory and calling UnloadByBundle must not dereference dangling pointer or crash
            *gpu = default;
            AssetSystem.UnloadByBundle(ref world.Assets, b2);
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, b1);
            AssetSystem.UnloadByBundle(ref world.Assets, b2);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            b1Arena.Dispose();
            b2Arena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after XMOD-01 / RENDER-05 / RENDER-06 GPU lifecycle test");
    }

    private static void TestGpuSdfTextTranslucentCompositing()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        var window = PlatformSystem.InitWindow("SDF Translucent Compositing Test", 320, 180, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu);
            FontHandle fontH = SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("r01_font"), 64f, 512, 8);

            const int W = 320, H = 180;
            CameraHandle camH = CameraSystem.CreateOrthographicUI(ref world.Cameras, W, H);
            CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, camH);
            cam->ClearFlags = CameraClearFlags.SolidColor;
            cam->ClearColor = new ColorF(0f, 0f, 1f, 1f); // Solid pure blue background (0, 0, 255)

            // Part 1: Translucent white SDF text (alpha=0.5, OutlineWidth=0, OutlineColor=Black) over pure blue ->
            // interior pixels MUST blend to 0.5*White + 0.5*Blue = (128, 128, 255), NOT darkened to (~128, ~128, ~191)!
            EntityId te = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, te, new Vec3(W * 0.5f, H * 0.5f, 0f));
            var td = RenderComponentSystem.DefaultHudText(te, fontH, "MMM", default);
            td.FontScale = 2.0f;
            td.AlignH = TextAlignmentH.Center;
            td.AlignV = TextAlignmentV.Middle;
            td.Color = new ColorF(1f, 1f, 1f, 0.5f);
            td.SecondaryColor = new ColorF(1f, 1f, 1f, 0.5f);
            td.OutlineWidth = 0f;
            td.OutlineColor = new ColorF(0f, 0f, 0f, 1f); // Black outline with width 0 must NOT darken fill!
            td.ShadowColor = ColorF.Transparent;
            td.ShadowOffset = Vec2.Zero;
            TextHandle th = RenderComponentSystem.AddText(ref world.RenderComponents, in td);

            byte[] rgba = new byte[W * H * 4];
            GpuSystem.EnsureCaptureTarget(ref *gpu, W, H);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, W, H);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);
            Assert.True(GpuSystem.ReadCaptureTextureRgba(ref *gpu, rgba), "Read translucent SDF capture");

            int bestR = 0, bestG = 0, bestB = 0;
            for (int i = 0; i < W * H; i++)
            {
                int r = rgba[i * 4 + 0];
                int g = rgba[i * 4 + 1];
                int b = rgba[i * 4 + 2];
                if (r > bestR)
                {
                    bestR = r;
                    bestG = g;
                    bestB = b;
                }
            }
            bool isSrgb = gpu->SwapchainFormat == SDL.GPUTextureFormat.B8G8R8A8UnormSRGB ||
                          gpu->SwapchainFormat == SDL.GPUTextureFormat.R8G8B8A8UnormSRGB;
            int minRg = isSrgb ? 160 : 100;
            int maxRg = isSrgb ? 215 : 155;
            Assert.True(bestR >= minRg && bestR <= maxRg && bestG >= minRg && bestG <= maxRg && bestB >= 240,
                $"Translucent white (a=0.5) over blue must blend cleanly without black outline darkening (srgb={isSrgb}), got ({bestR},{bestG},{bestB})");

            // Part 2: OutlineWidth=0.25 with OutlineColor=(1,0,0,0) over solid green (0,128,0) -> zero red fringe!
            cam->ClearColor = new ColorF(0f, 0.5f, 0f, 1f);
            TextData* tdPtr = RenderComponentSystem.TryGetText(in world.RenderComponents, th);
            tdPtr->Color = ColorF.White;
            tdPtr->SecondaryColor = ColorF.White;
            tdPtr->OutlineWidth = 0.25f;
            tdPtr->OutlineColor = new ColorF(1f, 0f, 0f, 0f); // Transparent red

            alloc.BeginFrame();
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);
            Assert.True(GpuSystem.ReadCaptureTextureRgba(ref *gpu, rgba), "Read zero-alpha outline capture");

            int maxRedExcess = 0;
            for (int i = 0; i < W * H; i++)
            {
                int r = rgba[i * 4 + 0];
                int b = rgba[i * 4 + 2];
                // Fill is white (r == b) and background is green (r == 0, b == 0), so r - b detects any red outline fringe!
                int excess = r - b;
                if (excess > maxRedExcess) maxRedExcess = excess;
            }
            Assert.True(maxRedExcess <= 8, $"OutlineColor.a == 0 must produce zero red fringe (max R-B excess={maxRedExcess})");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after RENDER-01 SDF translucent test");
    }

    private static void TestPlatformShutdownPreservesAudio()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 512 * 1024, 128 * 1024);

        AudioState* audio = null;
        PlatformWindow window = default;
        try
        {
            AudioConfig cfg = AudioConfig.Default;
            cfg.HeadlessFallback = 1;
            audio = AudioSystem.Start(ref alloc.Persistent, in cfg);
            Assert.Equal((byte)1, audio->Active, "AudioSystem started");

            DecodedPcmData sine = WavDecoder.GenerateSineWave(440f, 0.25f, cfg.SampleRate, 0.5f, 1);
            AudioClipHandle clip = AudioSystem.CreateClipFromDecoded(audio, ref sine, StringId.Hash("r03_sine"));
            Assert.True(clip.IsValid, "Sine clip created");
            AudioVoiceHandle voice = AudioSystem.Play(audio, clip, AudioPlayParams.Looping(AudioBus.Sfx, 1f));
            Assert.True(voice.IsValid, "Looping voice started");

            // Init and shutdown hidden PlatformWindow BEFORE stopping AudioSystem
            window = PlatformSystem.InitWindow("RENDER-03 Audio Isolation Test", 160, 120, hidden: true);
            PlatformSystem.Shutdown(ref window);

            // AudioSystem must remain active and continue mixing cleanly after PlatformSystem.Shutdown!
            Assert.Equal((byte)1, Volatile.Read(ref audio->Active), "AudioSystem remains active after PlatformSystem.Shutdown");
            float* mixBuf = stackalloc float[256 * 2];
            AudioSystem.RenderMixFrames(audio, mixBuf, 256);
            float maxAbs = 0f;
            for (int i = 0; i < 256 * 2; i++)
            {
                float a = DetMath.Abs(mixBuf[i]);
                if (a > maxAbs) maxAbs = a;
            }
            Assert.True(maxAbs > 0.05f, $"AudioSystem continues mixing cleanly after PlatformSystem.Shutdown (peak={maxAbs:F3})");
        }
        finally
        {
            if (window.Initialized != 0) PlatformSystem.Shutdown(ref window);
            if (audio != null) AudioSystem.Stop(audio);
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after RENDER-03 audio isolation test");
    }

    private static void TestBuiltinShaderProtectionAndViewportScissor()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 64);
        var bundleArena = LinearArena.Create(1 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        const int W = 320, H = 180;
        var window = PlatformSystem.InitWindow("Builtins & Scissor Test", W, H, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu);

            // RENDER-07: Refusing to release all 4 built-in shaders
            Assert.True(!ShaderSystem.ReleaseShader(ref *gpu, gpu->BuiltinPbrShader), "ReleaseShader refuses BuiltinPbrShader");
            Assert.True(!ShaderSystem.ReleaseShader(ref *gpu, gpu->BuiltinSpriteShader), "ReleaseShader refuses BuiltinSpriteShader");
            Assert.True(!ShaderSystem.ReleaseShader(ref *gpu, gpu->BuiltinSdfTextShader), "ReleaseShader refuses BuiltinSdfTextShader");
            Assert.True(!ShaderSystem.ReleaseShader(ref *gpu, gpu->BuiltinParticleShader), "ReleaseShader refuses BuiltinParticleShader");
            Assert.True(ShaderSystem.TryGetShader(in *gpu, gpu->BuiltinSpriteShader) != null, "BuiltinSpriteShader remains alive");

            // RENDER-10: Camera 0 with ViewportRect = Vec4.Zero (clears full screen black, skips invalid viewport draw)
            // + Camera 1 with sub-viewport (0.5, 0, 0.5, 1) drawing a huge red sprite spanning beyond viewport bounds ->
            // scissor MUST clip red strictly to right half [W/2 .. W)!
            CameraHandle cam0 = CameraSystem.CreateOrthographicUI(ref world.Cameras, W, H, depthOrder: 0, layerMask: 1u << 30);
            CameraData* c0 = CameraSystem.TryGetPtr(in world.Cameras, cam0);
            c0->ViewportRect = Vec4.Zero;
            c0->ClearFlags = CameraClearFlags.SolidColor;
            c0->ClearColor = new ColorF(0f, 0f, 0f, 1f);

            CameraHandle cam1 = CameraSystem.CreateOrthographicUI(ref world.Cameras, W, H, depthOrder: 1, layerMask: 1u << 31);
            CameraData* c1 = CameraSystem.TryGetPtr(in world.Cameras, cam1);
            c1->ViewportRect = new Vec4(0.5f, 0f, 0.5f, 1f);
            c1->ClearFlags = CameraClearFlags.None;

            TextureHandle whiteH = TextureLoader.CreateSolidColor(Color32.White, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("r10_white"));
            EntityId se = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, se, new Vec3(W * 0.5f, H * 0.5f, 0f));
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
            {
                Entity = se, Texture = whiteH, Size = new Vec2(W * 2f, H * 2f), Pivot = new Vec2(0.5f, 0.5f),
                Color = new ColorF(1f, 0f, 0f, 1f), BlendMode = SpriteBlendMode.AlphaBlend,
                DepthTest = 0, LayerMask = 1u << 31, Visible = 1,
            });

            byte[] rgba = new byte[W * H * 4];
            GpuSystem.EnsureCaptureTarget(ref *gpu, W, H);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, W, H);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);
            Assert.True(GpuSystem.ReadCaptureTextureRgba(ref *gpu, rgba), "Read scissor-clipped capture");

            int leftOff = ((H / 2) * W + (W / 4)) * 4;
            int rightOff = ((H / 2) * W + (3 * W / 4)) * 4;
            Assert.True(rgba[leftOff + 0] < 15 && rgba[leftOff + 1] < 15 && rgba[leftOff + 2] < 15,
                $"Left half outside sub-viewport must remain black via scissor, got ({rgba[leftOff]},{rgba[leftOff + 1]},{rgba[leftOff + 2]})");
            Assert.True(rgba[rightOff + 0] > 200 && rgba[rightOff + 1] < 15 && rgba[rightOff + 2] < 15,
                $"Right half inside sub-viewport must be bright red, got ({rgba[rightOff]},{rgba[rightOff + 1]},{rgba[rightOff + 2]})");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after RENDER-07 / RENDER-10 GPU test");
    }
}
