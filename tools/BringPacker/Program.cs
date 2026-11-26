using System;
using System.IO;
using BringEngine;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Packing;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

namespace BringPacker;

/// <summary>
/// Bring Engine build-time asset bundler & compressor CLI (NativeAOT, Zero-OOP).
///
/// Usage:
///   BringPacker --input raw_assets/common --output assets/common.bpack [--compression brotli|lz4|none]
///               [--atlas-compression auto|bc1|bc3|none] [--gltf-compression auto|bc1|bc3|none]
///               [--max-atlas 2048] [--padding 2]
///   BringPacker --verify-no-loose-textures dist
/// </summary>
public static unsafe class Program
{
    public static int Main(string[] args)
    {
        string? inputDir = null;
        string? outputPack = null;
        string? verifyDir = null;
        PackCompressionMode compression = PackCompressionMode.Brotli;
        TextureCompressionPolicy atlasComp = TextureCompressionPolicy.Auto;
        TextureCompressionPolicy gltfTexComp = TextureCompressionPolicy.Auto;
        int maxAtlas = 2048;
        int padding = 2;

        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h") { showHelp = true; }
            else if (a == "--input" || a == "-i")
            {
                if (!TryConsumeValue(args, ref i, a, out inputDir)) return 2;
            }
            else if (a == "--output" || a == "-o")
            {
                if (!TryConsumeValue(args, ref i, a, out outputPack)) return 2;
            }
            else if (a == "--verify-no-loose-textures")
            {
                if (!TryConsumeValue(args, ref i, a, out verifyDir)) return 2;
            }
            else if (a == "--compression")
            {
                if (!TryConsumeValue(args, ref i, a, out string? v) || !TryParseComp(v, out compression))
                {
                    Console.Error.WriteLine($"[BringPacker] ERROR: Invalid --compression value '{(v ?? "<missing>")}'. Expected brotli|lz4|none.");
                    return 2;
                }
            }
            else if (a == "--atlas-compression")
            {
                if (!TryConsumeValue(args, ref i, a, out string? v) || !TryParseTex(v, out atlasComp))
                {
                    Console.Error.WriteLine($"[BringPacker] ERROR: Invalid --atlas-compression value '{(v ?? "<missing>")}'. Expected auto|bc1|bc3|none.");
                    return 2;
                }
            }
            else if (a == "--gltf-compression")
            {
                if (!TryConsumeValue(args, ref i, a, out string? v) || !TryParseTex(v, out gltfTexComp))
                {
                    Console.Error.WriteLine($"[BringPacker] ERROR: Invalid --gltf-compression value '{(v ?? "<missing>")}'. Expected auto|bc1|bc3|none.");
                    return 2;
                }
            }
            else if (a == "--max-atlas")
            {
                if (!TryConsumeValue(args, ref i, a, out string? v) || !int.TryParse(v, out maxAtlas) || maxAtlas <= 0)
                {
                    Console.Error.WriteLine($"[BringPacker] ERROR: Invalid --max-atlas value '{(v ?? "<missing>")}'. Expected positive integer.");
                    return 2;
                }
            }
            else if (a == "--padding")
            {
                if (!TryConsumeValue(args, ref i, a, out string? v) || !int.TryParse(v, out padding) || padding < 0)
                {
                    Console.Error.WriteLine($"[BringPacker] ERROR: Invalid --padding value '{(v ?? "<missing>")}'. Expected integer >= 0.");
                    return 2;
                }
            }
            else
            {
                Console.Error.WriteLine($"[BringPacker] ERROR: Unknown argument '{a}'.");
                return 2;
            }
        }

        if (!string.IsNullOrEmpty(verifyDir))
        {
            if (!Directory.Exists(verifyDir))
            {
                Console.Error.WriteLine($"[BringPacker] FAIL: directory does not exist: '{verifyDir}'.");
                return 1;
            }
            if (AssetPackBuilder.VerifyNoLooseTextures(verifyDir, out string[] bad))
            {
                Console.WriteLine($"[BringPacker] OK: zero loose textures in '{verifyDir}'.");
                return 0;
            }
            Console.WriteLine($"[BringPacker] FAIL: found {bad.Length} loose texture(s) in '{verifyDir}' (final builds must ship compressed atlases inside .bpack only!):");
            for (int i = 0; i < bad.Length; i++)
                Console.WriteLine($"  - {bad[i]}");
            return 1;
        }

        if (showHelp || string.IsNullOrEmpty(inputDir) || string.IsNullOrEmpty(outputPack))
        {
            Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - BringPacker (Unity-style Atlas + BC1/BC3 + LZ4/Brotli Asset Bundler)");
            Console.WriteLine("Usage:");
            Console.WriteLine("  BringPacker --input <raw_dir> --output <out.bpack> [--compression brotli|lz4|none]");
            Console.WriteLine("              [--atlas-compression auto|bc1|bc3|none] [--gltf-compression auto|bc1|bc3|none]");
            Console.WriteLine("              [--max-atlas 2048] [--padding 2]");
            Console.WriteLine("  BringPacker --verify-no-loose-textures <dist_dir>");
            return showHelp ? 0 : 2;
        }

        var arena = LinearArena.Create(2 * 1024 * 1024, StringId.Hash("BringPacker").Value);
        JobScheduler* jobs = arena.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(jobs, ref arena, workerCount: -1);
        try
        {
            AssetPackBuildSummary s = AssetPackBuilder.BuildPackFromDirectory(
                inputDir, outputPack, compression, atlasComp, gltfTexComp, maxAtlas, padding, jobs);

            double ratio = s.RawInputBytes > 0 ? (100.0 * s.PackedOutputBytes / s.RawInputBytes) : 100.0;
            Console.WriteLine(
                $"[BringPacker] Packed '{inputDir}' -> '{outputPack}' ({compression}, atlas={atlasComp}, gltfTex={gltfTexComp}): " +
                $"atlases={s.AtlasPageCount} ({s.AtlasedSpriteCount} sprites), standaloneTex={s.StandaloneTextureCount}, " +
                $"glTF={s.GltfModelCount}, fonts={s.FontCount}, blobs={s.RawBlobCount}, " +
                $"size={s.PackedOutputBytes / 1024.0:F1}KB ({ratio:F1}% of raw {s.RawInputBytes / 1024.0:F1}KB)");
            return 0;
        }
        finally
        {
            JobSystem.Stop(jobs);
            arena.Dispose();
        }
    }

    private static bool TryConsumeValue(string[] args, ref int i, string flag, out string? value)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"[BringPacker] ERROR: Missing value for '{flag}'.");
            value = null;
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool TryParseComp(string? s, out PackCompressionMode mode)
    {
        switch (s?.ToLowerInvariant())
        {
            case "none": mode = PackCompressionMode.None; return true;
            case "lz4": mode = PackCompressionMode.Lz4; return true;
            case "brotli": mode = PackCompressionMode.Brotli; return true;
            default: mode = PackCompressionMode.Brotli; return false;
        }
    }

    private static bool TryParseTex(string? s, out TextureCompressionPolicy policy)
    {
        switch (s?.ToLowerInvariant())
        {
            case "auto": policy = TextureCompressionPolicy.Auto; return true;
            case "bc1" or "dxt1": policy = TextureCompressionPolicy.Bc1; return true;
            case "bc3" or "dxt5": policy = TextureCompressionPolicy.Bc3; return true;
            case "none" or "rgba8": policy = TextureCompressionPolicy.None; return true;
            default: policy = TextureCompressionPolicy.Auto; return false;
        }
    }
}
