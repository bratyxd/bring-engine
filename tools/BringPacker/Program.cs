using System;
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
            if (a == "--help" || a == "-h") showHelp = true;
            else if ((a == "--input" || a == "-i") && i + 1 < args.Length) inputDir = args[++i];
            else if ((a == "--output" || a == "-o") && i + 1 < args.Length) outputPack = args[++i];
            else if (a == "--verify-no-loose-textures" && i + 1 < args.Length) verifyDir = args[++i];
            else if (a == "--compression" && i + 1 < args.Length) compression = ParseComp(args[++i]);
            else if (a == "--atlas-compression" && i + 1 < args.Length) atlasComp = ParseTex(args[++i]);
            else if (a == "--gltf-compression" && i + 1 < args.Length) gltfTexComp = ParseTex(args[++i]);
            else if (a == "--max-atlas" && i + 1 < args.Length) int.TryParse(args[++i], out maxAtlas);
            else if (a == "--padding" && i + 1 < args.Length) int.TryParse(args[++i], out padding);
        }

        if (!string.IsNullOrEmpty(verifyDir))
        {
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

    private static PackCompressionMode ParseComp(string s) => s.ToLowerInvariant() switch
    {
        "none" => PackCompressionMode.None,
        "lz4" => PackCompressionMode.Lz4,
        _ => PackCompressionMode.Brotli,
    };

    private static TextureCompressionPolicy ParseTex(string s) => s.ToLowerInvariant() switch
    {
        "bc1" or "dxt1" => TextureCompressionPolicy.Bc1,
        "bc3" or "dxt5" => TextureCompressionPolicy.Bc3,
        "none" or "rgba8" => TextureCompressionPolicy.None,
        _ => TextureCompressionPolicy.Auto,
    };
}
