using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BringEngine.Core.Threading;
using StbImageSharp;

namespace BringEngine.Assets.Compression;

/// <summary>
/// Unity-style asset bundle & GLB compression modes:
/// - Lz4:    Fast runtime decompression (matches Unity ChunkBasedCompression / LZ4).
/// - Brotli: High-ratio compression for minimal build size (matches Unity LZMA-class distribution compression).
/// </summary>
public enum PackCompressionMode : byte
{
    None = 0,
    Lz4 = 1,
    Brotli = 2,
}

/// <summary>
/// Header for Bring Compressed GLB ("BGLB") files — transparently decoded by GltfLoader!
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BglbHeader
{
    public const uint MagicValue = 0x424C4742u; // "BGLB"
    public const byte CurrentVersion = 1;

    public uint Magic;
    public byte Version;
    public PackCompressionMode Compression;
    public ushort Reserved;
    public int UncompressedBytes;
    public int CompressedBytes;
}

/// <summary>
/// Pure NativeAOT, zero-reflection LZ4 & Brotli compression plus Unity-style GLB/glTF asset compression
/// (re-encoding embedded PNG/JPEG textures inside .glb to hardware BC1/BC3 BRTX blocks + LZ4/Brotli stream compression).
/// </summary>
public static unsafe class PackCompressionSystem
{
    private const int Lz4HashLog = 12;
    private const int Lz4HashSize = 1 << Lz4HashLog;
    private const int Lz4MinMatch = 4;
    private const int Lz4MaxDistance = 65535;
    private const uint GlbMagic = 0x46546C67u;
    private const uint ChunkJson = 0x4E4F534Au;
    private const uint ChunkBin = 0x004E4942u;

    public static byte[] Compress(ReadOnlySpan<byte> src, PackCompressionMode mode)
    {
        if (src.IsEmpty || mode == PackCompressionMode.None)
            return src.ToArray();

        if (mode == PackCompressionMode.Brotli)
        {
            int maxLen = BrotliEncoder.GetMaxCompressedLength(src.Length);
            byte[] buf = new byte[maxLen];
            if (!BrotliEncoder.TryCompress(src, buf, out int written, quality: 6, window: 22))
                throw new InvalidOperationException("Brotli compression failed.");
            byte[] trimmed = new byte[written];
            Buffer.BlockCopy(buf, 0, trimmed, 0, written);
            return trimmed;
        }

        // LZ4 block compression
        int bound = src.Length + (src.Length / 255) + 32;
        byte[] lz4Buf = new byte[bound];
        int lz4Written = CompressLz4Block(src, lz4Buf);
        byte[] lz4Trimmed = new byte[lz4Written];
        Buffer.BlockCopy(lz4Buf, 0, lz4Trimmed, 0, lz4Written);
        return lz4Trimmed;
    }

    public static int Decompress(ReadOnlySpan<byte> src, PackCompressionMode mode, Span<byte> dst)
    {
        if (mode == PackCompressionMode.None)
        {
            src.CopyTo(dst);
            return src.Length;
        }

        if (mode == PackCompressionMode.Brotli)
        {
            if (!BrotliDecoder.TryDecompress(src, dst, out int written))
                throw new InvalidDataException("Brotli decompression failed.");
            return written;
        }

        return DecompressLz4Block(src, dst);
    }

    /// <summary>
    /// Pure NativeAOT zero-GC LZ4 standard block compressor.
    /// </summary>
    public static int CompressLz4Block(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int srcLen = src.Length;
        if (srcLen == 0) return 0;

        int* hashTable = stackalloc int[Lz4HashSize];
        for (int i = 0; i < Lz4HashSize; i++) hashTable[i] = -1;

        fixed (byte* sBase = src)
        fixed (byte* dBase = dst)
        {
            int ip = 0;
            int anchor = 0;
            int op = 0;
            int mflimit = srcLen - 12;
            int matchLimit = srcLen - 5;

            if (srcLen >= 13)
            {
                while (ip <= mflimit)
                {
                    uint seq = *(uint*)(sBase + ip);
                    int h = (int)((seq * 2654435761u) >> (32 - Lz4HashLog));
                    int refPos = hashTable[h];
                    hashTable[h] = ip;

                    if (refPos >= 0 && (ip - refPos) <= Lz4MaxDistance && *(uint*)(sBase + refPos) == seq)
                    {
                        int litLen = ip - anchor;
                        int tokenPos = op++;
                        int tokenLit = litLen >= 15 ? 15 : litLen;
                        if (litLen >= 15)
                        {
                            int rem = litLen - 15;
                            while (rem >= 255) { dBase[op++] = 255; rem -= 255; }
                            dBase[op++] = (byte)rem;
                        }
                        if (litLen > 0)
                        {
                            Buffer.MemoryCopy(sBase + anchor, dBase + op, dst.Length - op, litLen);
                            op += litLen;
                        }

                        // Write 16-bit LE offset
                        int offset = ip - refPos;
                        dBase[op++] = (byte)(offset & 0xFF);
                        dBase[op++] = (byte)((offset >> 8) & 0xFF);

                        // Measure full match length
                        int matchLen = Lz4MinMatch;
                        while (ip + matchLen < matchLimit && sBase[refPos + matchLen] == sBase[ip + matchLen])
                            matchLen++;

                        int mlCode = matchLen - Lz4MinMatch;
                        int tokenMatch = mlCode >= 15 ? 15 : mlCode;
                        dBase[tokenPos] = (byte)((tokenLit << 4) | tokenMatch);
                        if (mlCode >= 15)
                        {
                            int rem = mlCode - 15;
                            while (rem >= 255) { dBase[op++] = 255; rem -= 255; }
                            dBase[op++] = (byte)rem;
                        }

                        ip += matchLen;
                        anchor = ip;
                    }
                    else
                    {
                        ip++;
                    }
                }
            }

            // Emit final literals
            int lastLit = srcLen - anchor;
            if (lastLit >= 15)
            {
                dBase[op++] = 0xF0;
                int rem = lastLit - 15;
                while (rem >= 255) { dBase[op++] = 255; rem -= 255; }
                dBase[op++] = (byte)rem;
            }
            else
            {
                dBase[op++] = (byte)(lastLit << 4);
            }

            if (lastLit > 0)
            {
                Buffer.MemoryCopy(sBase + anchor, dBase + op, dst.Length - op, lastLit);
                op += lastLit;
            }
            return op;
        }
    }

    /// <summary>
    /// Pure NativeAOT zero-GC LZ4 standard block decompressor.
    /// </summary>
    public static int DecompressLz4Block(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int srcLen = src.Length;
        int dstLen = dst.Length;
        if (srcLen == 0) return 0;

        fixed (byte* sBase = src)
        fixed (byte* dBase = dst)
        {
            int ip = 0;
            int op = 0;

            while (ip < srcLen)
            {
                byte token = sBase[ip++];
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    byte b;
                    do { b = sBase[ip++]; litLen += b; } while (b == 255 && ip < srcLen);
                }
                if (ip + litLen > srcLen || op + litLen > dstLen)
                    throw new InvalidDataException("Corrupt LZ4 literal stream.");
                if (litLen > 0)
                {
                    Buffer.MemoryCopy(sBase + ip, dBase + op, dstLen - op, litLen);
                    ip += litLen;
                    op += litLen;
                }
                if (ip >= srcLen) break;

                if (ip + 2 > srcLen)
                    throw new InvalidDataException("Corrupt LZ4 offset.");
                int offset = sBase[ip] | (sBase[ip + 1] << 8);
                ip += 2;
                if (offset <= 0 || offset > op)
                    throw new InvalidDataException("Corrupt LZ4 back-reference offset.");

                int matchLen = (token & 0x0F) + Lz4MinMatch;
                if ((token & 0x0F) == 15)
                {
                    byte b;
                    do { b = sBase[ip++]; matchLen += b; } while (b == 255 && ip < srcLen);
                }
                if (op + matchLen > dstLen)
                    throw new InvalidDataException("Corrupt LZ4 match exceeds output capacity.");

                int refPos = op - offset;
                for (int k = 0; k < matchLen; k++)
                    dBase[op++] = dBase[refPos + k];
            }
            return op;
        }
    }

    /// <summary>
    /// Wraps a raw .glb (optionally compressing embedded PNG/JPEG textures into BC1/BC3 BRTX first!)
    /// inside a Bring Compressed GLB ("BGLB") container readable transparently by GltfLoader.
    /// </summary>
    public static byte[] CompressGlb(
        ReadOnlySpan<byte> glbBytes,
        PackCompressionMode mode = PackCompressionMode.Brotli,
        TextureCompressionPolicy embeddedTexturePolicy = TextureCompressionPolicy.Auto,
        JobScheduler* jobs = null)
    {
        ReadOnlySpan<byte> prepared = glbBytes;
        byte[]? rewritten = null;
        if (embeddedTexturePolicy != TextureCompressionPolicy.None &&
            glbBytes.Length >= 12 &&
            MemoryMarshal.Read<uint>(glbBytes) == GlbMagic)
        {
            rewritten = RecompressGlbEmbeddedTextures(glbBytes, embeddedTexturePolicy, jobs);
            prepared = rewritten;
        }

        byte[] compressed = Compress(prepared, mode);
        int hdrSize = sizeof(BglbHeader);
        byte[] output = new byte[hdrSize + compressed.Length];

        var hdr = new BglbHeader
        {
            Magic = BglbHeader.MagicValue,
            Version = BglbHeader.CurrentVersion,
            Compression = mode,
            UncompressedBytes = prepared.Length,
            CompressedBytes = compressed.Length,
        };

        fixed (byte* p = output)
            *(BglbHeader*)p = hdr;
        Buffer.BlockCopy(compressed, 0, output, hdrSize, compressed.Length);
        return output;
    }

    /// <summary>
    /// Loads a .glb or .gltf from disk (inlining external .bin and image URIs into a self-contained GLB,
    /// optionally BC1/BC3-compressing embedded textures into BRTX so no loose textures remain!).
    /// </summary>
    public static byte[] BuildSelfContainedGlbFromFile(
        string gltfOrGlbPath,
        TextureCompressionPolicy embeddedTexturePolicy = TextureCompressionPolicy.Auto,
        JobScheduler* jobs = null)
    {
        byte[] raw = File.ReadAllBytes(gltfOrGlbPath);
        if (raw.Length >= 12 && MemoryMarshal.Read<uint>(raw) == BglbHeader.MagicValue)
            return raw;

        if (raw.Length >= 12 && MemoryMarshal.Read<uint>(raw) == GlbMagic)
        {
            return embeddedTexturePolicy == TextureCompressionPolicy.None
                ? raw
                : RecompressGlbEmbeddedTextures(raw, embeddedTexturePolicy, jobs);
        }

        // Convert loose .gltf + external .bin / images into a single self-contained .glb
        string baseDir = Path.GetDirectoryName(gltfOrGlbPath) ?? ".";
        return ConvertLooseGltfToGlb(raw, baseDir, embeddedTexturePolicy, jobs);
    }

    /// <summary>
    /// Rewrites embedded PNG/JPEG bufferViews in a GLB into GPU-ready BC1/BC3 BRTX blocks,
    /// preserving all mesh, skin, animation, and accessor bufferView indices intact.
    /// </summary>
    public static byte[] RecompressGlbEmbeddedTextures(
        ReadOnlySpan<byte> glbBytes,
        TextureCompressionPolicy policy,
        JobScheduler* jobs = null)
    {
        if (glbBytes.Length < 12 || MemoryMarshal.Read<uint>(glbBytes) != GlbMagic)
            return glbBytes.ToArray();

        ReadOnlySpan<byte> jsonSpan = ReadOnlySpan<byte>.Empty;
        ReadOnlySpan<byte> binSpan = ReadOnlySpan<byte>.Empty;
        int off = 12;
        while (off + 8 <= glbBytes.Length)
        {
            uint len = MemoryMarshal.Read<uint>(glbBytes.Slice(off));
            uint type = MemoryMarshal.Read<uint>(glbBytes.Slice(off + 4));
            off += 8;
            if (off + (int)len > glbBytes.Length) break;
            if (type == ChunkJson) jsonSpan = glbBytes.Slice(off, (int)len);
            else if (type == ChunkBin) binSpan = glbBytes.Slice(off, (int)len);
            off += (int)len;
        }
        if (jsonSpan.IsEmpty || binSpan.IsEmpty) return glbBytes.ToArray();

        // Parse image bufferView indices via JsonDocument (zero reflection, NativeAOT safe)
        ReadOnlyMemory<byte> jsonMem = StripUtf8Bom(jsonSpan.ToArray());
        using var doc = JsonDocument.Parse(jsonMem);
        if (!doc.RootElement.TryGetProperty("images", out JsonElement imagesEl) ||
            imagesEl.ValueKind != JsonValueKind.Array || imagesEl.GetArrayLength() == 0)
            return glbBytes.ToArray();

        if (!doc.RootElement.TryGetProperty("bufferViews", out JsonElement viewsEl) ||
            viewsEl.ValueKind != JsonValueKind.Array)
            return glbBytes.ToArray();

        int viewCount = viewsEl.GetArrayLength();
        bool[] isImageView = new bool[viewCount];
        bool anyImageView = false;

        foreach (JsonElement img in imagesEl.EnumerateArray())
        {
            if (img.TryGetProperty("bufferView", out JsonElement bvEl))
            {
                int bv = bvEl.GetInt32();
                if ((uint)bv < (uint)viewCount) { isImageView[bv] = true; anyImageView = true; }
            }
        }
        if (!anyImageView) return glbBytes.ToArray();

        // Rebuild tightly packed 4-byte-aligned BIN chunk, swapping image views to BRTX BC1/BC3
        using var newBin = new MemoryStream(binSpan.Length);
        int[] newOffsets = new int[viewCount];
        int[] newLengths = new int[viewCount];

        int vIdx = 0;
        foreach (JsonElement bv in viewsEl.EnumerateArray())
        {
            int oldOff = bv.TryGetProperty("byteOffset", out var oEl) ? oEl.GetInt32() : 0;
            int oldLen = bv.GetProperty("byteLength").GetInt32();
            int curPos = (int)newBin.Position;
            int pad = (4 - (curPos & 3)) & 3;
            for (int p = 0; p < pad; p++) newBin.WriteByte(0);
            newOffsets[vIdx] = (int)newBin.Position;

            if (isImageView[vIdx] && oldOff + oldLen <= binSpan.Length)
            {
                ReadOnlySpan<byte> imgRaw = binSpan.Slice(oldOff, oldLen);
                if (imgRaw.Length >= sizeof(BrtxHeader) && MemoryMarshal.Read<uint>(imgRaw) == BrtxHeader.MagicValue)
                {
                    newBin.Write(imgRaw);
                    newLengths[vIdx] = imgRaw.Length;
                }
                else
                {
                    ImageResult decoded = ImageResult.FromMemory(imgRaw.ToArray(), ColorComponents.RedGreenBlueAlpha);
                    byte[] brtx;
                    fixed (byte* px = decoded.Data)
                        brtx = TextureCompressionSystem.EncodeBrtxBytes(px, decoded.Width, decoded.Height, policy, srgb: true, clampEdge: false, nearest: false, jobs: jobs);
                    newBin.Write(brtx, 0, brtx.Length);
                    newLengths[vIdx] = brtx.Length;
                }
            }
            else if (oldOff + oldLen <= binSpan.Length)
            {
                newBin.Write(binSpan.Slice(oldOff, oldLen));
                newLengths[vIdx] = oldLen;
            }
            vIdx++;
        }

        while ((newBin.Length & 3) != 0) newBin.WriteByte(0);
        byte[] binOut = newBin.ToArray();

        // Rewrite JSON preserving all top-level keys while updating buffers[0].byteLength & bufferViews offsets/lengths
        using var jsonMs = new MemoryStream(jsonSpan.Length + 256);
        using (var writer = new Utf8JsonWriter(jsonMs))
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("buffers"))
                {
                    writer.WritePropertyName("buffers");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteNumber("byteLength", binOut.Length);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                else if (prop.NameEquals("bufferViews"))
                {
                    writer.WritePropertyName("bufferViews");
                    writer.WriteStartArray();
                    int idx = 0;
                    foreach (JsonElement bv in prop.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("buffer", 0);
                        writer.WriteNumber("byteOffset", newOffsets[idx]);
                        writer.WriteNumber("byteLength", newLengths[idx]);
                        if (bv.TryGetProperty("byteStride", out var strideEl))
                            writer.WriteNumber("byteStride", strideEl.GetInt32());
                        if (bv.TryGetProperty("target", out var targetEl))
                            writer.WriteNumber("target", targetEl.GetInt32());
                        writer.WriteEndObject();
                        idx++;
                    }
                    writer.WriteEndArray();
                }
                else if (prop.NameEquals("images"))
                {
                    writer.WritePropertyName("images");
                    writer.WriteStartArray();
                    foreach (JsonElement img in prop.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        if (img.TryGetProperty("bufferView", out var bvEl))
                        {
                            writer.WriteNumber("bufferView", bvEl.GetInt32());
                            writer.WriteString("mimeType", "application/x-bring-brtx");
                        }
                        else
                        {
                            foreach (JsonProperty ip in img.EnumerateObject()) ip.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return AssembleGlb(jsonMs.ToArray(), binOut);
    }

    private static byte[] ConvertLooseGltfToGlb(byte[] gltfJsonBytes, string baseDir, TextureCompressionPolicy texPolicy, JobScheduler* jobs)
    {
        using var doc = JsonDocument.Parse(StripUtf8Bom(gltfJsonBytes));
        using var binMs = new MemoryStream();

        // Collect all external/data buffers concatenated into buffer 0
        int[] bufBaseOffsets = Array.Empty<int>();
        if (doc.RootElement.TryGetProperty("buffers", out var bufsEl) && bufsEl.ValueKind == JsonValueKind.Array)
        {
            bufBaseOffsets = new int[bufsEl.GetArrayLength()];
            int bi = 0;
            foreach (JsonElement b in bufsEl.EnumerateArray())
            {
                while ((binMs.Length & 3) != 0) binMs.WriteByte(0);
                bufBaseOffsets[bi] = (int)binMs.Position;
                if (b.TryGetProperty("uri", out var uriEl))
                {
                    string uri = uriEl.GetString() ?? "";
                    byte[] data = ResolveUriBytes(uri, baseDir);
                    binMs.Write(data, 0, data.Length);
                }
                bi++;
            }
        }

        int existingViews = doc.RootElement.TryGetProperty("bufferViews", out var bvEl) && bvEl.ValueKind == JsonValueKind.Array
            ? bvEl.GetArrayLength() : 0;

        // Inline any URI images as new bufferViews (already BC1/BC3 compressed if policy != None!)
        int extraImageViews = 0;
        int imgCount = doc.RootElement.TryGetProperty("images", out var imgsEl) && imgsEl.ValueKind == JsonValueKind.Array
            ? imgsEl.GetArrayLength() : 0;
        int[] imgAssignedView = new int[imgCount];
        int[] extraViewOff = new int[imgCount];
        int[] extraViewLen = new int[imgCount];

        if (imgCount > 0)
        {
            int ii = 0;
            foreach (JsonElement img in imgsEl.EnumerateArray())
            {
                if (img.TryGetProperty("bufferView", out var existBv))
                {
                    imgAssignedView[ii] = existBv.GetInt32();
                }
                else if (img.TryGetProperty("uri", out var uriEl))
                {
                    byte[] rawImg = ResolveUriBytes(uriEl.GetString() ?? "", baseDir);
                    byte[] payload = rawImg;
                    if (texPolicy != TextureCompressionPolicy.None && rawImg.Length > 0)
                    {
                        ImageResult dec = ImageResult.FromMemory(rawImg, ColorComponents.RedGreenBlueAlpha);
                        fixed (byte* px = dec.Data)
                            payload = TextureCompressionSystem.EncodeBrtxBytes(px, dec.Width, dec.Height, texPolicy, srgb: true, clampEdge: false, nearest: false, jobs: jobs);
                    }
                    while ((binMs.Length & 3) != 0) binMs.WriteByte(0);
                    extraViewOff[extraImageViews] = (int)binMs.Position;
                    extraViewLen[extraImageViews] = payload.Length;
                    binMs.Write(payload, 0, payload.Length);
                    imgAssignedView[ii] = existingViews + extraImageViews;
                    extraImageViews++;
                }
                else
                {
                    imgAssignedView[ii] = -1;
                }
                ii++;
            }
        }

        while ((binMs.Length & 3) != 0) binMs.WriteByte(0);
        byte[] binOut = binMs.ToArray();

        using var jsonMs = new MemoryStream(gltfJsonBytes.Length + 256);
        using (var writer = new Utf8JsonWriter(jsonMs))
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("buffers"))
                {
                    writer.WritePropertyName("buffers");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteNumber("byteLength", binOut.Length);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                else if (prop.NameEquals("bufferViews"))
                {
                    writer.WritePropertyName("bufferViews");
                    writer.WriteStartArray();
                    foreach (JsonElement v in prop.Value.EnumerateArray())
                    {
                        int oldBuf = v.TryGetProperty("buffer", out var bEl) ? bEl.GetInt32() : 0;
                        int oldOff = v.TryGetProperty("byteOffset", out var oEl) ? oEl.GetInt32() : 0;
                        int baseOff = (uint)oldBuf < (uint)bufBaseOffsets.Length ? bufBaseOffsets[oldBuf] : 0;
                        writer.WriteStartObject();
                        writer.WriteNumber("buffer", 0);
                        writer.WriteNumber("byteOffset", baseOff + oldOff);
                        writer.WriteNumber("byteLength", v.GetProperty("byteLength").GetInt32());
                        if (v.TryGetProperty("byteStride", out var stEl)) writer.WriteNumber("byteStride", stEl.GetInt32());
                        if (v.TryGetProperty("target", out var tEl)) writer.WriteNumber("target", tEl.GetInt32());
                        writer.WriteEndObject();
                    }
                    for (int ev = 0; ev < extraImageViews; ev++)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("buffer", 0);
                        writer.WriteNumber("byteOffset", extraViewOff[ev]);
                        writer.WriteNumber("byteLength", extraViewLen[ev]);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else if (prop.NameEquals("images"))
                {
                    writer.WritePropertyName("images");
                    writer.WriteStartArray();
                    for (int ii = 0; ii < imgCount; ii++)
                    {
                        writer.WriteStartObject();
                        if (imgAssignedView[ii] >= 0)
                        {
                            writer.WriteNumber("bufferView", imgAssignedView[ii]);
                            writer.WriteString("mimeType", texPolicy != TextureCompressionPolicy.None ? "application/x-bring-brtx" : "image/png");
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        byte[] glb = AssembleGlb(jsonMs.ToArray(), binOut);
        return texPolicy != TextureCompressionPolicy.None
            ? RecompressGlbEmbeddedTextures(glb, texPolicy, jobs)
            : glb;
    }

    private static ReadOnlyMemory<byte> StripUtf8Bom(byte[] data)
        => (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            ? new ReadOnlyMemory<byte>(data, 3, data.Length - 3)
            : new ReadOnlyMemory<byte>(data);

    private static byte[] ResolveUriBytes(string uri, string baseDir)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = uri.IndexOf(',');
            return comma > 0 ? Convert.FromBase64String(uri.Substring(comma + 1)) : Array.Empty<byte>();
        }
        string full = Path.Combine(baseDir, uri);
        return File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
    }

    private static byte[] AssembleGlb(byte[] jsonRaw, byte[] binRaw)
    {
        int jsonPad = (4 - (jsonRaw.Length & 3)) & 3;
        int binPad = (4 - (binRaw.Length & 3)) & 3;
        int jsonLen = jsonRaw.Length + jsonPad;
        int binLen = binRaw.Length + binPad;
        int total = 12 + 8 + jsonLen + (binLen > 0 ? 8 + binLen : 0);

        byte[] glb = new byte[total];
        using var ms = new MemoryStream(glb);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(GlbMagic);
        bw.Write(2u);
        bw.Write((uint)total);

        bw.Write((uint)jsonLen);
        bw.Write(ChunkJson);
        bw.Write(jsonRaw);
        for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);

        if (binLen > 0)
        {
            bw.Write((uint)binLen);
            bw.Write(ChunkBin);
            bw.Write(binRaw);
            for (int i = 0; i < binPad; i++) bw.Write((byte)0);
        }
        return glb;
    }
}
