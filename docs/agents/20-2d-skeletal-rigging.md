# Agent Guide 20 — 2D Skeletal Rigging, Deformable Meshes & `BringRig2D` CLI (`Rig2D`)

> **Scope**: Spine-grade, Zero-OOP / Zero-GC 2D skeletal rigging & deformable mesh animation (`BringEngine.Simulation.Rig2D`), single-draw-call batched rendering (`rig_2d.hlsl`), `.bpack` integration (`PackEntryKind.Rig2D`), and the zero-editor `tools/BringRig2D/` AI authoring CLI.

---

## 1. Architecture & Non-Negotiable Guarantees

- **Zero-OOP & Unmanaged**: All asset and instance data live in `[StructLayout(LayoutKind.Sequential)]` unmanaged structs (`Rig2DAssetData`, `Rig2DInstanceData`, `Rig2DTable`); all logic lives in `public static unsafe class Rig2DSystem`, `Rig2DCompiler`, and `Rig2DLoader`.
- **Generational Handles**: `Rig2DAssetHandle` (asset in `BundleArena`) and `Rig2DInstanceHandle` (live component in `EngineWorld.Rigs`).
- **Zero-GC & Zero-Leak**:
  - `Rig2DTable` and its 4 coalescing range pools (`BonePool`, `SlotPool`, `SpringPool`, `OverridePool`) live inside `EngineAllocators.Persistent`.
  - Per-frame FFD vertex deltas (`FrameDeformDeltas`) live inside `EngineAllocators.Frame` (generation-validated like `AnimationTable.FrameJointPalette`).
  - `Rig2DSystem.StepAll` and `BatchRenderer.ExtractRenderPacket` / `SubmitRenderPacket` allocate **0 GC bytes** and exit with **`NativeMemoryTracker.LiveBytes == 0`**.
- **2-Thread Pipeline Safe**: `Rig2DSystem.StepAll` runs purely on CPU inside `EngineWorldSystem.StepCoreSystems` (right after `AnimationSystem.StepAll`). `BatchRenderer.ExtractRenderPacket` copies posed 32-byte `Rig2DVertexGpu` vertices, `uint` indices, and `Rig2DBatchCmd`s into `RenderPacket`; `SubmitRenderPacket` uploads them in the **existing single `SDL_GPUCopyPass`** (`cycle: true`) and merges all consecutive slots & rigs sharing an atlas, shader, blend mode, and depth test into **1 GPU draw call**.
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
- **8-Track Animation Mixer**: tracks `0..7` supporting smooth crossfade (`mixDuration`), additive layering (`additive: true`), `Linear` / `Step` / `CubicBezier` curves, `DrawOrder` swaps, and per-frame `Rig2DFiredEvent` notifications (`GetFiredEvents`).
- **Gameplay Interop**: `TryGetBoneWorldPose` (weapon/VFX sockets) and `SetIkTargetWorld` / `SetBoneOverride` (live aim/foot planting).

### Canonical Game Code Example
```csharp
// 1. Load compressed .bpack (contains BC3 atlas + compiled BRIG binary, zero loose PNGs)
BundleId b = BundleSystem.RegisterPack(ref bundles, "hero", 4 * 1024 * 1024, "assets/hero.bpack");
BundleSystem.Acquire(ref bundles, b, ref world, ref alloc.Scratch);

// 2. Spawn rigged character entity
Rig2DAssetHandle asset = Rig2DSystem.FindAsset(in world.Rigs, "crimson_warden");
EntityId ent = EntitySystem.Spawn(ref world.Entities, scene);
TransformSystem.Add(ref world.Transforms, ent, new Vec3(0f, 0f, 0f));

Rig2DInstanceHandle rig = Rig2DSystem.AddInstance(ref world.Rigs, ent, asset, scene);
Rig2DSystem.SetAnimation(ref world.Rigs, rig, "idle", track: 0, loop: true, mixDuration: 0.15f);

// 3. Runtime skin swap, hit-flash, IK world aim & event polling (0 GC!)
Rig2DSystem.SetSkin(ref world.Rigs, rig, "runic_halberd");
Rig2DSystem.SetHitFlash(ref world.Rigs, rig, 0.35f);
Rig2DSystem.SetIkTargetWorld(ref world.Rigs, rig, "foot_front_ik", in world.Transforms, new Vec3(0.4f, 0.0f, 0f));

// Inside Logic tick after EngineWorldSystem.StepCoreSystems(...):
ReadOnlySpan<Rig2DFiredEvent> events = Rig2DSystem.GetFiredEvents(in world.Rigs);
for (int i = 0; i < events.Length; i++)
{
    if (events[i].EventName == StringId.Hash("hit_frame"))
    {
        if (Rig2DSystem.TryGetBoneWorldPose(in world.Rigs, rig, "weapon_tip", in world.Transforms, out Vec3 tipPos, out _, out _))
            ParticleSystem.Burst(ref world.Particles, slashEmitter, tipPos, 12);
    }
}
```

---

## 3. Declarative `.rig2d.json` Format & `.bpack` Packing

- `AssetPackBuilder.BuildPackFromDirectory` (used by both `BringPacker` and `BringRig2D pack`) automatically discovers every `*.rig2d.json`, compiles it via `Rig2DCompiler.CompileFromFile` into a `PackEntryKind.Rig2D` (`BRIG`) binary entry named by relative path without `.rig2d.json`, and atlases/compresses all part `.png` files into BC1/BC3 atlas pages (`SpriteRegionData`).
- Attachment `"image"` paths (e.g. `"parts/torso"` or `"parts/torso.png"`) are normalized automatically (`\` -> `/`, extension stripped) to match `SpriteRegionData` hashes.
- Supported named curves in keyframes: `"linear"`, `"step"`, `"easeIn"`, `"easeOut"`, `"easeInOut"`, `"easeOutCubic"`, `"easeInOutSine"`, `"easeOutQuad"`, or `[cx1, cy1, cx2, cy2]`.

---

## 4. `BringRig2D` Zero-Editor Authoring CLI (`tools/BringRig2D/`)

Shipped inside every `bring-sdk-vX.Y.Z.zip` with zero external ML/Node dependencies:

```bash
# 1. Generate a starter rig template (biped | quadruped | creature | prop)
BringRig2D template --type biped --name hero --out raw/hero.rig2d.json

# 2. Clean AI-generated part PNGs (auto/explicit chroma key, soft feather, despill, tight trim + pad, optional island split)
BringRig2D prep --input raw_gen/ --output raw/parts/ --key auto --despill --trim --pad 4 --manifest raw/prep_manifest.json

# 3. Auto-triangulate silhouette meshes & compute smooth bone weights (honoring autoMesh + weightPins)
BringRig2D mesh --rig raw/hero.rig2d.json --images raw/ --density 24 --margin 2 --smooth 3

# 4. Validate schema, bone topology, image presence, mesh quality, constraints & joint continuity
BringRig2D validate --rig raw/hero.rig2d.json --images raw/

# 5. Headless software visual QA driven by the EXACT Bring Rig2D solver:
#    Outputs bind_pose.png, bind_pose_overlay.png, mesh_weights.png, strip_<anim>.png, preview_<anim>.gif
BringRig2D render --rig raw/hero.rig2d.json --images raw/ --out qa/ --width 512 --height 512 --frames 8 --fps 12

# 6. Pack into compressed BC3 atlas + binary BRIG .bpack (zero loose PNGs!)
BringRig2D pack --input raw/ --output dist/hero.bpack
```
