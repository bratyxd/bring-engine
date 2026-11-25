# Agent Guide 02 — Deterministic Math, Handles & Data Conventions

## Coordinate System & Matrix Convention
- **Right-Handed, Y-Up**: `+X` Right, `+Y` Up, `-Z` Forward.
- **`Mat4`**: Column-major (`C0`, `C1`, `C2`, `C3` = 64 bytes), matching HLSL/Vulkan/D3D12/SDL3_gpu conventions.
- **Depth Range**: `[0, 1]` clip-space Z (required by `SDL3_gpu` across D3D12, Vulkan, and Metal).

## Deterministic Fixed-Point Math (`BringEngine.Core.Math`)
Use `Fix32` (Q16.16) / `Fix64` (Q32.32) / `FixVec2` / `FixVec3` / `FixQuat` for gameplay simulation logic that must be bit-identical across OSes and CPU architectures:
- Pure integer arithmetic (`+`, `-`, `*`, `/`, `Sqrt`, `Sin`, `Cos`, `Atan2`).
- Zero platform `libm` calls.
- Convert cheaply to float render types at the Simulation -> View boundary via `.ToFloat()`, `.ToVec3()`, `.ToQuat()`.

```csharp
Fix32 speed = Fix32.FromFloat(5.5f);
Fix32 dt = Fix32.FromRatio(1, 60);
FixVec3 pos = FixVec3.Zero + FixVec3.UnitX * (speed * dt);
Vec3 renderPos = pos.ToVec3();
```

## Deterministic Float Math & PRNG
- `DetMath.Sin/Cos/Tan/Atan2` use deterministic minimax polynomials rather than OS-varying `MathF` trig.
- `DetRng` (Xoshiro128++) stores its 128-bit state explicitly inside your simulation/emitter data struct. **NEVER use `System.Random.Shared` in simulation code.**

## Generational Handles (`BringEngine.Core.Handles`)
All engine objects are referenced by 32-bit packed value handles (20-bit index + 12-bit generation):
- `EntityId`, `SceneId`, `BundleId`
- `MeshHandle`, `TextureHandle`, `MaterialHandle`, `ShaderHandle`, `FontHandle`, `GltfHandle`
- `AnimClipHandle`, `SkeletonHandle`, `TweenHandle`, `EmitterHandle`, `CameraHandle`, `SpriteHandle`, `TextHandle`
- Check `.IsValid` before dereferencing; `default` (`Raw == 0`) is always `Invalid`.
