using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets.Compression;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Assets;

/// <summary>
/// Unified 64-byte aligned 3D vertex format (static + skinned glTF meshes).
/// Matches pbr_mesh.hlsl vertex input layout exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex3D
{
    public Vec3 Position;      // offset 0  (12 bytes)
    public Vec3 Normal;        // offset 12 (12 bytes)
    public Vec2 Uv0;           // offset 24 (8 bytes)
    public Color32 Color;      // offset 32 (4 bytes) -> UNORM4
    public uint JointIndices;  // offset 36 (4 bytes) -> packed 4x uint8
    public Vec4 JointWeights;  // offset 40 (16 bytes)
    public Vec2 TangentHint;   // offset 56 (8 bytes) -> total 64 bytes!

    public static uint PackJoints(byte j0, byte j1, byte j2, byte j3)
        => (uint)j0 | ((uint)j1 << 8) | ((uint)j2 << 16) | ((uint)j3 << 24);
}

public enum MaterialAlphaMode : byte
{
    Opaque = 0,
    Mask = 1,
    Blend = 2,
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MeshAssetData
{
    public MeshHandle Handle;
    public BundleId OwnerBundle;
    public StringId Name;
    public Vertex3D* Vertices;
    public int VertexCount;
    public uint* Indices;
    public int IndexCount;
    public Bounds3 LocalBounds;
    public byte HasSkinning;
    public int GpuSlot; // -1 if not uploaded to GPU
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TextureAssetData
{
    public TextureHandle Handle;
    public BundleId OwnerBundle;
    public StringId Name;
    public byte* Pixels;
    public int Width;
    public int Height;
    public int DataBytes;            // Exact byte size of Pixels (auto-computed if 0 for legacy RGBA8/R8)
    public TexturePixelFormat Format; // Rgba8, R8, Bc1Rgba (DXT1), Bc3Rgba (DXT5)
    public byte Channels;            // 1 = R8 (SDF font), 4 = RGBA8/BC1/BC3
    public byte Srgb;
    public byte ClampEdge;
    public byte NearestFilter;
    public int GpuSlot;              // -1 if not uploaded to GPU
}

/// <summary>
/// Named sprite entry inside a compressed texture atlas, including optional 9-slice borders.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SpriteRegionData
{
    public StringId Name;
    public BundleId OwnerBundle;
    public TextureHandle AtlasTexture;
    public Vec4 UvRect;       // (u0, v0, uWidth, vHeight) inset with bleed padding
    public Vec2 PixelSize;    // Original unpadded sprite pixel dimensions
    public Vec4 SliceBorders; // 9-slice insets in source pixels: (Left, Bottom, Right, Top); Vec4.Zero if not 9-sliced
    public Vec2 Pivot;        // Default pivot (0.5, 0.5)
    public byte Valid;
}

[StructLayout(LayoutKind.Sequential)]
public struct MaterialAssetData
{
    public MaterialHandle Handle;
    public BundleId OwnerBundle;
    public StringId Name;
    public ColorF BaseColorFactor;
    public TextureHandle BaseColorTexture;
    public float MetallicFactor;
    public float RoughnessFactor;
    public float AlphaCutoff;
    public Vec3 EmissiveFactor;
    public MaterialAlphaMode AlphaMode;
    public byte DoubleSided;
    public ShaderHandle CustomShader;
    public Vec4 CustomParams; // Tweenable custom shader parameters (e.g. time/dissolve/glow/pulse)
}

[StructLayout(LayoutKind.Sequential)]
public struct GlyphMetrics
{
    public int Codepoint;
    public float Advance;
    public float BearingX;
    public float BearingY;
    public float Width;
    public float Height;
    public Vec4 UvRect; // (u0, v0, uWidth, vHeight)
    public byte HasBitmap;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FontAssetData
{
    public FontHandle Handle;
    public BundleId OwnerBundle;
    public StringId Name;
    public TextureHandle AtlasTexture;
    public float PixelSize;
    public float Ascent;
    public float Descent;
    public float LineGap;
    public float LineHeight;
    public int SdfPadding;
    public byte SdfOnEdgeValue;
    public float SdfPixelDistScale;
    public GlyphMetrics* AsciiGlyphs; // 96 entries for ASCII [32..127]
    public float* AsciiKerning;       // 96*96 kerning advance table
}

[StructLayout(LayoutKind.Sequential)]
public struct GltfPrimitiveRef
{
    public MeshHandle Mesh;
    public MaterialHandle Material;
}

[StructLayout(LayoutKind.Sequential)]
public struct GltfNodeData
{
    public FixedString64 Name;
    public int ParentIndex; // -1 for root
    public Vec3 LocalPosition;
    public Quat LocalRotation;
    public Vec3 LocalScale;
    public int PrimitiveStart;
    public int PrimitiveCount;
    public int SkinIndex; // -1 if none
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct GltfModelAssetData
{
    public GltfHandle Handle;
    public BundleId OwnerBundle;
    public StringId Name;
    public GltfNodeData* Nodes;
    public int NodeCount;
    public GltfPrimitiveRef* Primitives;
    public int PrimitiveCount;
    public SkeletonHandle* Skins;
    public int SkinCount;
    public AnimClipHandle* Animations;
    public int AnimationCount;
    public Bounds3 OverallBounds;
}

/// <summary>
/// Central data-oriented asset registry storing CPU asset metadata + optional GPU release hooks.
/// Actual bulk asset bytes (vertices, indices, textures, glyphs, animations) live inside each AssetBundle's LinearArena.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AssetRegistry
{
    public MeshAssetData* Meshes;
    public uint* MeshGens;
    public byte* MeshAlive;
    public int MaxMeshes;
    public int LiveMeshCount;

    public TextureAssetData* Textures;
    public uint* TextureGens;
    public byte* TextureAlive;
    public int MaxTextures;
    public int LiveTextureCount;

    public MaterialAssetData* Materials;
    public uint* MaterialGens;
    public byte* MaterialAlive;
    public int MaxMaterials;
    public int LiveMaterialCount;

    public FontAssetData* Fonts;
    public uint* FontGens;
    public byte* FontAlive;
    public int MaxFonts;
    public int LiveFontCount;

    public GltfModelAssetData* GltfModels;
    public uint* GltfGens;
    public byte* GltfAlive;
    public int MaxGltfModels;
    public int LiveGltfCount;

    public SpriteRegionData* SpriteRegions;
    public int MaxSpriteRegions;
    public int LiveSpriteRegionCount;

    // Name -> Handle raw lookup tables
    public NativeHashMap<uint> MeshByName;
    public NativeHashMap<uint> TextureByName;
    public NativeHashMap<uint> MaterialByName;
    public NativeHashMap<uint> FontByName;
    public NativeHashMap<uint> GltfByName;
    public NativeHashMap<int> SpriteRegionByName;

    // Optional GPU cleanup hooks set by GpuContext when running with renderer enabled
    public void* GpuContextPtr;
    public delegate*<void*, int, void> ReleaseGpuMesh;
    public delegate*<void*, int, void> ReleaseGpuTexture;

    public static AssetRegistry Create(
        ref LinearArena arena,
        int maxMeshes = 1024,
        int maxTextures = 512,
        int maxMaterials = 1024,
        int maxFonts = 64,
        int maxGltfs = 256,
        int maxSpriteRegions = 2048)
    {
        uint* mGens = arena.AllocUninitialized<uint>(maxMeshes, 16);
        uint* tGens = arena.AllocUninitialized<uint>(maxTextures, 16);
        uint* matGens = arena.AllocUninitialized<uint>(maxMaterials, 16);
        uint* fGens = arena.AllocUninitialized<uint>(maxFonts, 16);
        uint* gGens = arena.AllocUninitialized<uint>(maxGltfs, 16);
        for (int i = 0; i < maxMeshes; i++) mGens[i] = 1;
        for (int i = 0; i < maxTextures; i++) tGens[i] = 1;
        for (int i = 0; i < maxMaterials; i++) matGens[i] = 1;
        for (int i = 0; i < maxFonts; i++) fGens[i] = 1;
        for (int i = 0; i < maxGltfs; i++) gGens[i] = 1;

        return new AssetRegistry
        {
            Meshes = arena.Alloc<MeshAssetData>(maxMeshes, 64),
            MeshGens = mGens,
            MeshAlive = arena.Alloc<byte>(maxMeshes, 16),
            MaxMeshes = maxMeshes,

            Textures = arena.Alloc<TextureAssetData>(maxTextures, 64),
            TextureGens = tGens,
            TextureAlive = arena.Alloc<byte>(maxTextures, 16),
            MaxTextures = maxTextures,

            Materials = arena.Alloc<MaterialAssetData>(maxMaterials, 64),
            MaterialGens = matGens,
            MaterialAlive = arena.Alloc<byte>(maxMaterials, 16),
            MaxMaterials = maxMaterials,

            Fonts = arena.Alloc<FontAssetData>(maxFonts, 64),
            FontGens = fGens,
            FontAlive = arena.Alloc<byte>(maxFonts, 16),
            MaxFonts = maxFonts,

            GltfModels = arena.Alloc<GltfModelAssetData>(maxGltfs, 64),
            GltfGens = gGens,
            GltfAlive = arena.Alloc<byte>(maxGltfs, 16),
            MaxGltfModels = maxGltfs,

            SpriteRegions = arena.Alloc<SpriteRegionData>(maxSpriteRegions, 64),
            MaxSpriteRegions = maxSpriteRegions,

            MeshByName = NativeHashMap<uint>.Create(ref arena, maxMeshes),
            TextureByName = NativeHashMap<uint>.Create(ref arena, maxTextures),
            MaterialByName = NativeHashMap<uint>.Create(ref arena, maxMaterials),
            FontByName = NativeHashMap<uint>.Create(ref arena, maxFonts),
            GltfByName = NativeHashMap<uint>.Create(ref arena, maxGltfs),
            SpriteRegionByName = NativeHashMap<int>.Create(ref arena, maxSpriteRegions),
        };
    }
}

public static unsafe class AssetSystem
{
    public static MeshHandle RegisterMesh(ref AssetRegistry reg, in MeshAssetData data)
    {
        for (int i = 0; i < reg.MaxMeshes; i++)
        {
            if (reg.MeshAlive[i] == 0)
            {
                var h = MeshHandle.Create((uint)i, reg.MeshGens[i]);
                reg.MeshAlive[i] = 1;
                reg.LiveMeshCount++;
                reg.Meshes[i] = data;
                reg.Meshes[i].Handle = h;
                reg.Meshes[i].GpuSlot = -1;
                if (data.Name.IsValid) reg.MeshByName.Set(data.Name.Value, h.Raw);
                return h;
            }
        }
        throw new OutOfMemoryException("AssetRegistry mesh slots exhausted.");
    }

    public static TextureHandle RegisterTexture(ref AssetRegistry reg, in TextureAssetData data)
    {
        if (data.Width <= 0 || data.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(data), "Texture width and height must be > 0.");

        for (int i = 0; i < reg.MaxTextures; i++)
        {
            if (reg.TextureAlive[i] == 0)
            {
                var h = TextureHandle.Create((uint)i, reg.TextureGens[i]);
                reg.TextureAlive[i] = 1;
                reg.LiveTextureCount++;
                reg.Textures[i] = data;
                reg.Textures[i].Handle = h;
                reg.Textures[i].GpuSlot = -1;
                if (reg.Textures[i].Format == TexturePixelFormat.Rgba8 && reg.Textures[i].Channels == 1)
                    reg.Textures[i].Format = TexturePixelFormat.R8;
                if (reg.Textures[i].DataBytes <= 0)
                    reg.Textures[i].DataBytes = TextureCompressionSystem.CalcByteSize(reg.Textures[i].Format, reg.Textures[i].Width, reg.Textures[i].Height);
                if (data.Name.IsValid) reg.TextureByName.Set(data.Name.Value, h.Raw);
                return h;
            }
        }
        throw new OutOfMemoryException("AssetRegistry texture slots exhausted.");
    }

    public static MaterialHandle RegisterMaterial(ref AssetRegistry reg, in MaterialAssetData data)
    {
        for (int i = 0; i < reg.MaxMaterials; i++)
        {
            if (reg.MaterialAlive[i] == 0)
            {
                var h = MaterialHandle.Create((uint)i, reg.MaterialGens[i]);
                reg.MaterialAlive[i] = 1;
                reg.LiveMaterialCount++;
                reg.Materials[i] = data;
                reg.Materials[i].Handle = h;
                if (data.Name.IsValid) reg.MaterialByName.Set(data.Name.Value, h.Raw);
                return h;
            }
        }
        throw new OutOfMemoryException("AssetRegistry material slots exhausted.");
    }

    public static FontHandle RegisterFont(ref AssetRegistry reg, in FontAssetData data)
    {
        for (int i = 0; i < reg.MaxFonts; i++)
        {
            if (reg.FontAlive[i] == 0)
            {
                var h = FontHandle.Create((uint)i, reg.FontGens[i]);
                reg.FontAlive[i] = 1;
                reg.LiveFontCount++;
                reg.Fonts[i] = data;
                reg.Fonts[i].Handle = h;
                if (data.Name.IsValid) reg.FontByName.Set(data.Name.Value, h.Raw);
                return h;
            }
        }
        throw new OutOfMemoryException("AssetRegistry font slots exhausted.");
    }

    public static GltfHandle RegisterGltf(ref AssetRegistry reg, in GltfModelAssetData data)
    {
        for (int i = 0; i < reg.MaxGltfModels; i++)
        {
            if (reg.GltfAlive[i] == 0)
            {
                var h = GltfHandle.Create((uint)i, reg.GltfGens[i]);
                reg.GltfAlive[i] = 1;
                reg.LiveGltfCount++;
                reg.GltfModels[i] = data;
                reg.GltfModels[i].Handle = h;
                if (data.Name.IsValid) reg.GltfByName.Set(data.Name.Value, h.Raw);
                return h;
            }
        }
        throw new OutOfMemoryException("AssetRegistry glTF slots exhausted.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeshAssetData* TryGetMesh(in AssetRegistry reg, MeshHandle h)
        => (h.IsValid && h.Index < (uint)reg.MaxMeshes && reg.MeshAlive[h.Index] != 0 && reg.MeshGens[h.Index] == h.Generation)
            ? reg.Meshes + h.Index : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextureAssetData* TryGetTexture(in AssetRegistry reg, TextureHandle h)
        => (h.IsValid && h.Index < (uint)reg.MaxTextures && reg.TextureAlive[h.Index] != 0 && reg.TextureGens[h.Index] == h.Generation)
            ? reg.Textures + h.Index : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MaterialAssetData* TryGetMaterial(in AssetRegistry reg, MaterialHandle h)
        => (h.IsValid && h.Index < (uint)reg.MaxMaterials && reg.MaterialAlive[h.Index] != 0 && reg.MaterialGens[h.Index] == h.Generation)
            ? reg.Materials + h.Index : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FontAssetData* TryGetFont(in AssetRegistry reg, FontHandle h)
        => (h.IsValid && h.Index < (uint)reg.MaxFonts && reg.FontAlive[h.Index] != 0 && reg.FontGens[h.Index] == h.Generation)
            ? reg.Fonts + h.Index : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GltfModelAssetData* TryGetGltf(in AssetRegistry reg, GltfHandle h)
        => (h.IsValid && h.Index < (uint)reg.MaxGltfModels && reg.GltfAlive[h.Index] != 0 && reg.GltfGens[h.Index] == h.Generation)
            ? reg.GltfModels + h.Index : null;

    public static MeshHandle FindMesh(in AssetRegistry reg, StringId name)
        => reg.MeshByName.TryGet(name.Value, out uint raw) ? new MeshHandle(raw) : MeshHandle.Invalid;
    public static TextureHandle FindTexture(in AssetRegistry reg, StringId name)
        => reg.TextureByName.TryGet(name.Value, out uint raw) ? new TextureHandle(raw) : TextureHandle.Invalid;
    public static MaterialHandle FindMaterial(in AssetRegistry reg, StringId name)
        => reg.MaterialByName.TryGet(name.Value, out uint raw) ? new MaterialHandle(raw) : MaterialHandle.Invalid;
    public static FontHandle FindFont(in AssetRegistry reg, StringId name)
        => reg.FontByName.TryGet(name.Value, out uint raw) ? new FontHandle(raw) : FontHandle.Invalid;
    public static GltfHandle FindGltf(in AssetRegistry reg, StringId name)
        => reg.GltfByName.TryGet(name.Value, out uint raw) ? new GltfHandle(raw) : GltfHandle.Invalid;

    public static int RegisterSpriteRegion(ref AssetRegistry reg, in SpriteRegionData data)
    {
        for (int i = 0; i < reg.MaxSpriteRegions; i++)
        {
            if (reg.SpriteRegions[i].Valid == 0)
            {
                reg.SpriteRegions[i] = data;
                reg.SpriteRegions[i].Valid = 1;
                reg.LiveSpriteRegionCount++;
                if (data.Name.IsValid) reg.SpriteRegionByName.Set(data.Name.Value, i);
                return i;
            }
        }
        throw new OutOfMemoryException("AssetRegistry sprite region slots exhausted.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpriteRegionData* TryGetSpriteRegion(in AssetRegistry reg, StringId name)
    {
        if (!name.IsValid || !reg.SpriteRegionByName.TryGet(name.Value, out int slot)) return null;
        return ((uint)slot < (uint)reg.MaxSpriteRegions && reg.SpriteRegions[slot].Valid != 0)
            ? reg.SpriteRegions + slot : null;
    }

    public static bool TryFindSpriteRegion(in AssetRegistry reg, StringId name, out SpriteRegionData region)
    {
        SpriteRegionData* ptr = TryGetSpriteRegion(in reg, name);
        if (ptr == null) { region = default; return false; }
        region = *ptr;
        return true;
    }

    public static SpriteRegionData FindSpriteRegion(in AssetRegistry reg, StringId name)
    {
        SpriteRegionData* ptr = TryGetSpriteRegion(in reg, name);
        if (ptr == null) throw new InvalidOperationException($"SpriteRegion not found in AssetRegistry (hash=0x{name.Value:X16}).");
        return *ptr;
    }

    /// <summary>
    /// Releases all CPU metadata and GPU resources owned by a bundle.
    /// Called immediately before the bundle's LinearArena is disposed.
    /// </summary>
    public static void UnloadByBundle(ref AssetRegistry reg, BundleId bundle)
    {
        for (int i = 0; i < reg.MaxMeshes; i++)
        {
            if (reg.MeshAlive[i] != 0 && reg.Meshes[i].OwnerBundle == bundle)
            {
                if (reg.Meshes[i].GpuSlot >= 0 && reg.ReleaseGpuMesh != null)
                    reg.ReleaseGpuMesh(reg.GpuContextPtr, reg.Meshes[i].GpuSlot);
                if (reg.Meshes[i].Name.IsValid && reg.MeshByName.TryGet(reg.Meshes[i].Name.Value, out uint cur) && cur == reg.Meshes[i].Handle.Raw)
                    reg.MeshByName.Remove(reg.Meshes[i].Name.Value);
                reg.MeshAlive[i] = 0;
                reg.LiveMeshCount--;
                uint g = (reg.MeshGens[i] + 1) & HandlePacking.GenerationMask;
                reg.MeshGens[i] = g == 0 ? 1u : g;
            }
        }
        for (int i = 0; i < reg.MaxTextures; i++)
        {
            if (reg.TextureAlive[i] != 0 && reg.Textures[i].OwnerBundle == bundle)
            {
                if (reg.Textures[i].GpuSlot >= 0 && reg.ReleaseGpuTexture != null)
                    reg.ReleaseGpuTexture(reg.GpuContextPtr, reg.Textures[i].GpuSlot);
                if (reg.Textures[i].Name.IsValid && reg.TextureByName.TryGet(reg.Textures[i].Name.Value, out uint cur) && cur == reg.Textures[i].Handle.Raw)
                    reg.TextureByName.Remove(reg.Textures[i].Name.Value);
                reg.TextureAlive[i] = 0;
                reg.LiveTextureCount--;
                uint g = (reg.TextureGens[i] + 1) & HandlePacking.GenerationMask;
                reg.TextureGens[i] = g == 0 ? 1u : g;
            }
        }
        for (int i = 0; i < reg.MaxMaterials; i++)
        {
            if (reg.MaterialAlive[i] != 0 && reg.Materials[i].OwnerBundle == bundle)
            {
                if (reg.Materials[i].Name.IsValid && reg.MaterialByName.TryGet(reg.Materials[i].Name.Value, out uint cur) && cur == reg.Materials[i].Handle.Raw)
                    reg.MaterialByName.Remove(reg.Materials[i].Name.Value);
                reg.MaterialAlive[i] = 0;
                reg.LiveMaterialCount--;
                uint g = (reg.MaterialGens[i] + 1) & HandlePacking.GenerationMask;
                reg.MaterialGens[i] = g == 0 ? 1u : g;
            }
        }
        for (int i = 0; i < reg.MaxFonts; i++)
        {
            if (reg.FontAlive[i] != 0 && reg.Fonts[i].OwnerBundle == bundle)
            {
                if (reg.Fonts[i].Name.IsValid && reg.FontByName.TryGet(reg.Fonts[i].Name.Value, out uint cur) && cur == reg.Fonts[i].Handle.Raw)
                    reg.FontByName.Remove(reg.Fonts[i].Name.Value);
                reg.FontAlive[i] = 0;
                reg.LiveFontCount--;
                uint g = (reg.FontGens[i] + 1) & HandlePacking.GenerationMask;
                reg.FontGens[i] = g == 0 ? 1u : g;
            }
        }
        for (int i = 0; i < reg.MaxGltfModels; i++)
        {
            if (reg.GltfAlive[i] != 0 && reg.GltfModels[i].OwnerBundle == bundle)
            {
                if (reg.GltfModels[i].Name.IsValid && reg.GltfByName.TryGet(reg.GltfModels[i].Name.Value, out uint cur) && cur == reg.GltfModels[i].Handle.Raw)
                    reg.GltfByName.Remove(reg.GltfModels[i].Name.Value);
                reg.GltfAlive[i] = 0;
                reg.LiveGltfCount--;
                uint g = (reg.GltfGens[i] + 1) & HandlePacking.GenerationMask;
                reg.GltfGens[i] = g == 0 ? 1u : g;
            }
        }
        for (int i = 0; i < reg.MaxSpriteRegions; i++)
        {
            if (reg.SpriteRegions[i].Valid != 0 && reg.SpriteRegions[i].OwnerBundle == bundle)
            {
                if (reg.SpriteRegions[i].Name.IsValid && reg.SpriteRegionByName.TryGet(reg.SpriteRegions[i].Name.Value, out int curSlot) && curSlot == i)
                    reg.SpriteRegionByName.Remove(reg.SpriteRegions[i].Name.Value);
                reg.SpriteRegions[i] = default;
                reg.LiveSpriteRegionCount--;
            }
        }
    }
}
