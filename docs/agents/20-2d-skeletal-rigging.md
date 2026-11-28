# Agent Guide 20 — 2D Skeletal Rigging, Deformable Meshes & Zero-Editor AI Authoring (`Rig2D`)

> **Scope**: Spine-grade, Zero-OOP / Zero-GC 2D skeletal rigging & deformable mesh animation (`BringEngine.Simulation.Rig2D`), single-draw-call batched rendering (`rig_2d.hlsl`), `.bpack` integration (`PackEntryKind.Rig2D`), the zero-editor `tools/BringRig2D/` AI authoring CLI, and the **complete self-contained AI asset-creation workflow** (usable by any coding agent without external skills or human GUI editors).

---

## 1. Architecture & Non-Negotiable Data-Oriented Guarantees

Every runtime piece of `Rig2D` strictly follows Bring Engine's data-oriented design:

- **Zero-OOP & Unmanaged**: Zero class inheritance, zero `virtual`/`override`/`abstract`/interface dispatch, zero closures. All asset and instance data live in flat `[StructLayout(LayoutKind.Sequential)]` unmanaged structs (`Rig2DAssetData`, `Rig2DInstanceData`, `Rig2DTable`, `Rig2DAffine2D`, `Rig2DMeshVertex`); all runtime logic lives in `public static unsafe class Rig2DSystem` and `Rig2DLoader`.
- **32-Bit Generational Handles & Dense Storage**: `Rig2DAssetHandle` (flat slices in `BundleArena`) and `Rig2DInstanceHandle` (swap-back dense `Instances` array + slot-to-dense table in `EngineWorld.Rigs`).
- **Explicit Native Allocators, 0 GC & 0 Native Leaks**:
  - `Rig2DTable` and its 4 coalescing range pools (`BonePool`, `SlotPool`, `SpringPool`, `OverridePool` — `Rig2DRangePool`) live inside `EngineAllocators.Persistent`.
  - Per-frame FFD vertex deform deltas (`FrameDeformDeltas`) live inside `EngineAllocators.Frame` (generation-validated like `AnimationTable.FrameJointPalette`).
  - Temporary per-step bone poses, timeline evaluations, and constraint scratch buffers live inside `EngineAllocators.Scratch` (marker-rolled back every step).
  - `Rig2DSystem.StepAll` and `BatchRenderer.ExtractRenderPacket` / `SubmitRenderPacket` allocate **0 GC bytes/frame** and exit with **`NativeMemoryTracker.LiveBytes == 0`**.
  - *(Only offline authoring/packing — `Rig2DCompiler` JSON→`BRIG` and the `tools/BringRig2D/` CLI — uses managed buffers at asset build time, identical to `BringPacker`.)*
- **Deterministic & Sim/View Split**: Uses `DetMath` throughout; spring/cloth physics run fixed-substepped (`1/60s` with `8/60s` hitch clamp) so replays and headless runs are bit-identical across OSes. Zero SDL3 or GPU calls occur inside `Simulation/Rig2D/`.
- **2-Thread Pipeline & Single-Draw-Call GPU Batching**:
  - `Rig2DSystem.StepAll` runs purely on the Logic Thread inside `EngineWorldSystem.StepCoreSystems` (right after `AnimationSystem.StepAll`).
  - `BatchRenderer.ExtractRenderPacket` writes posed 32-byte `Rig2DVertexGpu` vertices, `uint` indices, and `Rig2DBatchCmd`s into `RenderPacket`.
  - `SubmitRenderPacket` uploads them inside Bring Engine's **existing single `SDL_GPUCopyPass`** (`cycle: true`) and merges all consecutive slots & rig instances sharing an atlas texture, shader, blend mode (`AlphaBlend` / `Additive` / `Multiply`), and depth test into **1 GPU draw call**.
- **Automatic Lifecycle Cleanup**:
  - `EngineWorldSystem.DestroyEntity` -> `Rig2DSystem.RemoveByEntity`
  - `EngineWorldSystem.DestroySceneObjects` -> `Rig2DSystem.RemoveByScene`
  - `BundleSystem.Acquire` rollback / `BundleSystem.Release` / `BundleSystem.UnloadAll` -> `Rig2DSystem.UnregisterByBundle`

---

## 2. Runtime Features & API (`Rig2DSystem`)

### Supported Rig Features
- **Bones**: Up to 255 bones per rig, topologically sorted (`parent < child`), with all 4 `Rig2DInheritMode`s (`Normal`, `OnlyTranslation`, `NoRotation`, `NoScale`) and `ShearX`/`ShearY`.
- **Slots & Skins**: Back-to-front painter's draw order, per-slot two-color tint (`LightColor` + `DarkColor`), `AlphaBlend` / `Additive` / `Multiply` (alpha-safe multiply via negative `AlphaCutoff`), named skins with automatic fallback to `default` skin, and runtime `SetSlotAttachment` / `ClearSlotAttachmentOverride`.
- **Attachments**:
  - `Region`: rigid textured quad bound to slot bone.
  - `WeightedMesh`: up to 4 bone weights per vertex (`Rig2DMeshVertex`, 40B) + animated FFD vertex deformation (`MeshDeform` timelines).
  - `Sequence`: multi-frame flipbook VFX slot (`Hold`, `Loop`, `PingPong` auto-playback or keyframed `SlotSequenceFrame`).
- **Constraints** (evaluated in unified `order`):
  - **1-Bone & 2-Bone Analytic IK**: `bendDirection` (`+1`/`-1`), `softness` knee easing, `stretch`, `compress`, animated `mix`.
  - **Transform Constraints**: world or local, absolute or relative, per-channel mixes & offsets.
  - **Deterministic Spring / Cloth Physics**: fixed-substepped damped harmonic oscillator driven by bone/entity motion inertia, `gravity`, and `wind`, clamped to `maxAngle` (bit-identical across runs).
- **8-Track Animation Mixer**: tracks `0..7` supporting smooth crossfade (`mixDuration`, preserving source `FromLoop` independently of target `Loop`), smooth track fade-out (`ClearTrack(ref rigs, inst, track, fadeOutDuration)`), runtime weight/speed tuning (`SetTrackAlpha`, `SetTrackSpeed`), additive layering (`additive: true`), `Linear` / `Step` / `CubicBezier` curves, `DrawOrder` swaps, and per-frame `Rig2DFiredEvent` notifications (`GetFiredEvents`).
- **Gameplay Interop**: `TryGetBoneWorldPose` (weapon/VFX sockets) and `SetIkTargetWorld` / `SetBoneOverride` (live aim/foot planting).

### Canonical Game Code Example
```csharp
// 1. Load compressed .bpack (contains BC3 atlas + compiled BRIG binary, zero loose PNGs)
BundleId b = BundleSystem.RegisterPack(ref bundles, "hero", 4 * 1024 * 1024, "assets/hero.bpack");
BundleSystem.Acquire(ref bundles, b, ref world, ref alloc.Scratch);

// 2. Spawn rigged character entity (works in both 2D Orthographic UI and 3D Perspective cameras)
Rig2DAssetHandle asset = Rig2DSystem.FindAsset(in world.Rigs, "crimson_warden");
EntityId ent = EntitySystem.Spawn(ref world.Entities, scene);
TransformSystem.Add(ref world.Transforms, ent, new Vec3(0f, 0f, 0f));

Rig2DInstanceHandle rig = Rig2DSystem.AddInstance(ref world.Rigs, ent, asset, scene);
// In pixel-unit 2D Orthographic cameras (CreateOrthographicUI), set Scale = (ppu * zoom, ppu * zoom):
// Rig2DSystem.TryGetInstance(in world.Rigs, rig)->Scale = new Vec2(100f, 100f);
Rig2DSystem.SetAnimation(ref world.Rigs, rig, "idle", track: 0, loop: true, mixDuration: 0.15f);

// 3. Runtime skin swap, flip, hit-flash, IK world aim & event polling (0 GC!)
Rig2DSystem.SetSkin(ref world.Rigs, rig, "runic_halberd");
Rig2DSystem.SetFlip(ref world.Rigs, rig, flipX: false, flipY: false);
Rig2DSystem.SetHitFlash(ref world.Rigs, rig, 0.35f);
Rig2DSystem.SetIkTargetWorld(ref world.Rigs, rig, "foot_front_ik", in world.Transforms, new Vec3(0.4f, 0.0f, 0f));

// Inside Logic tick after EngineWorldSystem.StepCoreSystems(...):
ReadOnlySpan<Rig2DFiredEvent> events = Rig2DSystem.GetFiredEvents(in world.Rigs);
for (int i = 0; i < events.Length; i++)
{
    if (events[i].EventName == StringId.Hash("hit_frame"))
    {
        if (Rig2DSystem.TryGetBoneWorldPose(in world.Rigs, rig, "weapon", in world.Transforms, out Vec3 pos, out _, out _))
            ParticleSystem.Burst(ref world.Particles, slashEmitter, pos, 12);
    }
}
```

---

## 3. Zero-Editor AI Asset-Creation Workflow (For Any Agent)

> **Where to author assets**: Never store game-specific art or rigs inside the `bring-engine` repository. Author assets either directly inside your game's `raw_assets/<bundle>/` folder (so `BringPacker` compiles them straight into `assets/<bundle>.bpack`) or in a standalone asset workspace (e.g. `<assets-root>/<slug>/`).

Follow these 7 steps to generate high-definition, consistent, Spine-grade 2D animated characters, creatures, or props with **zero human GUI editor**:

### Step 1 — Anchor-First Visual Consistency (`raw_gen/hero_anchor.png`)
- Generate **one full-body high-res neutral reference sprite first** (`raw_gen/hero_anchor.png`): clean 3/4 side-facing combat stance facing **right (`+X`)**, limbs slightly separated so silhouettes read clearly, on a **flat solid chroma background** (`#00FF00` for dark/warm/steel/crimson palettes, or `#FF00FF` if the character has bright greens) with **zero floor shadows, gradients, or backdrop glow**.
- Visually inspect `hero_anchor.png` before continuing. Never generate body parts independently from text alone.

### Step 2 — Anchor-Locked Modular Parts (`raw_gen/parts/` & optional `raw_gen/fx/`)
- Condition **every** isolated body part, alternate skin weapon/armor, and VFX smear on `hero_anchor.png` so brush strokes, ink weight, palette, lighting direction, and proportions stay 100% locked.
- Typical biped decomposition (8–11 parts): `head`, `torso`, `cape` (or hair/scarf for weighted mesh + springs), `arm_front_upper`, `arm_front_lower`, `arm_back_upper`, `arm_back_lower`, `thigh_front`, `shin_front` (reusable for back leg with darker slot tint), `weapon`, plus optional alternate skin weapons and 4-frame `fx/slash_0..3` Additive sequence sprites.
- **Critical**: Paint **rounded, extended overlap joint caps** at every shoulder, elbow, hip, knee, and neck pivot so rotating limbs ±90° never exposes transparent gaps.
- Keep each limb strictly upright/vertical on the same flat `#00FF00`/`#FF00FF` background and inspect every generated part before prepping.

### Step 3 — Deterministic Chroma Keying, Despill & Atlas Sizing (`BringRig2D prep`)
```bash
# Extract clean, halo-free RGBA cutouts and write exact pixel sizes to prep_manifest.json
BringRig2D prep --input raw_gen/parts --output raw/parts --key auto --tolerance 65 --feather 28 --despill --trim --pad 4 --manifest raw/prep_manifest.json
```
- `BringRig2D prep` uses border flood-fill + tight interior hole keying + 3 px edge-band despill by default (pass `--global-key` when the character contains zero background hue and has tattered interior holes, or `--split-islands` for multi-part sheets).
- **Single-Atlas Budget (1 GPU Draw Call)**: Downscale oversized AI part cutouts (e.g. `1024px`) to ~`384–512px` max dimension (still 2×+ HD supersampled in-game) so all parts + sequence frames fit into **one `2048×2048` BC3 atlas** (`atlases=1`).
- **Sequence FX alignment**: Keep all frames of a flipbook sequence (`fx/slash_0..3`) on the **exact same pixel canvas dimensions** (do not independently trim each frame) so the animation stays spatially locked.
- Read `raw/prep_manifest.json` for exact part `width × height` aspect ratios before authoring `.rig2d.json`.

### Step 4 — Author Declarative `.rig2d.json` (Coordinate & Angle Rules)
Bootstrap from `BringRig2D template --type biped|quadruped|creature|prop --name <slug> --out raw/<slug>.rig2d.json` or write directly using Section 4 below.

- **Skeleton space**: Pixel units (`pixelsPerUnit: 100`), `+X` right (facing right), `+Y` up, `root` at `(0, 0)` on the ground baseline between the feet.
- **Angles**: Degrees counter-clockwise (`0° = +X`, `+90° = +Y`, `-90° = -Y`). A bone extends along its local `+X` from `(0, 0)` to `(length, 0)`.
- **Attachment rotation rule of thumb**:
  - Upward bones (`torso`, `head`, `weapon` pointing `+90°` up from base/hilt to top/tip): set attachment **`"rotation": -90`**.
  - Downward hanging limb PNGs (`arm_*`, `thigh_*`, `shin_*` with parent joint at top of image and child joint at bottom): set attachment **`"rotation": 90`** so the limb automatically rotates along the bone's `+X` axis.
  - Arbitrary bind-angle meshes (`cape` with `cape_0` at `worldRot`): set **`"rotation": -worldRot`** to keep the image visually upright at bind pose.
- **Never hand-write mesh vertices or bone weights**: Declare `"type": "mesh"` + `"autoMesh": { "bones": [...], "density": 24, "outlineMargin": 2, "smoothIterations": 3, "weightPins": [...] }` and let `BringRig2D mesh` generate the triangulated silhouette mesh and smooth normalized bone weights.
- **2-bone leg IK**: Set `bendDirection` (`-1` for right-facing forward knee bend in standard `-Y` legs; verify visually in `strip_walk.png`), `softness: 4–8`, and animate `foot_front_ik` / `foot_back_ik` targets cleanly along the `y = 0` baseline.

### Step 5 — Auto-Mesh, Validate & Headless Software Visual QA (`BringRig2D`)
```bash
BringRig2D mesh     --rig raw/<slug>.rig2d.json --images raw/
BringRig2D validate --rig raw/<slug>.rig2d.json --images raw/
BringRig2D render   --rig raw/<slug>.rig2d.json --images raw/ --out qa/ --width 512 --height 512 --frames 8 --fps 12
```
- Confirm `validate` exits `0` with **0 errors**.
- `BringRig2D render` runs the **exact `BringEngine.Simulation.Rig2D.Rig2DSystem` solver** headlessly on CPU and emits:
  - `qa/bind_pose.png` (clean silhouette on checker backdrop)
  - `qa/bind_pose_overlay.png` (bone hierarchy, pivot markers, labels, attachment boxes, pixel grid & `y = 0` ground line)
  - `qa/mesh_weights.png` (triangulated wireframe + per-bone color-coded weight heatmap)
  - `qa/strip_<anim>.png` (8-frame contact sheets with timestamps & fired event badges like `FOOTSTEP` / `HIT_FRAME`)
  - `qa/preview_<anim>.gif` (animated previews)

### Step 6 — Mandatory Visual QA Self-Correction Loop
Inspect `bind_pose.png`, `bind_pose_overlay.png`, `mesh_weights.png`, and every `strip_<anim>.png`:
1. Verify silhouette matches `hero_anchor.png`, zero floating joint gaps, flat boots seated on `y = 0`, and zero chroma fringe.
2. Verify `mesh_weights.png` transitions smoothly along the cloth/cape chain (avoid including distant torso bones that pull lower cloth vertices).
3. Verify knee/elbow bend directions, weapon arcs staying cleanly above ground, and readable event timing (`FOOTSTEP`, `HIT_FRAME`, Additive `slash_fx` sweep).
4. Edit `.rig2d.json`, re-run `mesh` → `validate` → `render`, and re-inspect until clean (typically 1–3 fast iterations).

### Step 7 — Pack Zero-Loose-Texture `.bpack` & Verify on GPU
```bash
# Standalone rig bundle (or run BringPacker on the game's raw_assets/<bundle>/ folder)
BringRig2D pack --input raw/ --output assets/<slug>.bpack --compression brotli --atlas-compression auto --max-atlas 2048
BringPacker --verify-no-loose-textures dist
```
- `AssetPackBuilder.BuildPackFromDirectory` (shared by `BringPacker` and `BringRig2D pack`) automatically discovers every `*.rig2d.json`, compiles it via `Rig2DCompiler.CompileFromFile` into a `PackEntryKind.Rig2D` (`BRIG`) binary entry named by relative path without `.rig2d.json`, and atlases/compresses all part `.png`s into BC1/BC3 `SpriteRegionData`.
- Attachment `"image"` paths (e.g. `"parts/torso"` or `"parts/torso.png"`) are normalized automatically (`\` -> `/`, extension stripped).

---

## 4. Complete `.rig2d.json` Schema Reference

```jsonc
{
  "name": "crimson_warden",
  "pixelsPerUnit": 100,
  "origin": [0, 0],
  "size": [420, 460],

  "bones": [
    {
      "name": "root",
      "x": 0, "y": 0, "rotation": 0,
      "scaleX": 1, "scaleY": 1, "shearX": 0, "shearY": 0,
      "length": 0,
      "inherit": "normal" // "normal" | "onlyTranslation" | "noRotation" | "noScale"
    },
    { "name": "hip",   "parent": "root", "x": 0, "y": 162, "rotation": 0,  "length": 30 },
    { "name": "torso", "parent": "hip",  "x": 0, "y": 0,   "rotation": 90, "length": 132 }
  ],

  // Array order IS base painter's draw order (index 0 drawn first = farthest back)
  "slots": [
    {
      "name": "cape",
      "bone": "cape_0",
      "attachment": "cape_mesh",
      "color": "#FFFFFFFF",      // Optional [r,g,b,a] float 0..1 OR "#RRGGBB" / "#RRGGBBAA"
      "darkColor": "#00000000",  // Optional two-color dark tint
      "blend": "alpha"           // "alpha" (default) | "additive" | "multiply"
    },
    { "name": "slash_fx", "bone": "slash_origin", "attachment": "", "blend": "additive" }
  ],

  // "default" skin is always base; alternate skins only override changed slots
  "skins": {
    "default": {
      "cape": {
        "cape_mesh": {
          "type": "mesh",
          "image": "parts/cape",
          "x": 122, "y": 38, "rotation": 135, "width": 182, "height": 250,
          "autoMesh": {
            "density": 24, "outlineMargin": 2, "smoothIterations": 4,
            "bones": ["cape_0", "cape_1", "cape_2"],
            "weightPins": [
              { "bone": "cape_0", "shape": "circle", "x": -24, "y": 282, "radius": 62, "weight": 0.96 }
            ]
          }
        }
      },
      "weapon": {
        "weapon": { "type": "region", "image": "parts/weapon_sword", "x": 103, "y": 0, "rotation": -90, "width": 84, "height": 290 }
      },
      "slash_fx": {
        "slash_seq": {
          "type": "sequence",
          "frames": ["fx/slash_0", "fx/slash_1", "fx/slash_2", "fx/slash_3"],
          "fps": 20, "loop": "hold", "startIndex": 0,
          "x": 24, "y": 8, "rotation": 0, "width": 266, "height": 282
        }
      }
    },
    "runic_halberd": {
      "weapon": {
        "weapon": { "type": "region", "image": "parts/weapon_halberd", "x": 76, "y": 0, "rotation": -90, "width": 99, "height": 330 }
      }
    }
  },

  "constraints": {
    "ik": [
      {
        "name": "leg_front_ik", "order": 0,
        "bones": ["thigh_front", "shin_front"], "target": "foot_front_ik",
        "mix": 1.0, "softness": 6.0, "bendDirection": -1, "stretch": false, "compress": false
      }
    ],
    "transform": [
      {
        "name": "pauldron_follow", "order": 1, "bone": "pauldron", "target": "arm_front_upper",
        "mixRotate": 0.4, "mixX": 0, "mixY": 0, "mixScaleX": 0, "mixScaleY": 0, "mixShearY": 0,
        "offsetRotate": 0, "local": true, "relative": true
      }
    ],
    "spring": [
      {
        "name": "cape_spring_1", "order": 2, "bone": "cape_1",
        "inertia": 0.65, "strength": 55, "damping": 5.5, "mass": 1.0,
        "gravity": 95, "wind": -22, "maxAngle": 24, "mix": 1.0, "rotateScale": 1.0
      }
    ]
  },

  "events": {
    "footstep":  { "int": 0, "float": 1.0, "string": "plate_step" },
    "hit_frame": { "int": 1, "float": 1.0, "string": "crimson_cleave" }
  },

  "animations": {
    "attack": {
      "duration": 0.8,
      "bones": {
        // Bone translate/rotate/shear keys are RELATIVE DELTAS onto setup pose; scale keys multiply setup scale (1.0 = setup)
        "torso": {
          "rotate": [
            { "time": 0.0,  "value": 0,   "curve": "easeOut" },
            { "time": 0.22, "value": 16,  "curve": "easeIn" },
            { "time": 0.34, "value": -20, "curve": "easeOutCubic" },
            { "time": 0.8,  "value": 0 }
          ]
        }
      },
      "slots": {
        "slash_fx": {
          "attachment": [ { "time": 0.0, "name": "" }, { "time": 0.26, "name": "slash_seq" }, { "time": 0.56, "name": "" } ],
          "sequence":   [ { "time": 0.26, "frame": 0 }, { "time": 0.33, "frame": 1 }, { "time": 0.41, "frame": 2 }, { "time": 0.49, "frame": 3 } ],
          "color":      [ { "time": 0.26, "color": "#FFFFFFFF" }, { "time": 0.56, "color": "#FFFFFF00" } ]
        }
      },
      "drawOrder": [ { "time": 0.0, "offsets": [] } ],
      "events":    [ { "time": 0.33, "name": "hit_frame", "int": 1, "string": "crimson_cleave" } ]
    }
  }
}
```

Supported named curves: `"linear"`, `"step"` / `"stepped"`, `"easeIn"`, `"easeOut"`, `"easeInOut"`, `"easeOutCubic"`, `"easeInOutSine"`, `"easeOutQuad"`, or explicit cubic Bezier `[cx1, cy1, cx2, cy2]`.

---

## 5. `BringRig2D` CLI Quick Reference (`tools/BringRig2D/`)

Shipped inside every `bring-sdk-vX.Y.Z.zip` with zero external ML/Node dependencies:

```bash
BringRig2D template --type biped|quadruped|creature|prop --name <slug> --out <file.rig2d.json>
BringRig2D prep     --input <img|dir> --output <out> [--key auto|green|magenta|#RRGGBB] [--tolerance 65] [--feather 28] [--despill] [--global-key] [--trim] [--pad 4] [--split-islands] [--min-area 256] [--manifest <out.json>]
BringRig2D mesh     --rig <file.rig2d.json> [--images <dir>] [--density 24] [--margin 2] [--smooth 3] [--out <file.rig2d.json>]
BringRig2D validate --rig <file.rig2d.json> [--images <dir>]
BringRig2D render   --rig <file.rig2d.json> --out <dir> [--images <dir>] [--anim all|<name>] [--skin <name>] [--width 512] [--height 512] [--frames 8] [--fps 12] [--clean] [--overlay] [--weights] [--strip] [--gif] [--bg #161B2A]
BringRig2D pack     --input <raw_dir> --output <out.bpack> [--compression brotli|lz4|none] [--atlas-compression auto|bc1|bc3|none] [--max-atlas 2048] [--padding 2]
```
