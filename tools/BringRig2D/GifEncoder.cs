using System;
using System.Collections.Generic;
using System.IO;

namespace BringRig2D;

/// <summary>
/// Pure C# zero-dependency GIF89a animated encoder (256-color palette + ordered 2x2 Bayer dithering +
/// variable-width LZW compression + Netscape 2.0 infinite loop extension).
/// </summary>
public static class GifEncoder
{
    private static readonly int[,] Bayer2x2 = { { -12, 12 }, { 4, -4 } };

    public static void WriteAnimatedGif(string outputPath, List<byte[]> rgbaFrames, int width, int height, int fps)
    {
        if (rgbaFrames.Count == 0 || width <= 0 || height <= 0) return;

        byte[] palette = Build256ColorPalette();
        ushort delayCs = (ushort)Math.Clamp((int)MathF.Round(100f / Math.Max(1, fps)), 2, 1000);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(outputPath);
        using var bw = new BinaryWriter(fs);

        // 1. Header ("GIF89a")
        bw.Write((byte)'G'); bw.Write((byte)'I'); bw.Write((byte)'F');
        bw.Write((byte)'8'); bw.Write((byte)'9'); bw.Write((byte)'a');

        // 2. Logical Screen Descriptor
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((byte)0xF7); // Global color table (256 colors), 8-bit res
        bw.Write((byte)0);    // Background index
        bw.Write((byte)0);    // Aspect ratio

        // 3. Global Color Table (256 * 3 bytes)
        bw.Write(palette);

        // 4. Netscape 2.0 Application Extension (infinite loop)
        bw.Write((byte)0x21);
        bw.Write((byte)0xFF);
        bw.Write((byte)0x0B);
        bw.Write("NETSCAPE2.0"u8);
        bw.Write((byte)0x03);
        bw.Write((byte)0x01);
        bw.Write((ushort)0); // Loop count 0 = infinite
        bw.Write((byte)0x00);

        byte[] indexed = new byte[width * height];
        for (int f = 0; f < rgbaFrames.Count; f++)
        {
            QuantizeFrame(rgbaFrames[f], width, height, indexed);

            // Graphic Control Extension
            bw.Write((byte)0x21);
            bw.Write((byte)0xF9);
            bw.Write((byte)0x04);
            bw.Write((byte)0x04); // Disposal = 1 (keep/overwrite), no transparency
            bw.Write(delayCs);
            bw.Write((byte)0x00);
            bw.Write((byte)0x00);

            // Image Descriptor
            bw.Write((byte)0x2C);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)width);
            bw.Write((ushort)height);
            bw.Write((byte)0x00); // Use global color table, non-interlaced

            // LZW Image Data (minCodeSize = 8)
            WriteLzwData(bw, indexed, minCodeSize: 8);
        }

        // 5. Trailer
        bw.Write((byte)0x3B);
    }

    private static byte[] Build256ColorPalette()
    {
        // Indices 0..215: 6x6x6 RGB cube (step 51)
        // Indices 216..255: 40 rich grayscale/slate entries for smooth shadows & dark UI backdrops
        byte[] pal = new byte[256 * 3];
        int idx = 0;
        for (int r = 0; r < 6; r++)
        for (int g = 0; g < 6; g++)
        for (int b = 0; b < 6; b++)
        {
            pal[idx * 3 + 0] = (byte)(r * 51);
            pal[idx * 3 + 1] = (byte)(g * 51);
            pal[idx * 3 + 2] = (byte)(b * 51);
            idx++;
        }
        for (int i = 0; i < 40; i++)
        {
            byte v = (byte)Math.Clamp((int)MathF.Round(i * (255f / 39f)), 0, 255);
            pal[idx * 3 + 0] = v;
            pal[idx * 3 + 1] = v;
            pal[idx * 3 + 2] = v;
            idx++;
        }
        return pal;
    }

    private static void QuantizeFrame(byte[] rgba, int width, int height, byte[] dstIndexed)
    {
        for (int y = 0; y < height; y++)
        {
            int y2 = y & 1;
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                int d = Bayer2x2[y2, x & 1];
                int r = Math.Clamp(rgba[o + 0] + d, 0, 255);
                int g = Math.Clamp(rgba[o + 1] + d, 0, 255);
                int b = Math.Clamp(rgba[o + 2] + d, 0, 255);

                // Near-neutral pixels map cleanly into the 40-step grayscale ramp (indices 216..255)
                int maxC = Math.Max(r, Math.Max(g, b));
                int minC = Math.Min(r, Math.Min(g, b));
                if (maxC - minC <= 14)
                {
                    int lum = (r * 77 + g * 150 + b * 29) >> 8;
                    int gi = (int)MathF.Round(lum * (39f / 255f));
                    dstIndexed[y * width + x] = (byte)(216 + Math.Clamp(gi, 0, 39));
                }
                else
                {
                    int ri = (r * 5 + 127) / 255;
                    int gi = (g * 5 + 127) / 255;
                    int bi = (b * 5 + 127) / 255;
                    dstIndexed[y * width + x] = (byte)(ri * 36 + gi * 6 + bi);
                }
            }
        }
    }

    private static void WriteLzwData(BinaryWriter bw, byte[] pixels, int minCodeSize)
    {
        bw.Write((byte)minCodeSize);

        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        int nextCode = eoiCode + 1;
        int codeSize = minCodeSize + 1;
        int maxCode = (1 << codeSize) - 1;

        var dict = new Dictionary<int, int>(4096);
        byte[] subBlock = new byte[255];
        int subLen = 0;
        int bitBuf = 0;
        int bitCount = 0;

        void FlushSubBlock()
        {
            if (subLen > 0)
            {
                bw.Write((byte)subLen);
                bw.Write(subBlock, 0, subLen);
                subLen = 0;
            }
        }

        void EmitCode(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                subBlock[subLen++] = (byte)(bitBuf & 0xFF);
                if (subLen == 255) FlushSubBlock();
                bitBuf >>= 8;
                bitCount -= 8;
            }
        }

        EmitCode(clearCode);

        if (pixels.Length > 0)
        {
            int prefix = pixels[0];
            for (int i = 1; i < pixels.Length; i++)
            {
                byte k = pixels[i];
                int key = (prefix << 8) | k;
                if (dict.TryGetValue(key, out int found))
                {
                    prefix = found;
                }
                else
                {
                    EmitCode(prefix);
                    if (nextCode < 4096)
                    {
                        dict[key] = nextCode++;
                        if (nextCode > maxCode + 1 && codeSize < 12)
                        {
                            codeSize++;
                            maxCode = (1 << codeSize) - 1;
                        }
                    }
                    else
                    {
                        EmitCode(clearCode);
                        dict.Clear();
                        nextCode = eoiCode + 1;
                        codeSize = minCodeSize + 1;
                        maxCode = (1 << codeSize) - 1;
                    }
                    prefix = k;
                }
            }
            EmitCode(prefix);
        }

        EmitCode(eoiCode);
        if (bitCount > 0)
        {
            subBlock[subLen++] = (byte)(bitBuf & 0xFF);
            if (subLen == 255) FlushSubBlock();
        }
        FlushSubBlock();
        bw.Write((byte)0x00); // Block terminator
    }
}
