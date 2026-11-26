using System;
using System.IO;
using System.Text;
using BringEngine.Assets;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Rendering.Shaders;
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
        Assert.Run("BatchRenderer_CapacityCap_CastShadow0_SortingOrder_ShaderGen_DoubleSided", &TestBatchRendererRulesAndCapacityCap);
        Assert.Run("BatchRenderer_ShadowTexelSnapping_And_SmallShadowDistance", &TestShadowSnappingAndDistance);
        Assert.Run("ShaderCross_SpirvAndMslTranspileSmoke_AllBuiltinShaders", &TestSpirvAndMslSmoke);
        Assert.Run("InputState_FocusLostClearsHeldKeysAndMouseAndFiresRelease", &TestFocusLostInputRelease);
        if (!headlessOnly)
        {
            Assert.Run("GpuBatchRenderer_Sdl3GpuInstancingSpritesSdfTextParticlesAndPngCapture", &TestGpuRenderEndToEnd);
            Assert.Run("GpuDirectionalShadows_OccluderDarkensFloorAndCustom1SamplerShaderCompatible", &TestGpuDirectionalShadowsAndCustomShader);
            Assert.Run("GpuResources_Non4x4Bc1Bc3Fallback_BundleUnloadReuse_And_DisposeOrder", &TestGpuNon4x4AndBundleLifecycle);
            Assert.Run("ShaderSystem_ErrorPaths_ReleaseShader_CustomQuadAnd2SamplerShaders", &TestShaderSystemErrorsAndCustomQuadShaders);
            Assert.Run("GpuBuiltinShaders_MaskShadow_DoubleSided_SdfNoBleed_AdditiveNoDepth_BufferGrow", &TestBuiltinShaderFeaturesAndDynamicGrow);
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
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: false);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

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
            GpuSystem.Dispose(ref *gpu);
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
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

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
            GpuSystem.Dispose(ref *gpu);
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

            // 6.1: Capacity cap overflow (> 16,384 meshes and sprites) must terminate cleanly without hanging!
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

            alloc.BeginFrame();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket packetCap, log: log);
            Assert.Equal(16384, packetCap.Instances3D.Count, "3D instances capped at 16,384 without hanging");
            Assert.Equal(16384, packetCap.Sprites.Count, "Sprites capped at 16,384 without hanging");

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
        input.BeginFrame(); // W and mouse 0 now in Prev & Down

        Assert.True(input.IsKeyDown(KeyCode.W), "W is initially held");
        Assert.True(input.IsMouseDown(0), "Mouse 0 is initially held");

        // Simulate WindowFocusLost clear (matching PlatformSystem.PollEvents)
        input.SetWindowFocused(false);
        for (int k = 0; k < 8; k++) input.KeysDown[k] = 0;
        input.MouseButtonsDown = 0;

        Assert.True(!input.IsWindowFocused && input.WasWindowFocusLost, "Window focus lost recorded");
        Assert.True(!input.IsKeyDown(KeyCode.W) && input.IsKeyReleased(KeyCode.W), "Held key released cleanly on focus lost");
        Assert.True(!input.IsMouseDown(0) && input.IsMouseReleased(0), "Held mouse button released cleanly on focus lost");
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
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true, log: log);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

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

            // Unload bundle 1 -> GPU texture slots released immediately
            AssetSystem.UnloadByBundle(ref world.Assets, b1);
            Assert.Equal((byte)0, gpu->Textures[slot1].Alive, "Bundle unload released GPU texture slot 1");
            Assert.Equal((byte)0, gpu->Textures[slot2].Alive, "Bundle unload released GPU texture slot 2");

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
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true, maxShaders: 16, log: log);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

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
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true, log: log);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

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
}
