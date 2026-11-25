using System;
using System.IO;
using System.Runtime.InteropServices;
using BringEngine.Assets.Compression;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using StbImageSharp;

namespace BringEngine.Assets.Textures;

/// <summary>
/// Pure NativeAOT texture loading (StbImageSharp -> LinearArena) and procedural texture generators.
/// </summary>
public static unsafe class TextureLoader
{
    public static TextureHandle LoadFromMemory(
        ReadOnlySpan<byte> encodedBytes,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        bool srgb = true,
        bool clampEdge = false,
        TextureCompressionPolicy compression = TextureCompressionPolicy.None,
        JobScheduler* jobs = null)
    {
        // Fast path: already GPU block-compressed BRTX blob (zero decode, direct BC1/BC3 VRAM upload!)
        if (encodedBytes.Length >= sizeof(BrtxHeader) &&
            MemoryMarshal.Read<uint>(encodedBytes) == BrtxHeader.MagicValue)
        {
            return LoadBrtxFromMemory(encodedBytes, ref bundleArena, ref assets, bundle, name);
        }

        // StbImageSharp requires byte[] or Stream; decode into RGBA8 then store (or compress to BC1/BC3) in bundleArena
        byte[] copy = encodedBytes.ToArray();
        ImageResult img = ImageResult.FromMemory(copy, ColorComponents.RedGreenBlueAlpha);
        if (compression != TextureCompressionPolicy.None)
        {
            fixed (byte* px = img.Data)
            {
                TexturePixelFormat fmt = TextureCompressionSystem.ResolveFormat(compression, px, img.Width, img.Height);
                byte* dst = TextureCompressionSystem.EncodeToArena(px, img.Width, img.Height, fmt, ref bundleArena, out int dataBytes, jobs);
                return RegisterCompressed(dst, dataBytes, img.Width, img.Height, fmt, ref assets, bundle, name, srgb, clampEdge, nearest: false);
            }
        }
        return CreateFromRgba(img.Data, img.Width, img.Height, ref bundleArena, ref assets, bundle, name, srgb, clampEdge);
    }

    public static TextureHandle LoadBrtxFromMemory(
        ReadOnlySpan<byte> brtxBytes,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name)
    {
        if (brtxBytes.Length < sizeof(BrtxHeader))
            throw new InvalidDataException("BRTX buffer truncated.");

        BrtxHeader hdr = MemoryMarshal.Read<BrtxHeader>(brtxBytes);
        if (hdr.Magic != BrtxHeader.MagicValue)
            throw new InvalidDataException("Invalid BRTX header magic.");

        int payloadOffset = sizeof(BrtxHeader);
        if (payloadOffset + hdr.DataBytes > brtxBytes.Length)
            throw new InvalidDataException("BRTX payload truncated.");

        byte* dst = bundleArena.AllocUninitialized<byte>(hdr.DataBytes, 64);
        brtxBytes.Slice(payloadOffset, hdr.DataBytes).CopyTo(new Span<byte>(dst, hdr.DataBytes));
        bool srgb = (hdr.Flags & 1) != 0;
        bool clamp = (hdr.Flags & 2) != 0;
        bool nearest = (hdr.Flags & 4) != 0;
        return RegisterCompressed(dst, hdr.DataBytes, hdr.Width, hdr.Height, hdr.Format, ref assets, bundle, name, srgb, clamp, nearest);
    }

    public static TextureHandle RegisterCompressed(
        byte* dataPtr,
        int dataBytes,
        int width,
        int height,
        TexturePixelFormat format,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        bool srgb = true,
        bool clampEdge = true,
        bool nearest = false)
    {
        return AssetSystem.RegisterTexture(ref assets, new TextureAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            Pixels = dataPtr,
            Width = width,
            Height = height,
            DataBytes = dataBytes,
            Format = format,
            Channels = (byte)(format == TexturePixelFormat.R8 ? 1 : 4),
            Srgb = (byte)(srgb ? 1 : 0),
            ClampEdge = (byte)(clampEdge ? 1 : 0),
            NearestFilter = (byte)(nearest ? 1 : 0),
        });
    }

    public static TextureHandle LoadFromFile(
        string path,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        bool srgb = true,
        bool clampEdge = false,
        TextureCompressionPolicy compression = TextureCompressionPolicy.None,
        JobScheduler* jobs = null)
    {
        byte[] fileBytes = File.ReadAllBytes(path);
        return LoadFromMemory(fileBytes, ref bundleArena, ref assets, bundle, name, srgb, clampEdge, compression, jobs);
    }

    public static TextureHandle CreateFromRgba(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name,
        bool srgb = true,
        bool clampEdge = false,
        bool nearest = false)
    {
        int byteCount = width * height * 4;
        byte* pixels = bundleArena.AllocUninitialized<byte>(byteCount, 64);
        rgba.Slice(0, byteCount).CopyTo(new Span<byte>(pixels, byteCount));

        return AssetSystem.RegisterTexture(ref assets, new TextureAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            Pixels = pixels,
            Width = width,
            Height = height,
            Channels = 4,
            Srgb = (byte)(srgb ? 1 : 0),
            ClampEdge = (byte)(clampEdge ? 1 : 0),
            NearestFilter = (byte)(nearest ? 1 : 0),
        });
    }

    public static TextureHandle CreateSolidColor(
        Color32 color,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name)
    {
        byte* px = bundleArena.AllocUninitialized<byte>(4 * 4 * 4, 16);
        for (int i = 0; i < 16; i++)
        {
            px[i * 4 + 0] = color.R;
            px[i * 4 + 1] = color.G;
            px[i * 4 + 2] = color.B;
            px[i * 4 + 3] = color.A;
        }
        return AssetSystem.RegisterTexture(ref assets, new TextureAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            Pixels = px,
            Width = 4,
            Height = 4,
            Channels = 4,
            Srgb = 1,
        });
    }

    /// <summary>
    /// Procedural soft radial glow texture for particles and glowing 2D sprites in 3D space.
    /// </summary>
    public static TextureHandle CreateRadialGlow(
        int size,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name)
    {
        byte* px = bundleArena.AllocUninitialized<byte>(size * size * 4, 64);
        float invHalf = 2f / size;
        for (int y = 0; y < size; y++)
        {
            float dy = (y + 0.5f) * invHalf - 1f;
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) * invHalf - 1f;
                float d = DetMath.Sqrt(dx * dx + dy * dy);
                float falloff = DetMath.Clamp01(1f - d);
                float glow = falloff * falloff * (3f - 2f * falloff);
                byte a = (byte)(glow * 255f + 0.5f);
                int idx = (y * size + x) * 4;
                px[idx + 0] = 255;
                px[idx + 1] = 255;
                px[idx + 2] = 255;
                px[idx + 3] = a;
            }
        }
        return AssetSystem.RegisterTexture(ref assets, new TextureAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            Pixels = px,
            Width = size,
            Height = size,
            Channels = 4,
            Srgb = 1,
            ClampEdge = 1,
        });
    }

    /// <summary>
    /// Procedural 4x2 animated sprite sheet (energy orb / diamond / rune / heart / star frames) for 2D sprites in 3D.
    /// </summary>
    public static TextureHandle CreateArcadeSpriteSheet(
        int cellSize,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name)
    {
        const int cols = 4, rows = 2;
        int w = cellSize * cols;
        int h = cellSize * rows;
        byte* px = bundleArena.Alloc<byte>(w * h * 4, 64);

        for (int frame = 0; frame < cols * rows; frame++)
        {
            int cx0 = (frame % cols) * cellSize;
            int cy0 = (frame / cols) * cellSize;
            float angleOffset = frame * (DetMath.Pi * 0.25f);
            float pulse = 0.78f + 0.14f * DetMath.Sin(frame * 1.57f);

            for (int ly = 0; ly < cellSize; ly++)
            {
                float ny = ((ly + 0.5f) / cellSize) * 2f - 1f;
                for (int lx = 0; lx < cellSize; lx++)
                {
                    float nx = ((lx + 0.5f) / cellSize) * 2f - 1f;
                    float r = DetMath.Sqrt(nx * nx + ny * ny);
                    float ang = DetMath.Atan2(ny, nx) + angleOffset;

                    float alpha = 0f;
                    float bright = 1f;

                    if (frame < 4)
                    {
                        // Row 0: 4-frame rotating glowing 4-point star/crystal pickup
                        float star = 0.42f * pulse + 0.22f * DetMath.Abs(DetMath.Cos(ang * 2f));
                        float core = DetMath.Clamp01((star - r) * 8f);
                        float halo = DetMath.Clamp01(1f - r) * 0.65f;
                        alpha = DetMath.Clamp01(core + halo * halo);
                        bright = 0.7f + 0.3f * core;
                    }
                    else
                    {
                        // Row 1: 4-frame arcane rune ring with inner diamond
                        float ringDist = DetMath.Abs(r - 0.68f * pulse);
                        float ring = DetMath.Clamp01(1f - ringDist * 10f);
                        float ticks = 0.65f + 0.35f * DetMath.Sin(ang * 6f);
                        float diamond = DetMath.Clamp01((0.35f - (DetMath.Abs(nx) + DetMath.Abs(ny))) * 10f);
                        alpha = DetMath.Clamp01(ring * ticks + diamond);
                        bright = 0.8f + 0.2f * diamond;
                    }

                    int idx = ((cy0 + ly) * w + (cx0 + lx)) * 4;
                    byte c = (byte)(DetMath.Clamp01(bright) * 255f);
                    px[idx + 0] = c;
                    px[idx + 1] = c;
                    px[idx + 2] = 255;
                    px[idx + 3] = (byte)(DetMath.Clamp01(alpha) * 255f);
                }
            }
        }

        return AssetSystem.RegisterTexture(ref assets, new TextureAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            Pixels = px,
            Width = w,
            Height = h,
            Channels = 4,
            Srgb = 1,
            ClampEdge = 1,
        });
    }

    /// <summary>
    /// Procedural checkerboard texture for arena floors / PBR debugging.
    /// </summary>
    public static TextureHandle CreateCheckerGrid(
        int size,
        int tiles,
        Color32 a,
        Color32 b,
        Color32 gridLine,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle,
        StringId name)
    {
        byte* px = bundleArena.AllocUninitialized<byte>(size * size * 4, 64);
        int cell = size / tiles;
        if (cell < 2) cell = 2;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool border = (x % cell) < 2 || (y % cell) < 2;
                bool odd = (((x / cell) + (y / cell)) & 1) != 0;
                Color32 c = border ? gridLine : (odd ? a : b);
                int idx = (y * size + x) * 4;
                px[idx + 0] = c.R;
                px[idx + 1] = c.G;
                px[idx + 2] = c.B;
                px[idx + 3] = c.A;
            }
        }

        return AssetSystem.RegisterTexture(ref assets, new TextureAssetData
        {
            OwnerBundle = bundle,
            Name = name,
            Pixels = px,
            Width = size,
            Height = size,
            Channels = 4,
            Srgb = 1,
            ClampEdge = 0,
        });
    }
}
