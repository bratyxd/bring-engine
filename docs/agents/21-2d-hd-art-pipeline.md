# Agent Guide 21 — Style-Locked HD Hand-Drawn 2D Game Art Pipeline (`Art Bible` + Visual QA + `.bpack`)

> **Scope**: Complete, self-contained AI art-direction & asset production pipeline for generating **polished, high-definition, hand-drawn 2D game art** (environments, parallax layers, seamless tiles, props, 9-slice UI panels & buttons, HUD icons, portraits, VFX flipbooks, and static sprites) where **each game freely chooses its own art style, yet stays 100% visually consistent once locked for that game**.
> Works with **any AI coding agent** (Antigravity, Claude Code, Cursor, Codex, etc.), ships its deterministic Python helper scripts directly inside this SDK under [`docs/agents/scripts/bring-2d-art/`](scripts/bring-2d-art/), hands off articulated skeletal 2D characters cleanly to **`Rig2D`** ([`20-2d-skeletal-rigging.md`](20-2d-skeletal-rigging.md)), and outputs **`BringPacker`**-ready `pack.json` + BC1/BC3 `.bpack` bundles ([`13-asset-compression-atlases-and-9slice.md`](13-asset-compression-atlases-and-9slice.md)) with **zero loose textures in `dist/`**.

---

## 1. Architecture & Non-Negotiable Consistency Rules

Text prompts alone drift across 20–80 assets. Every Bring Engine 2D game enforces consistency through **5 concrete guardrails**:

1. **Per-Game Art Bible (`art_bible.json`) — Interchangeable Across Games, Locked Within One Game**:
   - Every game starts by picking one of **8 built-in HD hand-drawn presets** (or defining a custom hand-drawn style) with exact hex palette swatches, ink outline weight/color, brushwork/shading rules, lighting angle, camera projection, target texel density, and forbidden artifacts.
   - Once the game's root Visual Style Anchors are visually approved and `art_bible.json` is locked, **every subsequent asset prompt** is compiled deterministically from `art_bible.json` — never freehanded from memory.
2. **Visual Style Anchor Conditioning First**:
   - Generate `qa/palette_swatch.png` (deterministic) → `anchors/style_anchor_world.png` → `anchors/style_anchor_characters.png` + `anchors/style_anchor_ui.png` FIRST.
   - Pass the locked Visual Style Anchor(s) — plus sibling anchors for asset families (e.g., Icon #1 for Icons #2..N, VFX Frame 0 for Frames 1..3) — as **image references** on every generation call.
3. **Default Zero-Config Image Generation + Optional External Provider Slot**:
   - **Default (`"provider": "builtin"`)**: Uses your agent harness's built-in image generator (e.g., Antigravity `image-generator` subagent) with zero API keys, zero model-training latency, and instant style switching per game.
   - **Optional External API Adapter (`scripts/bring-2d-art/provider_adapter.py`)**: Plug in **Recraft** (true SVG vector UI/icons via `RECRAFT_API_KEY`), **Fal.ai Flux + LoRA** (`FAL_KEY`), or **Scenario** custom house-style models (`SCENARIO_API_KEY` + `SCENARIO_API_SECRET`) globally or per category (e.g., `ui_icon` only) — all downstream prep, QA, and packing stay identical.
4. **Value & Contrast Hierarchy (Gameplay Clarity First)**:
   - Backgrounds (`parallax_layer`, `tile_seamless`) stay lower-contrast and less saturated; foreground characters, enemies, projectiles, and interactive props carry crisp outlines and strong highlight separation.
5. **Mandatory Automated Audit + Multimodal Side-by-Side Consistency QA Board**:
   - Every generated batch runs through `image_prep.py audit` (chroma halo %, luminance, palette distance) and `qa_board.py` (composites locked palette + anchors on top, candidate assets on split alpha-checker / in-game `bg_mid` backdrop below). Visually inspect the board (plus 2×2 tile previews and 9-slice stretch previews) and regenerate any outlier before packing.

---

## 2. Interchangeable Hand-Drawn Style Presets & Projections

Choose a preset per game (or customize any field via `--custom-style`, `--light-dir`, `--chroma`, `--palette-override key=#RRGGBB`):

| Preset Key | Visual Identity & Hand-Drawn Craft | Default Chroma |
|---|---|---|
| `dark-fantasy-ink` | Stylized dark-fantasy hand-painted illustration (*Hades* / *Battle Chasers*): bold tapered dark ink contours, sculpted cel-painterly shading, dramatic specular rim gleams. | `#00FF00` |
| `gothic-hand-inked` | Atmospheric hand-inked metroidvania (*Hollow Knight* / *Ender Lilies*): fluid expressive black ink, velvety cool indigo/slate washes, luminous ivory & bioluminescent accents. | `#00FF00` |
| `storybook-gouache` | Lush hand-painted storybook gouache (*Rayman Legends* / *Ori*): visible traditional brushwork, warm dark-umber outlines, golden sunlit highlights & cool shadows. | `#FF00FF` |
| `comic-cel-action` | Dynamic hand-drawn comic action (*Streets of Rage 4* / *Banner Saga*): variable-weight comic ink, crisp two-tone cel planes with subtle painterly texture, high 60 FPS clarity. | `#00FF00` |
| `cozy-watercolor` | Hand-drawn cozy watercolor & ink (*Spiritfarer* / *Gris* / Studio Ghibli backgrounds): delicate warm-sepia ink outlines, translucent watercolor washes, gentle sunlit warmth. | `#FF00FF` |
| `grim-woodcut-etching` | Dramatic hand-inked chiaroscuro & hatching (*Darkest Dungeon* / *Don't Starve*): heavy dip-pen ink pools, controlled crosshatching, parchment/ochre/crimson washes. | `#00FF00` |
| `sci-fi-ligne-claire` | European hand-drawn ligne-claire sci-fi (*Moebius* / *Sable* / *Transistor*): crisp technical-pen linework, luminous graphic washes, coral/cyan/amber color blocks. | `#FF00FF` |
| `vibrant-arcade-illustrated` | Modern hand-drawn arcade illustration: smooth bold dark-plum outlines, saturated readable cel shading, glossy highlights, unmistakable silhouettes. | `#FF00FF` |

**Supported Camera Projections (`--projection`)**:
- `side-view` — strict 2D side-scroller orthographic elevation (characters facing right `+X`, horizontal platforms)
- `top-down-3q` — classic ~35–45° 3/4 top-down action RPG / roguelike orthographic projection
- `top-down-flat` — strict 90° overhead orthographic plan view
- `isometric` — strict 2:1 dimetric isometric projection
- `ui-orthographic` — flat front-facing orthographic HUD/UI view

---

## 3. Bundled SDK Toolchain (`docs/agents/scripts/bring-2d-art/`)

Requires Python 3.10+ and Pillow (`pip install pillow`); zero other third-party dependencies.

| Script | Purpose |
|---|---|
| [`art_bible_tool.py`](scripts/bring-2d-art/art_bible_tool.py) | `list-presets`, `init` (writes `art_bible.json` + renders `qa/palette_swatch.png`), `lock`, `validate`, `prompt` (emits locked `[STYLE LOCK]` + exact anchor reference list). |
| [`image_prep.py`](scripts/bring-2d-art/image_prep.py) | `to-png`, `cutout` (border flood-fill chroma removal + interior hole keying + 3px edge-band despill + trim + consistent texel-density resize; supports `--bring-rig2d-proj` C# acceleration), `tile` (seam energy scoring + C1 overlap-crop seamless wrap blend + 2×2 preview + 50% offset seam export), `slice9` (auto-estimates Bring Engine `[L, B, R, T]` borders + wide/tall stretch QA), `audit` (halo %, luminance, palette distance). |
| [`qa_board.py`](scripts/bring-2d-art/qa_board.py) | Composites the side-by-side **Style Consistency QA Board** (locked palette + anchors on top; candidate assets over split alpha-checker & in-game `bg_mid` backdrop below). |
| [`pack_manifest.py`](scripts/bring-2d-art/pack_manifest.py) | Generates/merges Bring Engine `pack.json` (`slice: [L,B,R,T]`, `pivot`, `atlas: false, wrap: true` for repeating tiles) and optionally invokes `BringPacker` directly. |
| [`provider_adapter.py`](scripts/bring-2d-art/provider_adapter.py) | Optional zero-dependency external backend adapter for `recraft`, `fal`, and `scenario` (built-in image generation remains default). |

---

## 4. End-to-End Authoring Workflow (For Any Game & Any Agent)

> **Folder discipline**: Never commit game-specific art inside the `bring-engine` repo. Inside a game repository, keep working art generation files under `_art_workspace/` (excluded from `assets/` and `dist/`), output cleaned game-ready PNGs + `pack.json` into `raw_assets/<bundle>/`, and ship ONLY compiled `assets/<bundle>.bpack` in `dist/`.

### Step 1 — Initialize `art_bible.json` & Palette Swatch

```powershell
$artTools = "$sdk/docs/agents/scripts/bring-2d-art"

python "$artTools/art_bible_tool.py" list-presets

python "$artTools/art_bible_tool.py" init `
  --game "Ashen Spire" `
  --preset dark-fantasy-ink `
  --projection side-view `
  --theme "Gothic ruined watchtower with glowing crimson embers" `
  --out-dir "_art_workspace"
```

Visually inspect `_art_workspace/qa/palette_swatch.png` to confirm color harmony before generating any art.

### Step 2 — Generate & Lock the Visual Style Anchors

Generate anchors **in order**, inspecting each with vision before continuing:

```text
qa/palette_swatch.png
        │
        ▼
anchors/style_anchor_world.png       (1st — establishes brushwork, materials, atmosphere & lighting)
        │
        ├──► anchors/style_anchor_characters.png  (2nd — conditioned on world anchor + swatch)
        └──► anchors/style_anchor_ui.png          (3rd — conditioned on world anchor + swatch)
```

```powershell
# Compile each anchor prompt + reference image list
python "$artTools/art_bible_tool.py" prompt --bible "_art_workspace/art_bible.json" --category anchor_world --subject "crumbling gothic stone bridge over misty abyss with iron braziers"
python "$artTools/art_bible_tool.py" prompt --bible "_art_workspace/art_bible.json" --category anchor_character --subject "armored lantern knight in dark steel plate and crimson cloak"
python "$artTools/art_bible_tool.py" prompt --bible "_art_workspace/art_bible.json" --category anchor_ui --subject "dark forged iron and tarnished gold ornamental dialogue panel"

# Convert accepted raw outputs into lossless PNGs under _art_workspace/anchors/, then lock:
python "$artTools/art_bible_tool.py" lock --bible "_art_workspace/art_bible.json"
python "$artTools/art_bible_tool.py" validate --bible "_art_workspace/art_bible.json"
```

### Step 3 — Generate Anchor-Conditioned Game Asset Batches

Always condition every category on its locked anchor(s) and sibling reference (`--extra-ref`):

| Category | Locked Primary Anchor | Recommended Sibling (`--extra-ref`) | Chroma Key? |
|---|---|---|---|
| `prop` | `style_anchor_world.png` | First approved biome prop | Yes (`#00FF00` / `#FF00FF`) |
| `tile_seamless` | `style_anchor_world.png` | Base terrain tile for variants | **No** (full-bleed edge-to-edge) |
| `parallax_layer` | `style_anchor_world.png` | Far sky layer (for mid/near bands) | Sky: No / Mid & Near: Yes |
| `character_static` | `style_anchor_characters.png` | Hero sprite (for matching scale/ink) | Yes |
| `portrait` | `style_anchor_characters.png` | Character's full-body sprite | Yes |
| `ui_panel` / `ui_button` | `style_anchor_ui.png` | — | Yes (demand **zero text/icons inside**) |
| `ui_icon` | `style_anchor_ui.png` | **Icon #1** (locks stroke weight & scale across all icons) | Yes |
| `vfx_frame` | `style_anchor_characters.png` | **Frame 0** of the same VFX sequence (`slash_0` → `slash_1..3`) | Yes (`#00FF00` or `#000000`) |

> **Chroma Clash Rule**: Use `art_bible.json`'s `chroma_key.default` across the game; pass `--chroma "#FF00FF"` (or `"#00FF00"`) ONLY for a single asset whose colors clash with the default chroma (e.g., an emerald potion or green foliage on `#00FF00`).

Compile prompts via `art_bible_tool.py prompt --category <cat> --subject "<desc>" [--extra-ref <path>] [--json]`. Convert accepted raw outputs to lossless PNGs under `_art_workspace/raw_gen/<category>/` via `image_prep.py to-png`.

### Step 4 — Deterministic Game-Ready Prep (`image_prep.py`)

```powershell
# 1. Transparent sprites, props, icons, UI, VFX (pass --bring-rig2d-proj for fast C# prep!)
python "$artTools/image_prep.py" cutout `
  --input "_art_workspace/raw_gen/props" `
  --output "raw_assets/common/props" `
  --key auto --tolerance 65 --feather 28 --despill --trim --pad 4 `
  --max-height 256 `
  --bring-rig2d-proj "$sdk/tools/BringRig2D/BringRig2D.csproj"

# 2. Seamless XY terrain tiles & X-only horizontal parallax bands
python "$artTools/image_prep.py" tile `
  --input "_art_workspace/raw_gen/tiles/stone_floor.png" `
  --output "raw_assets/common/tiles/stone_floor.png" `
  --axis xy --size 512 --auto-blend 28 `
  --preview "_art_workspace/qa/tile_check_stone_floor.png"

# For structured brick/plank tiles where blending blurs cracks:
# export --offset-out "_art_workspace/qa/offset_brick.png", have your image generator repaint ONLY
# the centered seam cross cleanly, then run `tile --offset-out` again to shift back!

# 3. Auto-detect Bring Engine 9-slice [Left, Bottom, Right, Top] borders & stretch QA
python "$artTools/image_prep.py" slice9 `
  --input "raw_assets/common/ui/panel_main.png" `
  --preview "_art_workspace/qa/slice9_panel_main.png"
```

### Step 5 — Automated Audit + Multimodal Consistency QA Gate

```powershell
python "$artTools/image_prep.py" audit `
  --input "raw_assets/common" `
  --bible "_art_workspace/art_bible.json" `
  --json-out "_art_workspace/qa/audit_report.json"

python "$artTools/qa_board.py" `
  --bible "_art_workspace/art_bible.json" `
  --assets "raw_assets/common" `
  --out "_art_workspace/qa/consistency_board.png"
```

Visually inspect `_art_workspace/qa/consistency_board.png`, every `tile_check_*.png`, and every `slice9_*.png`:
- Outline weight, ink color, brushwork, and lighting angle match the locked anchors.
- Zero green/magenta edge halos on both checker and `bg_mid` game backdrop halves.
- Seamless tiles show zero grid seams across the 2×2 preview (and low seam energy `< 18`).
- 9-slice UI corners remain razor-sharp under wide and tall stretches.
- Uniform texel/brush scale across props, icons, and characters.

Regenerate or re-prep any outlier until the entire board reads as one cohesive studio game.

### Step 6 — Handoff to `Rig2D` for Animated 2D Characters

For articulated skeletal characters, enemies, or bosses, follow [`docs/agents/20-2d-skeletal-rigging.md`](20-2d-skeletal-rigging.md) directly using `art_bible_tool.py`'s built-in Rig2D prompt categories so every rigged character matches the rest of the game 100%:

```powershell
# Stage 1 — Full-body neutral hero anchor (automatically references anchors/style_anchor_characters.png)
python "$artTools/art_bible_tool.py" prompt `
  --bible "_art_workspace/art_bible.json" `
  --category rig_hero_anchor `
  --subject "armored lantern knight in dark steel plate and crimson cloak"

# Stage 2 — Anchor-locked isolated modular parts with rounded joint overlap caps
python "$artTools/art_bible_tool.py" prompt `
  --bible "_art_workspace/art_bible.json" `
  --category rig_modular_part `
  --extra-ref "_art_workspace/raw_gen/hero/hero_anchor.png" `
  --subject "front armored arm and gauntlet hanging straight down vertically with rounded shoulder overlap cap"
```

Then run `BringRig2D prep` → author `<slug>.rig2d.json` → `BringRig2D mesh` → `validate` → `render` visual QA loop (`bind_pose.png`, `bind_pose_overlay.png`, `mesh_weights.png`, `strip_*.png`, `preview_*.gif`), and drop the cleaned `parts/`, `fx/`, and `<slug>.rig2d.json` inside `raw_assets/<bundle>/` so `BringPacker` compiles them into the exact same BC3 atlas & `.bpack` bundle.

### Step 7 — Emit `pack.json`, Compile `.bpack` & Load in Bring Engine

```powershell
python "$artTools/pack_manifest.py" `
  --bundle-dir "raw_assets/common" `
  --wrap-glob "tiles/" `
  --wrap-glob "parallax/" `
  --slice9 "ui/panel_main.png=auto" `
  --slice9 "ui/button_primary.png=auto" `
  --pack-out "assets/common.bpack" `
  --bring-packer-proj "$sdk/tools/BringPacker/BringPacker.csproj"

# Mandatory final-build guardrail (0 loose PNG/JPGs allowed in dist/)
dotnet run --project "$sdk/tools/BringPacker/BringPacker.csproj" -c Release -- --verify-no-loose-textures dist
```

Load and draw everything in **Zero-OOP / Zero-GC** Bring Engine game code (see [`13-asset-compression-atlases-and-9slice.md`](13-asset-compression-atlases-and-9slice.md) and [`20-2d-skeletal-rigging.md`](20-2d-skeletal-rigging.md)):

```csharp
// 1. Register compressed .bpack bundle once at startup
BundleId common = BundleSystem.RegisterPack(ref bundles, "common", 16 * 1024 * 1024, "assets/common.bpack");

// 2. Resolve cached handles ONCE inside Scene OnLoad (never per frame!)
SpriteRegionData* panelReg = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("ui/panel_main"));
SpriteRegionData* iconReg  = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("icons/potion_hp"));
SpriteRegionData* propReg  = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("props/iron_lantern"));
TextureHandle     tileTex  = AssetSystem.FindTexture(in world.Assets, StringId.Hash("tiles/stone_floor"));
Rig2DAssetHandle  heroRig  = Rig2DSystem.FindAsset(in world.Rigs, "hero");

// 3. 9-sliced UI panel + atlas icon merge into ONE single instanced GPU draw call!
SpriteData panel = RenderComponentSystem.DefaultUiSlicedPanel(panelEnt, in *panelReg, new Vec2(360f, 160f), ctx.Scene, orderInLayer: 10);
SpriteData icon  = RenderComponentSystem.SpriteFromRegion(iconEnt, in *iconReg, new Vec2(48f, 48f), ctx.Scene, orderInLayer: 11);
RenderComponentSystem.AddSprite(ref world.RenderComponents, in panel);
RenderComponentSystem.AddSprite(ref world.RenderComponents, in icon);
```

---

## 5. Optional External Image Providers (`provider_adapter.py`)

Default `"provider": "builtin"` requires zero API keys. To route all assets — or specific categories like vector UI icons — to an external API, set the provider in `art_bible.json` and export the corresponding key:

| Provider | Best For | Env Vars | `art_bible.json` Config (`backend.external_config`) |
|---|---|---|---|
| `builtin` *(default)* | Instant per-game style switching, 0 API keys, HD hand-drawn sprites/props/tiles/UI | None | — |
| `recraft` | Crisp vector SVGs & style-locked UI kits/icons | `RECRAFT_API_KEY` | `recraft_style`, `recraft_style_id` |
| `fal` | Custom open-weight Flux + Style LoRA pipelines | `FAL_KEY` | `fal_model`, `fal_lora_url` |
| `scenario` | Studio house-style custom-trained models (200+ assets in one fixed style) | `SCENARIO_API_KEY`, `SCENARIO_API_SECRET` | `scenario_model_id` |

```powershell
# Check configured providers
python "$artTools/provider_adapter.py" check

# Generate via external provider using the exact locked prompt from art_bible_tool.py
python "$artTools/provider_adapter.py" generate `
  --bible "_art_workspace/art_bible.json" `
  --provider recraft `
  --prompt "<compiled prompt>" `
  --out "_art_workspace/raw_gen/icons/potion_hp.png" `
  --width 1024 --height 1024
```
