# Agent Guide 09 — SoA Particle System & Custom HLSL Shaders

## High-Performance SoA Particles (`ParticleTable` + `ParticleSystem`)
Particles live in 64-byte aligned Structure-of-Arrays (`PosX/Y/Z`, `VelX/Y/Z`, `Size`, `Age`, `Lifetime`, `Color`, `Rotation`, etc.) for optimal L1/L2 cache utilization.

```csharp
var cfg = ParticleSystem.DefaultEmitter(new Vec3(0, 1, 0), seed: 42);
cfg.Shape = ParticleShape.Sphere;
cfg.BlendMode = ParticleBlendMode.Additive;
cfg.EmitRate = 20f;
cfg.StartColor = ColorF.Cyan;
cfg.EndColor = new ColorF(0.1f, 0.2f, 1f, 0f);
cfg.FollowEntity = playerEntity; // optional entity tracking
cfg.StretchedBillboard = 1;       // optional velocity/streak billboard stretch (RotExtra.y)

EmitterHandle emitter = ParticleSystem.CreateEmitter(ref particles, in cfg, sceneId);

// Trigger crisp, controlled impact/burst effects deterministically:
ParticleSystem.Burst(ref particles, emitter, 16);

// Atlas-aware flipbook emitter with GPU cross-frame blending (particle.hlsl):
SpriteRegionData smokeReg = AssetSystem.FindSpriteRegion(in world.Assets, StringId.Hash("fx/smoke_4x4"));
var smokeCfg = ParticleSystem.DefaultEmitter(new Vec3(0, 0.5f, 0), seed: 99);
ParticleSystem.SetFlipbookRegion(
    ref smokeCfg, in smokeReg,
    columns: 4, rows: 4, frameCount: 14,
    mode: ParticleFlipbookMode.OverLifetime, // OverLifetime | Loop | Hold | PingPong | RandomStatic
    fps: 0f,                                 // Used by Loop/Hold/PingPong (<= 0 falls back to frameCount/lifetime)
    blendFrames: true,                       // Smooth GPU lerp(frame0, frame1, FrameBlend) in particle.hlsl
    randomStart: false);                     // Deterministic per-particle random start frame via em.Rng

// Simulation step (headless-compatible, uses per-emitter DetRng):
ParticleSystem.Step(ref particles, in transforms, dt);
```

- **Flipbook Fields on `ParticleEmitterData`**: `BaseUvRect` (auto-defaults to `(0,0,1,1)` when `Z <= 0 || W <= 0`), `FlipbookMode`, `FlipbookRandomStart`, `FlipbookBlend`, `FlipbookFrameCount` (excludes empty trailing cells in partial sprite sheets), and `FlipbookFps`.
- **Custom Particle Shaders**: `ParticleGpuInstance` is **80 bytes** (`float4 PosSize`, `float4 Color`, `float4 UvRect`, `float4 RotExtra` where `z = FrameBlend`, `float4 UvRectNext`) — match this layout in any custom `ParticleEmitterData.CustomShader`.

> **Visual Restraint**: Keep particle counts, sizes, and lifetimes restrained (`EmitRate` ~`10–25`, routine hit/pickup bursts ~`8–16`, reserving larger bursts for major impacts or explosions) so effects stay crisp and readable without flooding the viewport or obscuring gameplay silhouettes.

## Custom Shaders (`ShaderSystem` — Added in Phase 6-7)
Bring Engine uses **HLSL** as its unified cross-platform shader language, compiled automatically via **`SDL_shadercross`** into DXIL (D3D12), SPIR-V (Vulkan), or MSL (Metal) matching the active GPU backend:
- Built-in shaders: `pbr_mesh.hlsl`, `shadow_depth.hlsl`, `shadow_depth_mask.hlsl`, `sprite_3d.hlsl`, `sdf_text.hlsl`, `particle.hlsl`.
- Games can register custom HLSL shaders via `ShaderSystem.CompileFromFile(...)` / `ShaderSystem.CompileFromSource(...)` (and unload via `ShaderSystem.ReleaseShader(...)`) and attach them to:
  - 3D `MaterialAssetData.CustomShader` + `CustomParams` (`Vec4` animatable with `TweenSystem`!)
  - 2D `SpriteData.CustomShader` + `CustomParams`
  - SDF `TextData.CustomShader` + `CustomParams` (forwarded to `GlyphGpuInstance`)
  - `ParticleEmitterData.CustomShader`
