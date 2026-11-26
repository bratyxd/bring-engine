using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

namespace BringEngine.Assets.Compression;

/// <summary>
/// GPU texture storage format (matches Unity's uncompressed vs hardware block-compressed DXT1/BC1 & DXT5/BC3 model).
/// </summary>
public enum TexturePixelFormat : byte
{
    Rgba8 = 0,   // 32 bpp uncompressed RGBA
    R8 = 1,      // 8 bpp single-channel (SDF fonts)
    Bc1Rgba = 2, // 4 bpp GPU block-compressed (DXT1, 8 bytes per 4x4 block — 8:1 VRAM savings for opaque textures)
    Bc3Rgba = 3, // 8 bpp GPU block-compressed (DXT5, 16 bytes per 4x4 block — 4:1 VRAM savings with smooth alpha)
}

/// <summary>
/// Policy used by AtlasPacker and AssetPackBuilder when choosing GPU texture compression.
/// </summary>
public enum TextureCompressionPolicy : byte
{
    Auto = 0,  // BC3 if any pixel has alpha < 255, otherwise BC1 (default!)
    Bc1 = 1,   // Force BC1 (8:1 VRAM, opaque or 1-bit punchthrough alpha)
    Bc3 = 2,   // Force BC3 (4:1 VRAM, full interpolated alpha)
    None = 3,  // Keep uncompressed RGBA8
}

/// <summary>
/// Binary header for Bring Compressed Texture ("BRTX") blobs embedded inside .bpack or compressed .glb models.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BrtxHeader
{
    public const uint MagicValue = 0x58545242u; // "BRTX"
    public const ushort CurrentVersion = 1;

    public uint Magic;
    public ushort Version;
    public TexturePixelFormat Format;
    public byte Flags; // bit 0 = Srgb, bit 1 = ClampEdge, bit 2 = NearestFilter
    public int Width;
    public int Height;
    public int DataBytes;
}

/// <summary>
/// Pure NativeAOT, zero-OOP, zero-GC hardware texture block compressor & decompressor (BC1/DXT1 & BC3/DXT5).
/// Supports optional JobSystem.ParallelFor acceleration on large atlases.
/// </summary>
public static unsafe class TextureCompressionSystem
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBlockCompressed(TexturePixelFormat fmt)
        => fmt == TexturePixelFormat.Bc1Rgba || fmt == TexturePixelFormat.Bc3Rgba;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalcByteSize(TexturePixelFormat fmt, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        long bytes;
        switch (fmt)
        {
            case TexturePixelFormat.R8:
                bytes = (long)width * height;
                break;
            case TexturePixelFormat.Bc1Rgba:
            {
                long bx = (width + 3L) >> 2;
                long by = (height + 3L) >> 2;
                bytes = bx * by * 8L;
                break;
            }
            case TexturePixelFormat.Bc3Rgba:
            {
                long bx = (width + 3L) >> 2;
                long by = (height + 3L) >> 2;
                bytes = bx * by * 16L;
                break;
            }
            default:
                bytes = (long)width * height * 4L;
                break;
        }
        if (bytes > int.MaxValue)
            throw new OverflowException($"Texture byte size exceeds 2GB ({width}x{height}, {fmt}).");
        return (int)bytes;
    }

    public static bool HasTransparency(byte* rgba, int width, int height)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be > 0.");
        long count = (long)width * height;
        for (long i = 0; i < count; i++)
        {
            if (rgba[i * 4 + 3] < 255) return true;
        }
        return false;
    }

    public static TexturePixelFormat ResolveFormat(TextureCompressionPolicy policy, byte* rgba, int width, int height)
    {
        return policy switch
        {
            TextureCompressionPolicy.None => TexturePixelFormat.Rgba8,
            TextureCompressionPolicy.Bc1 => TexturePixelFormat.Bc1Rgba,
            TextureCompressionPolicy.Bc3 => TexturePixelFormat.Bc3Rgba,
            _ => HasTransparency(rgba, width, height) ? TexturePixelFormat.Bc3Rgba : TexturePixelFormat.Bc1Rgba,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BcEncodeJobCtx
    {
        public byte* SrcRgba;
        public byte* DstBlocks;
        public int Width;
        public int Height;
        public int BlocksX;
        public byte IsBc3;
    }

    private static void EncodeRowRange(void* userData, int beginRow, int endRow, int workerIndex)
    {
        _ = workerIndex;
        BcEncodeJobCtx* ctx = (BcEncodeJobCtx*)userData;
        int w = ctx->Width;
        int h = ctx->Height;
        int bxCount = ctx->BlocksX;
        byte* src = ctx->SrcRgba;
        byte* dst = ctx->DstBlocks;
        bool isBc3 = ctx->IsBc3 != 0;
        int stride = isBc3 ? 16 : 8;

        byte* block = stackalloc byte[64]; // 4x4 RGBA8
        for (int by = beginRow; by < endRow; by++)
        {
            int py0 = by << 2;
            for (int bx = 0; bx < bxCount; bx++)
            {
                int px0 = bx << 2;
                ExtractBlock4x4(src, w, h, px0, py0, block);
                byte* outBlock = dst + (by * bxCount + bx) * stride;
                if (isBc3)
                    EncodeBc3Block(block, outBlock);
                else
                    EncodeBc1Block(block, outBlock);
            }
        }
    }

    public static void EncodeBc1(byte* rgba, int width, int height, byte* dstBlocks, JobScheduler* jobs = null)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        if (dstBlocks == null) throw new ArgumentNullException(nameof(dstBlocks));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be > 0.");

        int bx = (width + 3) >> 2;
        int by = (height + 3) >> 2;
        var ctx = new BcEncodeJobCtx { SrcRgba = rgba, DstBlocks = dstBlocks, Width = width, Height = height, BlocksX = bx, IsBc3 = 0 };
        JobSystem.ParallelFor(jobs, by, batchSize: 16, &ctx, &EncodeRowRange, minParallelCount: 64);
    }

    public static void EncodeBc3(byte* rgba, int width, int height, byte* dstBlocks, JobScheduler* jobs = null)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        if (dstBlocks == null) throw new ArgumentNullException(nameof(dstBlocks));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be > 0.");

        int bx = (width + 3) >> 2;
        int by = (height + 3) >> 2;
        var ctx = new BcEncodeJobCtx { SrcRgba = rgba, DstBlocks = dstBlocks, Width = width, Height = height, BlocksX = bx, IsBc3 = 1 };
        JobSystem.ParallelFor(jobs, by, batchSize: 16, &ctx, &EncodeRowRange, minParallelCount: 64);
    }

    /// <summary>
    /// Encodes RGBA8 pixels directly into a LinearArena using the chosen target format.
    /// </summary>
    public static byte* EncodeToArena(
        byte* rgba,
        int width,
        int height,
        TexturePixelFormat targetFormat,
        ref LinearArena dstArena,
        out int outBytes,
        JobScheduler* jobs = null)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be > 0.");

        outBytes = CalcByteSize(targetFormat, width, height);
        byte* dst = dstArena.AllocUninitialized<byte>(outBytes, 64);
        if (targetFormat == TexturePixelFormat.Bc1Rgba)
            EncodeBc1(rgba, width, height, dst, jobs);
        else if (targetFormat == TexturePixelFormat.Bc3Rgba)
            EncodeBc3(rgba, width, height, dst, jobs);
        else
            Buffer.MemoryCopy(rgba, dst, outBytes, outBytes);
        return dst;
    }

    /// <summary>
    /// Packs RGBA8 pixels into a self-describing BRTX byte array (header + BC1/BC3/RGBA8 payload).
    /// </summary>
    public static byte[] EncodeBrtxBytes(
        byte* rgba,
        int width,
        int height,
        TextureCompressionPolicy policy,
        bool srgb = true,
        bool clampEdge = true,
        bool nearest = false,
        JobScheduler* jobs = null)
    {
        if (rgba == null) throw new ArgumentNullException(nameof(rgba));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be > 0.");

        TexturePixelFormat fmt = ResolveFormat(policy, rgba, width, height);
        int dataBytes = CalcByteSize(fmt, width, height);
        int headerSize = sizeof(BrtxHeader);
        byte[] result = new byte[headerSize + dataBytes];

        var hdr = new BrtxHeader
        {
            Magic = BrtxHeader.MagicValue,
            Version = BrtxHeader.CurrentVersion,
            Format = fmt,
            Flags = (byte)((srgb ? 1 : 0) | (clampEdge ? 2 : 0) | (nearest ? 4 : 0)),
            Width = width,
            Height = height,
            DataBytes = dataBytes,
        };

        fixed (byte* outPtr = result)
        {
            *(BrtxHeader*)outPtr = hdr;
            byte* payload = outPtr + headerSize;
            if (fmt == TexturePixelFormat.Bc1Rgba)
                EncodeBc1(rgba, width, height, payload, jobs);
            else if (fmt == TexturePixelFormat.Bc3Rgba)
                EncodeBc3(rgba, width, height, payload, jobs);
            else
                Buffer.MemoryCopy(rgba, payload, dataBytes, dataBytes);
        }

        return result;
    }

    public static void DecodeBc1ToRgba(byte* srcBlocks, int width, int height, byte* dstRgba)
    {
        if (srcBlocks == null) throw new ArgumentNullException(nameof(srcBlocks));
        if (dstRgba == null) throw new ArgumentNullException(nameof(dstRgba));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be > 0.");

        int bxCount = (width + 3) >> 2;
        int byCount = (height + 3) >> 2;
        byte* blockOut = stackalloc byte[64];

        for (int by = 0; by < byCount; by++)
        {
            for (int bx = 0; bx < bxCount; bx++)
            {
                DecodeBc1Block(srcBlocks + (by * bxCount + bx) * 8, blockOut, hasExplicitAlpha: false);
                BlitBlock4x4(blockOut, dstRgba, width, height, bx << 2, by << 2);
            }
        }
    }

    public static void DecodeBc3ToRgba(byte* srcBlocks, int width, int height, byte* dstRgba)
    {
        if (srcBlocks == null) throw new ArgumentNullException(nameof(srcBlocks));
        if (dstRgba == null) throw new ArgumentNullException(nameof(dstRgba));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be > 0.");

        int bxCount = (width + 3) >> 2;
        int byCount = (height + 3) >> 2;
        byte* blockOut = stackalloc byte[64];

        for (int by = 0; by < byCount; by++)
        {
            for (int bx = 0; bx < bxCount; bx++)
            {
                byte* blk = srcBlocks + (by * bxCount + bx) * 16;
                DecodeBc1Block(blk + 8, blockOut, hasExplicitAlpha: true);
                DecodeBc3AlphaBlock(blk, blockOut);
                BlitBlock4x4(blockOut, dstRgba, width, height, bx << 2, by << 2);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExtractBlock4x4(byte* src, int w, int h, int px0, int py0, byte* dst)
    {
        for (int y = 0; y < 4; y++)
        {
            int sy = py0 + y;
            if (sy >= h) sy = h - 1;
            for (int x = 0; x < 4; x++)
            {
                int sx = px0 + x;
                if (sx >= w) sx = w - 1;
                int si = (sy * w + sx) * 4;
                int di = (y * 4 + x) * 4;
                dst[di + 0] = src[si + 0];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlitBlock4x4(byte* block, byte* dst, int w, int h, int px0, int py0)
    {
        for (int y = 0; y < 4; y++)
        {
            int dy = py0 + y;
            if (dy >= h) continue;
            for (int x = 0; x < 4; x++)
            {
                int dx = px0 + x;
                if (dx >= w) continue;
                int si = (y * 4 + x) * 4;
                int di = (dy * w + dx) * 4;
                dst[di + 0] = block[si + 0];
                dst[di + 1] = block[si + 1];
                dst[di + 2] = block[si + 2];
                dst[di + 3] = block[si + 3];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort PackRgb565(int r, int g, int b)
        => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnpackRgb565(ushort c, out int r, out int g, out int b)
    {
        int r5 = (c >> 11) & 31;
        int g6 = (c >> 5) & 63;
        int b5 = c & 31;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }

    private static void EncodeBc1Block(byte* rgba, byte* outBlock)
    {
        bool hasPunchthrough = false;
        for (int i = 0; i < 16; i++)
        {
            if (rgba[i * 4 + 3] < 128) { hasPunchthrough = true; break; }
        }
        EncodeColorBlock(rgba, outBlock, allowPunchthrough: hasPunchthrough);
    }

    private static void EncodeBc3Block(byte* rgba, byte* outBlock)
    {
        EncodeBc3AlphaBlock(rgba, outBlock);
        EncodeColorBlock(rgba, outBlock + 8, allowPunchthrough: false);
    }

    private static void EncodeColorBlock(byte* rgba, byte* out8, bool allowPunchthrough)
    {
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        int opaqueCount = 0;

        for (int i = 0; i < 16; i++)
        {
            if (rgba[i * 4 + 3] == 0) continue; // ignore fully transparent pixels for clean edges
            opaqueCount++;
            int r = rgba[i * 4 + 0], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            if (r < minR) minR = r;
            if (g < minG) minG = g;
            if (b < minB) minB = b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
        }

        if (opaqueCount == 0)
        {
            // All pixels transparent
            *(ushort*)(out8 + 0) = 0;
            *(ushort*)(out8 + 2) = 0;
            *(uint*)(out8 + 4) = allowPunchthrough ? 0xFFFF_FFFFu : 0u;
            return;
        }

        // Inset bounding box by 1/16 to reduce endpoint outlier distortion
        int inR = (maxR - minR) >> 4;
        int inG = (maxG - minG) >> 4;
        int inB = (maxB - minB) >> 4;
        minR = Math.Min(255, minR + inR);
        minG = Math.Min(255, minG + inG);
        minB = Math.Min(255, minB + inB);
        maxR = Math.Max(0, maxR - inR);
        maxG = Math.Max(0, maxG - inG);
        maxB = Math.Max(0, maxB - inB);

        ushort c0 = PackRgb565(maxR, maxG, maxB);
        ushort c1 = PackRgb565(minR, minG, minB);

        if (allowPunchthrough)
        {
            if (c0 > c1) { ushort tmp = c0; c0 = c1; c1 = tmp; }
        }
        else
        {
            if (c0 < c1) { ushort tmp = c0; c0 = c1; c1 = tmp; }
            else if (c0 == c1)
            {
                // Solid block
                *(ushort*)(out8 + 0) = c0;
                *(ushort*)(out8 + 2) = c1;
                *(uint*)(out8 + 4) = 0u;
                return;
            }
        }

        UnpackRgb565(c0, out int r0, out int g0, out int b0);
        UnpackRgb565(c1, out int r1, out int g1, out int b1);

        int* pr = stackalloc int[4];
        int* pg = stackalloc int[4];
        int* pb = stackalloc int[4];
        pr[0] = r0; pg[0] = g0; pb[0] = b0;
        pr[1] = r1; pg[1] = g1; pb[1] = b1;

        int palCount;
        if (allowPunchthrough)
        {
            pr[2] = (r0 + r1) >> 1; pg[2] = (g0 + g1) >> 1; pb[2] = (b0 + b1) >> 1;
            palCount = 3;
        }
        else
        {
            pr[2] = (2 * r0 + r1 + 1) / 3; pg[2] = (2 * g0 + g1 + 1) / 3; pb[2] = (2 * b0 + b1 + 1) / 3;
            pr[3] = (r0 + 2 * r1 + 1) / 3; pg[3] = (g0 + 2 * g1 + 1) / 3; pb[3] = (b0 + 2 * b1 + 1) / 3;
            palCount = 4;
        }

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            uint bestIdx;
            if (allowPunchthrough && rgba[i * 4 + 3] < 128)
            {
                bestIdx = 3u;
            }
            else
            {
                int r = rgba[i * 4 + 0], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
                int bestErr = int.MaxValue;
                bestIdx = 0u;
                for (int p = 0; p < palCount; p++)
                {
                    int dr = r - pr[p], dg = g - pg[p], db = b - pb[p];
                    int err = dr * dr + dg * dg + db * db;
                    if (err < bestErr) { bestErr = err; bestIdx = (uint)p; }
                }
            }
            indices |= bestIdx << (i * 2);
        }

        *(ushort*)(out8 + 0) = c0;
        *(ushort*)(out8 + 2) = c1;
        *(uint*)(out8 + 4) = indices;
    }

    private static void EncodeBc3AlphaBlock(byte* rgba, byte* out8)
    {
        int minA = 255, maxA = 0;
        int minMid = 255, maxMid = 0;
        bool hasZero = false, hasFull = false;

        for (int i = 0; i < 16; i++)
        {
            int a = rgba[i * 4 + 3];
            if (a < minA) minA = a;
            if (a > maxA) maxA = a;
            if (a == 0) hasZero = true;
            else if (a == 255) hasFull = true;
            else
            {
                if (a < minMid) minMid = a;
                if (a > maxMid) maxMid = a;
            }
        }

        byte* pal = stackalloc byte[8];
        byte a0, a1;
        if (minA == maxA)
        {
            out8[0] = (byte)maxA;
            out8[1] = (byte)minA;
            out8[2] = 0; out8[3] = 0; out8[4] = 0; out8[5] = 0; out8[6] = 0; out8[7] = 0;
            return;
        }

        bool use6Mode = (hasZero || hasFull) && (minMid <= maxMid);
        if (use6Mode)
        {
            a0 = (byte)minMid;
            a1 = (byte)maxMid;
            pal[0] = a0;
            pal[1] = a1;
            pal[2] = (byte)((4 * a0 + 1 * a1 + 2) / 5);
            pal[3] = (byte)((3 * a0 + 2 * a1 + 2) / 5);
            pal[4] = (byte)((2 * a0 + 3 * a1 + 2) / 5);
            pal[5] = (byte)((1 * a0 + 4 * a1 + 2) / 5);
            pal[6] = 0;
            pal[7] = 255;
        }
        else
        {
            a0 = (byte)maxA;
            a1 = (byte)minA;
            pal[0] = a0;
            pal[1] = a1;
            pal[2] = (byte)((6 * a0 + 1 * a1 + 3) / 7);
            pal[3] = (byte)((5 * a0 + 2 * a1 + 3) / 7);
            pal[4] = (byte)((4 * a0 + 3 * a1 + 3) / 7);
            pal[5] = (byte)((3 * a0 + 4 * a1 + 3) / 7);
            pal[6] = (byte)((2 * a0 + 5 * a1 + 3) / 7);
            pal[7] = (byte)((1 * a0 + 6 * a1 + 3) / 7);
        }

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = rgba[i * 4 + 3];
            int bestErr = int.MaxValue;
            ulong bestIdx = 0;
            for (int p = 0; p < 8; p++)
            {
                int d = a - pal[p];
                int err = d * d;
                if (err < bestErr) { bestErr = err; bestIdx = (ulong)p; }
            }
            bits |= bestIdx << (i * 3);
        }

        out8[0] = a0;
        out8[1] = a1;
        out8[2] = (byte)(bits & 0xFF);
        out8[3] = (byte)((bits >> 8) & 0xFF);
        out8[4] = (byte)((bits >> 16) & 0xFF);
        out8[5] = (byte)((bits >> 24) & 0xFF);
        out8[6] = (byte)((bits >> 32) & 0xFF);
        out8[7] = (byte)((bits >> 40) & 0xFF);
    }

    private static void DecodeBc1Block(byte* src8, byte* dstRgba64, bool hasExplicitAlpha)
    {
        ushort c0 = *(ushort*)(src8 + 0);
        ushort c1 = *(ushort*)(src8 + 2);
        uint indices = *(uint*)(src8 + 4);

        UnpackRgb565(c0, out int r0, out int g0, out int b0);
        UnpackRgb565(c1, out int r1, out int g1, out int b1);

        byte* pal = stackalloc byte[16];
        pal[0] = (byte)r0; pal[1] = (byte)g0; pal[2] = (byte)b0; pal[3] = 255;
        pal[4] = (byte)r1; pal[5] = (byte)g1; pal[6] = (byte)b1; pal[7] = 255;

        if (c0 > c1 || hasExplicitAlpha)
        {
            pal[8] = (byte)((2 * r0 + r1 + 1) / 3);
            pal[9] = (byte)((2 * g0 + g1 + 1) / 3);
            pal[10] = (byte)((2 * b0 + b1 + 1) / 3);
            pal[11] = 255;
            pal[12] = (byte)((r0 + 2 * r1 + 1) / 3);
            pal[13] = (byte)((g0 + 2 * g1 + 1) / 3);
            pal[14] = (byte)((b0 + 2 * b1 + 1) / 3);
            pal[15] = 255;
        }
        else
        {
            pal[8] = (byte)((r0 + r1) >> 1);
            pal[9] = (byte)((g0 + g1) >> 1);
            pal[10] = (byte)((b0 + b1) >> 1);
            pal[11] = 255;
            pal[12] = 0; pal[13] = 0; pal[14] = 0; pal[15] = 0;
        }

        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((indices >> (i * 2)) & 3u) * 4;
            int di = i * 4;
            dstRgba64[di + 0] = pal[idx + 0];
            dstRgba64[di + 1] = pal[idx + 1];
            dstRgba64[di + 2] = pal[idx + 2];
            dstRgba64[di + 3] = pal[idx + 3];
        }
    }

    private static void DecodeBc3AlphaBlock(byte* src8, byte* dstRgba64)
    {
        byte a0 = src8[0];
        byte a1 = src8[1];
        byte* pal = stackalloc byte[8];
        pal[0] = a0;
        pal[1] = a1;

        if (a0 > a1)
        {
            pal[2] = (byte)((6 * a0 + 1 * a1 + 3) / 7);
            pal[3] = (byte)((5 * a0 + 2 * a1 + 3) / 7);
            pal[4] = (byte)((4 * a0 + 3 * a1 + 3) / 7);
            pal[5] = (byte)((3 * a0 + 4 * a1 + 3) / 7);
            pal[6] = (byte)((2 * a0 + 5 * a1 + 3) / 7);
            pal[7] = (byte)((1 * a0 + 6 * a1 + 3) / 7);
        }
        else
        {
            pal[2] = (byte)((4 * a0 + 1 * a1 + 2) / 5);
            pal[3] = (byte)((3 * a0 + 2 * a1 + 2) / 5);
            pal[4] = (byte)((2 * a0 + 3 * a1 + 2) / 5);
            pal[5] = (byte)((1 * a0 + 4 * a1 + 2) / 5);
            pal[6] = 0;
            pal[7] = 255;
        }

        ulong bits = (ulong)src8[2]
                   | ((ulong)src8[3] << 8)
                   | ((ulong)src8[4] << 16)
                   | ((ulong)src8[5] << 24)
                   | ((ulong)src8[6] << 32)
                   | ((ulong)src8[7] << 40);

        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((bits >> (i * 3)) & 7UL);
            dstRgba64[i * 4 + 3] = pal[idx];
        }
    }
}
