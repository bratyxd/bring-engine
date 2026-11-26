using System;
using System.IO;
using BringEngine.Assets;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class RenderTests
{
    public static void RunAll(bool headlessOnly = false)
    {
        Console.WriteLine("=== Phases 6-7: SDL3_gpu Renderer, Instancing, 3D Sprites, SDF Text & Shaders ===");
        Assert.Run("TextLayoutSystem_WordWrapShadowTypewriterAndAnimZeroGc", &TestTextLayoutHeadless);
        if (!headlessOnly)
        {
            Assert.Run("GpuBatchRenderer_Sdl3GpuInstancingSpritesSdfTextParticlesAndPngCapture", &TestGpuRenderEndToEnd);
            Assert.Run("GpuDirectionalShadows_OccluderDarkensFloorAndCustom1SamplerShaderCompatible", &TestGpuDirectionalShadowsAndCustomShader);
        }
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
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, floorEnt, cubeMesh, floorMat, default, ColorF.White, 1u, -1);

            // Elevated occluder cube above center (0, 4.5, 0), scale (2.8, 0.6, 2.8): casts a clear shadow onto (0, 0, 0)
            // Placed above the camera frustum so off-camera shadow casting is verified too!
            EntityId occEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, occEnt, new Vec3(0f, 4.5f, 0f), Quat.Identity, new Vec3(2.8f, 0.6f, 2.8f));
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, occEnt, cubeMesh, floorMat, default, ColorF.White, 1u, -1);

            // Custom 1-sampler shader cube off to the side at (5, 0.5, 0)
            EntityId customEnt = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, customEnt, new Vec3(5f, 0.5f, 0f), Quat.Identity, Vec3.One);
            RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, customEnt, cubeMesh, customMat, default, ColorF.White, 1u, -1);

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
}
