using System;
using System.IO;
using BringEngine.Assets;
using BringEngine.Assets.Bundles;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Scenes;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class AssetBundleSceneGltfTests
{
    private static int s_sceneALoads;
    private static int s_sceneAUnloads;
    private static int s_sceneBLoads;
    private static int s_commonBundleLoads;
    private static int s_levelABundleLoads;
    private static int s_levelBBundleLoads;

    public static void RunAll()
    {
        Console.WriteLine("=== Phase 5: Bundles, Unity-Like Scenes, glTF 2.0/GLB & SDF Fonts ===");
        Assert.Run("GlbBuilderAndGltfLoader_RoundTripSkinnedAndAnimated", &TestGltfRoundTrip);
        Assert.Run("SdfFontLoader_BakesValidAtlasAndKerning", &TestSdfFont);
        Assert.Run("BundleAndSceneManager_SingleAdditiveRefCountAndMemoryReclaim", &TestBundlesAndScenes);
    }

    private static void TestGltfRoundTrip()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 1024, 256);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        // Generate real binary .glb with 2-joint skin + IdleBob animation
        var heroSpec = GlbBuilder.CreateHeroMechSpec(ColorF.Blue, ColorF.Cyan);
        byte[] glbBytes = GlbBuilder.BuildGlb(in heroSpec, includeDemoAnimation: true);
        Assert.True(glbBytes.Length > 512, "Valid GLB generated");

        // Parse with zero-reflection GltfLoader
        GltfHandle handle = GltfLoader.LoadFromMemory(
            glbBytes, null, ref bundleArena, ref alloc.Scratch,
            ref world.Assets, ref world.Animations, bundleId, StringId.Hash("hero_glb"));

        var model = AssetSystem.TryGetGltf(in world.Assets, handle);
        Assert.True(model != null, "glTF model loaded");
        Assert.Equal(3, model->NodeCount, "3 nodes (MeshRoot + JointRoot + JointTop)");
        Assert.Equal(1, model->PrimitiveCount, "1 primitive");
        Assert.Equal(1, model->SkinCount, "1 skin");
        Assert.Equal(1, model->AnimationCount, "1 animation clip");

        var mesh = AssetSystem.TryGetMesh(in world.Assets, model->Primitives[0].Mesh);
        Assert.True(mesh != null && mesh->VertexCount == 72 && mesh->IndexCount == 108, "Mesh vertices/indices match 3 boxes");
        Assert.Equal((byte)1, mesh->HasSkinning, "Mesh flagged as skinned");

        // Instantiate into EngineWorld and step animation
        var sceneId = SceneId.Create(1, 1);
        EntityId root = GltfLoader.Instantiate(ref world, ref alloc.Scratch, handle, new Vec3(2, 0, -3), Quat.Identity, Vec3.One, sceneId);
        Assert.True(EntitySystem.IsAlive(in world.Entities, root), "Instantiated root alive");
        Assert.Equal(1, world.RenderComponents.MeshRendererCount, "1 mesh renderer attached");
        Assert.Equal(1, world.Animations.PlayerCount, "1 skeletal anim player attached");

        alloc.BeginFrame();
        EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 0.5f);
        Assert.Equal(2, world.Animations.FrameJointPalette.Length, "2 skinned joint matrices computed");
        // At t=0.5s, JointRoot moves up to Y=0.22
        Assert.Near(0.22f, world.Animations.FrameJointPalette[0].C3.Y, 0.01f, "Animated joint Y=0.22 at t=0.5s");

        // Cleanup
        EngineWorldSystem.DestroySceneObjects(ref world, sceneId, ref alloc.Scratch);
        Assert.Equal(0, world.Entities.AliveCount, "Scene cleanup destroyed all instantiated glTF entities");
        Assert.Equal(0, world.RenderComponents.MeshRendererCount, "Scene cleanup removed mesh renderers");

        bundleArena.Dispose();
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native memory leaks");
    }

    private static void TestSdfFont()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(2, 1);

        FontHandle fh = SdfFontLoader.BakeSdfFromSystemOrFile(
            null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("ui_font"), 48f, 512, 6);

        FontAssetData* font = AssetSystem.TryGetFont(in world.Assets, fh);
        Assert.True(font != null, "Font registered");
        Assert.True(font->LineHeight > 20f, "Valid line height");
        var atlas = AssetSystem.TryGetTexture(in world.Assets, font->AtlasTexture);
        Assert.True(atlas != null && atlas->Width == 512 && atlas->Channels == 1, "512x512 R8 SDF atlas created");

        // Verify 'A' glyph has bitmap & positive advance
        ref GlyphMetrics gA = ref font->AsciiGlyphs['A' - SdfFontLoader.FirstAscii];
        Assert.Equal((byte)1, gA.HasBitmap, "Glyph 'A' has SDF bitmap");
        Assert.True(gA.Advance > 10f && gA.Width > 5f && gA.Height > 5f, "Glyph 'A' metrics valid");

        // Verify atlas contains non-zero SDF pixels
        int nonZero = 0;
        for (int i = 0; i < atlas->Width * atlas->Height; i++)
            if (atlas->Pixels[i] > 0) nonZero++;
        Assert.True(nonZero > 5000, "SDF atlas populated with distance field data");

        bundleArena.Dispose();
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks");
    }

    private static void LoadCommonBundle(ref BundleLoadContext ctx)
    {
        s_commonBundleLoads++;
        TextureLoader.CreateSolidColor(Color32.White, ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle, StringId.Hash("white"));
    }

    private static void LoadLevelABundle(ref BundleLoadContext ctx)
    {
        s_levelABundleLoads++;
        var box = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.Red);
        byte[] glb = GlbBuilder.BuildGlb(in box);
        GltfLoader.LoadFromMemory(glb, null, ref *ctx.BundleArena, ref *ctx.ScratchArena, ref ctx.World->Assets, ref ctx.World->Animations, ctx.Bundle, StringId.Hash("box_a"));
    }

    private static void LoadLevelBBundle(ref BundleLoadContext ctx)
    {
        s_levelBBundleLoads++;
        var crystal = GlbBuilder.CreateCrystalSpec(0.5f, 1.5f, ColorF.Cyan, new Vec3(0, 0.3f, 0.5f));
        byte[] glb = GlbBuilder.BuildGlb(in crystal);
        GltfLoader.LoadFromMemory(glb, null, ref *ctx.BundleArena, ref *ctx.ScratchArena, ref ctx.World->Assets, ref ctx.World->Animations, ctx.Bundle, StringId.Hash("crystal_b"));
    }

    private static void OnLoadSceneA(ref SceneContext ctx)
    {
        s_sceneALoads++;
        var gltf = AssetSystem.FindGltf(in ctx.World->Assets, StringId.Hash("box_a"));
        GltfLoader.Instantiate(ref *ctx.World, ref ctx.Allocators->Scratch, gltf, Vec3.Zero, Quat.Identity, Vec3.One, ctx.Scene);
    }

    private static void OnUnloadSceneA(ref SceneContext ctx) => s_sceneAUnloads++;

    private static void OnLoadSceneB(ref SceneContext ctx)
    {
        s_sceneBLoads++;
        var gltf = AssetSystem.FindGltf(in ctx.World->Assets, StringId.Hash("crystal_b"));
        GltfLoader.Instantiate(ref *ctx.World, ref ctx.Allocators->Scratch, gltf, new Vec3(3, 0, 0), Quat.Identity, Vec3.One, ctx.Scene);
    }

    private static void OnLoadHud(ref SceneContext ctx)
    {
        EntitySystem.Spawn(ref ctx.World->Entities, ctx.Scene);
    }

    private static void TestBundlesAndScenes()
    {
        NativeMemoryTracker.ResetStatsForTest();
        s_sceneALoads = s_sceneAUnloads = s_sceneBLoads = 0;
        s_commonBundleLoads = s_levelABundleLoads = s_levelBBundleLoads = 0;

        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 1024, 256);
        var bundles = BundleManager.Create(ref alloc.Persistent, 16);
        var scenes = SceneManager.Create(ref alloc.Persistent, 16, 8);

        var bCommon = BundleSystem.Register(ref bundles, "common", 256 * 1024, &LoadCommonBundle);
        var bA = BundleSystem.Register(ref bundles, "level_a", 512 * 1024, &LoadLevelABundle);
        var bB = BundleSystem.Register(ref bundles, "level_b", 512 * 1024, &LoadLevelBBundle);

        Span<BundleId> depsA = stackalloc BundleId[] { bCommon, bA };
        Span<BundleId> depsB = stackalloc BundleId[] { bCommon, bB };
        Span<BundleId> depsHud = stackalloc BundleId[] { bCommon };

        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "SceneA", depsA, 128 * 1024, &OnLoadSceneA, &OnUnloadSceneA);
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "SceneB", depsB, 128 * 1024, &OnLoadSceneB);
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "HUD", depsHud, 64 * 1024, &OnLoadHud);

        // 1. Load SceneA (Single)
        SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("SceneA"), SceneLoadMode.Single);
        Assert.Equal(1, s_sceneALoads, "SceneA loaded");
        Assert.Equal(1, s_commonBundleLoads, "common loaded once");
        Assert.Equal(1, s_levelABundleLoads, "level_a loaded once");
        Assert.True(BundleSystem.IsLoaded(in bundles, bA), "bA loaded");
        Assert.True(!BundleSystem.IsLoaded(in bundles, bB), "bB not loaded");

        // 2. Load HUD Additive -> shares common bundle (RefCount == 2)
        SceneId hudId = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("HUD"), SceneLoadMode.Additive);
        Assert.Equal(2, scenes.ActiveCount, "2 active scenes (SceneA + HUD)");
        Assert.Equal(1, s_commonBundleLoads, "common NOT reloaded");
        Assert.Equal(2, BundleSystem.GetRefCount(in bundles, bCommon), "common RefCount == 2");

        // 3. Switch to SceneB (Single) -> unloads SceneA + HUD + level_a, keeps common alive without reloading, loads level_b!
        SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("SceneB"), SceneLoadMode.Single);
        Assert.Equal(1, s_sceneAUnloads, "SceneA unloaded");
        Assert.Equal(1, s_sceneBLoads, "SceneB loaded");
        Assert.Equal(1, s_commonBundleLoads, "common stayed resident across Single transition!");
        Assert.True(!BundleSystem.IsLoaded(in bundles, bA), "level_a unloaded and RAM reclaimed!");
        Assert.True(BundleSystem.IsLoaded(in bundles, bB), "level_b loaded");
        Assert.True(!AssetSystem.FindGltf(in world.Assets, StringId.Hash("box_a")).IsValid, "box_a unregistered");
        Assert.True(AssetSystem.FindGltf(in world.Assets, StringId.Hash("crystal_b")).IsValid, "crystal_b registered");

        // 4. Unload everything -> 0 bundles, 0 entities, 0 native bytes leaked
        SceneSystem.UnloadAllScenes(ref scenes, ref world, ref alloc, ref bundles);
        Assert.Equal(0, bundles.LoadedBundleCount, "0 loaded bundles");
        Assert.Equal(0, world.Entities.AliveCount, "0 alive entities");
        Assert.Equal(0, world.Assets.LiveMeshCount, "0 live meshes");
        Assert.Equal(0, world.Assets.LiveTextureCount, "0 live textures");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after full scene/bundle teardown");
    }
}
