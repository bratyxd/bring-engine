using System;
using System.IO;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using StbTrueTypeSharp;

namespace BringEngine.Assets.Fonts;

/// <summary>
/// TrueType (.ttf) Signed Distance Field (SDF) font atlas baker.
/// Bakes crisp zoom-independent R8 SDF glyph atlases + exact kerning tables directly into a BundleArena.
/// 100% NativeAOT compatible (zero reflection).
/// </summary>
public static unsafe class SdfFontLoader
{
    public const int FirstAscii = 32;
    public const int AsciiCount = 96; // [32..127]

    public static FontHandle BakeSdfFromSystemOrFile(
        string? preferredTtfPath,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        float pixelSize = 48f,
        int atlasSize = 512,
        int sdfPadding = 8)
    {
        string? resolved = ResolveTtfPath(preferredTtfPath);
        if (resolved != null && File.Exists(resolved))
        {
            byte[] ttfBytes = File.ReadAllBytes(resolved);
            return BakeSdfFromMemory(ttfBytes, ref bundleArena, ref assets, bundle, name, pixelSize, atlasSize, sdfPadding);
        }

        return BakeProceduralFallbackSdf(ref bundleArena, ref assets, bundle, name, pixelSize, atlasSize, sdfPadding);
    }

    public static string? ResolveTtfPath(string? preferredPath)
    {
        if (!string.IsNullOrEmpty(preferredPath) && File.Exists(preferredPath))
            return preferredPath;

        // Cross-platform system font candidates
        ReadOnlySpan<string> candidates = new[]
        {
            @"C:\Windows\Fonts\segoeuib.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            @"C:\Windows\Fonts\arialbd.ttf",
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
            "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
            "/System/Library/Fonts/Helvetica.ttc",
        };
        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
                return candidates[i];
        }
        return null;
    }

    public static FontHandle BakeSdfFromMemory(
        byte[] ttfData,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        float pixelSize = 48f,
        int atlasSize = 512,
        int sdfPadding = 8)
    {
        // Symmetric SDF edge (128 = 0.502) gives full [128..255] interior range so small UI glyphs stay 100% opaque
        const byte onEdgeValue = 128;
        float pixelDistScale = onEdgeValue / (float)sdfPadding;

        byte* atlasPixels = bundleArena.Alloc<byte>(atlasSize * atlasSize, 64);
        GlyphMetrics* glyphs = bundleArena.Alloc<GlyphMetrics>(AsciiCount, 64);
        float* kernTable = bundleArena.Alloc<float>(AsciiCount * AsciiCount, 64);

        var fontInfo = new StbTrueType.stbtt_fontinfo();
        fixed (byte* ttfPtr = ttfData)
        {
            if (StbTrueType.stbtt_InitFont(fontInfo, ttfPtr, 0) == 0)
                throw new InvalidOperationException("Failed to initialize TrueType font.");

            float scale = StbTrueType.stbtt_ScaleForPixelHeight(fontInfo, pixelSize);
            int ascentI = 0, descentI = 0, lineGapI = 0;
            StbTrueType.stbtt_GetFontVMetrics(fontInfo, &ascentI, &descentI, &lineGapI);

            int penX = 2, penY = 2, shelfHeight = 0;
            float invAtlas = 1f / atlasSize;

            for (int idx = 0; idx < AsciiCount; idx++)
            {
                int cp = FirstAscii + idx;
                int advI = 0, lsbI = 0;
                StbTrueType.stbtt_GetCodepointHMetrics(fontInfo, cp, &advI, &lsbI);

                int gw = 0, gh = 0, xoff = 0, yoff = 0;
                byte* sdf = StbTrueType.stbtt_GetCodepointSDF(
                    fontInfo, scale, cp, sdfPadding, onEdgeValue, pixelDistScale,
                    &gw, &gh, &xoff, &yoff);

                ref GlyphMetrics gm = ref glyphs[idx];
                gm.Codepoint = cp;
                gm.Advance = advI * scale;

                if (sdf != null && gw > 0 && gh > 0)
                {
                    if (penX + gw + 2 >= atlasSize)
                    {
                        penX = 2;
                        penY += shelfHeight + 2;
                        shelfHeight = 0;
                    }

                    if (penY + gh + 2 < atlasSize)
                    {
                        for (int row = 0; row < gh; row++)
                        {
                            byte* srcRow = sdf + row * gw;
                            byte* dstRow = atlasPixels + (penY + row) * atlasSize + penX;
                            Buffer.MemoryCopy(srcRow, dstRow, gw, gw);
                        }

                        gm.BearingX = xoff;
                        gm.BearingY = -yoff; // Y-up offset from baseline to top of glyph quad
                        gm.Width = gw;
                        gm.Height = gh;
                        gm.UvRect = new Vec4(penX * invAtlas, penY * invAtlas, gw * invAtlas, gh * invAtlas);
                        gm.HasBitmap = 1;

                        penX += gw + 2;
                        if (gh > shelfHeight) shelfHeight = gh;
                    }

                    StbTrueType.stbtt_FreeSDF(sdf, null);
                }
            }

            // Precompute O(1) ASCII kerning table
            for (int a = 0; a < AsciiCount; a++)
            {
                for (int b = 0; b < AsciiCount; b++)
                {
                    int k = StbTrueType.stbtt_GetCodepointKernAdvance(fontInfo, FirstAscii + a, FirstAscii + b);
                    kernTable[a * AsciiCount + b] = k * scale;
                }
            }

            TextureHandle atlasTex = AssetSystem.RegisterTexture(ref assets, new TextureAssetData
            {
                OwnerBundle = bundle,
                Name = StringId.Hash($"{name.Value}_sdf_atlas"),
                Pixels = atlasPixels,
                Width = atlasSize,
                Height = atlasSize,
                Channels = 1, // R8_UNORM SDF
                Srgb = 0,
                ClampEdge = 1,
            });

            float ascent = ascentI * scale;
            float descent = descentI * scale;
            float lineGap = lineGapI * scale;

            return AssetSystem.RegisterFont(ref assets, new FontAssetData
            {
                OwnerBundle = bundle,
                Name = name,
                AtlasTexture = atlasTex,
                PixelSize = pixelSize,
                Ascent = ascent,
                Descent = descent,
                LineGap = lineGap,
                LineHeight = (ascent - descent + lineGap),
                SdfPadding = sdfPadding,
                SdfOnEdgeValue = onEdgeValue,
                SdfPixelDistScale = pixelDistScale,
                AsciiGlyphs = glyphs,
                AsciiKerning = kernTable,
            });
        }
    }

    /// <summary>
    /// Minimal procedural SDF fallback atlas so tests/headless containers always succeed even without OS fonts.
    /// </summary>
    private static FontHandle BakeProceduralFallbackSdf(
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        float pixelSize,
        int atlasSize,
        int sdfPadding)
    {
        byte* atlasPixels = bundleArena.Alloc<byte>(atlasSize * atlasSize, 64);
        GlyphMetrics* glyphs = bundleArena.Alloc<GlyphMetrics>(AsciiCount, 64);
        float* kernTable = bundleArena.Alloc<float>(AsciiCount * AsciiCount, 64);

        int cell = atlasSize / 16;
        float invAtlas = 1f / atlasSize;

        for (int idx = 0; idx < AsciiCount; idx++)
        {
            int cx = (idx % 16) * cell;
            int cy = (idx / 16) * cell;
            ref GlyphMetrics gm = ref glyphs[idx];
            gm.Codepoint = FirstAscii + idx;
            gm.Advance = pixelSize * 0.6f;

            if (idx > 0)
            {
                gm.Width = pixelSize * 0.55f;
                gm.Height = pixelSize * 0.75f;
                gm.BearingX = pixelSize * 0.02f;
                gm.BearingY = pixelSize * 0.75f;
                gm.UvRect = new Vec4(cx * invAtlas, cy * invAtlas, cell * invAtlas, cell * invAtlas);
                gm.HasBitmap = 1;

                for (int y = 0; y < cell; y++)
                {
                    float ny = ((y + 0.5f) / cell) * 2f - 1f;
                    for (int x = 0; x < cell; x++)
                    {
                        float nx = ((x + 0.5f) / cell) * 2f - 1f;
                        float box = DetMath.Max(DetMath.Abs(nx) - 0.55f, DetMath.Abs(ny) - 0.7f);
                        float val = DetMath.Clamp01(0.502f - box * 1.5f);
                        atlasPixels[(cy + y) * atlasSize + (cx + x)] = (byte)(val * 255f);
                    }
                }
            }
        }

        TextureHandle atlasTex = AssetSystem.RegisterTexture(ref assets, new TextureAssetData
        {
            OwnerBundle = bundle,
            Name = StringId.Hash($"{name.Value}_fallback_sdf"),
            Pixels = atlasPixels,
            Width = atlasSize,
            Height = atlasSize,
            Channels = 1,
            Srgb = 0,
            ClampEdge = 1,
        });

        return AssetSystem.RegisterFont(ref assets, new FontAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            AtlasTexture = atlasTex,
            PixelSize = pixelSize,
            Ascent = pixelSize * 0.8f,
            Descent = -pixelSize * 0.2f,
            LineGap = pixelSize * 0.1f,
            LineHeight = pixelSize * 1.1f,
            SdfPadding = sdfPadding,
            SdfOnEdgeValue = 128,
            SdfPixelDistScale = 128f / sdfPadding,
            AsciiGlyphs = glyphs,
            AsciiKerning = kernTable,
        });
    }
}
