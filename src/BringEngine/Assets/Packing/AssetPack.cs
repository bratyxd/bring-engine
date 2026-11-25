using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BringEngine.Assets.Atlases;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Simulation.World;
using StbImageSharp;

namespace BringEngine.Assets.Packing;

public enum PackEntryKind : byte
{
    CompressedAtlas = 1,   // BC1/BC3/RGBA8 atlas texture + SpriteRegionData table (including 9-slice borders)
    GltfModel = 2,         // Self-contained .glb (with BC1/BC3 embedded textures), LZ4/Brotli compressed
    SdfFont = 3,           // TrueType .ttf bytes + SDF bake parameters, LZ4/Brotli compressed
    StandaloneTexture = 4, // Explicit opt-out standalone compressed BC1/BC3 texture (e.g. tiled 3D floor)
    RawBlob = 5,           // Arbitrary compressed data (JSON, shaders, audio, level tables)
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BringPackHeader
{
    public const uint MagicValue = 0x4B415042u; // "BPAK"
    public const ushort CurrentVersion = 1;

    public uint Magic;
    public ushort Version;
    public PackCompressionMode Compression;
    public byte Reserved;
    public int EntryCount;
    public int TableOffset;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackEntryHeader
{
    public ulong NameHash;
    public PackEntryKind Kind;
    public PackCompressionMode Compression;
    public ushort Flags;
    public int DataOffset;
    public int CompressedBytes;
    public int UncompressedBytes;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedAtlasPayloadHeader
{
    public int Width;
    public int Height;
    public TexturePixelFormat Format;
    public byte Srgb;
    public byte NearestFilter;
    public byte ClampEdge;
    public int TextureDataBytes;
    public int RegionCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedSpriteRegionRecord
{
    public ulong NameHash;
    public Vec4 UvRect;
    public Vec2 PixelSize;
    public Vec4 SliceBorders;
    public Vec2 Pivot;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedFontPayloadHeader
{
    public float PixelSize;
    public int AtlasSize;
    public int SdfPadding;
    public int TtfBytes;
}

public struct AssetPackBuildSummary
{
    public int AtlasPageCount;
    public int AtlasedSpriteCount;
    public int StandaloneTextureCount;
    public int GltfModelCount;
    public int FontCount;
    public int RawBlobCount;
    public long RawInputBytes;
    public long PackedOutputBytes;
}

/// <summary>
/// Unity-style Asset Bundle builder for Bring Engine (.bpack):
/// - Atlases ALL 2D/UI textures by default with 2px bleed padding & BC1/BC3 GPU hardware compression.
/// - Compresses .glb/.gltf models (inlining external .bin/textures, BC1/BC3-compressing embedded textures,
///   and LZ4/Brotli-compressing the entire model binary).
/// - Ships ONLY compressed .bpack bundles in the final game build — zero loose PNG/JPG textures!
/// </summary>
public static unsafe class AssetPackBuilder
{
    private struct SpriteMeta
    {
        public string RelativePath;
        public string Name;
        public Vec4 SliceBorders;
        public Vec2 Pivot;
        public bool OptOutAtlas;
        public bool Nearest;
        public bool ClampEdge;
        public TextureCompressionPolicy Compression;
    }

    private struct FontMeta
    {
        public string RelativePath;
        public string Name;
        public float PixelSize;
        public int AtlasSize;
        public int SdfPadding;
    }

    private struct PreparedEntry
    {
        public ulong NameHash;
        public PackEntryKind Kind;
        public PackCompressionMode Compression;
        public byte[] CompressedPayload;
        public int UncompressedBytes;
    }

    /// <summary>
    /// Packs an entire source asset directory into a single compressed .bpack archive.
    /// Reads optional "pack.json" inside rawAssetsDir for 9-slice borders, explicit standalone 3D tiled textures,
    /// font baking settings, and compression overrides. All other PNG/JPG textures are atlased & compressed by default!
    /// </summary>
    public static AssetPackBuildSummary BuildPackFromDirectory(
        string rawAssetsDir,
        string outputBpackPath,
        PackCompressionMode compression = PackCompressionMode.Brotli,
        TextureCompressionPolicy atlasCompression = TextureCompressionPolicy.Auto,
        TextureCompressionPolicy gltfTextureCompression = TextureCompressionPolicy.Auto,
        int maxAtlasSize = 2048,
        int atlasPadding = 2,
        JobScheduler* jobs = null)
    {
        if (!Directory.Exists(rawAssetsDir))
            throw new DirectoryNotFoundException($"Source asset directory not found: {rawAssetsDir}");

        var summary = new AssetPackBuildSummary();
        string fullRoot = Path.GetFullPath(rawAssetsDir);

        // Optional pack.json metadata (9-slice borders, pivots, standalone 3D texture opt-outs, font params)
        SpriteMeta[] metaOverrides = Array.Empty<SpriteMeta>();
        FontMeta[] fontOverrides = Array.Empty<FontMeta>();
        string packJsonPath = Path.Combine(fullRoot, "pack.json");
        if (File.Exists(packJsonPath))
            ParsePackMetadata(packJsonPath, ref compression, ref atlasCompression, ref gltfTextureCompression, ref maxAtlasSize, ref atlasPadding, out metaOverrides, out fontOverrides);

        string[] allFiles = Directory.GetFiles(fullRoot, "*", SearchOption.AllDirectories);
        Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);

        // Identify textures referenced externally by loose .gltf files so they get inlined into the GLB, not duplicated in 2D atlas
        var gltfExternalTextures = CollectGltfReferencedImagePaths(allFiles);

        var entries = new PreparedEntry[Math.Max(16, allFiles.Length + 8)];
        int entryCount = 0;
        void AddEntry(in PreparedEntry e)
        {
            if (entryCount >= entries.Length)
            {
                var bigger = new PreparedEntry[entries.Length * 2];
                Array.Copy(entries, bigger, entries.Length);
                entries = bigger;
            }
            entries[entryCount++] = e;
        }

        // Collect all 2D/UI images to atlas (default for ALL PNG/JPG unless explicitly opted out or glTF-owned)
        var atlasImageBuffers = new byte[allFiles.Length][];
        var atlasSources = new AtlasSourceImage[allFiles.Length];
        int atlasSourceCount = 0;

        for (int fi = 0; fi < allFiles.Length; fi++)
        {
            string file = allFiles[fi];
            string rel = NormalizeRelPath(fullRoot, file);
            if (rel.Equals("pack.json", StringComparison.OrdinalIgnoreCase)) continue;

            string ext = Path.GetExtension(file).ToLowerInvariant();
            string assetKey = StripExtension(rel);

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tga")
            {
                if (IsInArray(gltfExternalTextures, Path.GetFullPath(file))) continue;

                byte[] rawBytes = File.ReadAllBytes(file);
                summary.RawInputBytes += rawBytes.Length;
                ImageResult dec = ImageResult.FromMemory(rawBytes, ColorComponents.RedGreenBlueAlpha);

                bool hasMeta = TryFindSpriteMeta(metaOverrides, rel, assetKey, out SpriteMeta sm);
                string finalName = (hasMeta && !string.IsNullOrEmpty(sm.Name)) ? sm.Name : assetKey;

                if (hasMeta && sm.OptOutAtlas)
                {
                    // Explicit standalone repeating 3D texture (still GPU-compressed BC1/BC3 + LZ4/Brotli!)
                    TextureCompressionPolicy pol = sm.Compression != TextureCompressionPolicy.Auto ? sm.Compression : atlasCompression;
                    byte[] brtx;
                    fixed (byte* px = dec.Data)
                        brtx = TextureCompressionSystem.EncodeBrtxBytes(px, dec.Width, dec.Height, pol, srgb: true, clampEdge: sm.ClampEdge, nearest: sm.Nearest, jobs: jobs);

                    AddEntry(new PreparedEntry
                    {
                        NameHash = StringId.Hash(finalName).Value,
                        Kind = PackEntryKind.StandaloneTexture,
                        Compression = compression,
                        CompressedPayload = PackCompressionSystem.Compress(brtx, compression),
                        UncompressedBytes = brtx.Length,
                    });
                    summary.StandaloneTextureCount++;
                }
                else
                {
                    // Default path: include in compressed Sprite Atlas!
                    atlasImageBuffers[atlasSourceCount] = dec.Data;
                    atlasSources[atlasSourceCount] = new AtlasSourceImage
                    {
                        Name = StringId.Hash(finalName),
                        Width = dec.Width,
                        Height = dec.Height,
                        SliceBorders = hasMeta ? sm.SliceBorders : Vec4.Zero,
                        Pivot = (hasMeta && (sm.Pivot.X != 0f || sm.Pivot.Y != 0f)) ? sm.Pivot : new Vec2(0.5f, 0.5f),
                    };
                    atlasSourceCount++;
                }
            }
            else if (ext == ".glb" || ext == ".gltf")
            {
                summary.RawInputBytes += new FileInfo(file).Length;
                byte[] glb = PackCompressionSystem.BuildSelfContainedGlbFromFile(file, gltfTextureCompression, jobs);
                AddEntry(new PreparedEntry
                {
                    NameHash = StringId.Hash(assetKey).Value,
                    Kind = PackEntryKind.GltfModel,
                    Compression = compression,
                    CompressedPayload = PackCompressionSystem.Compress(glb, compression),
                    UncompressedBytes = glb.Length,
                });
                summary.GltfModelCount++;
            }
            else if (ext == ".ttf")
            {
                byte[] ttf = File.ReadAllBytes(file);
                summary.RawInputBytes += ttf.Length;

                float pxSize = 64f;
                int aSize = 1024;
                int sdfPad = 8;
                string fName = assetKey;
                if (TryFindFontMeta(fontOverrides, rel, assetKey, out FontMeta fm))
                {
                    if (!string.IsNullOrEmpty(fm.Name)) fName = fm.Name;
                    if (fm.PixelSize > 0f) pxSize = fm.PixelSize;
                    if (fm.AtlasSize > 0) aSize = fm.AtlasSize;
                    if (fm.SdfPadding > 0) sdfPad = fm.SdfPadding;
                }

                byte[] payload = BuildFontPayload(ttf, pxSize, aSize, sdfPad);
                AddEntry(new PreparedEntry
                {
                    NameHash = StringId.Hash(fName).Value,
                    Kind = PackEntryKind.SdfFont,
                    Compression = compression,
                    CompressedPayload = PackCompressionSystem.Compress(payload, compression),
                    UncompressedBytes = payload.Length,
                });
                summary.FontCount++;
            }
            else if (ext == ".bin")
            {
                // External glTF .bin already inlined into self-contained GLB
                continue;
            }
            else
            {
                byte[] blob = File.ReadAllBytes(file);
                summary.RawInputBytes += blob.Length;
                AddEntry(new PreparedEntry
                {
                    NameHash = StringId.Hash(rel).Value,
                    Kind = PackEntryKind.RawBlob,
                    Compression = compression,
                    CompressedPayload = PackCompressionSystem.Compress(blob, compression),
                    UncompressedBytes = blob.Length,
                });
                summary.RawBlobCount++;
            }
        }

        // Pack all collected sprites into compressed atlas page(s)
        if (atlasSourceCount > 0)
        {
            GCHandle[] pins = new GCHandle[atlasSourceCount];
            try
            {
                for (int i = 0; i < atlasSourceCount; i++)
                {
                    pins[i] = GCHandle.Alloc(atlasImageBuffers[i], GCHandleType.Pinned);
                    atlasSources[i].RgbaPixels = (byte*)pins[i].AddrOfPinnedObject();
                }

                var opts = new AtlasBuildOptions
                {
                    MaxPageSize = maxAtlasSize,
                    Padding = atlasPadding,
                    Compression = atlasCompression,
                    Srgb = 1,
                    NearestFilter = 0,
                };
                AtlasPacker.BuildPages(atlasSources.AsSpan(0, atlasSourceCount), in opts, out PackedAtlasPage[] pages, out PackedAtlasRegion[] regions, jobs);

                for (int p = 0; p < pages.Length; p++)
                {
                    byte[] atlasPayload = SerializeAtlasPagePayload(in pages[p], regions, p);
                    AddEntry(new PreparedEntry
                    {
                        NameHash = StringId.Hash($"atlas_{p}").Value,
                        Kind = PackEntryKind.CompressedAtlas,
                        Compression = compression,
                        CompressedPayload = PackCompressionSystem.Compress(atlasPayload, compression),
                        UncompressedBytes = atlasPayload.Length,
                    });
                }

                summary.AtlasPageCount = pages.Length;
                summary.AtlasedSpriteCount = atlasSourceCount;
            }
            finally
            {
                for (int i = 0; i < atlasSourceCount; i++)
                    if (pins[i].IsAllocated) pins[i].Free();
            }
        }

        // Write .bpack binary archive
        string? outDir = Path.GetDirectoryName(outputBpackPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        WritePackFile(outputBpackPath, compression, entries.AsSpan(0, entryCount));
        summary.PackedOutputBytes = new FileInfo(outputBpackPath).Length;
        return summary;
    }

    /// <summary>
    /// Guardrail check ensuring NO loose PNG/JPG/BMP/TGA textures leaked into a game's final dist/ output directory.
    /// </summary>
    public static bool VerifyNoLooseTextures(string outputDir, out string[] offendingFiles)
    {
        if (!Directory.Exists(outputDir))
        {
            offendingFiles = Array.Empty<string>();
            return true;
        }

        string[] files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
        int badCount = 0;
        for (int i = 0; i < files.Length; i++)
        {
            if (IsLooseImageExtension(Path.GetExtension(files[i])))
                badCount++;
        }

        if (badCount == 0)
        {
            offendingFiles = Array.Empty<string>();
            return true;
        }

        offendingFiles = new string[badCount];
        int k = 0;
        for (int i = 0; i < files.Length; i++)
        {
            if (IsLooseImageExtension(Path.GetExtension(files[i])))
                offendingFiles[k++] = files[i];
        }
        return false;
    }

    private static bool IsLooseImageExtension(string ext)
    {
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tga", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] SerializeAtlasPagePayload(in PackedAtlasPage page, PackedAtlasRegion[] allRegions, int pageIdx)
    {
        int regionCount = 0;
        for (int i = 0; i < allRegions.Length; i++)
            if (allRegions[i].PageIndex == pageIdx) regionCount++;

        int hdrSize = sizeof(PackedAtlasPayloadHeader);
        int recSize = sizeof(PackedSpriteRegionRecord);
        int totalBytes = hdrSize + regionCount * recSize + page.CompressedData.Length;
        byte[] buf = new byte[totalBytes];

        fixed (byte* ptr = buf)
        {
            *(PackedAtlasPayloadHeader*)ptr = new PackedAtlasPayloadHeader
            {
                Width = page.Width,
                Height = page.Height,
                Format = page.Format,
                Srgb = page.Srgb,
                NearestFilter = page.NearestFilter,
                ClampEdge = 1,
                TextureDataBytes = page.CompressedData.Length,
                RegionCount = regionCount,
            };

            PackedSpriteRegionRecord* recs = (PackedSpriteRegionRecord*)(ptr + hdrSize);
            int k = 0;
            for (int i = 0; i < allRegions.Length; i++)
            {
                if (allRegions[i].PageIndex != pageIdx) continue;
                recs[k++] = new PackedSpriteRegionRecord
                {
                    NameHash = allRegions[i].Name.Value,
                    UvRect = allRegions[i].UvRect,
                    PixelSize = allRegions[i].PixelSize,
                    SliceBorders = allRegions[i].SliceBorders,
                    Pivot = allRegions[i].Pivot,
                };
            }

            byte* texDst = ptr + hdrSize + regionCount * recSize;
            page.CompressedData.AsSpan().CopyTo(new Span<byte>(texDst, page.CompressedData.Length));
        }

        return buf;
    }

    private static byte[] BuildFontPayload(byte[] ttf, float pixelSize, int atlasSize, int sdfPadding)
    {
        int hdrSize = sizeof(PackedFontPayloadHeader);
        byte[] buf = new byte[hdrSize + ttf.Length];
        fixed (byte* ptr = buf)
        {
            *(PackedFontPayloadHeader*)ptr = new PackedFontPayloadHeader
            {
                PixelSize = pixelSize,
                AtlasSize = atlasSize,
                SdfPadding = sdfPadding,
                TtfBytes = ttf.Length,
            };
        }
        Buffer.BlockCopy(ttf, 0, buf, hdrSize, ttf.Length);
        return buf;
    }

    private static void WritePackFile(string outputPath, PackCompressionMode mode, ReadOnlySpan<PreparedEntry> entries)
    {
        int hdrSize = sizeof(BringPackHeader);
        int entryHdrSize = sizeof(PackEntryHeader);
        int dataStart = hdrSize;

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Position = dataStart;

        PackEntryHeader[] table = new PackEntryHeader[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            ref readonly PreparedEntry e = ref entries[i];
            int offset = (int)fs.Position;
            fs.Write(e.CompressedPayload, 0, e.CompressedPayload.Length);
            table[i] = new PackEntryHeader
            {
                NameHash = e.NameHash,
                Kind = e.Kind,
                Compression = e.Compression,
                DataOffset = offset,
                CompressedBytes = e.CompressedPayload.Length,
                UncompressedBytes = e.UncompressedBytes,
            };
        }

        int tableOffset = (int)fs.Position;
        byte[] tableBytes = new byte[entries.Length * entryHdrSize];
        fixed (byte* tPtr = tableBytes)
        {
            PackEntryHeader* dst = (PackEntryHeader*)tPtr;
            for (int i = 0; i < entries.Length; i++) dst[i] = table[i];
        }
        fs.Write(tableBytes, 0, tableBytes.Length);

        var fileHdr = new BringPackHeader
        {
            Magic = BringPackHeader.MagicValue,
            Version = BringPackHeader.CurrentVersion,
            Compression = mode,
            EntryCount = entries.Length,
            TableOffset = tableOffset,
        };
        byte[] hdrBytes = new byte[hdrSize];
        fixed (byte* hPtr = hdrBytes)
            *(BringPackHeader*)hPtr = fileHdr;
        fs.Position = 0;
        fs.Write(hdrBytes, 0, hdrBytes.Length);
    }

    private static string[] CollectGltfReferencedImagePaths(string[] allFiles)
    {
        string[] found = Array.Empty<string>();
        int count = 0;
        for (int i = 0; i < allFiles.Length; i++)
        {
            if (!allFiles[i].EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) continue;
            string dir = Path.GetDirectoryName(allFiles[i]) ?? ".";
            using var doc = JsonDocument.Parse(StripUtf8Bom(File.ReadAllBytes(allFiles[i])));
            if (!doc.RootElement.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement img in imgs.EnumerateArray())
            {
                if (!img.TryGetProperty("uri", out var uriEl)) continue;
                string uri = uriEl.GetString() ?? "";
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(uri)) continue;
                string full = Path.GetFullPath(Path.Combine(dir, uri));
                if (count >= found.Length)
                {
                    var next = new string[Math.Max(8, found.Length * 2)];
                    Array.Copy(found, next, count);
                    found = next;
                }
                found[count++] = full;
            }
        }
        if (count == found.Length) return found;
        var trimmed = new string[count];
        Array.Copy(found, trimmed, count);
        return trimmed;
    }

    private static bool IsInArray(string[] arr, string target)
    {
        for (int i = 0; i < arr.Length; i++)
            if (arr[i].Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void ParsePackMetadata(
        string packJsonPath,
        ref PackCompressionMode compression,
        ref TextureCompressionPolicy atlasComp,
        ref TextureCompressionPolicy gltfTexComp,
        ref int maxAtlasSize,
        ref int atlasPadding,
        out SpriteMeta[] sprites,
        out FontMeta[] fonts)
    {
        sprites = Array.Empty<SpriteMeta>();
        fonts = Array.Empty<FontMeta>();

        using var doc = JsonDocument.Parse(StripUtf8Bom(File.ReadAllBytes(packJsonPath)));
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("compression", out var cEl))
            compression = ParsePackCompression(cEl.GetString(), compression);
        if (root.TryGetProperty("atlas_compression", out var acEl))
            atlasComp = ParseTexPolicy(acEl.GetString(), atlasComp);
        if (root.TryGetProperty("gltf_texture_compression", out var gcEl))
            gltfTexComp = ParseTexPolicy(gcEl.GetString(), gltfTexComp);
        if (root.TryGetProperty("max_atlas_size", out var msEl))
            maxAtlasSize = msEl.GetInt32();
        if (root.TryGetProperty("atlas_padding", out var apEl))
            atlasPadding = apEl.GetInt32();

        if (root.TryGetProperty("sprites", out var spEl) && spEl.ValueKind == JsonValueKind.Array)
        {
            sprites = new SpriteMeta[spEl.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in spEl.EnumerateArray())
            {
                var sm = new SpriteMeta
                {
                    RelativePath = item.TryGetProperty("path", out var pEl) ? NormalizeSlash(pEl.GetString() ?? "") : "",
                    Name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "",
                    OptOutAtlas = item.TryGetProperty("atlas", out var aEl) && !aEl.GetBoolean(),
                    Nearest = item.TryGetProperty("nearest", out var nrEl) && nrEl.GetBoolean(),
                    ClampEdge = !item.TryGetProperty("wrap", out var wEl) || !wEl.GetBoolean(),
                    Compression = item.TryGetProperty("compression", out var tcEl) ? ParseTexPolicy(tcEl.GetString(), TextureCompressionPolicy.Auto) : TextureCompressionPolicy.Auto,
                    Pivot = new Vec2(0.5f, 0.5f),
                };
                if (item.TryGetProperty("slice", out var slEl) && slEl.ValueKind == JsonValueKind.Array && slEl.GetArrayLength() == 4)
                {
                    sm.SliceBorders = new Vec4(slEl[0].GetSingle(), slEl[1].GetSingle(), slEl[2].GetSingle(), slEl[3].GetSingle());
                }
                if (item.TryGetProperty("pivot", out var pvEl) && pvEl.ValueKind == JsonValueKind.Array && pvEl.GetArrayLength() == 2)
                {
                    sm.Pivot = new Vec2(pvEl[0].GetSingle(), pvEl[1].GetSingle());
                }
                sprites[i++] = sm;
            }
        }

        if (root.TryGetProperty("fonts", out var fEl) && fEl.ValueKind == JsonValueKind.Array)
        {
            fonts = new FontMeta[fEl.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in fEl.EnumerateArray())
            {
                fonts[i++] = new FontMeta
                {
                    RelativePath = item.TryGetProperty("path", out var pEl) ? NormalizeSlash(pEl.GetString() ?? "") : "",
                    Name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "",
                    PixelSize = item.TryGetProperty("pixel_size", out var psEl) ? psEl.GetSingle() : 64f,
                    AtlasSize = item.TryGetProperty("atlas_size", out var asEl) ? asEl.GetInt32() : 1024,
                    SdfPadding = item.TryGetProperty("sdf_padding", out var sp2El) ? sp2El.GetInt32() : 8,
                };
            }
        }
    }

    private static bool TryFindSpriteMeta(SpriteMeta[] list, string rel, string key, out SpriteMeta meta)
    {
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase) ||
                list[i].RelativePath.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                list[i].Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                meta = list[i];
                return true;
            }
        }
        meta = default;
        return false;
    }

    private static bool TryFindFontMeta(FontMeta[] list, string rel, string key, out FontMeta meta)
    {
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i].RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase) ||
                list[i].Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                meta = list[i];
                return true;
            }
        }
        meta = default;
        return false;
    }

    private static PackCompressionMode ParsePackCompression(string? s, PackCompressionMode fallback) => s?.ToLowerInvariant() switch
    {
        "none" => PackCompressionMode.None,
        "lz4" => PackCompressionMode.Lz4,
        "brotli" => PackCompressionMode.Brotli,
        _ => fallback,
    };

    private static TextureCompressionPolicy ParseTexPolicy(string? s, TextureCompressionPolicy fallback) => s?.ToLowerInvariant() switch
    {
        "auto" => TextureCompressionPolicy.Auto,
        "bc1" or "dxt1" => TextureCompressionPolicy.Bc1,
        "bc3" or "dxt5" => TextureCompressionPolicy.Bc3,
        "none" or "rgba8" => TextureCompressionPolicy.None,
        _ => fallback,
    };

    private static ReadOnlyMemory<byte> StripUtf8Bom(byte[] data)
        => (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            ? new ReadOnlyMemory<byte>(data, 3, data.Length - 3)
            : new ReadOnlyMemory<byte>(data);

    private static string NormalizeRelPath(string root, string full)
        => NormalizeSlash(Path.GetRelativePath(root, full));

    private static string NormalizeSlash(string p) => p.Replace('\\', '/');

    private static string StripExtension(string rel)
    {
        int dot = rel.LastIndexOf('.');
        int slash = rel.LastIndexOf('/');
        return dot > slash ? rel.Substring(0, dot) : rel;
    }
}

/// <summary>
/// Runtime zero-reflection NativeAOT loader for compressed .bpack bundles.
/// Decompresses directly into ScratchArena / BundleArena, registers BC1/BC3 atlas textures + SpriteRegionData
/// (including 9-slice borders), glTF models, and SDF fonts with zero loose texture files.
/// </summary>
public static unsafe class AssetPackLoader
{
    public static int LoadIntoBundle(
        string bpackPath,
        ref LinearArena bundleArena,
        ref LinearArena scratchArena,
        ref EngineWorld world,
        BundleId bundle)
    {
        byte[] packBytes = File.ReadAllBytes(bpackPath);
        return LoadFromMemoryIntoBundle(packBytes, ref bundleArena, ref scratchArena, ref world, bundle);
    }

    public static int LoadFromMemoryIntoBundle(
        ReadOnlySpan<byte> packBytes,
        ref LinearArena bundleArena,
        ref LinearArena scratchArena,
        ref EngineWorld world,
        BundleId bundle)
    {
        if (packBytes.Length < sizeof(BringPackHeader))
            throw new InvalidDataException("Invalid .bpack file (truncated header).");

        BringPackHeader hdr = MemoryMarshal.Read<BringPackHeader>(packBytes);
        if (hdr.Magic != BringPackHeader.MagicValue || hdr.Version != BringPackHeader.CurrentVersion)
            throw new InvalidDataException($"Unsupported .bpack header (magic=0x{hdr.Magic:X8}, version={hdr.Version}).");

        int entrySize = sizeof(PackEntryHeader);
        if (hdr.TableOffset < sizeof(BringPackHeader) || hdr.TableOffset + hdr.EntryCount * entrySize > packBytes.Length)
            throw new InvalidDataException("Invalid .bpack entry table.");

        int loaded = 0;
        for (int i = 0; i < hdr.EntryCount; i++)
        {
            PackEntryHeader e = MemoryMarshal.Read<PackEntryHeader>(packBytes.Slice(hdr.TableOffset + i * entrySize, entrySize));
            if (e.DataOffset + e.CompressedBytes > packBytes.Length || e.UncompressedBytes < 0)
                throw new InvalidDataException($"Corrupt .bpack entry {i}.");

            ArenaMarker mark = scratchArena.SaveMarker();
            try
            {
                byte* rawPtr = scratchArena.AllocUninitialized<byte>(Math.Max(1, e.UncompressedBytes), 64);
                var rawSpan = new Span<byte>(rawPtr, e.UncompressedBytes);
                if (e.UncompressedBytes > 0)
                    PackCompressionSystem.Decompress(packBytes.Slice(e.DataOffset, e.CompressedBytes), e.Compression, rawSpan);

                StringId nameId = new StringId(e.NameHash);
                switch (e.Kind)
                {
                    case PackEntryKind.CompressedAtlas:
                        LoadAtlasEntry(rawSpan, nameId, ref bundleArena, ref world.Assets, bundle);
                        loaded++;
                        break;

                    case PackEntryKind.GltfModel:
                        GltfLoader.LoadFromMemory(rawSpan, null, ref bundleArena, ref scratchArena, ref world.Assets, ref world.Animations, bundle, nameId);
                        loaded++;
                        break;

                    case PackEntryKind.SdfFont:
                        LoadFontEntry(rawSpan, nameId, ref bundleArena, ref world.Assets, bundle);
                        loaded++;
                        break;

                    case PackEntryKind.StandaloneTexture:
                        TextureLoader.LoadBrtxFromMemory(rawSpan, ref bundleArena, ref world.Assets, bundle, nameId);
                        loaded++;
                        break;
                }
            }
            finally
            {
                scratchArena.Rollback(mark);
            }
        }
        return loaded;
    }

    /// <summary>
    /// Reads a compressed RawBlob entry (e.g., tuning.json, level data, shader source) from a .bpack file into dstArena.
    /// </summary>
    public static bool TryReadRawBlob(string bpackPath, StringId name, ref LinearArena dstArena, out NativeSlice<byte> outBytes)
    {
        byte[] packBytes = File.ReadAllBytes(bpackPath);
        return TryReadRawBlobFromMemory(packBytes, name, ref dstArena, out outBytes);
    }

    public static bool TryReadRawBlobFromMemory(ReadOnlySpan<byte> packBytes, StringId name, ref LinearArena dstArena, out NativeSlice<byte> outBytes)
    {
        outBytes = default;
        if (packBytes.Length < sizeof(BringPackHeader)) return false;
        BringPackHeader hdr = MemoryMarshal.Read<BringPackHeader>(packBytes);
        if (hdr.Magic != BringPackHeader.MagicValue) return false;

        int entrySize = sizeof(PackEntryHeader);
        for (int i = 0; i < hdr.EntryCount; i++)
        {
            PackEntryHeader e = MemoryMarshal.Read<PackEntryHeader>(packBytes.Slice(hdr.TableOffset + i * entrySize, entrySize));
            if (e.Kind == PackEntryKind.RawBlob && e.NameHash == name.Value)
            {
                byte* dst = dstArena.AllocUninitialized<byte>(Math.Max(1, e.UncompressedBytes), 16);
                var span = new Span<byte>(dst, e.UncompressedBytes);
                PackCompressionSystem.Decompress(packBytes.Slice(e.DataOffset, e.CompressedBytes), e.Compression, span);
                outBytes = new NativeSlice<byte>(dst, e.UncompressedBytes);
                return true;
            }
        }
        return false;
    }

    private static void LoadAtlasEntry(
        ReadOnlySpan<byte> payload,
        StringId atlasName,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle)
    {
        int hdrSize = sizeof(PackedAtlasPayloadHeader);
        int recSize = sizeof(PackedSpriteRegionRecord);
        if (payload.Length < hdrSize) throw new InvalidDataException("Truncated atlas payload.");

        PackedAtlasPayloadHeader ah = MemoryMarshal.Read<PackedAtlasPayloadHeader>(payload);
        int texOff = hdrSize + ah.RegionCount * recSize;
        if (texOff + ah.TextureDataBytes > payload.Length)
            throw new InvalidDataException("Truncated atlas texture data.");

        byte* texDst = bundleArena.AllocUninitialized<byte>(ah.TextureDataBytes, 64);
        payload.Slice(texOff, ah.TextureDataBytes).CopyTo(new Span<byte>(texDst, ah.TextureDataBytes));

        TextureHandle atlasTex = TextureLoader.RegisterCompressed(
            texDst, ah.TextureDataBytes, ah.Width, ah.Height, ah.Format,
            ref assets, bundle, atlasName,
            srgb: ah.Srgb != 0, clampEdge: ah.ClampEdge != 0, nearest: ah.NearestFilter != 0);

        for (int r = 0; r < ah.RegionCount; r++)
        {
            PackedSpriteRegionRecord rec = MemoryMarshal.Read<PackedSpriteRegionRecord>(payload.Slice(hdrSize + r * recSize, recSize));
            AssetSystem.RegisterSpriteRegion(ref assets, new SpriteRegionData
            {
                Name = new StringId(rec.NameHash),
                OwnerBundle = bundle,
                AtlasTexture = atlasTex,
                UvRect = rec.UvRect,
                PixelSize = rec.PixelSize,
                SliceBorders = rec.SliceBorders,
                Pivot = rec.Pivot,
            });
        }
    }

    private static void LoadFontEntry(
        ReadOnlySpan<byte> payload,
        StringId fontName,
        ref LinearArena bundleArena,
        ref AssetRegistry assets,
        BundleId bundle)
    {
        int hdrSize = sizeof(PackedFontPayloadHeader);
        if (payload.Length < hdrSize) throw new InvalidDataException("Truncated font payload.");
        PackedFontPayloadHeader fh = MemoryMarshal.Read<PackedFontPayloadHeader>(payload);
        if (hdrSize + fh.TtfBytes > payload.Length) throw new InvalidDataException("Truncated font TTF bytes.");

        byte[] ttf = payload.Slice(hdrSize, fh.TtfBytes).ToArray();
        SdfFontLoader.BakeSdfFromMemory(ttf, ref bundleArena, ref assets, bundle, fontName, fh.PixelSize, fh.AtlasSize, fh.SdfPadding);
    }
}
