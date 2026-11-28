using System;
using System.Globalization;
using System.IO;
using BringEngine;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Packing;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

namespace BringRig2D;

/// <summary>
/// Bring Engine zero-dependency 2D skeletal rigging &amp; AI authoring CLI workbench.
/// Commands: prep, mesh, validate, render, template, pack.
/// </summary>
public static unsafe class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }
        }

        try
        {
            return cmd switch
            {
                "prep" => RunPrep(args),
                "mesh" => RunMesh(args),
                "validate" => RunValidate(args),
                "render" => RunRender(args),
                "template" => RunTemplate(args),
                "pack" => RunPack(args),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BringRig2D] FATAL ({ex.GetType().Name}): {ex.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown command '{cmd}'. Run 'BringRig2D --help'.");
        return 2;
    }

    private static int RunPrep(string[] args)
    {
        string? input = null, output = null;
        var opts = new ImagePrep.PrepOptions
        {
            KeyMode = "auto",
            Tolerance = 65f,
            Feather = 28f,
            Despill = true,
            Trim = true,
            Pad = 4,
            SplitIslands = false,
            MinArea = 256,
            ManifestPath = null,
        };

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--input" or "-i") { if (!NextVal(args, ref i, a, out input)) return 2; }
            else if (a is "--output" or "-o") { if (!NextVal(args, ref i, a, out output)) return 2; }
            else if (a == "--key") { if (!NextVal(args, ref i, a, out string? v)) return 2; opts.KeyMode = v!; }
            else if (a == "--tolerance") { if (!NextFloat(args, ref i, a, out opts.Tolerance)) return 2; }
            else if (a == "--feather") { if (!NextFloat(args, ref i, a, out opts.Feather)) return 2; }
            else if (a == "--despill") opts.Despill = true;
            else if (a == "--no-despill") opts.Despill = false;
            else if (a == "--trim") opts.Trim = true;
            else if (a == "--no-trim") opts.Trim = false;
            else if (a == "--pad") { if (!NextInt(args, ref i, a, out opts.Pad)) return 2; }
            else if (a == "--split-islands") opts.SplitIslands = true;
            else if (a == "--min-area") { if (!NextInt(args, ref i, a, out opts.MinArea)) return 2; }
            else if (a == "--manifest") { if (!NextVal(args, ref i, a, out opts.ManifestPath)) return 2; }
            else { Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown prep option '{a}'."); return 2; }
        }

        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output))
        {
            Console.Error.WriteLine("[BringRig2D] ERROR: 'prep' requires --input and --output.");
            return 2;
        }
        return ImagePrep.Run(input, output, in opts);
    }

    private static int RunMesh(string[] args)
    {
        string? rig = null, images = null, outPath = null;
        float density = 24f;
        int margin = 2, smooth = 3;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--rig" or "-r") { if (!NextVal(args, ref i, a, out rig)) return 2; }
            else if (a == "--images") { if (!NextVal(args, ref i, a, out images)) return 2; }
            else if (a == "--density") { if (!NextFloat(args, ref i, a, out density)) return 2; }
            else if (a == "--margin") { if (!NextInt(args, ref i, a, out margin)) return 2; }
            else if (a == "--smooth") { if (!NextInt(args, ref i, a, out smooth)) return 2; }
            else if (a is "--out" or "-o") { if (!NextVal(args, ref i, a, out outPath)) return 2; }
            else { Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown mesh option '{a}'."); return 2; }
        }

        if (string.IsNullOrEmpty(rig))
        {
            Console.Error.WriteLine("[BringRig2D] ERROR: 'mesh' requires --rig <file.rig2d.json>.");
            return 2;
        }
        return AutoMesher.Run(rig, images, density, margin, smooth, outPath);
    }

    private static int RunValidate(string[] args)
    {
        string? rig = null, images = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--rig" or "-r") { if (!NextVal(args, ref i, a, out rig)) return 2; }
            else if (a == "--images") { if (!NextVal(args, ref i, a, out images)) return 2; }
            else { Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown validate option '{a}'."); return 2; }
        }
        if (string.IsNullOrEmpty(rig))
        {
            Console.Error.WriteLine("[BringRig2D] ERROR: 'validate' requires --rig <file.rig2d.json>.");
            return 2;
        }
        return RigValidator.Run(rig, images, out _, out _);
    }

    private static int RunRender(string[] args)
    {
        string? rig = null, outDir = null;
        bool explicitMode = false;
        var opts = new SoftwareRenderer.RenderOptions
        {
            ImagesDir = null,
            AnimFilter = "all",
            SkinName = null,
            Width = 512,
            Height = 512,
            StripFrames = 8,
            GifFps = 12,
            EmitClean = false,
            EmitOverlay = false,
            EmitWeights = false,
            EmitStrip = false,
            EmitGif = false,
            BgColor = new Color32(22, 27, 42, 255),
        };

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--rig" or "-r") { if (!NextVal(args, ref i, a, out rig)) return 2; }
            else if (a is "--out" or "-o") { if (!NextVal(args, ref i, a, out outDir)) return 2; }
            else if (a == "--images") { if (!NextVal(args, ref i, a, out opts.ImagesDir)) return 2; }
            else if (a == "--anim") { if (!NextVal(args, ref i, a, out string? v)) return 2; opts.AnimFilter = v!; }
            else if (a == "--skin") { if (!NextVal(args, ref i, a, out opts.SkinName)) return 2; }
            else if (a == "--width") { if (!NextInt(args, ref i, a, out opts.Width)) return 2; }
            else if (a == "--height") { if (!NextInt(args, ref i, a, out opts.Height)) return 2; }
            else if (a == "--frames") { if (!NextInt(args, ref i, a, out opts.StripFrames)) return 2; }
            else if (a == "--fps") { if (!NextInt(args, ref i, a, out opts.GifFps)) return 2; }
            else if (a == "--bg") { if (!NextVal(args, ref i, a, out string? v)) return 2; opts.BgColor = SoftwareRenderer.ParseBgHex(v!, opts.BgColor); }
            else if (a == "--clean") { opts.EmitClean = true; explicitMode = true; }
            else if (a == "--overlay") { opts.EmitOverlay = true; explicitMode = true; }
            else if (a == "--weights") { opts.EmitWeights = true; explicitMode = true; }
            else if (a == "--strip") { opts.EmitStrip = true; explicitMode = true; }
            else if (a == "--gif") { opts.EmitGif = true; explicitMode = true; }
            else { Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown render option '{a}'."); return 2; }
        }

        if (string.IsNullOrEmpty(rig) || string.IsNullOrEmpty(outDir))
        {
            Console.Error.WriteLine("[BringRig2D] ERROR: 'render' requires --rig <file.rig2d.json> and --out <dir>.");
            return 2;
        }

        if (!explicitMode)
        {
            opts.EmitClean = true;
            opts.EmitOverlay = true;
            opts.EmitWeights = true;
            opts.EmitStrip = true;
            opts.EmitGif = true;
        }
        return SoftwareRenderer.Run(rig, outDir, in opts);
    }

    private static int RunTemplate(string[] args)
    {
        string type = "biped", name = "", outPath = "";
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--type") { if (!NextVal(args, ref i, a, out string? v)) return 2; type = v!; }
            else if (a == "--name") { if (!NextVal(args, ref i, a, out string? v)) return 2; name = v!; }
            else if (a is "--out" or "-o") { if (!NextVal(args, ref i, a, out string? v)) return 2; outPath = v!; }
            else { Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown template option '{a}'."); return 2; }
        }
        if (string.IsNullOrEmpty(outPath))
        {
            Console.Error.WriteLine("[BringRig2D] ERROR: 'template' requires --out <file.rig2d.json>.");
            return 2;
        }
        return RigTemplates.Run(type, name, outPath);
    }

    private static int RunPack(string[] args)
    {
        string? inputDir = null, outputPack = null;
        PackCompressionMode comp = PackCompressionMode.Brotli;
        TextureCompressionPolicy atlasComp = TextureCompressionPolicy.Auto;
        int maxAtlas = 2048, padding = 2;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--input" or "-i") { if (!NextVal(args, ref i, a, out inputDir)) return 2; }
            else if (a is "--output" or "-o") { if (!NextVal(args, ref i, a, out outputPack)) return 2; }
            else if (a == "--compression")
            {
                if (!NextVal(args, ref i, a, out string? v)) return 2;
                comp = v!.ToLowerInvariant() switch { "none" => PackCompressionMode.None, "lz4" => PackCompressionMode.Lz4, _ => PackCompressionMode.Brotli };
            }
            else if (a == "--atlas-compression")
            {
                if (!NextVal(args, ref i, a, out string? v)) return 2;
                atlasComp = v!.ToLowerInvariant() switch { "bc1" => TextureCompressionPolicy.Bc1, "bc3" => TextureCompressionPolicy.Bc3, "none" or "rgba8" => TextureCompressionPolicy.None, _ => TextureCompressionPolicy.Auto };
            }
            else if (a == "--max-atlas") { if (!NextInt(args, ref i, a, out maxAtlas)) return 2; }
            else if (a == "--padding") { if (!NextInt(args, ref i, a, out padding)) return 2; }
            else { Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown pack option '{a}'."); return 2; }
        }

        if (string.IsNullOrEmpty(inputDir) || string.IsNullOrEmpty(outputPack))
        {
            Console.Error.WriteLine("[BringRig2D] ERROR: 'pack' requires --input <raw_dir> and --output <out.bpack>.");
            return 2;
        }

        var arena = LinearArena.Create(2 * 1024 * 1024, StringId.Hash("BringRig2DPack").Value);
        JobScheduler* jobs = arena.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(jobs, ref arena, workerCount: -1);
        try
        {
            AssetPackBuildSummary s = AssetPackBuilder.BuildPackFromDirectory(inputDir, outputPack, comp, atlasComp, TextureCompressionPolicy.Auto, maxAtlas, padding, jobs);
            Console.WriteLine($"[BringRig2D] Packed '{inputDir}' -> '{outputPack}': rigs2d={s.Rig2DCount}, atlases={s.AtlasPageCount} ({s.AtlasedSpriteCount} sprites), size={s.PackedOutputBytes / 1024.0:F1}KB");
            return 0;
        }
        finally
        {
            JobSystem.Stop(jobs);
            arena.Dispose();
        }
    }

    private static bool NextVal(string[] args, ref int i, string flag, out string? val)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"[BringRig2D] ERROR: Missing value for '{flag}'.");
            val = null;
            return false;
        }
        val = args[++i];
        return true;
    }

    private static bool NextInt(string[] args, ref int i, string flag, out int val)
    {
        val = 0;
        if (!NextVal(args, ref i, flag, out string? s) || !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
        {
            Console.Error.WriteLine($"[BringRig2D] ERROR: Invalid integer for '{flag}'.");
            return false;
        }
        return true;
    }

    private static bool NextFloat(string[] args, ref int i, string flag, out float val)
    {
        val = 0f;
        if (!NextVal(args, ref i, flag, out string? s) || !float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val) || !float.IsFinite(val))
        {
            Console.Error.WriteLine($"[BringRig2D] ERROR: Invalid number for '{flag}'.");
            return false;
        }
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - BringRig2D (Zero-Editor 2D Skeletal Rigging & AI Authoring CLI)");
        Console.WriteLine("Commands:");
        Console.WriteLine("  BringRig2D prep     --input <img|dir> --output <out> [--key auto|green|magenta|#RRGGBB] [--tolerance 65] [--feather 28] [--despill] [--trim] [--pad 4] [--split-islands] [--min-area 256] [--manifest <out.json>]");
        Console.WriteLine("  BringRig2D mesh     --rig <file.rig2d.json> [--images <dir>] [--density 24] [--margin 2] [--smooth 3] [--out <file.rig2d.json>]");
        Console.WriteLine("  BringRig2D validate --rig <file.rig2d.json> [--images <dir>]");
        Console.WriteLine("  BringRig2D render   --rig <file.rig2d.json> --out <dir> [--images <dir>] [--anim all|<name>] [--skin <name>] [--width 512] [--height 512] [--frames 8] [--fps 12] [--clean] [--overlay] [--weights] [--strip] [--gif] [--bg #161B2A]");
        Console.WriteLine("  BringRig2D template --type biped|quadruped|creature|prop --name <slug> --out <file.rig2d.json>");
        Console.WriteLine("  BringRig2D pack     --input <raw_dir> --output <out.bpack> [--compression brotli|lz4|none] [--atlas-compression auto|bc1|bc3|none] [--max-atlas 2048] [--padding 2]");
    }
}
