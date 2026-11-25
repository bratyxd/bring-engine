# Agent Guide 10 — Multi-Camera System & Headless Testing

## Multi-Camera Setup (`CameraTable` + `CameraSystem`)
Bring Engine supports up to 32 simultaneous cameras sorted by `DepthOrder`:
- **3D Perspective Main Camera**: `CameraSystem.CreatePerspective(ref cameras, pos, lookTarget, fovYDeg: 60f, depthOrder: 0)`
- **2D Orthographic UI Overlay Camera**: `CameraSystem.CreateOrthographicUI(ref cameras, width, height, depthOrder: 100, layerMask: 1u << 31)`
- **Split-Screen / Minimap**: Set `cam->ViewportRect = new Vec4(0.75f, 0.75f, 0.23f, 0.23f)` and `cam->DepthOrder = 10`.

## Built-in Camera Controllers & Trauma Shake
All controllers live as plain fields inside `CameraData`:
- `CameraMode.FollowTarget`: set `TargetEntity`, `FollowOffset`, `LookOffset`, `FollowDamping`.
- `CameraMode.OrbitTarget`: set `TargetEntity`, `OrbitYaw`, `OrbitPitch`, `OrbitDistance`.
- `CameraMode.LookAtTarget`: aims at `TargetEntity + LookOffset`.
- **Deterministic Screen Shake**: call `CameraSystem.AddTrauma(ref cameras, camHandle, 0.6f)`. Shake decays deterministically using trigonometric noise with zero RNG state corruption.

## Frustum Culling & Picking
After `CameraSystem.UpdateAll(ref cameras, in transforms, dt, width, height)`:
- `cam.Frustum.IntersectsBounds(in worldBounds)` / `IntersectsSphere(center, radius)`
- `CameraSystem.ScreenToWorldRay(in cam, mouseX, mouseY, width, height)`
- `CameraSystem.WorldToScreen(in cam, worldPos, width, height, out screenPos)`

## Headless Determinism & Testing Blueprint
Because `BringEngine.Simulation.*` and `<Game>.Simulation.*` NEVER call SDL3 or GPU functions:
1. Initialize `EngineAllocators`, `EngineWorld`, and `<Game>SimState`.
2. Step fixed ticks (`dt = 1/60s`) with synthetic `InputState`.
3. Hash the resulting positions/scores/health across two independent runs — they MUST produce bit-identical state hashes.
4. Wrap the hot tick loop in `GC.GetAllocatedBytesForCurrentThread()` — it MUST equal `0` bytes.
