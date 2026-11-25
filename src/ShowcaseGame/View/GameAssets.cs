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

namespace ShowcaseGame.View;

/// <summary>
/// Manages genuine .glb model generation on disk and BundleLoader callbacks for ShowcaseGame.
/// </summary>
public static unsafe class GameAssets
{
    public static string ModelsDir => Path.Combine(AppContext.BaseDirectory, "Assets", "models");
    public static string ShadersDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");

    /// <summary>
    /// Ensures real spec-compliant .glb 2.0 files exist on disk so GltfLoader loads genuine files.
    /// </summary>
    public static void EnsureGlbFilesOnDisk()
    {
        Directory.CreateDirectory(ModelsDir);

        WriteIfMissing("hero_mech.glb", () =>
        {
            var spec = GlbBuilder.CreateHeroMechSpec(new ColorF(0.18f, 0.52f, 0.95f, 1f), ColorF.Cyan);
            return GlbBuilder.BuildGlb(in spec, includeDemoAnimation: true);
        });

        WriteIfMissing("sentinel_crystal.glb", () =>
        {
            var spec = GlbBuilder.CreateCrystalSpec(0.68f, 1.85f, new ColorF(0.95f, 0.22f, 0.42f, 1f), new Vec3(0.45f, 0.05f, 0.18f), skinned: true);
            return GlbBuilder.BuildGlb(in spec, includeDemoAnimation: true);
        });

        WriteIfMissing("orbital_sentinel.glb", () =>
        {
            var spec = GlbBuilder.CreateCrystalSpec(0.75f, 2.0f, new ColorF(0.78f, 0.28f, 1.0f, 1f), new Vec3(0.35f, 0.08f, 0.55f), skinned: true);
            return GlbBuilder.BuildGlb(in spec, includeDemoAnimation: true);
        });

        WriteIfMissing("arena_pillar.glb", () =>
        {
            var spec = GlbBuilder.CreateBoxSpec(new Vec3(1.5f, 4.2f, 1.5f), new ColorF(0.24f, 0.29f, 0.40f, 1f), new Vec3(0.03f, 0.08f, 0.14f), metallic: 0.45f, roughness: 0.38f);
            return GlbBuilder.BuildGlb(in spec, includeDemoAnimation: false);
        });

        WriteIfMissing("energy_core.glb", () =>
        {
            var spec = GlbBuilder.CreateCrystalSpec(1.35f, 3.2f, new ColorF(0.15f, 0.85f, 1.0f, 0.85f), new Vec3(0.1f, 0.5f, 0.8f), skinned: false);
            return GlbBuilder.BuildGlb(in spec, includeDemoAnimation: false);
        });
    }

    private static void WriteIfMissing(string fileName, Func<byte[]> build)
    {
        string path = Path.Combine(ModelsDir, fileName);
        if (!File.Exists(path))
            File.WriteAllBytes(path, build());
    }

    /// <summary>
    /// Shared 'common' asset bundle (stays loaded across Title <-> Arena transitions).
    /// </summary>
    public static void LoadCommonBundle(ref BundleLoadContext ctx)
    {
        ref var arena = ref *ctx.BundleArena;
        ref var assets = ref ctx.World->Assets;

        SdfFontLoader.BakeSdfFromSystemOrFile(null, ref arena, ref assets, ctx.Bundle, StringId.Hash("main_font"), pixelSize: 64f, atlasSize: 1024, sdfPadding: 8);
        TextureLoader.CreateSolidColor(Color32.White, ref arena, ref assets, ctx.Bundle, StringId.Hash("white"));
        TextureLoader.CreateRadialGlow(64, ref arena, ref assets, ctx.Bundle, StringId.Hash("radial_glow"));
        TextureLoader.CreateArcadeSpriteSheet(64, ref arena, ref assets, ctx.Bundle, StringId.Hash("arcade_sheet"));
        TextureLoader.CreateCheckerGrid(
            256, 8,
            new Color32(28, 36, 54, 255),
            new Color32(20, 26, 42, 255),
            new Color32(45, 115, 165, 255),
            ref arena, ref assets, ctx.Bundle, StringId.Hash("arena_grid"));
    }

    /// <summary>
    /// Title scene dedicated bundle (unloaded immediately when switching to Arena!).
    /// </summary>
    public static void LoadTitleBundle(ref BundleLoadContext ctx)
    {
        ref var arena = ref *ctx.BundleArena;
        ref var scratch = ref *ctx.ScratchArena;
        ref var assets = ref ctx.World->Assets;
        ref var anims = ref ctx.World->Animations;

        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "hero_mech.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("title_hero"));
        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "sentinel_crystal.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("title_sentinel"));
        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "energy_core.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("title_core"));
        CreateArenaFloorAsset(ref arena, ref assets, ctx.Bundle, StringId.Hash("title_floor_mesh"), StringId.Hash("title_floor_mat"), 18f);
    }

    /// <summary>
    /// Arena gameplay dedicated bundle (unloaded if returning to Title!).
    /// </summary>
    public static void LoadArenaBundle(ref BundleLoadContext ctx)
    {
        ref var arena = ref *ctx.BundleArena;
        ref var scratch = ref *ctx.ScratchArena;
        ref var assets = ref ctx.World->Assets;
        ref var anims = ref ctx.World->Animations;

        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "hero_mech.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("arena_hero"));
        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "sentinel_crystal.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("arena_chaser"));
        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "orbital_sentinel.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("arena_orbital"));
        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "arena_pillar.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("arena_pillar"));
        GltfLoader.LoadFromFile(Path.Combine(ModelsDir, "energy_core.glb"), ref arena, ref scratch, ref assets, ref anims, ctx.Bundle, StringId.Hash("arena_core"));
        CreateArenaFloorAsset(ref arena, ref assets, ctx.Bundle, StringId.Hash("arena_floor_mesh"), StringId.Hash("arena_floor_mat"), 26f);
    }

    private static void CreateArenaFloorAsset(
        ref LinearArena arena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId meshName,
        StringId matName,
        float size)
    {
        float h = size * 0.5f;
        Vertex3D* verts = arena.Alloc<Vertex3D>(4, 64);
        verts[0] = new Vertex3D { Position = new Vec3(-h, 0f, h), Normal = Vec3.Up, Uv0 = new Vec2(0, 6), Color = Color32.White };
        verts[1] = new Vertex3D { Position = new Vec3(h, 0f, h), Normal = Vec3.Up, Uv0 = new Vec2(6, 6), Color = Color32.White };
        verts[2] = new Vertex3D { Position = new Vec3(h, 0f, -h), Normal = Vec3.Up, Uv0 = new Vec2(6, 0), Color = Color32.White };
        verts[3] = new Vertex3D { Position = new Vec3(-h, 0f, -h), Normal = Vec3.Up, Uv0 = new Vec2(0, 0), Color = Color32.White };

        uint* idx = arena.AllocUninitialized<uint>(6, 16);
        idx[0] = 0; idx[1] = 1; idx[2] = 2;
        idx[3] = 0; idx[4] = 2; idx[5] = 3;

        AssetSystem.RegisterMesh(ref assets, new MeshAssetData
        {
            OwnerBundle = bundle,
            Name = meshName,
            Vertices = verts,
            VertexCount = 4,
            Indices = idx,
            IndexCount = 6,
            LocalBounds = Bounds3.FromCenterExtents(Vec3.Zero, new Vec3(h, 0.2f, h)),
        });

        AssetSystem.RegisterMaterial(ref assets, new MaterialAssetData
        {
            OwnerBundle = bundle,
            Name = matName,
            BaseColorFactor = ColorF.White,
            BaseColorTexture = AssetSystem.FindTexture(in assets, StringId.Hash("arena_grid")),
            MetallicFactor = 0.2f,
            RoughnessFactor = 0.55f,
        });
    }
}
