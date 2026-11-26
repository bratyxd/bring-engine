# Agent Guide 08 — Universal Tweening & Skeletal/Sprite Animation

## Universal Data-Driven Tweening (`TweenTable` + `TweenSystem`)
Because everything in Bring Engine is flat unmanaged data, **one universal `TweenSystem` animates any engine or game property** with zero OOP, zero reflection, and zero GC allocations.

### Two Binding Modes
1. **Handle-Safe Engine Channels** (survives swap-back table compactions):
   - `TweenSystem.TweenTransformPos / Scale / Rot(...)`
   - `TweenSystem.TweenCameraFov(...)` (and `CameraPosition`, `CameraOrthoHeight`, `CameraOrbitYaw`, `CameraOrbitDistance`)
   - `TweenSystem.TextTypewriter(...)`, `TweenSystem.TextGlowIntensity(...)`, `TweenSystem.TextColor(...)` (takes `TextHandle`)
   - `TweenSystem.SpriteColor(...)` (takes `SpriteHandle`)
2. **Direct Unmanaged Pointer Binding** (animate ANY field of ANY **stable, non-compacted** struct in your game's native arenas!):
   - `TweenSystem.TweenFloatPtr(ref tweens, &myState->ShieldAlpha, 0f, 1f, 0.5f, EaseType.OutBack)`
   - `TweenSystem.TweenVec3Ptr`, `TweenColorPtr`, `TweenFix32Ptr`, `PtrVec2`, `PtrVec4`, `PtrQuat`, `PtrFixVec3`
   - Also supports `TweenTargetKind.CustomCallback` with unmanaged function pointers `delegate*<uint, Vec4, void>`!
   - **Warning**: NEVER pass pointers into swap-back compacted dense tables (`RenderComponentTables.Texts`, `RenderComponentTables.Sprites`, `TransformTable`, `CameraTable`, `TweenTable`) to `Tween*Ptr` — removing any sibling entry relocates dense elements and silently corrupts another slot! Use the handle-safe channels above (or `CustomCallback`) for dense engine components, and reserve `Tween*Ptr` for stable pinned structs (e.g. `GameSimState`, `GameViewState`, or fixed-slot `AssetRegistry.Materials`).

### Sequences, Loops, Curves & Scene Groups
```csharp
var t1 = TweenSystem.TweenTransformScale(ref tweens, e, Vec3.One, new Vec3(1.5f, 1.5f, 1.5f), 0.25f, EaseType.OutBack, scene: sceneId);
var t2 = TweenSystem.TweenTransformScale(ref tweens, e, new Vec3(1.5f, 1.5f, 1.5f), Vec3.One, 0.35f, EaseType.OutElastic, scene: sceneId);
TweenSystem.Chain(ref tweens, t1, t2); // t2 automatically waits for t1 to finish

// 31 built-in EaseType curves + custom CubicCurve
// Control & cleanup: Pause/Resume(handle), Kill(handle), KillByEntity(entity), SetGroupPaused/KillGroup(groupId), KillByScene(sceneId)
```

## Skeletal & Sprite Animation (`AnimationTable` + `AnimationSystem`)
- **glTF Skeletal Animation**: `AnimPlayerHandle player = AnimationSystem.AddPlayer(ref anims, entity, skeletonHandle, clipHandle, sceneId)`
  - Generation-safe 32-bit `AnimPlayerHandle` (`TryGetPlayer`, `RemovePlayer`, `Crossfade`).
  - Supports `Step`, `Linear`, and `CubicSpline` channels + `AnimationSystem.Crossfade(...)`.
  - `AnimationSystem.StepAll(ref anims, dt, ref frameArena, ref scratchArena)` writes the final skinning joint matrices into `anims.FrameJointPalette` inside `FrameArena` for single-copy-pass GPU upload.
- **2D Sprite Sheet Animation**: Register a `SpriteAnimClipData` (`Columns`, `Rows`, `StartFrame`, `FrameCount`, `Fps`, `Loop`, deduplicated automatically by `Name`) to receive a generation-safe 32-bit `SpriteAnimClipHandle`:
  ```csharp
  SpriteAnimClipHandle clip = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData { ... });
  SpriteAnimPlayerHandle sp = AnimationSystem.AddSpritePlayer(ref anims, entity, clip, sceneId);
  ```
  Read `AnimationSystem.TryGetSpritePlayer(in anims, sp)->CurrentUvRect` during view sync to update your sprite's UV sub-rect.
