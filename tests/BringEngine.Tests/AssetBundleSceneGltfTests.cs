using System;
using System.IO;
using System.Text;
using BringEngine.Assets;
using BringEngine.Assets.Bundles;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Scenes;
using BringEngine.Simulation.World;
using StbImageWriteSharp;

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
        Assert.Run("GlbBuilder_CrystalSpec_OutwardNormalsAndCcwWinding", &TestCrystalSpecOutwardNormals);
        Assert.Run("GltfLoader_ExternalUriBase64MatrixAlphaCubicSplineAndOutOfOrderJoints", &TestGltfAdvancedFeatures);
        Assert.Run("GltfLoader_CorruptInputsAndInstantiateErrorCleanup", &TestGltfCorruptAndInstantiateCleanup);
        Assert.Run("SdfFontLoader_BakesValidAtlasAndKerning", &TestSdfFont);
        Assert.Run("BundleAndSceneManager_SingleAdditiveRefCountAndMemoryReclaim", &TestBundlesAndScenes);
        Assert.Run("BundleAndScene_RollbackMissingManifestNameCollisionAndAdditiveGltfSurvival", &TestBundleAndSceneRobustness);
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

    private static void TestCrystalSpecOutwardNormals()
    {
        var spec = GlbBuilder.CreateCrystalSpec(0.5f, 1.5f, ColorF.Cyan, Vec3.Zero, skinned: true);
        Vec3 center = new Vec3(0f, 0.75f, 0f);
        Assert.Equal(24, spec.Positions.Length, "Crystal has 24 vertices");
        for (int i = 0; i < spec.Positions.Length; i++)
        {
            float d = Vec3.Dot(spec.Normals[i], spec.Positions[i] - center);
            Assert.True(d > 0f, $"Crystal vertex {i} normal points outward (dot={d:F4})");
        }
        for (int t = 0; t < spec.Indices.Length; t += 3)
        {
            Vec3 p0 = spec.Positions[spec.Indices[t]];
            Vec3 p1 = spec.Positions[spec.Indices[t + 1]];
            Vec3 p2 = spec.Positions[spec.Indices[t + 2]];
            Vec3 faceN = Vec3.Cross(p1 - p0, p2 - p0);
            Assert.True(Vec3.Dot(faceN, spec.Normals[spec.Indices[t]]) > 0f, $"Crystal triangle {t / 3} has CCW outward winding");
        }
    }

    private static void TestGltfAdvancedFeatures()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tmpDir = Path.Combine(Path.GetTempPath(), $"bring_gltf_adv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 1024, 256);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(3, 1);

        try
        {
            // 1. .gltf + external .bin + external PNG + Matrix + AlphaMode Mask/Blend + DoubleSided + CubicSpline + Out-of-order skin.joints
            byte[] png32 = MakeTestPngBytes(32, 32);
            string extPngPath = Path.Combine(tmpDir, "albedo.png");
            File.WriteAllBytes(extPngPath, png32);

            using var binMs = new MemoryStream();
            using var bw = new BinaryWriter(binMs);
            // 3 vertices (POS, UV), 3 indices (USHORT), 4 joints, 4 weights, 2 IBMs, 2 times, 6 CubicSpline vec3, 2 rot vec4
            int offPos = (int)binMs.Position;
            float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            foreach (float f in pos) bw.Write(f);
            int lenPos = pos.Length * 4;

            int offUv = (int)binMs.Position;
            float[] uv = { 0, 0, 1, 0, 0, 1 };
            foreach (float f in uv) bw.Write(f);
            int lenUv = uv.Length * 4;

            int offIdx = (int)binMs.Position;
            ushort[] idx = { 0, 1, 2 };
            foreach (ushort u in idx) bw.Write(u);
            while ((binMs.Position & 3) != 0) bw.Write((byte)0);
            int lenIdx = idx.Length * 2;

            int offJ = (int)binMs.Position;
            // All 3 vertices bind to raw glTF joint index 0 (which is ChildJoint in out-of-order joints:[2,1]!)
            byte[] jnts = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            bw.Write(jnts);
            int lenJ = jnts.Length;

            int offW = (int)binMs.Position;
            float[] wts = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            foreach (float f in wts) bw.Write(f);
            int lenW = wts.Length * 4;

            int offIbm = (int)binMs.Position;
            for (int m = 0; m < 2; m++)
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        bw.Write(r == c ? 1f : 0f);
            int lenIbm = 32 * 4;

            int offTime = (int)binMs.Position;
            bw.Write(0f); bw.Write(1f);
            int lenTime = 8;

            // CubicSpline output: Key0(in=(0,0,0), val=(0,0,0), out=(0,3,0)), Key1(in=(0,-3,0), val=(0,2,0), out=(0,0,0))
            int offCubic = (int)binMs.Position;
            float[] cubic = { 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, -3, 0, 0, 2, 0, 0, 0, 0 };
            foreach (float f in cubic) bw.Write(f);
            int lenCubic = cubic.Length * 4;

            int offRot = (int)binMs.Position;
            float[] rot = { 0, 0, 0, 1, 0, 0, 0, 1 };
            foreach (float f in rot) bw.Write(f);
            int lenRot = rot.Length * 4;

            byte[] binBytes = binMs.ToArray();
            File.WriteAllBytes(Path.Combine(tmpDir, "mesh.bin"), binBytes);

            // Node 0: Matrix with translation=(4,5,6), rotation=90deg Y, scale=(-2, 3, 1.5)
            Mat4 node0Mat = Mat4.CreateTRS(new Vec3(4f, 5f, 6f), Quat.FromAxisAngle(Vec3.UnitY, DetMath.Pi * 0.5f), new Vec3(-2f, 3f, 1.5f));
            string matJson = $"[{Fmt(node0Mat.C0.X)},{Fmt(node0Mat.C0.Y)},{Fmt(node0Mat.C0.Z)},{Fmt(node0Mat.C0.W)},"
                           + $"{Fmt(node0Mat.C1.X)},{Fmt(node0Mat.C1.Y)},{Fmt(node0Mat.C1.Z)},{Fmt(node0Mat.C1.W)},"
                           + $"{Fmt(node0Mat.C2.X)},{Fmt(node0Mat.C2.Y)},{Fmt(node0Mat.C2.Z)},{Fmt(node0Mat.C2.W)},"
                           + $"{Fmt(node0Mat.C3.X)},{Fmt(node0Mat.C3.Y)},{Fmt(node0Mat.C3.Z)},{Fmt(node0Mat.C3.W)}]";

            string gltfJson = "{"
                + "\"asset\":{\"version\":\"2.0\"},"
                + "\"buffers\":[{\"uri\":\"mesh.bin\",\"byteLength\":" + binBytes.Length + "}],"
                + "\"bufferViews\":["
                + $"{{\"buffer\":0,\"byteOffset\":{offPos},\"byteLength\":{lenPos}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offUv},\"byteLength\":{lenUv}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offIdx},\"byteLength\":{lenIdx}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offJ},\"byteLength\":{lenJ}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offW},\"byteLength\":{lenW}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offIbm},\"byteLength\":{lenIbm}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offTime},\"byteLength\":{lenTime}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offCubic},\"byteLength\":{lenCubic}}},"
                + $"{{\"buffer\":0,\"byteOffset\":{offRot},\"byteLength\":{lenRot}}}"
                + "],"
                + "\"accessors\":["
                + "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},"
                + "{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"},"
                + "{\"bufferView\":2,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"},"
                + "{\"bufferView\":3,\"componentType\":5121,\"count\":3,\"type\":\"VEC4\"},"
                + "{\"bufferView\":4,\"componentType\":5126,\"count\":3,\"type\":\"VEC4\"},"
                + "{\"bufferView\":5,\"componentType\":5126,\"count\":2,\"type\":\"MAT4\"},"
                + "{\"bufferView\":6,\"componentType\":5126,\"count\":2,\"type\":\"SCALAR\"},"
                + "{\"bufferView\":7,\"componentType\":5126,\"count\":6,\"type\":\"VEC3\"},"
                + "{\"bufferView\":8,\"componentType\":5126,\"count\":2,\"type\":\"VEC4\"}"
                + "],"
                + "\"images\":[{\"uri\":\"albedo.png\"}],"
                + "\"textures\":[{\"source\":0}],"
                + "\"materials\":["
                + "{\"name\":\"MaskMat\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.35,\"doubleSided\":true,\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}},"
                + "{\"name\":\"BlendMat\",\"alphaMode\":\"BLEND\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,1,1,0.5]}}"
                + "],"
                + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1,\"JOINTS_0\":3,\"WEIGHTS_0\":4},\"indices\":2,\"material\":0}]}],"
                + "\"nodes\":["
                + $"{{\"name\":\"MeshNode\",\"mesh\":0,\"skin\":0,\"matrix\":{matJson},\"children\":[1]}},"
                + "{\"name\":\"ParentJoint\",\"translation\":[0,0,0],\"children\":[2]},"
                + "{\"name\":\"ChildJoint\",\"translation\":[1,0,0]}"
                + "],"
                + "\"skins\":[{\"inverseBindMatrices\":5,\"joints\":[2,1]}],"
                + "\"animations\":[{\"name\":\"SplineAnim\","
                + "\"samplers\":[{\"input\":6,\"output\":7,\"interpolation\":\"CUBICSPLINE\"},{\"input\":6,\"output\":8,\"interpolation\":\"STEP\"}],"
                + "\"channels\":[{\"sampler\":0,\"target\":{\"node\":1,\"path\":\"translation\"}},{\"sampler\":1,\"target\":{\"node\":0,\"path\":\"rotation\"}}]"
                + "}]"
                + "}";

            string gltfPath = Path.Combine(tmpDir, "model.gltf");
            File.WriteAllText(gltfPath, gltfJson, new UTF8Encoding(false));

            GltfHandle gh = GltfLoader.LoadFromFile(gltfPath, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("ext_gltf"));
            var model = AssetSystem.TryGetGltf(in world.Assets, gh);
            Assert.True(model != null, "External .gltf + .bin + .png loaded");
            Assert.Equal(3, model->NodeCount, "3 nodes parsed");
            Assert.Equal(1, model->SkinCount, "1 skin parsed");
            Assert.Equal(1, model->AnimationCount, "1 animation parsed");

            // Check Matrix decomposition (negative scale + rotation)
            GltfNodeData n0 = model->Nodes[0];
            Mat4 recomposed = Mat4.CreateTRS(n0.LocalPosition, n0.LocalRotation, n0.LocalScale);
            Assert.Near(node0Mat.C0.X, recomposed.C0.X, 1e-3f, "Decomposed negative-det matrix C0.X round-trips");
            Assert.Near(node0Mat.C0.Z, recomposed.C0.Z, 1e-3f, "Decomposed negative-det matrix C0.Z round-trips");
            Assert.Near(node0Mat.C1.Y, recomposed.C1.Y, 1e-3f, "Decomposed negative-det matrix C1.Y round-trips");
            Assert.Near(node0Mat.C2.X, recomposed.C2.X, 1e-3f, "Decomposed negative-det matrix C2.X round-trips");
            Assert.Near(node0Mat.C3.X, recomposed.C3.X, 1e-3f, "Decomposed matrix translation X");

            // Check Material Mask + DoubleSided + External Texture
            MaterialAssetData* maskMat = AssetSystem.TryGetMaterial(in world.Assets, model->Primitives[0].Material);
            Assert.True(maskMat != null, "Mask material valid");
            Assert.Equal((byte)MaterialAlphaMode.Mask, (byte)maskMat->AlphaMode, "AlphaMode.Mask preserved");
            Assert.Near(0.35f, maskMat->AlphaCutoff, 1e-4f, "AlphaCutoff preserved");
            Assert.Equal((byte)1, maskMat->DoubleSided, "DoubleSided preserved");
            TextureAssetData* extTex = AssetSystem.TryGetTexture(in world.Assets, maskMat->BaseColorTexture);
            Assert.True(extTex != null && extTex->Width == 32 && extTex->Height == 32, "External PNG loaded as 32x32");

            MaterialHandle blendMatH = AssetSystem.FindMaterial(in world.Assets, StringId.Hash("BlendMat"));
            MaterialAssetData* blendMat = AssetSystem.TryGetMaterial(in world.Assets, blendMatH);
            Assert.True(blendMat != null && blendMat->AlphaMode == MaterialAlphaMode.Blend, "AlphaMode.Blend preserved");

            // Check auto-computed normals & remapped JOINTS_0 (old joint 0 -> new topological joint 1)
            MeshAssetData* meshPtr = AssetSystem.TryGetMesh(in world.Assets, model->Primitives[0].Mesh);
            Assert.True(meshPtr != null, "Mesh valid");
            Assert.Near(1f, meshPtr->Vertices[0].Normal.Z, 1e-3f, "Auto-computed normal points +Z");
            Assert.Equal((byte)1, (byte)(meshPtr->Vertices[0].JointIndices & 0xFFu), "JOINTS_0 remapped ChildJoint 0 -> 1");

            // Check topological sort & non-joint channel=-1
            SkeletonData* skel = AnimationSystem.TryGetSkeleton(in world.Animations, model->Skins[0]);
            Assert.True(skel != null && skel->JointCount == 2, "Skeleton has 2 joints");
            Assert.Equal(-1, skel->ParentIndices[0], "Topological joint 0 is ParentJoint (parent=-1)");
            Assert.Equal(0, skel->ParentIndices[1], "Topological joint 1 is ChildJoint (parent=0)");

            AnimClipData* clip = AnimationSystem.TryGetClip(in world.Animations, model->Animations[0]);
            Assert.True(clip != null && clip->ChannelCount == 2, "Clip has 2 channels");
            Assert.Equal(0, clip->Channels[0].TargetJointIndex, "ParentJoint channel targets topological joint 0");
            Assert.Equal(-1, clip->Channels[1].TargetJointIndex, "Non-joint node channel has TargetJointIndex == -1");

            // Step CubicSpline at t = 0.5 -> ParentJoint Y = 1.75, ChildJoint = (1, 1.75, 0)
            var sceneId = SceneId.Create(1, 1);
            GltfLoader.Instantiate(ref world, ref alloc.Scratch, gh, Vec3.Zero, Quat.Identity, Vec3.One, sceneId);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 0.5f);
            Assert.Equal(2, world.Animations.FrameJointPalette.Length, "2 joints in palette");
            Assert.Near(1.75f, world.Animations.FrameJointPalette[0].C3.Y, 1e-3f, "CubicSpline ParentJoint Y == 1.75");
            Assert.Near(1f, world.Animations.FrameJointPalette[1].C3.X, 1e-3f, "ChildJoint X == 1");
            Assert.Near(1.75f, world.Animations.FrameJointPalette[1].C3.Y, 1e-3f, "ChildJoint inherits ParentJoint Y == 1.75");

            // 2. Embedded base64 data: image URI > 1KB with baseDirectory: null
            byte[] randPng32 = MakeRandomPngBytes(32, 32);
            string b64Png = Convert.ToBase64String(randPng32);
            Assert.True(b64Png.Length > 1024, $"Base64 PNG > 1KB ({b64Png.Length} chars)");
            string b64Gltf = "{"
                + "\"asset\":{\"version\":\"2.0\"},"
                + "\"images\":[{\"uri\":\"data:image/png;base64," + b64Png + "\"}],"
                + "\"textures\":[{\"source\":0}],"
                + "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]"
                + "}";
            GltfHandle b64H = GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(b64Gltf), null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("b64_img_gltf"));
            var b64Model = AssetSystem.TryGetGltf(in world.Assets, b64H);
            Assert.True(b64Model != null, "Base64 >1KB image glTF loaded");
            TextureAssetData* b64Tex = AssetSystem.TryGetTexture(in world.Assets, AssetSystem.FindTexture(in world.Assets, StringId.Hash($"{StringId.Hash("b64_img_gltf").Value}_tex_0")));
            Assert.True(b64Tex != null && b64Tex->Width == 32 && b64Tex->Height == 32, "Base64 >1KB PNG decoded completely");

            // 3. Path traversal rejection (buffer & image)
            string evilBufGltf = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"../../outside.bin\",\"byteLength\":4}]}";
            bool threwTraversal1 = false;
            try { GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(evilBufGltf), tmpDir, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("evil1")); }
            catch (InvalidDataException) { threwTraversal1 = true; }
            Assert.True(threwTraversal1, "Buffer path traversal '..' rejected");

            string evilImgGltf = "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"/etc/passwd\"}],\"textures\":[{\"source\":0}]}";
            bool threwTraversal2 = false;
            try { GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(evilImgGltf), tmpDir, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("evil2")); }
            catch (InvalidDataException) { threwTraversal2 = true; }
            Assert.True(threwTraversal2, "Rooted image path '/etc/passwd' rejected");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            AnimationSystem.UnregisterByBundle(ref world.Animations, bundleId);
            bundleArena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after advanced glTF tests");
    }

    private static void TestGltfCorruptAndInstantiateCleanup()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
        var bundleArena = LinearArena.Create(1 * 1024 * 1024);
        var bundleId = BundleId.Create(4, 1);

        try
        {
            // 1. Corrupt GLB chunkLen = 0xFFFFFFFFu
            byte[] badChunkGlb = new byte[24];
            BitConverter.TryWriteBytes(badChunkGlb.AsSpan(0, 4), 0x46546C67u);
            BitConverter.TryWriteBytes(badChunkGlb.AsSpan(4, 4), 2u);
            BitConverter.TryWriteBytes(badChunkGlb.AsSpan(8, 4), 24u);
            BitConverter.TryWriteBytes(badChunkGlb.AsSpan(12, 4), 0xFFFFFFFFu);
            BitConverter.TryWriteBytes(badChunkGlb.AsSpan(16, 4), 0x4E4F534Au);
            AssertThrows<InvalidDataException>(() =>
                GltfLoader.LoadFromMemory(badChunkGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bad1")),
                "GLB chunkLen=0xFFFFFFFF rejected");

            // 2. Omitted bufferView (defaults to -1)
            string noBv = "{\"asset\":{\"version\":\"2.0\"},\"accessors\":[{\"componentType\":5126,\"count\":1,\"type\":\"VEC3\"}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]}";
            AssertThrows<InvalidDataException>(() =>
                GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(noBv), null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bad2")),
                "Omitted bufferView rejected");

            // 3. COLOR_0 with MAT4 type (no stack buffer overflow)
            string b64Data = Convert.ToBase64String(new byte[128]);
            string mat4Color = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"data:application/octet-stream;base64," + b64Data + "\",\"byteLength\":128}],"
                + "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":128}],"
                + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":1,\"type\":\"VEC3\"},{\"bufferView\":0,\"componentType\":5126,\"count\":1,\"type\":\"MAT4\"}],"
                + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"COLOR_0\":1}}]}]}";
            AssertThrows<InvalidDataException>(() =>
                GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(mat4Color), null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bad3")),
                "COLOR_0 MAT4 rejected safely");

            // 4. OOB vertex index
            byte[] oobBin = new byte[48];
            BitConverter.TryWriteBytes(oobBin.AsSpan(36, 4), 99u); // index 99 >= vertCount 3
            string oobIdx = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(oobBin) + "\",\"byteLength\":48}],"
                + "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"bufferView\":0,\"byteOffset\":36,\"byteLength\":12}],"
                + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5125,\"count\":3,\"type\":\"SCALAR\"}],"
                + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]}";
            AssertThrows<InvalidDataException>(() =>
                GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(oobIdx), null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bad4")),
                "OOB vertex index rejected");

            // 5. Truncated CubicSpline output accessor
            string truncCubic = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"data:application/octet-stream;base64," + b64Data + "\",\"byteLength\":128}],"
                + "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":128}],"
                + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":2,\"type\":\"SCALAR\"},{\"bufferView\":0,\"componentType\":5126,\"count\":2,\"type\":\"VEC3\"}],"
                + "\"nodes\":[{\"name\":\"N\"}],\"animations\":[{\"samplers\":[{\"input\":0,\"output\":1,\"interpolation\":\"CUBICSPLINE\"}],\"channels\":[{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"translation\"}}]}]}";
            AssertThrows<InvalidDataException>(() =>
                GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(truncCubic), null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bad5")),
                "Truncated CubicSpline accessor rejected");

            // 6. Malformed JSON array
            string badVec3 = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"translation\":[1.0,2.0]}]}";
            AssertThrows<InvalidDataException>(() =>
                GltfLoader.LoadFromMemory(Encoding.UTF8.GetBytes(badVec3), null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bad6")),
                "Short translation array rejected");

            // 7. Nested BGLB rejected
            var box = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] innerBglb = PackCompressionSystem.CompressGlb(GlbBuilder.BuildGlb(in box), PackCompressionMode.Lz4, TextureCompressionPolicy.None);
            byte[] outerBglb = PackCompressionSystem.CompressGlb(innerBglb, PackCompressionMode.Lz4, TextureCompressionPolicy.None);
            AssertThrows<InvalidDataException>(() =>
                GltfLoader.LoadFromMemory(outerBglb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bad7")),
                "Nested BGLB rejected");

            // 8. Instantiate failure rollback (exhaust EntityTable with maxEntities=3 on 3-node skinned HeroMech: 1 root + 3 nodes = 4 entities)
            var hero = GlbBuilder.CreateHeroMechSpec(ColorF.Red, ColorF.Gold);
            byte[] heroGlb = GlbBuilder.BuildGlb(in hero, includeDemoAnimation: true);
            GltfHandle heroH = GltfLoader.LoadFromMemory(heroGlb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("hero_fail"));

            var tinyWorld = EngineWorld.Create(ref alloc.Persistent, maxEntities: 3, maxParticles: 8);
            nuint scratchBefore = alloc.Scratch.Offset;
            bool threwOom = false;
            try
            {
                GltfLoader.Instantiate(ref tinyWorld, ref alloc.Scratch, heroH, Vec3.Zero, Quat.Identity, Vec3.One);
            }
            catch (OutOfMemoryException)
            {
                threwOom = true;
            }
            Assert.True(threwOom, "Instantiate throws OutOfMemoryException when EntityTable exhausted");
            Assert.Equal(0, tinyWorld.Entities.AliveCount, "Instantiate failure cleaned up all spawned entities");
            Assert.Equal(0, tinyWorld.Transforms.Count, "Instantiate failure cleaned up all transforms");
            Assert.Equal(0, tinyWorld.Animations.PlayerCount, "Instantiate failure cleaned up AnimPlayer");
            Assert.Equal(0, tinyWorld.RenderComponents.MeshRendererCount, "Instantiate failure cleaned up MeshRenderers");
            Assert.Equal(scratchBefore, alloc.Scratch.Offset, "Instantiate failure rolled back ScratchArena");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            AnimationSystem.UnregisterByBundle(ref world.Animations, bundleId);
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after corrupt glTF tests");
    }

    private static int s_partialTextureCreated;
    private static void LoadThrowingBundle(ref BundleLoadContext ctx)
    {
        s_partialTextureCreated++;
        TextureLoader.CreateSolidColor(new Color32(255, 0, 0, 255), ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle, StringId.Hash("partial_tex"));
        throw new InvalidOperationException("Simulated mid-load bundle failure");
    }

    private static void LoadAtlas0BundleA(ref BundleLoadContext ctx)
    {
        TextureLoader.CreateSolidColor(new Color32(255, 0, 0, 255), ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle, StringId.Hash("atlas_0"));
    }

    private static void LoadAtlas0BundleB(ref BundleLoadContext ctx)
    {
        TextureLoader.CreateSolidColor(new Color32(0, 0, 255, 255), ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle, StringId.Hash("atlas_0"));
    }

    private static void LoadSkinnedHeroBundle(ref BundleLoadContext ctx)
    {
        var hero = GlbBuilder.CreateHeroMechSpec(ColorF.Blue, ColorF.Cyan);
        byte[] glb = GlbBuilder.BuildGlb(in hero, includeDemoAnimation: true);
        GltfLoader.LoadFromMemory(glb, null, ref *ctx.BundleArena, ref *ctx.ScratchArena, ref ctx.World->Assets, ref ctx.World->Animations, ctx.Bundle, StringId.Hash("skinned_hero"));
    }

    private static EntityId s_survivedHeroRoot;
    private static void OnLoadHeroScene1(ref SceneContext ctx)
    {
        var gltf = AssetSystem.FindGltf(in ctx.World->Assets, StringId.Hash("skinned_hero"));
        GltfLoader.Instantiate(ref *ctx.World, ref ctx.Allocators->Scratch, gltf, Vec3.Zero, Quat.Identity, Vec3.One, ctx.Scene);
    }

    private static void OnLoadHeroScene2(ref SceneContext ctx)
    {
        var gltf = AssetSystem.FindGltf(in ctx.World->Assets, StringId.Hash("skinned_hero"));
        s_survivedHeroRoot = GltfLoader.Instantiate(ref *ctx.World, ref ctx.Allocators->Scratch, gltf, new Vec3(4, 0, 0), Quat.Identity, Vec3.One, ctx.Scene);
    }

    private static void TestBundleAndSceneRobustness()
    {
        NativeMemoryTracker.ResetStatsForTest();
        s_partialTextureCreated = 0;
        s_survivedHeroRoot = EntityId.Invalid;
        string tmpDir = Path.Combine(Path.GetTempPath(), $"bring_bundle_robust_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, "BringBundleTest", directoryOverride: Path.Combine(tmpDir, "logs"), hookExceptions: false);

        var world = EngineWorld.Create(ref alloc.Persistent, 1024, 256);
        var bundles = BundleManager.Create(ref alloc.Persistent, 16, log);
        var scenes = SceneManager.Create(ref alloc.Persistent, 16, maxActiveScenes: 2, log);

        try
        {
            // 1. Missing .bpack file throws FileNotFoundException & rolls back cleanly
            var bMissing = BundleSystem.RegisterPack(ref bundles, "missing_pack", 64 * 1024, Path.Combine(tmpDir, "does_not_exist.bpack"));
            bool threwFnf = false;
            try { BundleSystem.Acquire(ref bundles, bMissing, ref world, ref alloc.Scratch); }
            catch (FileNotFoundException) { threwFnf = true; }
            Assert.True(threwFnf, "Missing .bpack throws FileNotFoundException");
            Assert.True(!BundleSystem.IsLoaded(in bundles, bMissing), "Missing .bpack remains unloaded");
            Assert.Equal(0, BundleSystem.GetRefCount(in bundles, bMissing), "Missing .bpack RefCount == 0");

            // 2. Throwing LoaderFunc rolls back BundleArena + partial texture
            var bFail = BundleSystem.Register(ref bundles, "failing", 64 * 1024, &LoadThrowingBundle);
            bool threwLoader = false;
            try { BundleSystem.Acquire(ref bundles, bFail, ref world, ref alloc.Scratch); }
            catch (InvalidOperationException) { threwLoader = true; }
            Assert.True(threwLoader && s_partialTextureCreated == 1, "Throwing LoaderFunc propagated");
            Assert.True(!BundleSystem.IsLoaded(in bundles, bFail), "Failed bundle IsLoaded == false");
            Assert.Equal(0, BundleSystem.GetRefCount(in bundles, bFail), "Failed bundle RefCount == 0");
            Assert.Equal(0, world.Assets.LiveTextureCount, "Partial texture rolled back on Acquire failure");

            // 3. RegisterManifest end-to-end (gltf + texture + font)
            var box = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.Green);
            File.WriteAllBytes(Path.Combine(tmpDir, "man_box.glb"), GlbBuilder.BuildGlb(in box));
            File.WriteAllBytes(Path.Combine(tmpDir, "man_tex.png"), MakeTestPngBytes(16, 16));
            string manifestJson = "{\"assets\":["
                + "{\"type\":\"gltf\",\"name\":\"man_gltf\",\"path\":\"man_box.glb\"},"
                + "{\"type\":\"texture\",\"name\":\"man_tex\",\"path\":\"man_tex.png\"},"
                + "{\"type\":\"font\",\"name\":\"man_font\",\"path\":\"\",\"pixelSize\":32}"
                + "]}";
            string manifestPath = Path.Combine(tmpDir, "manifest.json");
            File.WriteAllText(manifestPath, manifestJson);
            var bMan = BundleSystem.RegisterManifest(ref bundles, "manifest_bundle", 1024 * 1024, manifestPath);
            BundleSystem.Acquire(ref bundles, bMan, ref world, ref alloc.Scratch);
            Assert.True(AssetSystem.FindGltf(in world.Assets, StringId.Hash("man_gltf")).IsValid, "Manifest glTF loaded");
            Assert.True(AssetSystem.FindTexture(in world.Assets, StringId.Hash("man_tex")).IsValid, "Manifest texture loaded");
            Assert.True(AssetSystem.FindFont(in world.Assets, StringId.Hash("man_font")).IsValid, "Manifest font loaded");
            BundleSystem.Release(ref bundles, bMan, ref world);

            // 4. Multi-bundle UnloadByBundle preserves live colliding name ("atlas_0")
            var bColA = BundleSystem.Register(ref bundles, "col_a", 64 * 1024, &LoadAtlas0BundleA);
            var bColB = BundleSystem.Register(ref bundles, "col_b", 64 * 1024, &LoadAtlas0BundleB);
            BundleSystem.Acquire(ref bundles, bColA, ref world, ref alloc.Scratch);
            BundleSystem.Acquire(ref bundles, bColB, ref world, ref alloc.Scratch);
            TextureHandle liveAtlasB = AssetSystem.FindTexture(in world.Assets, StringId.Hash("atlas_0"));
            Assert.True(liveAtlasB.IsValid, "Colliding atlas_0 resolves to B");
            BundleSystem.Release(ref bundles, bColA, ref world);
            TextureHandle afterUnloadA = AssetSystem.FindTexture(in world.Assets, StringId.Hash("atlas_0"));
            Assert.Equal(liveAtlasB.Raw, afterUnloadA.Raw, "Unloading bundle A preserved bundle B's live atlas_0 lookup");
            BundleSystem.Release(ref bundles, bColB, ref world);
            Assert.True(!AssetSystem.FindTexture(in world.Assets, StringId.Hash("atlas_0")).IsValid, "Unloading bundle B cleared atlas_0");

            // 5. GltfInstantiate + DestroyEntity + Additive UnloadScene + MaxActiveScenes rollback
            var bHero = BundleSystem.Register(ref bundles, "hero_bundle", 1024 * 1024, &LoadSkinnedHeroBundle);
            var bExtra = BundleSystem.Register(ref bundles, "extra_bundle", 64 * 1024, &LoadCommonBundle);
            Span<BundleId> heroDeps = stackalloc BundleId[] { bHero };
            Span<BundleId> extraDeps = stackalloc BundleId[] { bExtra };
            SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "Hero1", heroDeps, 64 * 1024, &OnLoadHeroScene1);
            SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "Hero2", heroDeps, 64 * 1024, &OnLoadHeroScene2);
            SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "Hero3", extraDeps, 64 * 1024, &OnLoadHud);

            SceneId s1 = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("Hero1"), SceneLoadMode.Single);
            SceneId s2 = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("Hero2"), SceneLoadMode.Additive);
            Assert.Equal(2, world.Animations.PlayerCount, "2 skinned AnimPlayers alive");
            Assert.Equal(8, world.Entities.AliveCount, "2 hero roots + 6 glTF child nodes alive");

            // MaxActiveScenes == 2 is full: Additive load of Hero3 must fail WITHOUT loading extra_bundle
            bool threwMaxScenes = false;
            try { SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("Hero3"), SceneLoadMode.Additive); }
            catch (OutOfMemoryException) { threwMaxScenes = true; }
            Assert.True(threwMaxScenes, "MaxActiveScenes throws OutOfMemoryException");
            Assert.True(!BundleSystem.IsLoaded(in bundles, bExtra), "extra_bundle stayed unloaded on MaxActiveScenes failure");
            Assert.Equal(0, BundleSystem.GetRefCount(in bundles, bExtra), "extra_bundle RefCount == 0");

            // Unload Hero1 Additive -> Hero1 entities & child nodes destroyed, Hero2 surviving AnimPlayerHandle still animates!
            SceneSystem.UnloadScene(ref scenes, ref world, ref alloc, ref bundles, s1);
            Assert.Equal(4, world.Entities.AliveCount, "Hero1 root + 3 children destroyed, Hero2 intact");
            Assert.Equal(1, world.Animations.PlayerCount, "1 surviving AnimPlayer");

            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 0.5f);
            Assert.Equal(2, world.Animations.FrameJointPalette.Length, "Surviving Hero2 still computes joint palette");
            Assert.Near(0.22f, world.Animations.FrameJointPalette[0].C3.Y, 0.01f, "Surviving Hero2 animates Y=0.22 cleanly");

            // DestroyEntity on s_survivedHeroRoot destroys all 3 child glTF nodes too
            EngineWorldSystem.DestroyEntity(ref world, s_survivedHeroRoot);
            Assert.Equal(0, world.Entities.AliveCount, "DestroyEntity on glTF root destroyed all child entities (0 leaked)");
            Assert.Equal(0, world.RenderComponents.MeshRendererCount, "DestroyEntity removed MeshRenderers");
            Assert.Equal(0, world.Animations.PlayerCount, "DestroyEntity removed AnimPlayer");

            SceneSystem.UnloadScene(ref scenes, ref world, ref alloc, ref bundles, s2);
            Assert.Equal(0, bundles.LoadedBundleCount, "All bundles unloaded");
        }
        finally
        {
            SceneSystem.UnloadAllScenes(ref scenes, ref world, ref alloc, ref bundles);
            BundleSystem.UnloadAll(ref bundles, ref world);
            LogSystem.Stop(log);
            alloc.Dispose();
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after bundle/scene robustness tests");
    }

    private static string Fmt(float v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] MakeTestPngBytes(int w, int h)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o + 0] = (byte)((x * 37 + y * 13) & 0xFF);
                rgba[o + 1] = (byte)((x * 19 + y * 53) & 0xFF);
                rgba[o + 2] = (byte)((x * 71 + y * 29) & 0xFF);
                rgba[o + 3] = 255;
            }
        }
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }

    private static byte[] MakeRandomPngBytes(int w, int h)
    {
        byte[] rgba = new byte[w * h * 4];
        uint state = 0x9E3779B9u;
        for (int i = 0; i < w * h; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            int o = i * 4;
            rgba[o + 0] = (byte)(state & 0xFF);
            rgba[o + 1] = (byte)((state >> 8) & 0xFF);
            rgba[o + 2] = (byte)((state >> 16) & 0xFF);
            rgba[o + 3] = 255;
        }
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }

    private static void AssertThrows<TException>(Action act, string msg) where TException : Exception
    {
        bool threw = false;
        try { act(); }
        catch (TException) { threw = true; }
        Assert.True(threw, msg);
    }
}
