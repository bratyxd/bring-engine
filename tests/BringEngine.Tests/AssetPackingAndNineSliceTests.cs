using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BringEngine.Assets;
using BringEngine.Assets.Atlases;
using BringEngine.Assets.Bundles;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Packing;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;
using StbImageWriteSharp;

namespace BringEngine.Tests;

public static unsafe class AssetPackingAndNineSliceTests
{
    public static void RunAll(bool headlessOnly = false)
    {
        Console.WriteLine("=== Phase 9: Unity-Style Compression (BC1/BC3, LZ4/Brotli, GLB), Default Atlasing, .bpack & 9-Slice UI ===");
        Assert.Run("TextureCompression_Bc1AndBc3_BlockSizesRoundTripQualityAndParallelEncode", &TestTextureCompressionBc1Bc3);
        Assert.Run("TextureCompression_Non4x4_Bc1Punchthrough_AndDimensionGuards", &TestTextureNon4x4PunchthroughAndGuards);
        Assert.Run("PackCompression_Lz4AndBrotli_RoundTripAndGlbEmbeddedTextureCompression", &TestPackAndGlbCompression);
        Assert.Run("CompressionAndContainers_CorruptLz4BrotliBpackBglbBrtx_RejectedSafely", &TestCorruptStreamsAndContainers);
        Assert.Run("AtlasPacker_DefaultAtlasingBleedPaddingNineSliceMetadataAndUnload", &TestAtlasPacker);
        Assert.Run("AssetPack_BuildDirectoryBrotliAndLz4_LoadViaBundleSystemAndZeroLooseTextures", &TestAssetPackEndToEnd);
        Assert.Run("AssetPack_MultiPageTtfLooseGltfBglb_VerifyMissingDir_AndBringPackerCli", &TestMultiPageTtfLooseGltfAndCli);
        Assert.Run("PackingAndAtlas_AuditRegressions_OpaqueBc1_Overflow_UnrefBin_EmptyBrotli_Extensions", &TestPackingAndAtlasAuditRegressions);
        Assert.Run("NineSlice_BatchRenderer_SingleDrawCallCornerSizingClampAndZeroGc", &TestNineSliceHeadlessBatching);
        if (!headlessOnly)
            Assert.Run("Gpu_CompressedBc1Bc3AtlasAndNineSliceUi_SingleDrawCallAndPngCapture", &TestGpuCompressedAtlasAndNineSlice);
    }

    private static void TestTextureCompressionBc1Bc3()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 128, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(1, 1);

        JobScheduler* jobs = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(jobs, ref alloc.Persistent, workerCount: 4, workerScratchBytes: 64 * 1024);

        try
        {
            const int W = 256, H = 256; // 64 block rows -> exercises parallel JobSystem path!
            byte[] opaque = new byte[W * H * 4];
            byte[] translucent = new byte[W * H * 4];

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    opaque[o + 0] = (byte)x;
                    opaque[o + 1] = (byte)y;
                    opaque[o + 2] = (byte)((x + y) >> 1);
                    opaque[o + 3] = 255;

                    translucent[o + 0] = (byte)(255 - x);
                    translucent[o + 1] = (byte)y;
                    translucent[o + 2] = 180;
                    // Include exact 0, exact 255, and smooth mid alpha values
                    translucent[o + 3] = (x < 8 && y < 8) ? (byte)0
                                       : (x >= W - 8 && y >= H - 8) ? (byte)255
                                       : (byte)((x + y) >> 1);
                }
            }

            fixed (byte* opPtr = opaque)
            fixed (byte* trPtr = translucent)
            {
                Assert.Equal((byte)TexturePixelFormat.Bc1Rgba, (byte)TextureCompressionSystem.ResolveFormat(TextureCompressionPolicy.Auto, opPtr, W, H), "Auto selects BC1 for opaque");
                Assert.Equal((byte)TexturePixelFormat.Bc3Rgba, (byte)TextureCompressionSystem.ResolveFormat(TextureCompressionPolicy.Auto, trPtr, W, H), "Auto selects BC3 for translucent");

                int bc1Bytes = TextureCompressionSystem.CalcByteSize(TexturePixelFormat.Bc1Rgba, W, H);
                int bc3Bytes = TextureCompressionSystem.CalcByteSize(TexturePixelFormat.Bc3Rgba, W, H);
                Assert.Equal(32768, bc1Bytes, "BC1 256x256 is 32768 bytes (8:1 VRAM savings)");
                Assert.Equal(65536, bc3Bytes, "BC3 256x256 is 65536 bytes (4:1 VRAM savings)");

                byte[] bc1Serial = new byte[bc1Bytes];
                byte[] bc1Parallel = new byte[bc1Bytes];
                byte[] bc3Serial = new byte[bc3Bytes];
                byte[] bc3Parallel = new byte[bc3Bytes];
                byte[] decoded = new byte[W * H * 4];

                fixed (byte* s1 = bc1Serial)
                fixed (byte* p1 = bc1Parallel)
                fixed (byte* s3 = bc3Serial)
                fixed (byte* p3 = bc3Parallel)
                fixed (byte* decPtr = decoded)
                {
                    TextureCompressionSystem.EncodeBc1(opPtr, W, H, s1, jobs: null);
                    TextureCompressionSystem.EncodeBc1(opPtr, W, H, p1, jobs: jobs);
                    Assert.True(bc1Serial.AsSpan().SequenceEqual(bc1Parallel), "BC1 serial and parallel JobSystem outputs are bit-exact");

                    TextureCompressionSystem.DecodeBc1ToRgba(p1, W, H, decPtr);
                    long errSum = 0;
                    for (int i = 0; i < W * H; i++)
                    {
                        errSum += Math.Abs(decoded[i * 4 + 0] - opaque[i * 4 + 0]);
                        errSum += Math.Abs(decoded[i * 4 + 1] - opaque[i * 4 + 1]);
                        errSum += Math.Abs(decoded[i * 4 + 2] - opaque[i * 4 + 2]);
                        Assert.Equal((byte)255, decoded[i * 4 + 3], "Decoded BC1 opaque alpha is 255");
                    }
                    double avgBc1Err = errSum / (double)(W * H * 3);
                    Assert.True(avgBc1Err < 8.0, $"BC1 avg channel error < 8.0 (got {avgBc1Err:F2})");

                    TextureCompressionSystem.EncodeBc3(trPtr, W, H, s3, jobs: null);
                    TextureCompressionSystem.EncodeBc3(trPtr, W, H, p3, jobs: jobs);
                    Assert.True(bc3Serial.AsSpan().SequenceEqual(bc3Parallel), "BC3 serial and parallel JobSystem outputs are bit-exact");

                    TextureCompressionSystem.DecodeBc3ToRgba(p3, W, H, decPtr);
                    long errRgb = 0, errA = 0;
                    for (int i = 0; i < W * H; i++)
                    {
                        errA += Math.Abs(decoded[i * 4 + 3] - translucent[i * 4 + 3]);
                        if (translucent[i * 4 + 3] > 0)
                        {
                            errRgb += Math.Abs(decoded[i * 4 + 0] - translucent[i * 4 + 0]);
                            errRgb += Math.Abs(decoded[i * 4 + 1] - translucent[i * 4 + 1]);
                            errRgb += Math.Abs(decoded[i * 4 + 2] - translucent[i * 4 + 2]);
                        }
                    }
                    double avgAlphaErr = errA / (double)(W * H);
                    double avgRgbErr = errRgb / (double)(W * H * 3);
                    Assert.True(avgAlphaErr < 4.0, $"BC3 avg alpha error < 4.0 (got {avgAlphaErr:F2})");
                    Assert.True(avgRgbErr < 8.0, $"BC3 avg RGB error < 8.0 (got {avgRgbErr:F2})");
                    Assert.Equal((byte)0, decoded[3], "BC3 preserves exact 0 alpha corner");
                    Assert.Equal((byte)255, decoded[((H - 1) * W + (W - 1)) * 4 + 3], "BC3 preserves exact 255 alpha corner");
                }

                // BRTX container auto-detection via TextureLoader.LoadFromMemory
                byte[] brtx = TextureCompressionSystem.EncodeBrtxBytes(trPtr, W, H, TextureCompressionPolicy.Auto, srgb: true, clampEdge: true, nearest: false, jobs: jobs);
                TextureHandle th = TextureLoader.LoadFromMemory(brtx, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("brtx_tex"));
                TextureAssetData* tex = AssetSystem.TryGetTexture(in world.Assets, th);
                Assert.True(tex != null, "BRTX texture registered");
                Assert.Equal((byte)TexturePixelFormat.Bc3Rgba, (byte)tex->Format, "BRTX loaded directly as BC3 without PNG decode");
                Assert.Equal(bc3Bytes, tex->DataBytes, "BRTX DataBytes exact");
            }
        }
        finally
        {
            JobSystem.Stop(jobs);
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after BC1/BC3 tests");
    }

    private static void TestPackAndGlbCompression()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 128);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(2, 1);

        try
        {
            // 1. LZ4 & Brotli round-trip + zero-GC LZ4 decompression
            byte[] src = new byte[64 * 1024];
            for (int i = 0; i < src.Length; i++)
                src[i] = (byte)(((i / 17) * 13 + (i % 32)) & 0xFF);

            byte[] lz4 = PackCompressionSystem.Compress(src, PackCompressionMode.Lz4);
            byte[] brotli = PackCompressionSystem.Compress(src, PackCompressionMode.Brotli);
            Assert.True(lz4.Length < src.Length / 2, $"LZ4 compressed 64KB -> {lz4.Length} bytes");
            Assert.True(brotli.Length < lz4.Length, $"Brotli ({brotli.Length}B) smaller than LZ4 ({lz4.Length}B)");

            byte[] outBuf = new byte[src.Length];
            int brotliLen = PackCompressionSystem.Decompress(brotli, PackCompressionMode.Brotli, outBuf);
            Assert.Equal(src.Length, brotliLen, "Brotli decompressed length exact");
            Assert.True(outBuf.AsSpan().SequenceEqual(src), "Brotli round-trip bit-exact");

            Array.Clear(outBuf);
            int lz4Len = PackCompressionSystem.Decompress(lz4, PackCompressionMode.Lz4, outBuf);
            Assert.Equal(src.Length, lz4Len, "LZ4 decompressed length exact");
            Assert.True(outBuf.AsSpan().SequenceEqual(src), "LZ4 round-trip bit-exact");

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int r = 0; r < 10; r++)
                PackCompressionSystem.DecompressLz4Block(lz4, outBuf);
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "LZ4 decompression allocates 0 GC bytes");

            // 2. Real GLB with embedded 64x64 PNG texture -> CompressGlb (Brotli + LZ4) -> GltfLoader transparent load!
            byte[] pngBytes = MakeTestPngBytes(64, 64, opaque: true);
            byte[] rawTexturedGlb = BuildTexturedBoxGlb(pngBytes);

            byte[] bglbBrotli = PackCompressionSystem.CompressGlb(rawTexturedGlb, PackCompressionMode.Brotli, TextureCompressionPolicy.Auto);
            byte[] bglbLz4 = PackCompressionSystem.CompressGlb(rawTexturedGlb, PackCompressionMode.Lz4, TextureCompressionPolicy.Auto);
            Assert.True(bglbBrotli.Length < rawTexturedGlb.Length, $"Compressed BGLB ({bglbBrotli.Length}B) < raw textured GLB ({rawTexturedGlb.Length}B)");

            GltfHandle g1 = GltfLoader.LoadFromMemory(bglbBrotli, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("crate_brotli"));
            GltfHandle g2 = GltfLoader.LoadFromMemory(bglbLz4, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("crate_lz4"));

            var m1 = AssetSystem.TryGetGltf(in world.Assets, g1);
            var m2 = AssetSystem.TryGetGltf(in world.Assets, g2);
            Assert.True(m1 != null && m1->PrimitiveCount == 1, "Brotli BGLB loaded via GltfLoader");
            Assert.True(m2 != null && m2->PrimitiveCount == 1, "LZ4 BGLB loaded via GltfLoader");

            MaterialAssetData* mat1 = AssetSystem.TryGetMaterial(in world.Assets, m1->Primitives[0].Material);
            Assert.True(mat1 != null && mat1->BaseColorTexture.IsValid, "BGLB material has embedded BaseColorTexture");
            TextureAssetData* embeddedTex = AssetSystem.TryGetTexture(in world.Assets, mat1->BaseColorTexture);
            Assert.True(embeddedTex != null, "Embedded texture registered");
            Assert.Equal((byte)TexturePixelFormat.Bc1Rgba, (byte)embeddedTex->Format, "Embedded GLB opaque PNG recompressed to GPU BC1 BRTX!");
            Assert.Equal(64, embeddedTex->Width, "Embedded texture width == 64");
            Assert.Equal(64, embeddedTex->Height, "Embedded texture height == 64");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after GLB/pack compression tests");
    }

    private static void TestAtlasPacker()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 128, 64);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(3, 1);

        try
        {
            byte[] panelPx = MakeNineSlicePanelRgba(64, 64, 12, 12, 12, 12);
            byte[] badgePx = MakeSolidRgba(32, 32, 240, 80, 60, 255);
            byte[] barPx = MakeSolidRgba(48, 24, 40, 220, 255, 200);

            fixed (byte* pPanel = panelPx)
            fixed (byte* pBadge = badgePx)
            fixed (byte* pBar = barPx)
            {
                Span<AtlasSourceImage> sources = stackalloc AtlasSourceImage[3];
                sources[0] = new AtlasSourceImage
                {
                    Name = StringId.Hash("ui/panel"),
                    RgbaPixels = pPanel,
                    Width = 64,
                    Height = 64,
                    SliceBorders = new Vec4(12f, 12f, 12f, 12f),
                    Pivot = Vec2.Zero,
                };
                sources[1] = new AtlasSourceImage
                {
                    Name = StringId.Hash("ui/badge"),
                    RgbaPixels = pBadge,
                    Width = 32,
                    Height = 32,
                };
                sources[2] = new AtlasSourceImage
                {
                    Name = StringId.Hash("ui/bar"),
                    RgbaPixels = pBar,
                    Width = 48,
                    Height = 24,
                };

                var opts = AtlasBuildOptions.Default();
                int pages = AtlasPacker.BuildAndRegisterInBundle(sources, in opts, "ui_atlas", ref bundleArena, ref world.Assets, bundleId);
                Assert.Equal(1, pages, "All 3 sprites packed into 1 compressed atlas page");

                SpriteRegionData* rPanel = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/panel"));
                SpriteRegionData* rBadge = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/badge"));
                SpriteRegionData* rBar = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/bar"));
                Assert.True(rPanel != null && rBadge != null && rBar != null, "All 3 SpriteRegionData registered");
                Assert.Equal(rPanel->AtlasTexture, rBadge->AtlasTexture, "Panel and badge share single atlas TextureHandle");
                Assert.Equal(rPanel->AtlasTexture, rBar->AtlasTexture, "Panel and bar share single atlas TextureHandle");
                Assert.Near(12f, rPanel->SliceBorders.X, 0.001f, "9-slice left border preserved");
                Assert.Near(64f, rPanel->PixelSize.X, 0.001f, "Source pixel width preserved");

                TextureAssetData* atlasTex = AssetSystem.TryGetTexture(in world.Assets, rPanel->AtlasTexture);
                Assert.True(atlasTex != null, "Atlas texture valid");
                Assert.Equal((byte)TexturePixelFormat.Bc3Rgba, (byte)atlasTex->Format, "Atlas auto-compressed to BC3");
                Assert.Equal(0, atlasTex->Width & 3, "Atlas width 4-aligned");
                Assert.Equal(0, atlasTex->Height & 3, "Atlas height 4-aligned");
                Assert.Equal(TextureCompressionSystem.CalcByteSize(TexturePixelFormat.Bc3Rgba, atlasTex->Width, atlasTex->Height), atlasTex->DataBytes, "Atlas DataBytes matches BC3 size");
                Assert.True(rPanel->UvRect.X > 0f && rPanel->UvRect.Y > 0f, "2px bleed padding inset present on UV origin");

                // Unload bundle cleans up both atlas texture AND sprite regions
                AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
                Assert.True(AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/panel")) == null, "SpriteRegionData unregistered on bundle unload");
                Assert.Equal(0, world.Assets.LiveTextureCount, "Atlas texture unregistered on bundle unload");
            }
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after AtlasPacker tests");
    }

    private static void TestAssetPackEndToEnd()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tmpRoot = Path.Combine(Path.GetTempPath(), $"bring_bpack_test_{Guid.NewGuid():N}");
        string rawDir = Path.Combine(tmpRoot, "raw_assets");
        string outDir = Path.Combine(tmpRoot, "dist_assets");
        Directory.CreateDirectory(Path.Combine(rawDir, "ui"));
        Directory.CreateDirectory(Path.Combine(rawDir, "textures"));
        Directory.CreateDirectory(Path.Combine(rawDir, "models"));
        Directory.CreateDirectory(outDir);

        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 128);
        var bundles = BundleManager.Create(ref alloc.Persistent, 8);

        try
        {
            // Write raw source PNGs, a textured GLB, a raw JSON blob, and pack.json
            File.WriteAllBytes(Path.Combine(rawDir, "ui", "panel.png"), MakePngFromRgba(MakeNineSlicePanelRgba(64, 64, 10, 12, 14, 16), 64, 64));
            File.WriteAllBytes(Path.Combine(rawDir, "ui", "icon.png"), MakeTestPngBytes(32, 32, opaque: false));
            File.WriteAllBytes(Path.Combine(rawDir, "textures", "floor.png"), MakeTestPngBytes(64, 64, opaque: true));
            File.WriteAllBytes(Path.Combine(rawDir, "models", "crate.glb"), BuildTexturedBoxGlb(MakeTestPngBytes(32, 32, opaque: true)));
            File.WriteAllText(Path.Combine(rawDir, "tuning.json"), "{\"speed\":12}", new UTF8Encoding(false));

            const string packJson = """
            {
              "compression": "brotli",
              "atlas_compression": "auto",
              "gltf_texture_compression": "auto",
              "max_atlas_size": 1024,
              "atlas_padding": 2,
              "sprites": [
                { "path": "ui/panel.png", "name": "ui/panel", "slice": [10, 12, 14, 16], "pivot": [0.0, 0.0] },
                { "path": "textures/floor.png", "name": "floor", "atlas": false, "wrap": true }
              ]
            }
            """;
            File.WriteAllText(Path.Combine(rawDir, "pack.json"), packJson, Encoding.UTF8);

            // Guardrail must catch loose PNGs in rawDir!
            Assert.True(!AssetPackBuilder.VerifyNoLooseTextures(rawDir, out string[] rawOffenders), "VerifyNoLooseTextures rejects raw source folder");
            Assert.Equal(3, rawOffenders.Length, "VerifyNoLooseTextures caught all 3 loose PNGs");

            // Build Brotli .bpack
            string brotliPack = Path.Combine(outDir, "common.bpack");
            AssetPackBuildSummary sBrotli = AssetPackBuilder.BuildPackFromDirectory(rawDir, brotliPack, PackCompressionMode.Brotli);
            Assert.Equal(1, sBrotli.AtlasPageCount, "1 compressed atlas page emitted");
            Assert.Equal(2, sBrotli.AtlasedSpriteCount, "Both ui/panel and unlisted ui/icon atlased by default");
            Assert.Equal(1, sBrotli.StandaloneTextureCount, "Explicit atlas:false floor packed standalone");
            Assert.Equal(1, sBrotli.GltfModelCount, "1 GLB packed");
            Assert.Equal(1, sBrotli.RawBlobCount, "1 RawBlob packed");

            // Build LZ4 .bpack too (overriding pack.json by passing a separate dir without compression override or via direct check)
            string lz4Pack = Path.Combine(outDir, "common_lz4.bpack");
            File.WriteAllText(Path.Combine(rawDir, "pack.json"), packJson.Replace("\"brotli\"", "\"lz4\""), Encoding.UTF8);
            AssetPackBuildSummary sLz4 = AssetPackBuilder.BuildPackFromDirectory(rawDir, lz4Pack, PackCompressionMode.Lz4);
            Assert.True(sBrotli.PackedOutputBytes <= sLz4.PackedOutputBytes, $"Brotli pack ({sBrotli.PackedOutputBytes}B) <= LZ4 pack ({sLz4.PackedOutputBytes}B)");

            // Guardrail MUST PASS on dist output folder (ZERO loose textures!)
            Assert.True(AssetPackBuilder.VerifyNoLooseTextures(outDir, out string[] distOffenders), "dist output passes zero-loose-texture check");
            Assert.Equal(0, distOffenders.Length, "0 loose textures in dist output");

            // Load via BundleSystem.RegisterPack + Acquire
            BundleId bId = BundleSystem.RegisterPack(ref bundles, "common", 8 * 1024 * 1024, brotliPack);
            BundleSystem.Acquire(ref bundles, bId, ref world, ref alloc.Scratch);

            SpriteRegionData* rPanel = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/panel"));
            SpriteRegionData* rIcon = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/icon"));
            Assert.True(rPanel != null && rIcon != null, "Atlased sprites loaded from .bpack");
            Assert.Equal(rPanel->AtlasTexture, rIcon->AtlasTexture, "ui/panel and ui/icon share one compressed atlas");
            Assert.Near(10f, rPanel->SliceBorders.X, 0.001f, "9-slice Left loaded from .bpack");
            Assert.Near(12f, rPanel->SliceBorders.Y, 0.001f, "9-slice Bottom loaded from .bpack");
            Assert.Near(14f, rPanel->SliceBorders.Z, 0.001f, "9-slice Right loaded from .bpack");
            Assert.Near(16f, rPanel->SliceBorders.W, 0.001f, "9-slice Top loaded from .bpack");
            Assert.Near(0f, rPanel->Pivot.X, 0.001f, "Explicit [0,0] pivot X preserved from pack.json");
            Assert.Near(0f, rPanel->Pivot.Y, 0.001f, "Explicit [0,0] pivot Y preserved from pack.json");
            SpriteData panelSprite = RenderComponentSystem.SpriteFromRegion(EntityId.Invalid, in *rPanel, new Vec2(100, 100));
            Assert.Near(0f, panelSprite.Pivot.X, 0.001f, "SpriteFromRegion preserves [0,0] pivot X");
            Assert.Near(0f, panelSprite.Pivot.Y, 0.001f, "SpriteFromRegion preserves [0,0] pivot Y");

            TextureHandle floorH = AssetSystem.FindTexture(in world.Assets, StringId.Hash("floor"));
            TextureAssetData* floorTex = AssetSystem.TryGetTexture(in world.Assets, floorH);
            Assert.True(floorTex != null, "Standalone opt-out floor loaded");
            Assert.Equal((byte)TexturePixelFormat.Bc1Rgba, (byte)floorTex->Format, "Standalone opaque floor GPU-compressed to BC1");
            Assert.Equal((byte)0, floorTex->ClampEdge, "Standalone wrap:true sets ClampEdge = 0");

            GltfHandle crateH = AssetSystem.FindGltf(in world.Assets, StringId.Hash("models/crate"));
            var crate = AssetSystem.TryGetGltf(in world.Assets, crateH);
            Assert.True(crate != null && crate->PrimitiveCount == 1, "Compressed GLB loaded from .bpack");
            MaterialAssetData* crateMat = AssetSystem.TryGetMaterial(in world.Assets, crate->Primitives[0].Material);
            TextureAssetData* crateTex = AssetSystem.TryGetTexture(in world.Assets, crateMat->BaseColorTexture);
            Assert.Equal((byte)TexturePixelFormat.Bc1Rgba, (byte)crateTex->Format, "GLB inside .bpack has BC1 compressed embedded texture");

            // RawBlob read
            ArenaMarker mark = alloc.Scratch.SaveMarker();
            Assert.True(AssetPackLoader.TryReadRawBlob(brotliPack, StringId.Hash("tuning.json"), ref alloc.Scratch, out NativeSlice<byte> rawSlice), "RawBlob found in .bpack");
            string tuningStr = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(rawSlice.Ptr, rawSlice.Length));
            Assert.Equal("{\"speed\":12}", tuningStr, "RawBlob decompressed exact JSON");
            alloc.Scratch.Rollback(mark);

            // Release bundle -> reclaims 100% RAM
            BundleSystem.Release(ref bundles, bId, ref world);
            Assert.Equal(0, bundles.LoadedBundleCount, "Bundle unloaded cleanly");
            Assert.Equal(0, world.Assets.LiveTextureCount, "0 live textures after release");
            Assert.Equal(0, world.Assets.LiveMeshCount, "0 live meshes after release");
            Assert.True(AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/panel")) == null, "Sprite regions unloaded");
        }
        finally
        {
            BundleSystem.UnloadAll(ref bundles, ref world);
            alloc.Dispose();
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after .bpack end-to-end test");
    }

    private static void TestNineSliceHeadlessBatching()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 1 * 1024 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 128);
        var bundleArena = LinearArena.Create(1 * 1024 * 1024);
        var bundleId = BundleId.Create(4, 1);

        try
        {
            byte[] panelPx = MakeNineSlicePanelRgba(64, 64, 16, 12, 16, 12);
            byte[] iconPx = MakeSolidRgba(32, 32, 255, 210, 60, 255);
            fixed (byte* pPanel = panelPx)
            fixed (byte* pIcon = iconPx)
            {
                Span<AtlasSourceImage> sources = stackalloc AtlasSourceImage[2];
                sources[0] = new AtlasSourceImage { Name = StringId.Hash("panel"), RgbaPixels = pPanel, Width = 64, Height = 64, SliceBorders = new Vec4(16f, 12f, 16f, 12f) };
                sources[1] = new AtlasSourceImage { Name = StringId.Hash("icon"), RgbaPixels = pIcon, Width = 32, Height = 32 };

                var opts = AtlasBuildOptions.Default();
                AtlasPacker.BuildAndRegisterInBundle(sources, in opts, "hud_atlas", ref bundleArena, ref world.Assets, bundleId);
            }

            SpriteRegionData* rPanel = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("panel"));
            SpriteRegionData* rIcon = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("icon"));
            TextureAssetData* atlasTex = AssetSystem.TryGetTexture(in world.Assets, rPanel->AtlasTexture);
            atlasTex->GpuSlot = 1; // Mock GPU slot for headless RenderPacket extraction

            CameraSystem.CreateOrthographicUI(ref world.Cameras, 1280f, 720f);

            // 1. Standard 9-sliced panel (300 x 120) -> 9 sub-quads
            EntityId e1 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e1, new Vec3(100f, 200f, 0f));
            SpriteData s1 = RenderComponentSystem.DefaultUiSlicedPanel(e1, in *rPanel, new Vec2(300f, 120f), orderInLayer: 10);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in s1);

            // 2. Hollow 9-slice frame (SlicedNoCenter) -> 8 sub-quads
            EntityId e2 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e2, new Vec3(450f, 200f, 0f));
            SpriteData s2 = RenderComponentSystem.DefaultUiSlicedPanel(e2, in *rPanel, new Vec2(200f, 100f), orderInLayer: 11);
            s2.DrawMode = SpriteDrawMode.SlicedNoCenter;
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in s2);

            // 3. Clamped small panel (10 x 8 < borders 32 x 24) -> 4 proportionally-shrunk corner quads
            EntityId e3 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e3, new Vec3(700f, 200f, 0f));
            SpriteData s3 = RenderComponentSystem.DefaultUiSlicedPanel(e3, in *rPanel, new Vec2(10f, 8f), orderInLayer: 12);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in s3);

            // 4. Simple icon on SAME atlas -> 1 quad
            EntityId e4 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e4, new Vec3(140f, 260f, 0f));
            SpriteData s4 = RenderComponentSystem.SpriteFromRegion(e4, in *rIcon, new Vec2(32f, 32f), orderInLayer: 13);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in s4);

            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 1280, 720);
            var shaders = new RenderShaderDefaults { Sprite = ShaderHandle.Create(1, 1) };

            // Warmup & verify geometry + single batch
            alloc.BeginFrame();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket packet);
            Assert.Equal(9 + 8 + 4 + 1, packet.Sprites.Count, "9 + 8 + 4 + 1 = 22 sub-quads emitted");
            Assert.Equal(1, packet.SpriteBatches.Count, "All 9-slice panels + icon from same atlas merge into 1 single draw call!");

            // Verify panel 1 exact unstretched corners and stretched center!
            // Sub-quad 0 = bottom-left corner (row 0, col 0): width=16, height=12, origin=(100,200)
            SpriteGpuInstance bl = packet.Sprites[0];
            Assert.Near(16f, bl.Model.C0.X, 0.01f, "Bottom-left corner width stays exact 16px");
            Assert.Near(12f, bl.Model.C1.Y, 0.01f, "Bottom-left corner height stays exact 12px");
            Assert.Near(100f, bl.Model.C3.X, 0.01f, "Bottom-left corner origin X=100");
            Assert.Near(200f, bl.Model.C3.Y, 0.01f, "Bottom-left corner origin Y=200");

            // Sub-quad 4 = center (row 1, col 1): width = 300-32 = 268, height = 120-24 = 96
            SpriteGpuInstance center = packet.Sprites[4];
            Assert.Near(268f, center.Model.C0.X, 0.01f, "Center stretched width == 268px");
            Assert.Near(96f, center.Model.C1.Y, 0.01f, "Center stretched height == 96px");

            // Sub-quad 17 = clamped small panel corner 0: shrunk proportionally to 5 x 4!
            SpriteGpuInstance clampedBl = packet.Sprites[9 + 8];
            Assert.Near(5f, clampedBl.Model.C0.X, 0.01f, "Clamped panel corner width shrinks gracefully to 5px");
            Assert.Near(4f, clampedBl.Model.C1.Y, 0.01f, "Clamped panel corner height shrinks gracefully to 4px");

            // Zero-GC hot extraction loop check
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 30; f++)
            {
                alloc.BeginFrame();
                BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out packet);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "9-slice ExtractRenderPacket allocates 0 GC bytes");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after 9-slice headless tests");
    }

    private static void TestGpuCompressedAtlasAndNineSlice()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 512 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 256);
        var bundleArena = LinearArena.Create(4 * 1024 * 1024);
        var bundleId = BundleId.Create(5, 1);

        var window = PlatformSystem.InitWindow("BringEngine Compressed Atlas + 9-Slice GPU Test", 640, 360, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        *gpu = GpuSystem.Create(window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);
        GpuSystem.BindAssetCleanupHooks(gpu, ref world.Assets);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu);

            // Pack a translucent 9-slice UI panel + gold icon into ONE BC3 compressed atlas
            byte[] panelPx = MakeNineSlicePanelRgba(64, 64, 14, 14, 14, 14);
            byte[] iconPx = MakeSolidRgba(32, 32, 255, 200, 40, 255);
            // Also upload a standalone BC1 opaque texture to validate BC1 upload path under SDL3_gpu debugMode!
            byte[] opaquePx = MakeSolidRgba(64, 64, 60, 140, 220, 255);

            fixed (byte* pPanel = panelPx)
            fixed (byte* pIcon = iconPx)
            fixed (byte* pOpaque = opaquePx)
            {
                Span<AtlasSourceImage> sources = stackalloc AtlasSourceImage[2];
                sources[0] = new AtlasSourceImage { Name = StringId.Hash("ui_panel"), RgbaPixels = pPanel, Width = 64, Height = 64, SliceBorders = new Vec4(14f, 14f, 14f, 14f) };
                sources[1] = new AtlasSourceImage { Name = StringId.Hash("ui_icon"), RgbaPixels = pIcon, Width = 32, Height = 32 };

                var opts = AtlasBuildOptions.Default();
                AtlasPacker.BuildAndRegisterInBundle(sources, in opts, "ui_gpu_atlas", ref bundleArena, ref world.Assets, bundleId);

                byte[] bc1Brtx = TextureCompressionSystem.EncodeBrtxBytes(pOpaque, 64, 64, TextureCompressionPolicy.Bc1);
                TextureHandle bc1TexH = TextureLoader.LoadBrtxFromMemory(bc1Brtx, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("bc1_test"));
                TextureAssetData* bc1Tex = AssetSystem.TryGetTexture(in world.Assets, bc1TexH);
                Assert.Equal((byte)TexturePixelFormat.Bc1Rgba, (byte)bc1Tex->Format, "Standalone BC1 texture ready for GPU upload");
            }

            SpriteRegionData* rPanel = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui_panel"));
            SpriteRegionData* rIcon = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui_icon"));

            // Orthographic UI camera clearing background to dark navy
            CameraHandle camH = CameraSystem.CreateOrthographicUI(ref world.Cameras, 640f, 360f);
            CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, camH);
            cam->ClearFlags = CameraClearFlags.SolidColor;
            cam->ClearColor = new ColorF(0.06f, 0.08f, 0.14f, 1f);

            // Wide 9-slice header panel (340 x 110)
            EntityId p1 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, p1, new Vec3(30f, 210f, 0f));
            SpriteData sp1 = RenderComponentSystem.DefaultUiSlicedPanel(p1, in *rPanel, new Vec2(340f, 110f), orderInLayer: 10);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in sp1);

            // Tall 9-slice side panel (160 x 260)
            EntityId p2 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, p2, new Vec3(430f, 50f, 0f));
            SpriteData sp2 = RenderComponentSystem.DefaultUiSlicedPanel(p2, in *rPanel, new Vec2(160f, 260f), orderInLayer: 11);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in sp2);

            // Two icons from the same BC3 atlas
            EntityId i1 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, i1, new Vec3(68f, 265f, 0f));
            SpriteData si1 = RenderComponentSystem.SpriteFromRegion(i1, in *rIcon, new Vec2(36f, 36f), orderInLayer: 12);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in si1);

            EntityId i2 = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, i2, new Vec3(510f, 180f, 0f));
            SpriteData si2 = RenderComponentSystem.SpriteFromRegion(i2, in *rIcon, new Vec2(48f, 48f), orderInLayer: 13);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, in si2);

            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 640, 360);
            BatchRenderer.UploadPendingAssets(ref *gpu, ref world);
            GpuSystem.EnsureCaptureTarget(ref *gpu, 640, 360);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);

            RenderStats st = gpu->LastFrameStats;
            Assert.Equal(20, st.SpriteInstances, "2 9-slice panels (18 quads) + 2 icons = 20 sprite instances");
            Assert.Equal(1, st.DrawCalls, $"All 20 quads from compressed BC3 atlas rendered in 1 draw call (got {st.DrawCalls})");
            Assert.Equal(1, st.SetPassCalls, $"1 SetPassCall for compressed BC3 atlas UI (got {st.SetPassCalls})");

            string pngPath = Path.Combine(AppContext.BaseDirectory, "verify_nineslice_bc3_test.png");
            GpuSystem.SaveCaptureTextureToPng(ref *gpu, pngPath);
            Assert.True(File.Exists(pngPath) && new FileInfo(pngPath).Length > 1024, "Captured 9-slice BC3 atlas GPU PNG screenshot");
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after compressed BC1/BC3 GPU render");
    }

    private static byte[] MakeSolidRgba(int w, int h, byte r, byte g, byte b, byte a)
    {
        byte[] data = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            data[i * 4 + 0] = r;
            data[i * 4 + 1] = g;
            data[i * 4 + 2] = b;
            data[i * 4 + 3] = a;
        }
        return data;
    }

    private static byte[] MakeNineSlicePanelRgba(int w, int h, int bL, int bB, int bR, int bT)
    {
        byte[] data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            bool inTop = y < bT;
            bool inBot = y >= h - bB;
            for (int x = 0; x < w; x++)
            {
                bool inLeft = x < bL;
                bool inRight = x >= w - bR;
                int o = (y * w + x) * 4;
                if ((inLeft || inRight) && (inTop || inBot))
                {
                    // Bright cyan corners
                    data[o + 0] = 60; data[o + 1] = 230; data[o + 2] = 255; data[o + 3] = 255;
                }
                else if (inLeft || inRight || inTop || inBot)
                {
                    // Crisp white-blue edges
                    data[o + 0] = 170; data[o + 1] = 210; data[o + 2] = 245; data[o + 3] = 240;
                }
                else
                {
                    // Semi-transparent dark interior
                    data[o + 0] = 24; data[o + 1] = 36; data[o + 2] = 64; data[o + 3] = 190;
                }
            }
        }
        return data;
    }

    private static byte[] MakePngFromRgba(byte[] rgba, int w, int h)
    {
        using var ms = new MemoryStream();
        var writer = new ImageWriter();
        writer.WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }

    private static byte[] MakeTestPngBytes(int w, int h, bool opaque)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bool chk = ((x >> 3) + (y >> 3)) % 2 == 0;
                rgba[o + 0] = chk ? (byte)220 : (byte)45;
                rgba[o + 1] = chk ? (byte)160 : (byte)95;
                rgba[o + 2] = chk ? (byte)60 : (byte)185;
                rgba[o + 3] = opaque ? (byte)255 : (byte)((x == 0 || y == 0) ? 0 : 210);
            }
        }
        return MakePngFromRgba(rgba, w, h);
    }

    /// <summary>
    /// Builds a valid minimal glTF 2.0 binary (.glb) containing a unit quad mesh and an embedded PNG BaseColorTexture.
    /// </summary>
    private static byte[] BuildTexturedBoxGlb(byte[] embeddedPng)
    {
        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);

        // 4 vertices (quad)
        float[] pos = { -0.5f, -0.5f, 0f,  0.5f, -0.5f, 0f,  0.5f, 0.5f, 0f,  -0.5f, 0.5f, 0f };
        float[] nrm = { 0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f };
        float[] uvs = { 0f, 1f,  1f, 1f,  1f, 0f,  0f, 0f };
        uint[] idx = { 0, 1, 2, 0, 2, 3 };

        int offPos = (int)bin.Position; foreach (float f in pos) bw.Write(f); int lenPos = pos.Length * 4;
        int offNrm = (int)bin.Position; foreach (float f in nrm) bw.Write(f); int lenNrm = nrm.Length * 4;
        int offUv = (int)bin.Position; foreach (float f in uvs) bw.Write(f); int lenUv = uvs.Length * 4;
        int offIdx = (int)bin.Position; foreach (uint u in idx) bw.Write(u); int lenIdx = idx.Length * 4;
        while ((bin.Position & 3) != 0) bw.Write((byte)0);
        int offImg = (int)bin.Position; bw.Write(embeddedPng); int lenImg = embeddedPng.Length;
        while ((bin.Position & 3) != 0) bw.Write((byte)0);
        byte[] binBytes = bin.ToArray();

        string json = "{"
            + "\"asset\":{\"version\":\"2.0\"},"
            + "\"scene\":0,\"scenes\":[{\"nodes\":[0]}],"
            + "\"nodes\":[{\"name\":\"Quad\",\"mesh\":0}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0}]}],"
            + "\"materials\":[{\"name\":\"Textured\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0.1,\"roughnessFactor\":0.7}}],"
            + "\"textures\":[{\"source\":0}],"
            + "\"images\":[{\"bufferView\":4,\"mimeType\":\"image/png\"}],"
            + $"\"buffers\":[{{\"byteLength\":{binBytes.Length}}}],"
            + "\"bufferViews\":["
            + $"{{\"buffer\":0,\"byteOffset\":{offPos},\"byteLength\":{lenPos}}},"
            + $"{{\"buffer\":0,\"byteOffset\":{offNrm},\"byteLength\":{lenNrm}}},"
            + $"{{\"buffer\":0,\"byteOffset\":{offUv},\"byteLength\":{lenUv}}},"
            + $"{{\"buffer\":0,\"byteOffset\":{offIdx},\"byteLength\":{lenIdx}}},"
            + $"{{\"buffer\":0,\"byteOffset\":{offImg},\"byteLength\":{lenImg}}}"
            + "],"
            + "\"accessors\":["
            + "{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\",\"min\":[-0.5,-0.5,0],\"max\":[0.5,0.5,0]},"
            + "{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\"},"
            + "{\"bufferView\":2,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"},"
            + "{\"bufferView\":3,\"componentType\":5125,\"count\":6,\"type\":\"SCALAR\"}"
            + "]}";

        byte[] jsonRaw = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonRaw.Length & 3)) & 3;
        int jsonLen = jsonRaw.Length + jsonPad;
        int total = 12 + 8 + jsonLen + 8 + binBytes.Length;

        using var outMs = new MemoryStream(total);
        using var obw = new BinaryWriter(outMs);
        obw.Write(0x46546C67u); // "glTF"
        obw.Write(2u);
        obw.Write((uint)total);
        obw.Write((uint)jsonLen);
        obw.Write(0x4E4F534Au); // "JSON"
        obw.Write(jsonRaw);
        for (int i = 0; i < jsonPad; i++) obw.Write((byte)0x20);
        obw.Write((uint)binBytes.Length);
        obw.Write(0x004E4942u); // "BIN\0"
        obw.Write(binBytes);
        return outMs.ToArray();
    }

    private static void TestTextureNon4x4PunchthroughAndGuards()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 16);
        var bundleArena = LinearArena.Create(512 * 1024);
        var bundleId = BundleId.Create(6, 1);

        try
        {
            (int w, int h)[] sizes = { (1, 1), (3, 5), (17, 13), (65, 63) };
            for (int si = 0; si < sizes.Length; si++)
            {
                int w = sizes[si].w, h = sizes[si].h;
                byte[] src = MakeSolidRgba(w, h, 80, 190, 240, 255);
                byte[] bc1 = new byte[TextureCompressionSystem.CalcByteSize(TexturePixelFormat.Bc1Rgba, w, h)];
                byte[] bc3 = new byte[TextureCompressionSystem.CalcByteSize(TexturePixelFormat.Bc3Rgba, w, h)];
                byte[] dec = new byte[w * h * 4];

                fixed (byte* sPtr = src)
                fixed (byte* b1Ptr = bc1)
                fixed (byte* b3Ptr = bc3)
                fixed (byte* dPtr = dec)
                {
                    TextureCompressionSystem.EncodeBc1(sPtr, w, h, b1Ptr);
                    TextureCompressionSystem.DecodeBc1ToRgba(b1Ptr, w, h, dPtr);
                    Assert.True(Math.Abs(dec[0] - 80) <= 8 && Math.Abs(dec[1] - 190) <= 8 && dec[3] == 255, $"Non-4x4 BC1 ({w}x{h}) round-trip valid");

                    src[3] = 64; // add alpha < 255 for BC3
                    TextureCompressionSystem.EncodeBc3(sPtr, w, h, b3Ptr);
                    TextureCompressionSystem.DecodeBc3ToRgba(b3Ptr, w, h, dPtr);
                    Assert.True(Math.Abs(dec[3] - 64) <= 4, $"Non-4x4 BC3 ({w}x{h}) alpha round-trip valid");
                }
            }

            // BC1 1-bit punchthrough alpha test (alpha < 128 -> 0, alpha >= 128 -> 255)
            byte[] punch = MakeSolidRgba(4, 4, 200, 100, 50, 255);
            punch[0 * 4 + 3] = 0;
            punch[5 * 4 + 3] = 100;
            byte[] punchBc1 = new byte[8];
            byte[] punchDec = new byte[4 * 4 * 4];
            fixed (byte* pPtr = punch)
            fixed (byte* bPtr = punchBc1)
            fixed (byte* dPtr = punchDec)
            {
                TextureCompressionSystem.EncodeBc1(pPtr, 4, 4, bPtr);
                ushort c0 = *(ushort*)(bPtr + 0);
                ushort c1 = *(ushort*)(bPtr + 2);
                Assert.True(c0 <= c1, "BC1 punchthrough uses c0 <= c1 mode");
                TextureCompressionSystem.DecodeBc1ToRgba(bPtr, 4, 4, dPtr);
                Assert.Equal((byte)0, punchDec[0 * 4 + 3], "BC1 punchthrough pixel 0 alpha == 0");
                Assert.Equal((byte)0, punchDec[5 * 4 + 3], "BC1 punchthrough pixel 5 (a=100 < 128) alpha == 0");
                Assert.Equal((byte)255, punchDec[15 * 4 + 3], "BC1 punchthrough opaque pixel alpha == 255");
            }

            // Dimension guards <= 0
            byte[] dummy = new byte[16];
            fixed (byte* d = dummy)
            {
                byte* localD = d;
                AssertThrows<ArgumentOutOfRangeException>(() => TextureCompressionSystem.EncodeBc1(localD, 0, 4, localD), "EncodeBc1 rejects width<=0");
                AssertThrows<ArgumentOutOfRangeException>(() => TextureCompressionSystem.DecodeBc3ToRgba(localD, 4, -1, localD), "DecodeBc3 rejects height<=0");
            }
            AssertThrows<ArgumentOutOfRangeException>(() => TextureLoader.CreateFromRgba(dummy, 0, 4, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("g1")), "CreateFromRgba rejects width<=0");
            AssertThrows<ArgumentOutOfRangeException>(() => TextureLoader.CreateRadialGlow(0, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("g2")), "CreateRadialGlow rejects size<=0");
            AssertThrows<ArgumentOutOfRangeException>(() => TextureLoader.CreateCheckerGrid(16, 0, Color32.White, Color32.Black, new Color32(255, 0, 0, 255), ref bundleArena, ref world.Assets, bundleId, StringId.Hash("g3")), "CreateCheckerGrid rejects tiles<=0");
            AssertThrows<ArgumentOutOfRangeException>(() => SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("f1"), 32f, 512, 0), "SdfFontLoader rejects sdfPadding<=0");
            AssertThrows<ArgumentOutOfRangeException>(() => SdfFontLoader.BakeSdfFromSystemOrFile(null, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("f2"), 32f, 8, 4), "SdfFontLoader rejects atlasSize<16");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after non-4x4 & guards tests");
    }

    private static void TestCorruptStreamsAndContainers()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
        var bundleArena = LinearArena.Create(512 * 1024);
        var bundleId = BundleId.Create(7, 1);

        try
        {
            byte[] outBuf = new byte[64];

            // 1. Corrupt LZ4 streams (EOF 1-byte OOB cases + extended run overflow + bad offsets)
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.DecompressLz4Block(new byte[] { 0xF0 }, outBuf), "LZ4 [0xF0] rejected");
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.DecompressLz4Block(new byte[] { 0x1F, 0x00, 0x01, 0x00 }, outBuf), "LZ4 [0x1F,0,1,0] rejected");
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.DecompressLz4Block(new byte[] { 0xF0, 0xFF, 0xFF, 0xFF, 0x00 }, outBuf), "LZ4 0xFF overflow run rejected");
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.DecompressLz4Block(new byte[] { 0x10, 0xAA, 0x00, 0x00 }, outBuf), "LZ4 offset=0 rejected");
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.DecompressLz4Block(new byte[] { 0x10, 0xAA, 0x05, 0x00 }, outBuf), "LZ4 offset>op rejected");

            // CompressLz4Block undersized dst
            byte[] srcData = new byte[32];
            for (int i = 0; i < srcData.Length; i++) srcData[i] = (byte)i;
            AssertThrows<ArgumentException>(() => PackCompressionSystem.CompressLz4Block(srcData, new byte[4]), "CompressLz4Block rejects undersized dst");

            // Decompress invalid enum & None length mismatch
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.Decompress(srcData, (PackCompressionMode)99, outBuf), "Decompress rejects invalid mode 99");
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.Decompress(srcData, PackCompressionMode.None, new byte[8]), "Decompress None rejects oversized src");

            // Corrupt Brotli
            AssertThrows<InvalidDataException>(() => PackCompressionSystem.Decompress(new byte[] { 0xFF, 0xFE, 0xFD }, PackCompressionMode.Brotli, outBuf), "Corrupt Brotli rejected");

            // 2. Corrupt BRTX
            byte[] solid4 = MakeSolidRgba(4, 4, 1, 2, 3, 255);
            byte[] goodBrtx;
            fixed (byte* s4Ptr = solid4)
                goodBrtx = TextureCompressionSystem.EncodeBrtxBytes(s4Ptr, 4, 4, TextureCompressionPolicy.Bc1);
            byte[] badVerBrtx = (byte[])goodBrtx.Clone();
            BitConverter.TryWriteBytes(badVerBrtx.AsSpan(4, 2), (ushort)99);
            AssertThrows<InvalidDataException>(() => TextureLoader.LoadBrtxFromMemory(badVerBrtx, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("brtx_badv")), "Bad BRTX version rejected");

            byte[] badLenBrtx = (byte[])goodBrtx.Clone();
            BitConverter.TryWriteBytes(badLenBrtx.AsSpan(16, 4), 4); // DataBytes=4 != expected 8
            AssertThrows<InvalidDataException>(() => TextureLoader.LoadBrtxFromMemory(badLenBrtx, ref bundleArena, ref world.Assets, bundleId, StringId.Hash("brtx_badl")), "Mismatched BRTX DataBytes rejected");

            // 3. Corrupt BGLB
            var box = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.White);
            byte[] goodBglb = PackCompressionSystem.CompressGlb(GlbBuilder.BuildGlb(in box), PackCompressionMode.Lz4, TextureCompressionPolicy.None);
            byte[] badBglb = (byte[])goodBglb.Clone();
            badBglb[4] = 99; // bad Version
            AssertThrows<InvalidDataException>(() => GltfLoader.LoadFromMemory(badBglb, null, ref bundleArena, ref alloc.Scratch, ref world.Assets, ref world.Animations, bundleId, StringId.Hash("bglb_bad")), "Bad BGLB version rejected");

            // 4. Corrupt .bpack + CRC32 bit-flip + TryReadRawBlobFromMemory safety
            string tmpDir = Path.Combine(Path.GetTempPath(), $"bring_corrupt_bpack_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "data.txt"), "hello world payload 1234567890", new UTF8Encoding(false));
                string packPath = Path.Combine(tmpDir, "test.bpack");
                AssetPackBuilder.BuildPackFromDirectory(tmpDir, packPath, PackCompressionMode.None);
                byte[] packBytes = File.ReadAllBytes(packPath);

                // Bit-flip inside payload -> CRC32 catches corruption!
                byte[] flipped = (byte[])packBytes.Clone();
                flipped[sizeof(BringPackHeader) + 2] ^= 0x01;
                AssertThrows<InvalidDataException>(() => AssetPackLoader.LoadFromMemoryIntoBundle(flipped, ref bundleArena, ref alloc.Scratch, ref world, bundleId), "Bit-flipped .bpack rejected by CRC32");
                Assert.True(!AssetPackLoader.TryReadRawBlobFromMemory(flipped, StringId.Hash("data.txt"), ref alloc.Scratch, out _), "TryReadRawBlobFromMemory returns false on CRC32 mismatch");

                // Missing path in TryReadRawBlob returns false cleanly
                Assert.True(!AssetPackLoader.TryReadRawBlob(Path.Combine(tmpDir, "missing.bpack"), StringId.Hash("data.txt"), ref alloc.Scratch, out _), "TryReadRawBlob returns false on missing path");

                // Negative EntryCount & truncated header
                byte[] truncPack = new byte[8];
                AssertThrows<InvalidDataException>(() => AssetPackLoader.LoadFromMemoryIntoBundle(truncPack, ref bundleArena, ref alloc.Scratch, ref world, bundleId), "Truncated .bpack rejected");
                Assert.True(!AssetPackLoader.TryReadRawBlobFromMemory(truncPack, StringId.Hash("data.txt"), ref alloc.Scratch, out _), "TryReadRawBlobFromMemory returns false on truncated header");
            }
            finally
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
            }
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after corrupt stream/container tests");
    }

    private static void TestMultiPageTtfLooseGltfAndCli()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tmpRoot = Path.Combine(Path.GetTempPath(), $"bring_multipage_cli_{Guid.NewGuid():N}");
        string rawDir = Path.Combine(tmpRoot, "raw");
        string outDir = Path.Combine(tmpRoot, "out");
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(outDir);

        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 128);
        var bundleArena = LinearArena.Create(4 * 1024 * 1024);
        var bundleId = BundleId.Create(8, 1);

        try
        {
            // 1. Multi-page AtlasPacker with non-PoT4 sprites & explicit HasPivot=1, Pivot=(0,0)
            byte[] sA = MakeSolidRgba(33, 29, 255, 60, 60, 255);
            byte[] sB = MakeSolidRgba(41, 37, 60, 255, 60, 255);
            byte[] sC = MakeSolidRgba(35, 31, 60, 60, 255, 255);
            fixed (byte* pA = sA)
            fixed (byte* pB = sB)
            fixed (byte* pC = sC)
            {
                Span<AtlasSourceImage> srcs = stackalloc AtlasSourceImage[3];
                srcs[0] = new AtlasSourceImage { Name = StringId.Hash("mp_a"), RgbaPixels = pA, Width = 33, Height = 29, HasPivot = 1, Pivot = Vec2.Zero };
                srcs[1] = new AtlasSourceImage { Name = StringId.Hash("mp_b"), RgbaPixels = pB, Width = 41, Height = 37 };
                srcs[2] = new AtlasSourceImage { Name = StringId.Hash("mp_c"), RgbaPixels = pC, Width = 35, Height = 31 };

                var opts = AtlasBuildOptions.Default();
                opts.MaxPageSize = 64; // Forces multiple pages!
                int pageCount = AtlasPacker.BuildAndRegisterInBundle(srcs, in opts, "mp", ref bundleArena, ref world.Assets, bundleId);
                Assert.True(pageCount >= 2, $"Small MaxPageSize=64 forced multi-page atlas ({pageCount} pages)");

                SpriteRegionData* rA = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("mp_a"));
                SpriteRegionData* rB = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("mp_b"));
                Assert.True(rA != null && rB != null, "Multi-page regions registered");
                Assert.Near(0f, rA->Pivot.X, 0.001f, "AtlasPacker HasPivot=1 preserves Pivot.X==0");
                Assert.Near(0f, rA->Pivot.Y, 0.001f, "AtlasPacker HasPivot=1 preserves Pivot.Y==0");
                Assert.Near(0.5f, rB->Pivot.X, 0.001f, "AtlasPacker default pivot X==0.5");
            }

            // 2. Pack loose .gltf + .bin + .png, .bglb, and .ttf (if system font exists) into .bpack
            byte[] extPng = MakeTestPngBytes(16, 16, opaque: true);
            File.WriteAllBytes(Path.Combine(rawDir, "crate_tex.png"), extPng);
            byte[] binBuf = new byte[4 * 12 + 6 * 4];
            float[] qPos = { -0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0 };
            Buffer.BlockCopy(qPos, 0, binBuf, 0, 48);
            uint[] qIdx = { 0, 1, 2, 0, 2, 3 };
            Buffer.BlockCopy(qIdx, 0, binBuf, 48, 24);
            File.WriteAllBytes(Path.Combine(rawDir, "loose.bin"), binBuf);
            string looseGltf = "{\"asset\":{\"version\":\"2.0\"},"
                + "\"buffers\":[{\"uri\":\"loose.bin\",\"byteLength\":72}],"
                + "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":48},{\"buffer\":0,\"byteOffset\":48,\"byteLength\":24}],"
                + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5125,\"count\":6,\"type\":\"SCALAR\"}],"
                + "\"images\":[{\"uri\":\"crate_tex.png\"}],\"textures\":[{\"source\":0}],"
                + "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}],"
                + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1,\"material\":0}]}],"
                + "\"nodes\":[{\"name\":\"LooseNode\",\"mesh\":0}]}";
            File.WriteAllText(Path.Combine(rawDir, "loose_model.gltf"), looseGltf, new UTF8Encoding(false));

            var boxSpec = GlbBuilder.CreateBoxSpec(Vec3.One, ColorF.Gold);
            byte[] bglbBytes = PackCompressionSystem.CompressGlb(GlbBuilder.BuildGlb(in boxSpec), PackCompressionMode.Lz4, TextureCompressionPolicy.None);
            File.WriteAllBytes(Path.Combine(rawDir, "packed_box.bglb"), bglbBytes);

            string? sysTtf = SdfFontLoader.ResolveTtfPath(null);
            bool packedTtf = sysTtf != null && sysTtf.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && File.Exists(sysTtf);
            if (packedTtf)
                File.Copy(sysTtf!, Path.Combine(rawDir, "ui_test.ttf"), overwrite: true);

            string packPath = Path.Combine(outDir, "combined.bpack");
            AssetPackBuildSummary sum = AssetPackBuilder.BuildPackFromDirectory(rawDir, packPath, PackCompressionMode.Lz4);
            Assert.Equal(2, sum.GltfModelCount, "Both loose .gltf and .bglb packed as GltfModel");
            Assert.Equal(0, sum.AtlasedSpriteCount, "glTF external PNG was inlined into GLB and NOT duplicated in 2D atlas");
            if (packedTtf)
                Assert.Equal(1, sum.FontCount, ".ttf packed as SdfFont");

            AssetPackLoader.LoadIntoBundle(packPath, ref bundleArena, ref alloc.Scratch, ref world, bundleId);
            Assert.True(AssetSystem.FindGltf(in world.Assets, StringId.Hash("loose_model")).IsValid, "Loose .gltf loaded from .bpack");
            Assert.True(AssetSystem.FindGltf(in world.Assets, StringId.Hash("packed_box")).IsValid, ".bglb loaded as GltfModel from .bpack");
            if (packedTtf)
                Assert.True(AssetSystem.FindFont(in world.Assets, StringId.Hash("ui_test")).IsValid, ".ttf SdfFont loaded from .bpack");

            // 3. VerifyNoLooseTextures on non-existent directory returns false
            Assert.True(!AssetPackBuilder.VerifyNoLooseTextures(Path.Combine(tmpRoot, "does_not_exist"), out _), "VerifyNoLooseTextures returns false on non-existent directory");

            // 4. BringPacker CLI strict arg validation (when BringPacker.dll is present in sibling build output)
            string? packerDll = FindBringPackerDll();
            if (packerDll != null)
            {
                Assert.Equal(2, RunBringPackerCli(packerDll, "--compresson"), "BringPacker rejects unknown flag with exit 2");
                Assert.Equal(2, RunBringPackerCli(packerDll, "--input"), "BringPacker rejects missing value with exit 2");
                Assert.Equal(2, RunBringPackerCli(packerDll, "--compression foo"), "BringPacker rejects invalid --compression with exit 2");
                Assert.Equal(2, RunBringPackerCli(packerDll, "--max-atlas abc"), "BringPacker rejects non-int --max-atlas with exit 2");
                Assert.Equal(2, RunBringPackerCli(packerDll, "--padding -1"), "BringPacker rejects negative --padding with exit 2");
                Assert.Equal(1, RunBringPackerCli(packerDll, $"--verify-no-loose-textures \"{Path.Combine(tmpRoot, "missing_dist")}\""), "BringPacker --verify-no-loose-textures missing dir returns exit 1");
            }
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            bundleArena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after multi-page/loose-gltf/CLI tests");
    }

    private static string? FindBringPackerDll()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "tools", "BringPacker", "bin", "Release", "net10.0", "BringPacker.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "tools", "BringPacker", "bin", "Debug", "net10.0", "BringPacker.dll")),
        };
        for (int i = 0; i < candidates.Length; i++)
            if (File.Exists(candidates[i])) return candidates[i];
        return null;
    }

    private static int RunBringPackerCli(string packerDll, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{packerDll}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(15000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit();
        }
        Task.WaitAll(outTask, errTask);
        return proc.ExitCode;
    }

    private static void TestPackingAndAtlasAuditRegressions()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tmpRoot = Path.Combine(Path.GetTempPath(), $"bring_pack_audit_{Guid.NewGuid():N}");
        string rawDir = Path.Combine(tmpRoot, "raw");
        string outDir = Path.Combine(tmpRoot, "out");
        string looseDir = Path.Combine(tmpRoot, "loose_check");
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(looseDir);

        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 512 * 1024, 1 * 1024 * 1024, 128 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 128, 32);
        var bundleArena = LinearArena.Create(2 * 1024 * 1024);
        var bundleId = BundleId.Create(9, 1);

        try
        {
            // REV-ASSETS-04: Opaque non-PoT sprites (16x16, 10x10, 24x18) with AtlasBuildOptions.Default() -> Bc1Rgba with exact BC1 byte count
            byte[] op1 = MakeSolidRgba(16, 16, 220, 120, 60, 255);
            byte[] op2 = MakeSolidRgba(10, 10, 40, 200, 180, 255);
            byte[] op3 = MakeSolidRgba(24, 18, 90, 90, 240, 255);
            fixed (byte* p1 = op1)
            fixed (byte* p2 = op2)
            fixed (byte* p3 = op3)
            {
                Span<AtlasSourceImage> opaqueSrcs = stackalloc AtlasSourceImage[3];
                opaqueSrcs[0] = new AtlasSourceImage { Name = StringId.Hash("op1"), RgbaPixels = p1, Width = 16, Height = 16 };
                opaqueSrcs[1] = new AtlasSourceImage { Name = StringId.Hash("op2"), RgbaPixels = p2, Width = 10, Height = 10 };
                opaqueSrcs[2] = new AtlasSourceImage { Name = StringId.Hash("op3"), RgbaPixels = p3, Width = 24, Height = 18 };

                var defOpts = AtlasBuildOptions.Default();
                AtlasPacker.BuildPages(opaqueSrcs, in defOpts, out PackedAtlasPage[] pages, out _);
                Assert.Equal(1, pages.Length, "REV-ASSETS-04: 3 opaque non-PoT sprites pack into 1 page");
                Assert.Equal((byte)TexturePixelFormat.Bc1Rgba, (byte)pages[0].Format, "REV-ASSETS-04: Auto selects BC1 (not BC3) for opaque sprites despite canvas slack");
                int expectedBc1Bytes = TextureCompressionSystem.CalcByteSize(TexturePixelFormat.Bc1Rgba, pages[0].Width, pages[0].Height);
                Assert.Equal(expectedBc1Bytes, pages[0].CompressedData.Length, "REV-ASSETS-04: Exact BC1 byte count");

                // Also verify decoded alpha is 255 everywhere (slack alpha pre-filled so no 3-color punchthrough triggered)
                byte[] dec = new byte[pages[0].Width * pages[0].Height * 4];
                fixed (byte* cPtr = pages[0].CompressedData)
                fixed (byte* dPtr = dec)
                    TextureCompressionSystem.DecodeBc1ToRgba(cPtr, pages[0].Width, pages[0].Height, dPtr);
                bool allAlpha255 = true;
                for (int i = 3; i < dec.Length; i += 4)
                    if (dec[i] != 255) { allAlpha255 = false; break; }
                Assert.True(allAlpha255, "REV-ASSETS-04: BC1 decoded page has alpha==255 everywhere (no punchthrough slack)");

                // REV-ASSETS-18: Overflow padding & MaxPageSize throw cleanly
                var overflowPadOpts = AtlasBuildOptions.Default();
                overflowPadOpts.Padding = int.MaxValue / 2;
                bool threwPad = false;
                try { AtlasPacker.BuildPages(opaqueSrcs, in overflowPadOpts, out _, out _); }
                catch (ArgumentOutOfRangeException) { threwPad = true; }
                Assert.True(threwPad, "REV-ASSETS-18: Overflow padding throws ArgumentOutOfRangeException");

                var overflowPageOpts = AtlasBuildOptions.Default();
                overflowPageOpts.MaxPageSize = int.MaxValue;
                bool threwMaxPage = false;
                try { AtlasPacker.BuildPages(opaqueSrcs, in overflowPageOpts, out _, out _); }
                catch (ArgumentOutOfRangeException) { threwMaxPage = true; }
                Assert.True(threwMaxPage, "REV-ASSETS-18: Overflow MaxPageSize throws ArgumentOutOfRangeException");
            }

            // REV-ASSETS-12: TextureCompressionSystem (int.MaxValue, 4) rejected cleanly without negative block count
            byte[] tiny = new byte[16];
            fixed (byte* tPtr = tiny)
            {
                byte* localT = tPtr;
                AssertThrows<OverflowException>(() => TextureCompressionSystem.CalcByteSize(TexturePixelFormat.Bc1Rgba, int.MaxValue, 4), "REV-ASSETS-12: CalcByteSize rejects (int.MaxValue, 4)");
                AssertThrows<OverflowException>(() => TextureCompressionSystem.EncodeBc1(localT, int.MaxValue, 4, localT), "REV-ASSETS-12: EncodeBc1 rejects (int.MaxValue, 4)");
                AssertThrows<OverflowException>(() => TextureCompressionSystem.EncodeBc3(localT, int.MaxValue, 4, localT), "REV-ASSETS-12: EncodeBc3 rejects (int.MaxValue, 4)");
                AssertThrows<OverflowException>(() => TextureCompressionSystem.DecodeBc1ToRgba(localT, int.MaxValue, 4, localT), "REV-ASSETS-12: DecodeBc1 rejects (int.MaxValue, 4)");
                AssertThrows<OverflowException>(() => TextureCompressionSystem.DecodeBc3ToRgba(localT, int.MaxValue, 4, localT), "REV-ASSETS-12: DecodeBc3 rejects (int.MaxValue, 4)");
            }

            // REV-ASSETS-14: Empty Brotli round-trips 0 bytes
            byte[] emptyBrotli = PackCompressionSystem.Compress(ReadOnlySpan<byte>.Empty, PackCompressionMode.Brotli);
            Assert.Equal(0, emptyBrotli.Length, "REV-ASSETS-14: Empty Brotli compress returns 0 bytes");
            int emptyDecomp = PackCompressionSystem.Decompress(emptyBrotli, PackCompressionMode.Brotli, Span<byte>.Empty);
            Assert.Equal(0, emptyDecomp, "REV-ASSETS-14: Empty Brotli decompress returns 0 bytes");

            // REV-ASSETS-14 & REV-ASSETS-18: Unreferenced level.bin packed as RawBlob, referenced glTF .bin skipped, and HasPivot=0 when omitted in pack.json
            byte[] levelBinData = new byte[] { 0x4C, 0x56, 0x4C, 0x31, 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(rawDir, "level.bin"), levelBinData);
            File.WriteAllBytes(Path.Combine(rawDir, "panel_nopiv.png"), MakeTestPngBytes(16, 16, opaque: true));
            File.WriteAllText(Path.Combine(rawDir, "pack.json"), "{\"sprites\":[{\"path\":\"panel_nopiv.png\",\"name\":\"panel_nopiv\",\"slice\":[2,2,2,2]}]}", new UTF8Encoding(false));

            string packPath = Path.Combine(outDir, "audit.bpack");
            AssetPackBuildSummary sum = AssetPackBuilder.BuildPackFromDirectory(rawDir, packPath, PackCompressionMode.Brotli);
            Assert.Equal(1, sum.RawBlobCount, "REV-ASSETS-14: Unreferenced level.bin packed as RawBlob");

            ArenaMarker mark = alloc.Scratch.SaveMarker();
            Assert.True(AssetPackLoader.TryReadRawBlob(packPath, StringId.Hash("level.bin"), ref alloc.Scratch, out NativeSlice<byte> binSlice), "REV-ASSETS-14: Unreferenced level.bin readable via TryReadRawBlob");
            Assert.True(new ReadOnlySpan<byte>(binSlice.Ptr, binSlice.Length).SequenceEqual(levelBinData), "REV-ASSETS-14: level.bin payload bit-exact");
            alloc.Scratch.Rollback(mark);

            AssetPackLoader.LoadIntoBundle(packPath, ref bundleArena, ref alloc.Scratch, ref world, bundleId);
            SpriteRegionData* noPivReg = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("panel_nopiv"));
            Assert.True(noPivReg != null, "REV-ASSETS-18: Sprite with slice-only metadata loaded");
            Assert.Near(0.5f, noPivReg->Pivot.X, 1e-4f, "REV-ASSETS-18: Omitted pivot defaults to 0.5 X");
            Assert.Near(0.5f, noPivReg->Pivot.Y, 1e-4f, "REV-ASSETS-18: Omitted pivot defaults to 0.5 Y");

            // XMOD-14: VerifyNoLooseTextures rejects .WEBP, .GIF, .PSD, .HDR, .PNG
            string[] looseExts = { "a.WEBP", "b.GIF", "c.PSD", "d.HDR", "e.PNG" };
            for (int i = 0; i < looseExts.Length; i++)
            {
                string sub = Path.Combine(looseDir, $"case_{i}");
                Directory.CreateDirectory(sub);
                File.WriteAllBytes(Path.Combine(sub, looseExts[i]), new byte[] { 1, 2, 3, 4 });
                Assert.True(!AssetPackBuilder.VerifyNoLooseTextures(sub, out string[] off) && off.Length == 1, $"XMOD-14: VerifyNoLooseTextures rejects {looseExts[i]}");
            }

            // XMOD-14: BringPacker CLI --help precedence over --verify-no-loose-textures & flag-as-value rejection
            string? packerDll = FindBringPackerDll();
            if (packerDll != null)
            {
                Assert.Equal(0, RunBringPackerCli(packerDll, $"--verify-no-loose-textures \"{looseDir}\" --help"), "XMOD-14: --help takes precedence over --verify-no-loose-textures (exit 0)");
                Assert.Equal(2, RunBringPackerCli(packerDll, "--input --output foo.bpack"), "XMOD-14: --input followed by --output rejected as missing value (exit 2)");
            }
        }
        finally
        {
            AssetSystem.UnloadByBundle(ref world.Assets, bundleId);
            bundleArena.Dispose();
            alloc.Dispose();
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after packing & atlas audit regression tests");
    }

    private static void AssertThrows<TException>(Action act, string msg) where TException : Exception
    {
        bool threw = false;
        try { act(); }
        catch (TException) { threw = true; }
        Assert.True(threw, msg);
    }
}
