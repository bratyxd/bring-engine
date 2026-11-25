using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Simulation.RenderData;

/// <summary>
/// 3D Mesh renderer component stored in flat dense SoA/sparse arrays.
/// Populated by Simulation/View or GltfSystem.Instantiate; read by SDL3_gpu batch renderer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshRendererData
{
    public EntityId Entity;
    public SceneId OwnerScene;
    public MeshHandle Mesh;
    public MaterialHandle Material;
    public ColorF Tint;
    public uint LayerMask;
    public int AnimPlayerIndex; // -1 if static mesh, >= 0 for GPU skinned mesh!
    public byte Visible;
    public byte CastShadow;
}

public enum SpriteBillboardMode : byte
{
    None = 0,         // Oriented freely in 3D space (Unity SpriteRenderer style)
    Spherical = 1,    // Faces camera on all axes
    CylindricalY = 2, // Faces camera around Y axis (HD-2D / Octopath style)
}

public enum SpriteBlendMode : byte
{
    AlphaBlend = 0,
    AlphaCutout = 1,
    Additive = 2,
}

public enum SpriteDrawMode : byte
{
    Simple = 0,          // Standard 1-quad sprite (default)
    Sliced = 1,          // 9-slice UI/sprite: crisp unstretched corners, stretched edges & center (batches in 1 draw call!)
    SlicedNoCenter = 2,  // 9-slice hollow frame: crisp corners & edges, no center quad (8 quads in 1 draw call)
}

/// <summary>
/// Unity-style 2D Sprite living in 3D World Space (or 2D Screen Space via UI camera LayerMask).
/// Supports sprite atlases (UvRect) and Unity-style 9-slicing (DrawMode = Sliced, SliceBorders).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SpriteData
{
    public SpriteHandle Handle;
    public EntityId Entity;
    public SceneId OwnerScene;
    public TextureHandle Texture;
    public ShaderHandle CustomShader;

    public Vec2 Size;            // World-space width/height (or virtual pixels in UI ortho camera)
    public Vec2 Pivot;           // (0.5, 0.5) = center
    public Vec4 UvRect;          // (u0, v0, uWidth, vHeight)
    public ColorF Color;
    public Vec4 CustomParams;    // Tweenable custom shader parameters

    // 9-Slice UI / Panel parameters (used when DrawMode != SpriteDrawMode.Simple)
    public Vec4 SliceBorders;    // Insets in source sprite pixels: (Left, Bottom, Right, Top)
    public Vec2 SourcePixelSize; // Unpadded source sprite size in pixels (auto-set by SpriteFromRegion)
    public float SliceScale;     // Border thickness scale multiplier (0 = treated as 1.0)

    public uint LayerMask;
    public short SortingLayer;
    public short OrderInLayer;
    public SpriteBillboardMode Billboard;
    public SpriteBlendMode BlendMode;
    public SpriteDrawMode DrawMode;
    public byte FlipX;
    public byte FlipY;
    public byte Visible;
    public byte DepthTest;       // 1 = depth test with 3D world, 0 = always on top (HUD)
}

public enum TextAlignmentH : byte { Left = 0, Center = 1, Right = 2 }
public enum TextAlignmentV : byte { Top = 0, Middle = 1, Bottom = 2 }

[Flags]
public enum TextAnimFlags : uint
{
    None = 0,
    Wave = 1 << 0,
    Bounce = 1 << 1,
    Shake = 1 << 2,
    Rainbow = 1 << 3,
    ScalePop = 1 << 4,
    Typewriter = 1 << 5,
    GlowPulse = 1 << 6,
    ShineSweep = 1 << 7,
}

/// <summary>
/// Advanced SDF Text component supporting both 3D World Space (oriented/billboarded) and 2D Screen Space,
/// word wrapping, kerning, per-character animation/tweening, and rich SDF shader styling.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TextData
{
    public TextHandle Handle;
    public EntityId Entity;
    public SceneId OwnerScene;
    public FontHandle Font;
    public ShaderHandle CustomShader;

    public FixedString256 Content;
    public float FontScale;        // World units per em or pixel scale
    public float MaxWidth;         // > 0 enables word wrapping
    public float LetterSpacing;
    public float LineSpacing;
    public TextAlignmentH AlignH;
    public TextAlignmentV AlignV;
    public SpriteBillboardMode Billboard;
    public byte DepthTest;
    public byte Visible;

    // Tweenable visual & SDF shader properties
    public ColorF Color;
    public ColorF SecondaryColor;  // Bottom gradient or rainbow tint
    public ColorF OutlineColor;
    public ColorF ShadowColor;
    public ColorF GlowColor;

    public float OutlineWidth;     // SDF edge outline thickness (0..0.35)
    public float Softness;         // Anti-alias edge softness
    public float GlowIntensity;    // Outer halo glow (0..2)
    public Vec2 ShadowOffset;      // Drop shadow UV/local offset
    public float TypewriterProgress; // 0..1 visible character fraction

    // Per-character procedural animation parameters
    public TextAnimFlags AnimFlags;
    public float AnimTime;
    public float AnimSpeed;
    public float AnimAmplitude;
    public float AnimFrequency;
    public Vec4 CustomParams;

    public uint LayerMask;
    public short SortingOrder;
}

/// <summary>
/// Central tables for 3D MeshRenderers, 2D-in-3D Sprites, and Advanced SDF Text components.
/// Pure CPU data — 100% headless-safe.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RenderComponentTables
{
    // 3D Mesh Renderers (dense array)
    public MeshRendererData* MeshRenderers;
    public int MeshRendererCount;
    public int MaxMeshRenderers;

    // 2D Sprites (generational handles + dense array)
    public SpriteData* Sprites;
    public int* SpriteSlotToDense;
    public uint* SpriteGens;
    public uint* SpriteFreeSlots;
    public int SpriteCapacity;
    public int SpriteFreeCount;
    public int SpriteNextSlot;
    public int SpriteCount;

    // Advanced SDF Text (generational handles + dense array)
    public TextData* Texts;
    public int* TextSlotToDense;
    public uint* TextGens;
    public uint* TextFreeSlots;
    public int TextCapacity;
    public int TextFreeCount;
    public int TextNextSlot;
    public int TextCount;

    public static RenderComponentTables Create(
        ref LinearArena arena,
        int maxMeshRenderers = 8192,
        int maxSprites = 4096,
        int maxTexts = 1024)
    {
        int* s2d = arena.AllocUninitialized<int>(maxSprites, 64);
        uint* sGens = arena.AllocUninitialized<uint>(maxSprites, 64);
        for (int i = 0; i < maxSprites; i++) { s2d[i] = -1; sGens[i] = 1; }

        int* t2d = arena.AllocUninitialized<int>(maxTexts, 64);
        uint* tGens = arena.AllocUninitialized<uint>(maxTexts, 64);
        for (int i = 0; i < maxTexts; i++) { t2d[i] = -1; tGens[i] = 1; }

        return new RenderComponentTables
        {
            MeshRenderers = arena.Alloc<MeshRendererData>(maxMeshRenderers, 64),
            MeshRendererCount = 0,
            MaxMeshRenderers = maxMeshRenderers,

            Sprites = arena.Alloc<SpriteData>(maxSprites, 64),
            SpriteSlotToDense = s2d,
            SpriteGens = sGens,
            SpriteFreeSlots = arena.AllocUninitialized<uint>(maxSprites, 64),
            SpriteCapacity = maxSprites,

            Texts = arena.Alloc<TextData>(maxTexts, 64),
            TextSlotToDense = t2d,
            TextGens = tGens,
            TextFreeSlots = arena.AllocUninitialized<uint>(maxTexts, 64),
            TextCapacity = maxTexts,
        };
    }
}

public static unsafe class RenderComponentSystem
{
    public static int AddMeshRenderer(
        ref RenderComponentTables t,
        EntityId entity,
        MeshHandle mesh,
        MaterialHandle material,
        SceneId scene = default,
        ColorF? tint = null,
        uint layerMask = 1u,
        int animPlayerIndex = -1)
    {
        if (t.MeshRendererCount >= t.MaxMeshRenderers)
            throw new OutOfMemoryException("MeshRenderers capacity exceeded.");
        int idx = t.MeshRendererCount++;
        t.MeshRenderers[idx] = new MeshRendererData
        {
            Entity = entity,
            OwnerScene = scene,
            Mesh = mesh,
            Material = material,
            Tint = tint ?? ColorF.White,
            LayerMask = layerMask,
            AnimPlayerIndex = animPlayerIndex,
            Visible = 1,
            CastShadow = 1,
        };
        return idx;
    }

    public static SpriteHandle AddSprite(ref RenderComponentTables t, in SpriteData template)
    {
        uint slot = t.SpriteFreeCount > 0 ? t.SpriteFreeSlots[--t.SpriteFreeCount] : (uint)t.SpriteNextSlot++;
        if (slot >= (uint)t.SpriteCapacity)
            throw new OutOfMemoryException("SpriteTable capacity exceeded.");

        var h = SpriteHandle.Create(slot, t.SpriteGens[slot]);
        int d = t.SpriteCount++;
        t.SpriteSlotToDense[slot] = d;
        t.Sprites[d] = template;
        t.Sprites[d].Handle = h;
        if (t.Sprites[d].UvRect.Z == 0f && t.Sprites[d].UvRect.W == 0f)
            t.Sprites[d].UvRect = new Vec4(0f, 0f, 1f, 1f);
        if (t.Sprites[d].Size.X == 0f && t.Sprites[d].Size.Y == 0f)
            t.Sprites[d].Size = Vec2.One;
        return h;
    }

    /// <summary>
    /// Builds a SpriteData directly from a packed atlas SpriteRegionData.
    /// Automatically wires the compressed atlas TextureHandle, UV sub-rect, source pixel size,
    /// and enables 9-slicing (SpriteDrawMode.Sliced) whenever the region carries SliceBorders!
    /// </summary>
    public static SpriteData SpriteFromRegion(
        EntityId entity,
        in SpriteRegionData region,
        Vec2 size,
        SceneId scene = default,
        uint layerMask = 1u << 31,
        short orderInLayer = 0,
        byte depthTest = 0)
    {
        bool hasSlice = region.SliceBorders.X > 0f || region.SliceBorders.Y > 0f
                     || region.SliceBorders.Z > 0f || region.SliceBorders.W > 0f;
        return new SpriteData
        {
            Entity = entity,
            OwnerScene = scene,
            Texture = region.AtlasTexture,
            Size = size,
            Pivot = region.Pivot,
            UvRect = region.UvRect,
            Color = ColorF.White,
            SliceBorders = region.SliceBorders,
            SourcePixelSize = region.PixelSize,
            SliceScale = 1f,
            LayerMask = layerMask,
            SortingLayer = 0,
            OrderInLayer = orderInLayer,
            Billboard = SpriteBillboardMode.None,
            BlendMode = SpriteBlendMode.AlphaBlend,
            DrawMode = hasSlice ? SpriteDrawMode.Sliced : SpriteDrawMode.Simple,
            Visible = 1,
            DepthTest = depthTest,
        };
    }

    /// <summary>
    /// Convenience constructor for a 2D HUD 9-sliced panel/button anchored at bottom-left (Pivot = 0,0) on Layer 31.
    /// </summary>
    public static SpriteData DefaultUiSlicedPanel(
        EntityId entity,
        in SpriteRegionData region,
        Vec2 size,
        SceneId scene = default,
        short orderInLayer = 0,
        Vec4? overrideBorders = null,
        float sliceScale = 1f)
    {
        SpriteData sp = SpriteFromRegion(entity, in region, size, scene, layerMask: 1u << 31, orderInLayer: orderInLayer, depthTest: 0);
        sp.Pivot = Vec2.Zero;
        if (overrideBorders.HasValue) sp.SliceBorders = overrideBorders.Value;
        sp.SliceScale = sliceScale > 0f ? sliceScale : 1f;
        sp.DrawMode = SpriteDrawMode.Sliced;
        return sp;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpriteData* TryGetSprite(in RenderComponentTables t, SpriteHandle h)
    {
        if (!h.IsValid || h.Index >= (uint)t.SpriteCapacity) return null;
        if (t.SpriteGens[h.Index] != h.Generation) return null;
        int d = t.SpriteSlotToDense[h.Index];
        return (d >= 0 && d < t.SpriteCount) ? t.Sprites + d : null;
    }

    public static bool RemoveSprite(ref RenderComponentTables t, SpriteHandle h)
    {
        SpriteData* ptr = TryGetSprite(in t, h);
        if (ptr == null) return false;
        int d = (int)(ptr - t.Sprites);
        uint slot = h.Index;
        uint g = (t.SpriteGens[slot] + 1) & HandlePacking.GenerationMask;
        t.SpriteGens[slot] = g == 0 ? 1u : g;
        t.SpriteSlotToDense[slot] = -1;
        t.SpriteFreeSlots[t.SpriteFreeCount++] = slot;

        int last = --t.SpriteCount;
        if (d != last)
        {
            t.Sprites[d] = t.Sprites[last];
            t.SpriteSlotToDense[t.Sprites[d].Handle.Index] = d;
        }
        return true;
    }

    public static TextHandle AddText(ref RenderComponentTables t, in TextData template)
    {
        uint slot = t.TextFreeCount > 0 ? t.TextFreeSlots[--t.TextFreeCount] : (uint)t.TextNextSlot++;
        if (slot >= (uint)t.TextCapacity)
            throw new OutOfMemoryException("TextTable capacity exceeded.");

        var h = TextHandle.Create(slot, t.TextGens[slot]);
        int d = t.TextCount++;
        t.TextSlotToDense[slot] = d;
        t.Texts[d] = template;
        t.Texts[d].Handle = h;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextData* TryGetText(in RenderComponentTables t, TextHandle h)
    {
        if (!h.IsValid || h.Index >= (uint)t.TextCapacity) return null;
        if (t.TextGens[h.Index] != h.Generation) return null;
        int d = t.TextSlotToDense[h.Index];
        return (d >= 0 && d < t.TextCount) ? t.Texts + d : null;
    }

    public static bool RemoveText(ref RenderComponentTables t, TextHandle h)
    {
        TextData* ptr = TryGetText(in t, h);
        if (ptr == null) return false;
        int d = (int)(ptr - t.Texts);
        uint slot = h.Index;
        uint g = (t.TextGens[slot] + 1) & HandlePacking.GenerationMask;
        t.TextGens[slot] = g == 0 ? 1u : g;
        t.TextSlotToDense[slot] = -1;
        t.TextFreeSlots[t.TextFreeCount++] = slot;

        int last = --t.TextCount;
        if (d != last)
        {
            t.Texts[d] = t.Texts[last];
            t.TextSlotToDense[t.Texts[d].Handle.Index] = d;
        }
        return true;
    }

    public static TextData DefaultWorldText(EntityId entity, FontHandle font, ReadOnlySpan<char> text, SceneId scene = default) => new TextData
    {
        Entity = entity,
        OwnerScene = scene,
        Font = font,
        Content = FixedString256.From(text),
        FontScale = 0.02f,
        LineSpacing = 1.15f,
        AlignH = TextAlignmentH.Center,
        AlignV = TextAlignmentV.Middle,
        Billboard = SpriteBillboardMode.Spherical,
        DepthTest = 1,
        Visible = 1,
        Color = ColorF.White,
        SecondaryColor = ColorF.White,
        OutlineColor = new ColorF(0.05f, 0.05f, 0.1f, 1f),
        ShadowColor = new ColorF(0f, 0f, 0f, 0.65f),
        GlowColor = ColorF.Cyan,
        OutlineWidth = 0.12f,
        Softness = 0.04f,
        ShadowOffset = new Vec2(0.06f, -0.06f),
        TypewriterProgress = 1f,
        AnimSpeed = 3.5f,
        AnimAmplitude = 0.12f,
        AnimFrequency = 0.65f,
        LayerMask = 1u,
    };

    public static TextData DefaultHudText(EntityId entity, FontHandle font, ReadOnlySpan<char> text, SceneId scene = default)
    {
        var td = DefaultWorldText(entity, font, text, scene);
        td.FontScale = 0.65f;
        td.AlignH = TextAlignmentH.Left;
        td.AlignV = TextAlignmentV.Top;
        td.Billboard = SpriteBillboardMode.None;
        td.DepthTest = 0;
        td.LayerMask = 1u << 31; // UI camera layer
        td.AnimAmplitude = 5f;
        td.ShadowOffset = new Vec2(2f, -2f);
        return td;
    }

    public static void StepTextAnimClocks(ref RenderComponentTables t, float dt)
    {
        for (int i = 0; i < t.TextCount; i++)
            t.Texts[i].AnimTime += dt;
    }

    public static void RemoveByEntity(ref RenderComponentTables t, EntityId entity)
    {
        for (int i = t.MeshRendererCount - 1; i >= 0; i--)
        {
            if (t.MeshRenderers[i].Entity == entity)
                t.MeshRenderers[i] = t.MeshRenderers[--t.MeshRendererCount];
        }
        for (int i = t.SpriteCount - 1; i >= 0; i--)
        {
            if (t.Sprites[i].Entity == entity)
                RemoveSprite(ref t, t.Sprites[i].Handle);
        }
        for (int i = t.TextCount - 1; i >= 0; i--)
        {
            if (t.Texts[i].Entity == entity)
                RemoveText(ref t, t.Texts[i].Handle);
        }
    }

    public static void RemoveByScene(ref RenderComponentTables t, SceneId scene)
    {
        for (int i = t.MeshRendererCount - 1; i >= 0; i--)
        {
            if (t.MeshRenderers[i].OwnerScene == scene)
                t.MeshRenderers[i] = t.MeshRenderers[--t.MeshRendererCount];
        }
        for (int i = t.SpriteCount - 1; i >= 0; i--)
        {
            if (t.Sprites[i].OwnerScene == scene)
                RemoveSprite(ref t, t.Sprites[i].Handle);
        }
        for (int i = t.TextCount - 1; i >= 0; i--)
        {
            if (t.Texts[i].OwnerScene == scene)
                RemoveText(ref t, t.Texts[i].Handle);
        }
    }
}
