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
   - `TweenSystem.TweenFloatPtr(ref tweens, &myState->ShieldAlpha, 0f, 1f, 0.35f, EaseType.OutCubic)`
   - `TweenSystem.TweenVec3Ptr`, `TweenColorPtr`, `TweenFix32Ptr`, `PtrVec2`, `PtrVec4`, `PtrQuat`, `PtrFixVec3`
   - Also supports `TweenTargetKind.CustomCallback` with unmanaged function pointers `delegate*<uint, Vec4, void>`!
   - **Warning**: NEVER pass pointers into swap-back compacted dense tables (`RenderComponentTables.Texts`, `RenderComponentTables.Sprites`, `TransformTable`, `CameraTable`, `TweenTable`) to `Tween*Ptr` — removing any sibling entry relocates dense elements and silently corrupts another slot! Use the handle-safe channels above (or `CustomCallback`) for dense engine components, and reserve `Tween*Ptr` for stable pinned structs (e.g. `GameSimState`, `GameViewState`, or fixed-slot `AssetRegistry.Materials`).

### Sequences, Loops, Curves & Scene Groups
```csharp
// Subtle, smooth emphasis (gentle 1.0 -> 1.08 -> 1.0 ease without harsh bounce)
var t1 = TweenSystem.TweenTransformScale(ref tweens, e, Vec3.One, new Vec3(1.08f, 1.08f, 1.08f), 0.14f, EaseType.OutCubic, scene: sceneId);
var t2 = TweenSystem.TweenTransformScale(ref tweens, e, new Vec3(1.08f, 1.08f, 1.08f), Vec3.One, 0.22f, EaseType.InOutSine, scene: sceneId);
TweenSystem.Chain(ref tweens, t1, t2); // t2 automatically waits for t1 to finish

// 31 built-in EaseType curves + custom CubicCurve
// Control & cleanup: Pause/Resume(handle), Kill(handle), KillByEntity(entity), SetGroupPaused/KillGroup(groupId), KillByScene(sceneId)
```

> **Smooth Motion & Restraint**: Prefer smooth, understated curves (`EaseType.OutCubic`, `EaseType.InOutSine`, `EaseType.OutQuad`) and modest deltas (`1.05x–1.12x` scale pops, clean fades/slides) over heavy elastic or overshoot curves (`OutBack`, `OutElastic`, `OutBounce`) unless exaggerated arcade/cartoon juice is specifically requested. Do not attach tweens to HUD/menu/label text unless text animation was explicitly asked for (see `07-text-fonts-and-text-effects.md`).

## Skeletal & Sprite Animation (`AnimationTable` + `AnimationSystem`)
- **Multi-Track 3D glTF Skeletal Animation (4 Tracks `0..3`, Zero GC)**:
  - `AnimPlayerHandle player = AnimationSystem.AddPlayer(ref anims, entity, skeletonHandle, clipHandle, sceneId)`
  - Generation-safe 32-bit `AnimPlayerHandle` (`TryGetPlayer`, `RemovePlayer`, `Crossfade`, `SetTrack`, `SetTrackWeight`, `SetTrackSpeed`, `SetTrackJointMask`, `ClearTrackJointMask`, `ClearTrack`, `BlendTwoClips`).
  - Supports `Step`, `Linear`, and `CubicSpline` channels, independent `FromLoop`/`Loop` crossfades (`Crossfade(..., bool nextLoop)`), 128-joint bitmask upper/lower-body layering (`SetTrackJointMask`), additive delta layers (`additive: true`), smooth track fade-out (`ClearTrack(..., fadeOutDuration)`), and continuous 2-clip locomotion blending (`BlendTwoClips` without resetting clip clocks):
    ```csharp
    // Continuous Idle <-> Run blend on Tracks 0 & 1 (preserves both clip clocks!)
    AnimationSystem.BlendTwoClips(ref world.Animations, player, idleClip, runClip, speedRatio);

    // Upper-body attack overlay on Track 2 with joint mask, then smooth fade-out
    AnimationSystem.SetTrack(ref world.Animations, player, track: 2, attackClip, loop: false, crossfadeDuration: 0.1f);
    AnimationSystem.SetTrackJointMask(ref world.Animations, player, track: 2, upperBodyMaskLo);
    AnimationSystem.ClearTrack(ref world.Animations, player, track: 2, fadeOutDuration: 0.2f);
    ```
  - `AnimationSystem.StepAll(ref anims, dt, ref frameArena, ref scratchArena)` (4-arg overload, or 5-arg `StepAll(..., RenderComponentTables* renderComponents)` to also auto-update bound `TargetSprite` UVs — invoked automatically by `EngineWorldSystem.StepCoreSystems`) writes the final skinning joint matrices into `anims.FrameJointPalette` inside `FrameArena` for single-copy-pass GPU upload.
- **Atlas-Aware 2D Sprite Flipbook Animation**:
  - Register a full-texture clip with `RegisterSpriteClip` OR slice a flipbook grid strictly inside a packed atlas `SpriteRegionData` with `RegisterSpriteClipFromRegion` (`SpriteAnimLoopMode.Hold` / `Loop` / `PingPong`, deduplicated automatically by `Name`, scene, bundle, grid, `Fps`, loop mode, and `BaseUvRect`):
    ```csharp
    SpriteRegionData reg = AssetSystem.FindSpriteRegion(in world.Assets, StringId.Hash("fx/coin_sheet"));
    SpriteHandle sprite = RenderComponentSystem.AddSprite(ref world.RenderComponents,
        RenderComponentSystem.SpriteFromRegion(entity, in reg, new Vec2(1f, 1f), sceneId));

    SpriteAnimClipHandle clip = AnimationSystem.RegisterSpriteClipFromRegion(
        ref world.Animations, StringId.Hash("coin_spin"), in reg,
        columns: 4, rows: 2, fps: 12f, loopMode: SpriteAnimLoopMode.PingPong, scene: sceneId);

    // Direct TargetSprite binding auto-updates SpriteData.UvRect inside EngineWorldSystem.StepCoreSystems (0 GC!)
    SpriteAnimPlayerHandle sp = AnimationSystem.AddSpritePlayer(ref world.Animations, entity, clip, sceneId, targetSprite: sprite);
    // Switch clips or rebind at runtime: AnimationSystem.SetSpriteClip(...) / AnimationSystem.BindSpritePlayer(...)
    ```
  - Without a `TargetSprite`, you can still read `AnimationSystem.TryGetSpritePlayer(in anims, sp)->CurrentUvRect` during view sync.
