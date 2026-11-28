using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using StbImageSharp;
using StbImageWriteSharp;

namespace BringRig2D;

/// <summary>
/// Image preparation pipeline for AI-generated 2D character parts:
/// auto/explicit chroma keying, soft alpha feathering, green/magenta/general color-spill suppression,
/// morphological speckle cleanup, tight trim + transparent bleed padding, and connected-component island splitting.
/// </summary>
public static class ImagePrep
{
    public struct PrepOptions
    {
        public string KeyMode; // "auto", "green", "magenta", "none", or "#RRGGBB"
        public float Tolerance;
        public float Feather;
        public bool Despill;
        public bool GlobalKey; // Legacy global chroma key mode (default false = border flood-fill + tight interior pockets)
        public bool Trim;
        public int Pad;
        public bool SplitIslands;
        public int MinArea;
        public string? ManifestPath;
    }

    private struct ManifestItem
    {
        public string File;
        public int SrcX, SrcY, Width, Height;
    }

    public static int Run(string inputPath, string outputPath, in PrepOptions opts)
    {
        var manifestItems = new List<ManifestItem>();

        if (File.Exists(inputPath))
        {
            bool outIsDir = opts.SplitIslands || Directory.Exists(outputPath) || outputPath.EndsWith('/') || outputPath.EndsWith('\\');
            if (outIsDir) Directory.CreateDirectory(outputPath);
            ProcessSingleFile(inputPath, outputPath, outIsDir, in opts, manifestItems);
        }
        else if (Directory.Exists(inputPath))
        {
            Directory.CreateDirectory(outputPath);
            string[] files = Directory.GetFiles(inputPath, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga")) continue;
                string rel = Path.GetRelativePath(inputPath, f);
                string dstFile = Path.Combine(outputPath, Path.ChangeExtension(rel, ".png"));
                string? dstDir = Path.GetDirectoryName(dstFile);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                ProcessSingleFile(f, opts.SplitIslands ? (dstDir ?? outputPath) : dstFile, opts.SplitIslands, in opts, manifestItems);
            }
        }
        else
        {
            Console.Error.WriteLine($"[BringRig2D] ERROR: Input path not found: '{inputPath}'.");
            return 1;
        }

        if (!string.IsNullOrEmpty(opts.ManifestPath))
        {
            WriteManifest(opts.ManifestPath, manifestItems);
            Console.WriteLine($"[BringRig2D] Wrote prep manifest ({manifestItems.Count} part(s)) -> '{opts.ManifestPath}'.");
        }
        return 0;
    }

    private static void ProcessSingleFile(string inFile, string outTarget, bool outIsDir, in PrepOptions opts, List<ManifestItem> manifest)
    {
        byte[] raw = File.ReadAllBytes(inFile);
        ImageResult img = ImageResult.FromMemory(raw, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        int w = img.Width;
        int h = img.Height;
        byte[] rgba = (byte[])img.Data.Clone();

        // 1. Chroma key removal (unless key == "none" or image already has alpha border in auto mode)
        bool doKey = !opts.KeyMode.Equals("none", StringComparison.OrdinalIgnoreCase);
        byte kr = 0, kg = 255, kb = 0;
        bool isGreenKey = false;
        bool isMagentaKey = false;

        if (doKey)
        {
            if (opts.KeyMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (HasTransparentBorder(rgba, w, h))
                {
                    doKey = false;
                }
                else
                {
                    DetectBorderMedianColor(rgba, w, h, out kr, out kg, out kb);
                    isGreenKey = kg > kr + 45 && kg > kb + 45;
                    isMagentaKey = kr > kg + 45 && kb > kg + 45 && Math.Abs(kr - kb) < 60;
                }
            }
            else if (opts.KeyMode.Equals("green", StringComparison.OrdinalIgnoreCase))
            {
                kr = 0; kg = 255; kb = 0; isGreenKey = true;
            }
            else if (opts.KeyMode.Equals("magenta", StringComparison.OrdinalIgnoreCase))
            {
                kr = 255; kg = 0; kb = 255; isMagentaKey = true;
            }
            else if (TryParseHexRgb(opts.KeyMode, out kr, out kg, out kb))
            {
                isGreenKey = kg > kr + 45 && kg > kb + 45;
                isMagentaKey = kr > kg + 45 && kb > kg + 45 && Math.Abs(kr - kb) < 60;
            }
        }

        if (doKey)
        {
            ApplyChromaKeyAndDespill(rgba, w, h, kr, kg, kb, opts.Tolerance, opts.Feather, opts.Despill, opts.GlobalKey, isGreenKey, isMagentaKey);
            RemoveSpeckles(rgba, w, h);
        }

        // 2. Island splitting or tight single-image export
        if (opts.SplitIslands)
        {
            string stem = Path.GetFileNameWithoutExtension(inFile);
            var islands = ExtractIslands(rgba, w, h, Math.Max(1, opts.MinArea));
            for (int i = 0; i < islands.Count; i++)
            {
                var isl = islands[i];
                CropAndPad(rgba, w, h, isl.MinX, isl.MinY, isl.MaxX, isl.MaxY, isl.Mask, opts.Pad, out byte[] outBuf, out int ow, out int oh);
                string fileName = islands.Count == 1 ? $"{stem}.png" : $"{stem}_part_{i:D2}.png";
                string fullOut = Path.Combine(outTarget, fileName);
                SavePng(fullOut, outBuf, ow, oh);
                manifest.Add(new ManifestItem { File = fileName, SrcX = isl.MinX, SrcY = isl.MinY, Width = ow, Height = oh });
                Console.WriteLine($"[BringRig2D] prep island -> '{fullOut}' ({ow}x{oh})");
            }
        }
        else
        {
            byte[] finalRgba = rgba;
            int fw = w, fh = h, sx = 0, sy = 0;
            if (opts.Trim)
            {
                if (FindAlphaBounds(rgba, w, h, out int minX, out int minY, out int maxX, out int maxY))
                {
                    sx = minX; sy = minY;
                    CropAndPad(rgba, w, h, minX, minY, maxX, maxY, null, opts.Pad, out finalRgba, out fw, out fh);
                }
            }
            else if (opts.Pad > 0)
            {
                CropAndPad(rgba, w, h, 0, 0, w - 1, h - 1, null, opts.Pad, out finalRgba, out fw, out fh);
            }

            string finalOut = outIsDir ? Path.Combine(outTarget, Path.ChangeExtension(Path.GetFileName(inFile), ".png")) : outTarget;
            string? dir = Path.GetDirectoryName(Path.GetFullPath(finalOut));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SavePng(finalOut, finalRgba, fw, fh);
            manifest.Add(new ManifestItem { File = Path.GetFileName(finalOut), SrcX = sx, SrcY = sy, Width = fw, Height = fh });
            Console.WriteLine($"[BringRig2D] prep -> '{finalOut}' ({fw}x{fh}, key={(doKey ? $"#{kr:X2}{kg:X2}{kb:X2}" : "preserved")})");
        }
    }

    private static bool HasTransparentBorder(byte[] rgba, int w, int h)
    {
        int total = 0, transparent = 0;
        for (int x = 0; x < w; x++)
        {
            total += 2;
            if (rgba[(0 * w + x) * 4 + 3] < 128) transparent++;
            if (rgba[((h - 1) * w + x) * 4 + 3] < 128) transparent++;
        }
        for (int y = 1; y < h - 1; y++)
        {
            total += 2;
            if (rgba[(y * w + 0) * 4 + 3] < 128) transparent++;
            if (rgba[(y * w + (w - 1)) * 4 + 3] < 128) transparent++;
        }
        return total > 0 && transparent > total * 0.25;
    }

    private static void DetectBorderMedianColor(byte[] rgba, int w, int h, out byte kr, out byte kg, out byte kb)
    {
        var rs = new List<byte>();
        var gs = new List<byte>();
        var bs = new List<byte>();
        int step = Math.Max(1, Math.Min(w, h) / 64);

        void Sample(int x, int y)
        {
            int o = (y * w + x) * 4;
            if (rgba[o + 3] < 128) return;
            rs.Add(rgba[o + 0]);
            gs.Add(rgba[o + 1]);
            bs.Add(rgba[o + 2]);
        }

        // Sample corners heavily + border perimeter
        int cw = Math.Min(6, w), ch = Math.Min(6, h);
        for (int y = 0; y < ch; y++)
        for (int x = 0; x < cw; x++)
        {
            Sample(x, y);
            Sample(w - 1 - x, y);
            Sample(x, h - 1 - y);
            Sample(w - 1 - x, h - 1 - y);
        }
        for (int x = 0; x < w; x += step) { Sample(x, 0); Sample(x, h - 1); }
        for (int y = 0; y < h; y += step) { Sample(0, y); Sample(w - 1, y); }

        if (rs.Count == 0) { kr = 0; kg = 255; kb = 0; return; }
        rs.Sort(); gs.Sort(); bs.Sort();
        kr = rs[rs.Count / 2];
        kg = gs[gs.Count / 2];
        kb = bs[bs.Count / 2];
    }

    private static float ComputeRgbDist(int r, int g, int b, byte kr, byte kg, byte kb)
    {
        float dr = r - kr;
        float dg = g - kg;
        float db = b - kb;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static float ComputeChromaDist(int r, int g, int b, byte kr, byte kg, byte kb, bool isGreen, bool isMagenta, out float rgbDist)
    {
        rgbDist = ComputeRgbDist(r, g, b, kr, kg, kb);
        float dist = rgbDist;

        // Strengthen chroma distance for bright green/magenta fringes so dark/neutral shades are preserved
        if (isGreen)
        {
            float greenDom = g - Math.Max(r, b);
            if (greenDom > 24f && g > 80)
                dist = MathF.Min(dist, MathF.Max(0f, 160f - greenDom * 1.35f));
        }
        else if (isMagenta)
        {
            float magDom = Math.Min(r, b) - g;
            if (magDom > 24f && Math.Min(r, b) > 80)
                dist = MathF.Min(dist, MathF.Max(0f, 160f - magDom * 1.35f));
        }
        return dist;
    }

    private static void ApplyDespillPixel(
        byte[] rgba, int o,
        int r, int g, int b, int newA,
        byte kr, byte kg, byte kb,
        float dist, float tol, float fea,
        bool isGreen, bool isMagenta)
    {
        float spillWeight = Math.Clamp(1f - (dist - tol) / (fea * 2.5f), 0f, 1f);
        if (isGreen)
        {
            int cap = (r + b) / 2;
            if (g > cap)
                rgba[o + 1] = (byte)Math.Clamp((int)MathF.Round(g - (g - cap) * Math.Max(spillWeight, 0.75f)), 0, 255);
        }
        else if (isMagenta)
        {
            int cap = g;
            int minRB = Math.Min(r, b);
            if (minRB > cap)
            {
                float excess = (minRB - cap) * Math.Max(spillWeight, 0.75f);
                rgba[o + 0] = (byte)Math.Clamp((int)MathF.Round(r - excess), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((int)MathF.Round(b - excess), 0, 255);
            }
        }
        else if (newA > 0 && newA < 250)
        {
            // General matte decontamination: un-premultiply estimated key background color
            float fa = newA / 255f;
            float bg = (1f - fa) * spillWeight * 0.5f;
            rgba[o + 0] = (byte)Math.Clamp((int)MathF.Round((r - kr * bg) / MathF.Max(0.25f, 1f - bg)), 0, 255);
            rgba[o + 1] = (byte)Math.Clamp((int)MathF.Round((g - kg * bg) / MathF.Max(0.25f, 1f - bg)), 0, 255);
            rgba[o + 2] = (byte)Math.Clamp((int)MathF.Round((b - kb * bg) / MathF.Max(0.25f, 1f - bg)), 0, 255);
        }
    }

    private static void ApplyChromaKeyAndDespill(
        byte[] rgba, int w, int h,
        byte kr, byte kg, byte kb,
        float tolerance, float feather,
        bool despill, bool globalKey,
        bool isGreen, bool isMagenta)
    {
        float tol = Math.Max(1f, tolerance);
        float fea = Math.Max(1f, feather);
        float invFea = 1f / fea;
        int n = w * h;

        if (globalKey)
        {
            // Legacy global chroma key mode (--global-key)
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                int a = rgba[o + 3];
                if (a == 0) continue;
                int r = rgba[o + 0], g = rgba[o + 1], b = rgba[o + 2];

                float dist = ComputeChromaDist(r, g, b, kr, kg, kb, isGreen, isMagenta, out _);
                if (dist <= tol)
                {
                    rgba[o + 0] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
                    continue;
                }

                float newAlphaFactor = dist < tol + fea ? Math.Clamp((dist - tol) * invFea, 0f, 1f) : 1f;
                int newA = (int)MathF.Round(a * newAlphaFactor);
                rgba[o + 3] = (byte)Math.Clamp(newA, 0, 255);

                if (despill && (newA < 252 || dist < tol + fea * 2.5f))
                    ApplyDespillPixel(rgba, o, r, g, b, newA, kr, kg, kb, dist, tol, fea, isGreen, isMagenta);
            }
            return;
        }

        // Default smart keying:
        // 1. Border flood-fill BFS (strict RGB distance <= tol) removes outer background without eating interior painted hues.
        bool[] isBg = new bool[n];
        var queue = new Queue<int>();

        void TrySeedBorder(int x, int y)
        {
            int idx = y * w + x;
            if (isBg[idx]) return;
            int o = idx * 4;
            if (rgba[o + 3] == 0 || ComputeRgbDist(rgba[o + 0], rgba[o + 1], rgba[o + 2], kr, kg, kb) <= tol)
            {
                isBg[idx] = true;
                queue.Enqueue(idx);
            }
        }

        for (int x = 0; x < w; x++) { TrySeedBorder(x, 0); TrySeedBorder(x, h - 1); }
        for (int y = 1; y < h - 1; y++) { TrySeedBorder(0, y); TrySeedBorder(w - 1, y); }

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur % w;
            int cy = cur / w;

            void CheckNeighbor(int nx, int ny)
            {
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) return;
                int nIdx = ny * w + nx;
                if (isBg[nIdx]) return;
                int o = nIdx * 4;
                if (rgba[o + 3] == 0 || ComputeRgbDist(rgba[o + 0], rgba[o + 1], rgba[o + 2], kr, kg, kb) <= tol)
                {
                    isBg[nIdx] = true;
                    queue.Enqueue(nIdx);
                }
            }

            CheckNeighbor(cx - 1, cy);
            CheckNeighbor(cx + 1, cy);
            CheckNeighbor(cx, cy - 1);
            CheckNeighbor(cx, cy + 1);
        }

        // 2. Remove enclosed interior background pockets strictly within tight key RGB distance (<= tol * 0.65)
        float tightTol = tol * 0.65f;
        for (int i = 0; i < n; i++)
        {
            if (isBg[i]) continue;
            int o = i * 4;
            if (rgba[o + 3] == 0) continue;
            if (ComputeRgbDist(rgba[o + 0], rgba[o + 1], rgba[o + 2], kr, kg, kb) <= tightTol)
                isBg[i] = true;
        }

        for (int i = 0; i < n; i++)
        {
            if (!isBg[i]) continue;
            int o = i * 4;
            rgba[o + 0] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
        }

        // 3. Compute 3px edge band around keyed background (outer border + enclosed holes)
        const int edgeBandPx = 3;
        byte[] bandDist = new byte[n];
        Array.Fill(bandDist, (byte)255);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (!isBg[idx]) continue;
                bandDist[idx] = 0;
                bool touchesFg = false;
                for (int dy = -1; dy <= 1 && !touchesFg; dy++)
                {
                    int ny = y + dy;
                    if ((uint)ny >= (uint)h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if ((uint)nx >= (uint)w) continue;
                        if (!isBg[ny * w + nx]) { touchesFg = true; break; }
                    }
                }
                if (touchesFg) queue.Enqueue(idx);
            }
        }

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            byte d = bandDist[cur];
            if (d >= edgeBandPx) continue;
            int cx = cur % w;
            int cy = cur / w;
            byte nd = (byte)(d + 1);
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = cy + dy;
                if ((uint)ny >= (uint)h) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx;
                    if ((uint)nx >= (uint)w) continue;
                    int nIdx = ny * w + nx;
                    if (bandDist[nIdx] == 255)
                    {
                        bandDist[nIdx] = nd;
                        if (nd < edgeBandPx) queue.Enqueue(nIdx);
                    }
                }
            }
        }

        // 4. Apply soft feather + greenDom/magDom despill ONLY within the 3px edge band (protects interior artwork!)
        for (int i = 0; i < n; i++)
        {
            byte bd = bandDist[i];
            if (bd == 0 || bd > edgeBandPx) continue;
            int o = i * 4;
            int a = rgba[o + 3];
            if (a == 0) continue;
            int r = rgba[o + 0], g = rgba[o + 1], b = rgba[o + 2];

            float dist = ComputeChromaDist(r, g, b, kr, kg, kb, isGreen, isMagenta, out float rgbDist);
            float alphaDist = bd == 1 ? dist : rgbDist;
            if (alphaDist <= tol)
            {
                rgba[o + 0] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
                continue;
            }

            float newAlphaFactor = alphaDist < tol + fea ? Math.Clamp((alphaDist - tol) * invFea, 0f, 1f) : 1f;
            int newA = (int)MathF.Round(a * newAlphaFactor);
            rgba[o + 3] = (byte)Math.Clamp(newA, 0, 255);

            if (despill && (newA < 252 || dist < tol + fea * 2.5f))
                ApplyDespillPixel(rgba, o, r, g, b, newA, kr, kg, kb, dist, tol, fea, isGreen, isMagenta);
        }
    }

    private static void RemoveSpeckles(byte[] rgba, int w, int h)
    {
        byte[] alphaCopy = new byte[w * h];
        for (int i = 0; i < w * h; i++) alphaCopy[i] = rgba[i * 4 + 3];

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int idx = y * w + x;
                if (alphaCopy[idx] == 0) continue;
                int solidNeighbors = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (alphaCopy[(y + dy) * w + (x + dx)] > 24) solidNeighbors++;
                }
                if (solidNeighbors <= 1)
                {
                    int o = idx * 4;
                    rgba[o + 0] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
                }
            }
        }
    }

    private struct IslandInfo
    {
        public int MinX, MinY, MaxX, MaxY, Area;
        public byte[] Mask;
    }

    private static List<IslandInfo> ExtractIslands(byte[] rgba, int w, int h, int minArea)
    {
        int[] labels = new int[w * h];
        var islands = new List<IslandInfo>();
        var queue = new Queue<int>();
        int nextLabel = 1;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int start = y * w + x;
                if (labels[start] != 0 || rgba[start * 4 + 3] < 20) continue;

                int label = nextLabel++;
                labels[start] = label;
                queue.Enqueue(start);
                int minX = x, maxX = x, minY = y, maxY = y, area = 0;

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    int cx = cur % w;
                    int cy = cur / w;
                    area++;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int nIdx = ny * w + nx;
                        if (labels[nIdx] == 0 && rgba[nIdx * 4 + 3] >= 12)
                        {
                            labels[nIdx] = label;
                            queue.Enqueue(nIdx);
                        }
                    }
                }

                if (area < minArea) continue;

                int bw = maxX - minX + 1;
                int bh = maxY - minY + 1;
                byte[] mask = new byte[bw * bh];
                for (int py = minY; py <= maxY; py++)
                for (int px = minX; px <= maxX; px++)
                    if (labels[py * w + px] == label)
                        mask[(py - minY) * bw + (px - minX)] = 1;

                islands.Add(new IslandInfo { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, Area = area, Mask = mask });
            }
        }

        islands.Sort((a, b) =>
        {
            int rowA = a.MinY / 64, rowB = b.MinY / 64;
            int c = rowA.CompareTo(rowB);
            return c != 0 ? c : a.MinX.CompareTo(b.MinX);
        });
        return islands;
    }

    private static bool FindAlphaBounds(byte[] rgba, int w, int h, out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = w; minY = h; maxX = -1; maxY = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (rgba[(y * w + x) * 4 + 3] > 8)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX >= minX && maxY >= minY;
    }

    private static void CropAndPad(
        byte[] src, int sw, int sh,
        int minX, int minY, int maxX, int maxY,
        byte[]? localMask,
        int pad,
        out byte[] dst, out int dw, out int dh)
    {
        int cw = maxX - minX + 1;
        int ch = maxY - minY + 1;
        int p = Math.Max(0, pad);
        dw = cw + p * 2;
        dh = ch + p * 2;
        dst = new byte[dw * dh * 4];

        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                if (localMask != null && localMask[y * cw + x] == 0) continue;
                int so = ((minY + y) * sw + (minX + x)) * 4;
                int doff = ((y + p) * dw + (x + p)) * 4;
                dst[doff + 0] = src[so + 0];
                dst[doff + 1] = src[so + 1];
                dst[doff + 2] = src[so + 2];
                dst[doff + 3] = src[so + 3];
            }
        }
    }

    private static bool TryParseHexRgb(string s, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string hex = s.Trim().TrimStart('#');
        if (hex.Length != 6) return false;
        return byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, null, out r)
            && byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, null, out g)
            && byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, null, out b);
    }

    public static void SavePng(string path, byte[] rgba, int w, int h)
    {
        using var fs = File.Create(path);
        var writer = new ImageWriter();
        writer.WritePng(rgba, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, fs);
    }

    private static void WriteManifest(string path, List<ManifestItem> items)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"parts\": [");
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            string comma = i + 1 < items.Count ? "," : "";
            string esc = it.File.Replace("\\", "\\\\").Replace("\"", "\\\"");
            sb.AppendLine($"    {{ \"file\": \"{esc}\", \"x\": {it.SrcX}, \"y\": {it.SrcY}, \"width\": {it.Width}, \"height\": {it.Height} }}{comma}");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
