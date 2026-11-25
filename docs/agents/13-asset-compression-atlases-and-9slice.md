# Agent Guide 13 — Unity-Style Asset Compression, Default Texture Atlasing, `.bpack` Bundling & 9-Slice UI

## Overview
Bring Engine ships a full **Unity-style asset bundling & compression pipeline** designed for minimal distribution size, minimal VRAM, minimal draw calls (`DRAWS` / `SETPASS`), and strict **NativeAOT / Zero-OOP** compatibility:

1. **GPU Block Texture Compression (`TextureCompressionSystem`, `BRTX`)**:
   - **BC1 / DXT1** (`TexturePixelFormat.Bc1Rgba`): 4 bpp (**8:1 VRAM savings** over RGBA8) for opaque textures.
   - **BC3 / DXT5** (`TexturePixelFormat.Bc3Rgba`): 8 bpp (**4:1 VRAM savings** over RGBA8) for alpha/UI textures with smooth interpolated alpha.
   - `TextureCompressionPolicy.Auto` (default) inspects alpha automatically and chooses **BC1** when opaque or **BC3** when any pixel has `alpha < 255`.
   - Large textures/atlases (`>= 64` block-rows = `256px` tall) automatically accelerate encoding across worker threads when a `JobScheduler*` is passed to `TextureCompressionSystem` / `AtlasPacker` / `AssetPackBuilder`.
   - `GpuSystem.EnsureTextureUploaded` uploads BC1 (`BC1RGBAUnorm`) / BC3 (`BC3RGBAUnorm`) directly to SDL3_gpu VRAM, with transparent software decode fallback to `RGBA8` if a GPU driver lacks BC support.

2. **Unity-Style Bundle & glTF/GLB Compression (`PackCompressionSystem`, `BGLB`, `.bpack`)**:
   - `PackCompressionMode.Brotli` (default — matches Unity LZMA-class distribution compression for minimal build size).
   - `PackCompressionMode.Lz4` (matches Unity `ChunkBasedCompression` — ultra-fast zero-GC runtime decompression).
   - **glTF / GLB Compression**: `AssetPackBuilder` and `PackCompressionSystem.CompressGlb` / `BuildSelfContainedGlbFromFile`:
     - Automatically inline external `.gltf` `.bin` and image URI files into a single self-contained binary GLB.
     - Re-encode embedded PNG/JPEG textures inside `.glb` into GPU-ready **BC1/BC3 `BRTX`** blocks (zero PNG decoding at runtime + 4–8× lower VRAM!).
     - Compress the resulting GLB stream with **Brotli** or **LZ4** (either inside a `.bpack` bundle or standalone `.bglb` readable transparently by `GltfLoader.LoadFromMemory`).

3. **Default Texture Atlasing — Zero Loose Textures in Final Builds (`AtlasPacker`, `AssetPackBuilder`, `BringPacker`)**:
   - **All 2D/UI textures are atlased by default** via deterministic shelf bin-packing with **2 px edge-extruded bleed padding** (eliminating bilinear seam halos) and **BC1/BC3 compression**.
   - **Raw source textures MUST NEVER ship in the final game build (`dist/`).** Keep raw `.png`/`.jpg`/`.glb`/`.ttf` source assets in `raw_assets/<bundle>/` (excluded from `dist/`), compile them with `BringPacker` into compressed `assets/<bundle>.bpack`, and verify with `BringPacker --verify-no-loose-textures dist` (or `AssetPackBuilder.VerifyNoLooseTextures`).
   - Only repeating 3D wrap textures (e.g., tiled ground/brick albedo) should opt out of atlasing via `"atlas": false, "wrap": true` in `pack.json` — and even those ship GPU-compressed (BC1/BC3 `BRTX`) inside the `.bpack`, never as loose PNGs.

4. **9-Slice 2D UI Sprites (`SpriteDrawMode.Sliced`, `SlicedNoCenter`, `SliceBorders`)**:
   - Define 9-slice border insets `(Left, Bottom, Right, Top)` in source pixels once in `pack.json` (or `AtlasSourceImage.SliceBorders`).
   - When scaled to any UI panel/button width & height, the 4 corners remain crisp and unstretched, edges stretch along one axis, and the center stretches on both axes (or is omitted with `SpriteDrawMode.SlicedNoCenter` for hollow frames).
   - All 9 sub-quads share the exact same `SortKey`, atlas `TextureHandle`, and pipeline — so **every 9-sliced panel, button, and icon from the same compressed atlas merges into ONE single instanced GPU draw call (`DRAWS = 1`, `SETPASS = 1`)** with **0 GC allocations**.

---

## Packing a Game Bundle with `BringPacker`

Organize raw source files outside `assets/` (never copied to `dist/`):
```text
raw_assets/
└── common/
    ├── pack.json              # Optional metadata (9-slice borders, pivots, wrap opt-outs, font params)
    ├── ui/
    │   ├── panel_frame.png    # Atlased + BC3 compressed + 9-slice metadata
    │   ├── button_bg.png      # Atlased + BC3 compressed + 9-slice metadata
    │   └── icon_heart.png     # Atlased + BC3 compressed (default!)
    ├── textures/
    │   └── floor_tile.png     # Opted out of atlas in pack.json ("atlas": false, "wrap": true), still BC1 compressed inside .bpack
    ├── models/
    │   └── hero.glb           # Embedded textures re-encoded to BC1/BC3 + Brotli/LZ4 compressed inside .bpack
    ├── fonts/
    │   └── ui_font.ttf        # Compressed inside .bpack; baked to R8 SDF on load
    └── tuning.json            # Stored as compressed RawBlob
```

Example `raw_assets/common/pack.json`:
```json
{
  "compression": "brotli",
  "atlas_compression": "auto",
  "gltf_texture_compression": "auto",
  "max_atlas_size": 2048,
  "atlas_padding": 2,
  "sprites": [
    { "path": "ui/panel_frame.png", "name": "ui/panel_frame", "slice": [16, 16, 16, 16] },
    { "path": "ui/button_bg.png",   "name": "ui/button_bg",   "slice": [12, 12, 12, 12], "pivot": [0.5, 0.5] },
    { "path": "textures/floor_tile.png", "name": "floor_tile", "atlas": false, "wrap": true }
  ],
  "fonts": [
    { "path": "fonts/ui_font.ttf", "name": "main_font", "pixel_size": 64, "atlas_size": 1024, "sdf_padding": 8 }
  ]
}
```

Run `BringPacker` to emit the compressed `.bpack` bundle and enforce the zero-loose-texture guardrail:
```bash
# Build compressed bundle (Brotli for smallest release size, or --compression lz4 for fastest load)
dotnet run --project tools/BringPacker/BringPacker.csproj -c Release -- \
  --input raw_assets/common \
  --output assets/common.bpack \
  --compression brotli

# Guardrail: fail CI / Final Gate if any loose .png/.jpg/.bmp/.tga exists in dist/
dotnet run --project tools/BringPacker/BringPacker.csproj -c Release -- \
  --verify-no-loose-textures dist
```

---

## Loading `.bpack` Bundles & Rendering 9-Sliced UI at Runtime

```csharp
// 1. Register compressed .bpack bundle (uses BundleManager ref-counting & dedicated LinearArena!)
BundleId commonBundle = BundleSystem.RegisterPack(
    ref bundles,
    "common",
    arenaCapacityBytes: 16 * 1024 * 1024,
    bpackPath: "assets/common.bpack",
    postLoadHook: &OnCommonPostLoad); // optional extra procedural setup

// 2. Inside Scene OnLoad (resolve handles ONCE and cache in GameViewState — never per frame!)
SpriteRegionData* panelReg = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/panel_frame"));
SpriteRegionData* iconReg  = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/icon_heart"));
GltfHandle heroModel       = AssetSystem.FindGltf(in world.Assets, StringId.Hash("models/hero"));
TextureHandle floorTex     = AssetSystem.FindTexture(in world.Assets, StringId.Hash("floor_tile"));
FontHandle uiFont          = AssetSystem.FindFont(in world.Assets, StringId.Hash("main_font"));

// 3. Spawn a crisp 9-sliced HUD panel (320 x 140 virtual pixels, corners stay exact 16x16!)
EntityId panelEnt = EntitySystem.Spawn(ref world.Entities, ctx.Scene);
TransformSystem.Add(ref world.Transforms, panelEnt, new Vec3(24f, 540f, 0f));
SpriteData panelSprite = RenderComponentSystem.DefaultUiSlicedPanel(
    panelEnt,
    in *panelReg,
    size: new Vec2(320f, 140f),
    scene: ctx.Scene,
    orderInLayer: 10);
RenderComponentSystem.AddSprite(ref world.RenderComponents, in panelSprite);

// 4. Spawn an icon from the SAME atlas — merges into the SAME single GPU draw call!
EntityId iconEnt = EntitySystem.Spawn(ref world.Entities, ctx.Scene);
TransformSystem.Add(ref world.Transforms, iconEnt, new Vec3(48f, 610f, 0f));
SpriteData iconSprite = RenderComponentSystem.SpriteFromRegion(
    iconEnt,
    in *iconReg,
    size: new Vec2(32f, 32f),
    scene: ctx.Scene,
    layerMask: 1u << 31,
    orderInLayer: 11,
    depthTest: 0);
RenderComponentSystem.AddSprite(ref world.RenderComponents, in iconSprite);

// 5. Optional hollow frame (8 quads, skips center fill):
panelSprite.DrawMode = SpriteDrawMode.SlicedNoCenter;

// 6. Optional compressed RawBlob lookup (e.g. tuning.json stored inside common.bpack):
ArenaMarker mark = alloc.Scratch.SaveMarker();
if (AssetPackLoader.TryReadRawBlob("assets/common.bpack", StringId.Hash("tuning.json"), ref alloc.Scratch, out NativeSlice<byte> jsonBytes))
{
    // Parse with Utf8JsonReader...
}
alloc.Scratch.Rollback(mark);
```

## Runtime / Programmatic Atlasing (`AtlasPacker`)
When generating procedural UI sprites or icons at startup/bundle load (no files on disk), still pack them into a single compressed atlas instead of registering separate textures:

```csharp
Span<AtlasSourceImage> sources = stackalloc AtlasSourceImage[2];
sources[0] = new AtlasSourceImage { Name = StringId.Hash("ui_panel"), RgbaPixels = panelPx, Width = 64, Height = 64, SliceBorders = new Vec4(12, 12, 12, 12) };
sources[1] = new AtlasSourceImage { Name = StringId.Hash("ui_icon"),  RgbaPixels = iconPx,  Width = 32, Height = 32 };

AtlasBuildOptions opts = AtlasBuildOptions.Default(); // MaxPageSize=2048, Padding=2, Compression=Auto (BC1/BC3)
AtlasPacker.BuildAndRegisterInBundle(sources, in opts, "ui_atlas", ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle);
```

## Compression Mode Guidance (Unity Equivalent)
| Mode | Unity Equivalent | Best For |
|---|---|---|
| `PackCompressionMode.Brotli` (default) | LZMA-class release compression | Final builds (`dist/`), smallest `.bpack` / `.bglb` download size |
| `PackCompressionMode.Lz4` | `ChunkBasedCompression` (LZ4) | Fast scene streaming / instant startup decompression |
| `TextureCompressionPolicy.Auto` (default) | DXT1/BC1 (opaque) & DXT5/BC3 (alpha) | Default for all atlases & embedded glTF textures (4:1 to 8:1 VRAM savings) |
| `TextureCompressionPolicy.None` | Uncompressed RGBA8 | Only when exact bit-perfect pixel data is required (e.g. lookup tables) |
