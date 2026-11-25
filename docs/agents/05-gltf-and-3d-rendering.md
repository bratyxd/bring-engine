# Agent Guide 05 — glTF 2.0 First-Class Pipeline & 3D Rendering

## Loading `.glb` and `.gltf` Files
Bring Engine includes a pure NativeAOT, zero-reflection `Utf8JsonReader` glTF 2.0 loader (`BringEngine.Assets.Gltf.GltfLoader`) that reads directly into a bundle's `LinearArena`:
- Supports `.glb` binary files AND `.gltf` JSON + external `.bin` / base64 URIs.
- Extracts PBR Metallic-Roughness materials, embedded/external PNG/JPEG textures, vertex positions/normals/UV0/colors, auto-computed normals, `JOINTS_0`/`WEIGHTS_0` skinning, node hierarchies, skins (inverse bind matrices), and Step/Linear/CubicSpline animations.

```csharp
// Inside a bundle loader:
GltfHandle heroGltf = GltfLoader.LoadFromFile(
    "Assets/hero.glb",
    ref *ctx.BundleArena, ref *ctx.ScratchArena,
    ref ctx.World->Assets, ref ctx.World->Animations,
    ctx.Bundle, StringId.Hash("hero"));

// Instantiate into the world (spawns entities, transform tree, mesh renderers, and animation player):
EntityId heroRoot = GltfLoader.Instantiate(
    ref world, ref alloc.Scratch, heroGltf,
    position: new Vec3(0, 0, 0), rotation: Quat.Identity, scale: Vec3.One,
    ownerScene: sceneId);
```

## Procedural `.glb` Authoring (`GlbBuilder`)
Need to generate real `.glb` files for tests or procedural assets? Use `GlbBuilder.BuildGlb(in meshSpec, includeDemoAnimation: true)` with `CreateBoxSpec`, `CreateCrystalSpec`, or `CreateHeroMechSpec`.

## Instanced 3D PBR Batching (Phase 6)
All `MeshRendererData` components are automatically:
1. Frustum-culled against the active camera's 6 planes using transformed `LocalBounds`.
2. Sorted in `FrameArena` by `(AlphaMode, Shader, Material, Mesh, SkinnedFlag)`.
3. Merged into **instanced draw calls** sharing one storage buffer upload per frame (single `SDL_GPUCopyPass` before `SDL_GPURenderPass`).
4. Skinned meshes automatically sample `AnimationTable.FrameJointPalette` uploaded to the GPU joint storage buffer!
