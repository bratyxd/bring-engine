# Agent Guide 09 — SoA Particle System & Custom HLSL Shaders

## High-Performance SoA Particles (`ParticleTable` + `ParticleSystem`)
Particles live in 64-byte aligned Structure-of-Arrays (`PosX/Y/Z`, `VelX/Y/Z`, `Size`, `Age`, `Lifetime`, `Color`, `Rotation`, etc.) for optimal L1/L2 cache utilization.

```csharp
var cfg = ParticleSystem.DefaultEmitter(new Vec3(0, 1, 0), seed: 42);
cfg.Shape = ParticleShape.Sphere;
cfg.BlendMode = ParticleBlendMode.Additive;
cfg.EmitRate = 60f;
cfg.StartColor = ColorF.Cyan;
cfg.EndColor = new ColorF(0.1f, 0.2f, 1f, 0f);
cfg.FollowEntity = playerEntity; // optional entity tracking
cfg.StretchedBillboard = 1;       // optional velocity/streak billboard stretch (RotExtra.y)

EmitterHandle emitter = ParticleSystem.CreateEmitter(ref particles, in cfg, sceneId);

// Trigger instant explosions/hits deterministically:
ParticleSystem.Burst(ref particles, emitter, 48);

// Simulation step (headless-compatible, uses per-emitter DetRng):
ParticleSystem.Step(ref particles, in transforms, dt);
```

## Custom Shaders (`ShaderSystem` — Added in Phase 6-7)
Bring Engine uses **HLSL** as its unified cross-platform shader language, compiled automatically via **`SDL_shadercross`** into DXIL (D3D12), SPIR-V (Vulkan), or MSL (Metal) matching the active GPU backend:
- Built-in shaders: `pbr_mesh.hlsl`, `shadow_depth.hlsl`, `shadow_depth_mask.hlsl`, `sprite_3d.hlsl`, `sdf_text.hlsl`, `particle.hlsl`.
- Games can register custom HLSL shaders via `ShaderSystem.CompileFromFile(...)` / `ShaderSystem.CompileFromSource(...)` (and unload via `ShaderSystem.ReleaseShader(...)`) and attach them to:
  - 3D `MaterialAssetData.CustomShader` + `CustomParams` (`Vec4` animatable with `TweenSystem`!)
  - 2D `SpriteData.CustomShader` + `CustomParams`
  - SDF `TextData.CustomShader` + `CustomParams` (forwarded to `GlyphGpuInstance`)
  - `ParticleEmitterData.CustomShader`
