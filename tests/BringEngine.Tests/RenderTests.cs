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
    public static void RunAll()
    {
        Console.WriteLine("=== Phases 6-7: SDL3_gpu Renderer, Instancing, 3D Sprites, SDF Text & Shaders ===");
        Assert.Run("TextLayoutSystem_WordWrapShadowTypewriterAndAnimZeroGc", &TestTextLayoutHeadless);
        Assert.Run("GpuBatchRenderer_Sdl3GpuInstancingSpritesSdfTextParticlesAndPngCapture", &TestGpuRenderEndToEnd);
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
}
