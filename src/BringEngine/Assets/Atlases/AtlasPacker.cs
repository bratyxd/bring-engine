using System;
using System.Runtime.InteropServices;
using BringEngine.Assets.Compression;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

namespace BringEngine.Assets.Atlases;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AtlasSourceImage
{
    public StringId Name;
    public byte* RgbaPixels;
    public int Width;
    public int Height;
    public Vec4 SliceBorders; // 9-slice insets in source pixels: (Left, Bottom, Right, Top)
    public Vec2 Pivot;        // (0.5, 0.5) if HasPivot == 0 and Pivot == Vec2.Zero
    public byte HasPivot;     // Set to 1 to preserve explicit Pivot == (0, 0)
}

[StructLayout(LayoutKind.Sequential)]
public struct AtlasBuildOptions
{
    public int MaxPageSize;
    public int Padding;
    public TextureCompressionPolicy Compression;
    public byte Srgb;
    public byte NearestFilter;

    public static AtlasBuildOptions Default() => new AtlasBuildOptions
    {
        MaxPageSize = 2048,
        Padding = 2,
        Compression = TextureCompressionPolicy.Auto, // BC3 if any alpha < 255, else BC1!
        Srgb = 1,
        NearestFilter = 0,
    };
}

[StructLayout(LayoutKind.Sequential)]
public struct PackedAtlasRegion
{
    public StringId Name;
    public int PageIndex;
    public Vec4 UvRect;
    public Vec2 PixelSize;
    public Vec4 SliceBorders;
    public Vec2 Pivot;
}

public struct PackedAtlasPage
{
    public int Width;
    public int Height;
    public TexturePixelFormat Format;
    public byte Srgb;
    public byte NearestFilter;
    public byte[] CompressedData;
}

/// <summary>
/// Deterministic skyline/shelf texture atlas packer with 2px edge extrusion bleed padding
/// and default GPU block compression (BC1/BC3). Zero OOP, 100% NativeAOT safe.
/// </summary>
public static unsafe class AtlasPacker
{
    private struct Placement
    {
        public int SourceIndex;
        public int PageIndex;
        public int X;
        public int Y;
    }

    private struct PageSize
    {
        public int Width;
        public int Height;
    }

    /// <summary>
    /// Packs all source sprites into minimal 4x4-aligned atlas pages with edge-extruded bleed padding
    /// and GPU block compression (BC1/BC3 by default).
    /// </summary>
    public static void BuildPages(
        ReadOnlySpan<AtlasSourceImage> sources,
        in AtlasBuildOptions options,
        out PackedAtlasPage[] pages,
        out PackedAtlasRegion[] regions,
        JobScheduler* jobs = null)
    {
        if (sources.Length == 0)
        {
            pages = Array.Empty<PackedAtlasPage>();
            regions = Array.Empty<PackedAtlasRegion>();
            return;
        }

        int maxPage = options.MaxPageSize > 0 ? AlignUp4(options.MaxPageSize) : 2048;
        int pad = options.Padding >= 0 ? options.Padding : 2;

        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i].RgbaPixels == null)
                throw new ArgumentNullException(nameof(sources), $"Atlas source {i} has null RgbaPixels.");
            if (sources[i].Width <= 0 || sources[i].Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(sources), $"Atlas source {i} dimensions must be > 0 (got {sources[i].Width}x{sources[i].Height}).");
        }

        // Deterministic sort by height desc, then width desc, then name hash asc
        int[] order = new int[sources.Length];
        for (int i = 0; i < sources.Length; i++) order[i] = i;
        for (int i = 1; i < order.Length; i++)
        {
            int key = order[i];
            int j = i - 1;
            while (j >= 0 && CompareDesc(in sources[order[j]], in sources[key]) > 0)
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = key;
        }

        // Shelf pack across 1..N pages (4px-aligned cells prevent BC1/BC3 4x4 block crosstalk between neighbors)
        var placements = new Placement[sources.Length];
        int pageCap = 16;
        var pageSizes = new PageSize[pageCap];
        int pageCount = 0;

        int curPage = 0;
        int curX = 0;
        int curY = 0;
        int shelfH = 0;
        int pageUsedW = 0;
        int pageUsedH = 0;

        void FinalizePage(int pIdx, int usedW, int usedH)
        {
            if (pIdx >= pageSizes.Length)
            {
                var bigger = new PageSize[pageSizes.Length * 2];
                Array.Copy(pageSizes, bigger, pageSizes.Length);
                pageSizes = bigger;
            }
            pageSizes[pIdx] = new PageSize
            {
                Width = NextPoT4Aligned(Math.Max(4, usedW), maxPage),
                Height = NextPoT4Aligned(Math.Max(4, usedH), maxPage),
            };
            if (pIdx + 1 > pageCount) pageCount = pIdx + 1;
        }

        for (int oi = 0; oi < order.Length; oi++)
        {
            int si = order[oi];
            int sw = sources[si].Width;
            int sh = sources[si].Height;
            int pw = AlignUp4(sw + pad * 2);
            int ph = AlignUp4(sh + pad * 2);

            if (pw > maxPage || ph > maxPage)
                throw new InvalidOperationException($"Sprite ({sw}x{sh} + padding {pad}) exceeds MaxPageSize ({maxPage}).");

            if (curX + pw > maxPage)
            {
                curY += shelfH;
                curX = 0;
                shelfH = 0;
            }

            if (curY + ph > maxPage)
            {
                FinalizePage(curPage, pageUsedW, pageUsedH);
                curPage++;
                curX = 0;
                curY = 0;
                shelfH = 0;
                pageUsedW = 0;
                pageUsedH = 0;
            }

            placements[oi] = new Placement
            {
                SourceIndex = si,
                PageIndex = curPage,
                X = curX + pad,
                Y = curY + pad,
            };

            curX += pw;
            if (ph > shelfH) shelfH = ph;
            if (curX > pageUsedW) pageUsedW = curX;
            if (curY + shelfH > pageUsedH) pageUsedH = curY + shelfH;
        }

        FinalizePage(curPage, pageUsedW, pageUsedH);

        pages = new PackedAtlasPage[pageCount];
        regions = new PackedAtlasRegion[sources.Length];

        // Blit each page's sprites + extruded edge bleed padding, then compress to BC1/BC3/RGBA8
        for (int p = 0; p < pageCount; p++)
        {
            int w = pageSizes[p].Width;
            int h = pageSizes[p].Height;
            long rgbaBytes = (long)w * h * 4L;
            if (rgbaBytes > int.MaxValue)
                throw new OverflowException("Atlas page pixel buffer exceeds 2GB.");
            byte[] rgba = new byte[(int)rgbaBytes];

            fixed (byte* dstPtr = rgba)
            {
                for (int oi = 0; oi < placements.Length; oi++)
                {
                    ref Placement pl = ref placements[oi];
                    if (pl.PageIndex != p) continue;
                    ref readonly AtlasSourceImage src = ref sources[pl.SourceIndex];

                    BlitWithEdgeBleed(src.RgbaPixels, src.Width, src.Height, dstPtr, w, h, pl.X, pl.Y, pad);

                    Vec2 piv = src.HasPivot != 0
                        ? src.Pivot
                        : ((src.Pivot.X == 0f && src.Pivot.Y == 0f) ? new Vec2(0.5f, 0.5f) : src.Pivot);
                    regions[pl.SourceIndex] = new PackedAtlasRegion
                    {
                        Name = src.Name,
                        PageIndex = p,
                        UvRect = new Vec4(pl.X / (float)w, pl.Y / (float)h, src.Width / (float)w, src.Height / (float)h),
                        PixelSize = new Vec2(src.Width, src.Height),
                        SliceBorders = src.SliceBorders,
                        Pivot = piv,
                    };
                }

                TexturePixelFormat fmt = TextureCompressionSystem.ResolveFormat(options.Compression, dstPtr, w, h);
                int byteCount = TextureCompressionSystem.CalcByteSize(fmt, w, h);
                byte[] compBytes = new byte[byteCount];
                fixed (byte* outPtr = compBytes)
                {
                    if (fmt == TexturePixelFormat.Bc1Rgba)
                        TextureCompressionSystem.EncodeBc1(dstPtr, w, h, outPtr, jobs);
                    else if (fmt == TexturePixelFormat.Bc3Rgba)
                        TextureCompressionSystem.EncodeBc3(dstPtr, w, h, outPtr, jobs);
                    else
                        Buffer.MemoryCopy(dstPtr, outPtr, byteCount, byteCount);
                }

                pages[p] = new PackedAtlasPage
                {
                    Width = w,
                    Height = h,
                    Format = fmt,
                    Srgb = options.Srgb,
                    NearestFilter = options.NearestFilter,
                    CompressedData = compBytes,
                };
            }
        }
    }

    /// <summary>
    /// Runtime convenience: packs sprites into compressed atlas page(s), stores compressed blocks directly in bundleArena,
    /// and registers the atlas TextureHandle(s) + all SpriteRegionData entries in AssetRegistry.
    /// </summary>
    public static int BuildAndRegisterInBundle(
        ReadOnlySpan<AtlasSourceImage> sources,
        in AtlasBuildOptions options,
        ReadOnlySpan<char> atlasNamePrefix,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        JobScheduler* jobs = null)
    {
        BuildPages(sources, in options, out PackedAtlasPage[] pages, out PackedAtlasRegion[] regions, jobs);
        if (pages.Length == 0) return 0;

        TextureHandle[] pageHandles = new TextureHandle[pages.Length];
        for (int p = 0; p < pages.Length; p++)
        {
            ref PackedAtlasPage pg = ref pages[p];
            byte* dst = bundleArena.AllocUninitialized<byte>(pg.CompressedData.Length, 64);
            pg.CompressedData.AsSpan().CopyTo(new Span<byte>(dst, pg.CompressedData.Length));

            StringId texName = StringId.Hash($"{atlasNamePrefix.ToString()}_{p}");
            pageHandles[p] = AssetSystem.RegisterTexture(ref assets, new TextureAssetData
            {
                OwnerBundle = bundle,
                Name = texName,
                Pixels = dst,
                Width = pg.Width,
                Height = pg.Height,
                DataBytes = pg.CompressedData.Length,
                Format = pg.Format,
                Channels = 4,
                Srgb = pg.Srgb,
                ClampEdge = 1,
                NearestFilter = pg.NearestFilter,
            });
        }

        for (int r = 0; r < regions.Length; r++)
        {
            ref PackedAtlasRegion reg = ref regions[r];
            AssetSystem.RegisterSpriteRegion(ref assets, new SpriteRegionData
            {
                Name = reg.Name,
                OwnerBundle = bundle,
                AtlasTexture = pageHandles[reg.PageIndex],
                UvRect = reg.UvRect,
                PixelSize = reg.PixelSize,
                SliceBorders = reg.SliceBorders,
                Pivot = reg.Pivot,
            });
        }

        return pages.Length;
    }

    private static void BlitWithEdgeBleed(
        byte* src, int sw, int sh,
        byte* dst, int dw, int dh,
        int ox, int oy, int pad)
    {
        if (src == null || dst == null || sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0)
            return;

        int xMin = Math.Max(0, ox - pad);
        int xMax = Math.Min(dw - 1, ox + sw - 1 + pad);
        int yMin = Math.Max(0, oy - pad);
        int yMax = Math.Min(dh - 1, oy + sh - 1 + pad);

        for (int dy = yMin; dy <= yMax; dy++)
        {
            int sy = Math.Clamp(dy - oy, 0, sh - 1);
            for (int dx = xMin; dx <= xMax; dx++)
            {
                int sx = Math.Clamp(dx - ox, 0, sw - 1);
                int si = (sy * sw + sx) * 4;
                int di = (dy * dw + dx) * 4;
                dst[di + 0] = src[si + 0];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
    }

    private static int CompareDesc(in AtlasSourceImage a, in AtlasSourceImage b)
    {
        if (a.Height != b.Height) return b.Height.CompareTo(a.Height);
        if (a.Width != b.Width) return b.Width.CompareTo(a.Width);
        return a.Name.Value.CompareTo(b.Name.Value);
    }

    private static int AlignUp4(int v) => (v + 3) & ~3;

    private static int NextPoT4Aligned(int value, int max)
    {
        int pot = 16;
        while (pot < value && pot < max) pot <<= 1;
        if (pot > max) pot = AlignUp4(max);
        return AlignUp4(pot);
    }
}
