# Agent Guide 05 — glTF 2.0 First-Class Pipeline & 3D Rendering

## Loading `.glb` and `.gltf` Files
Bring Engine includes a zero-reflection `Utf8JsonReader` glTF 2.0 loader (`BringEngine.Assets.Gltf.GltfLoader`) that reads directly into a bundle's `LinearArena`:
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
2. Sorted in `Scratch` arena by `(AlphaMode, DoubleSided, Shader, Texture, Mesh, OriginalIndex)`.
3. Merged into **instanced draw calls** sharing one storage buffer upload per frame (single `SDL_GPUCopyPass` before `SDL_GPURenderPass`).
4. Skinned meshes automatically sample `AnimationTable.FrameJointPalette` uploaded to the GPU joint storage buffer (via `MeshRendererData.AnimPlayer` `AnimPlayerHandle`)!
5. `MaterialAssetData.DoubleSided` (`!= 0`) automatically selects a `CullMode.None` pipeline variant in both the main 3D pass and the directional shadow pass.

## Directional Sun Shadow Mapping (`SceneLighting` + `shadow_depth.hlsl` / `shadow_depth_mask.hlsl`)
Built-in 3D PBR rendering (`pbr_mesh.hlsl`) automatically renders a **texel-snapped directional sun shadow map** (`D32Float`, **3×3 PCF soft shadows**) before the main scene pass whenever `SceneLighting.ShadowsEnabled != 0` (enabled by default in `SceneLighting.Default`).

- Both **static** and **skinned animated glTF** opaque (`shadow_depth.hlsl`) and alpha-masked cutout (`MaterialAlphaMode.Mask`, `shadow_depth_mask.hlsl`) meshes automatically cast shadows whenever `MeshRendererData.CastShadow != 0`, tracked in `RenderStats.ShadowDrawCalls`.
- Opaque casters batch strictly by `(DoubleSided, MeshGpuSlot)` (ignoring material/texture so many meshes sharing geometry collapse into 1 shadow draw call); `Mask` casters batch by `(DoubleSided, TextureGpuSlot, MeshGpuSlot)` and test `AlbedoTex` alpha against `AlphaCutoff`.
- Set `mr.CastShadow = 0` to opt a mesh out of shadow casting; transparent meshes (`MaterialAlphaMode.Blend`) never cast opaque depth shadows.
- **Large Worlds**: Update `lighting.ShadowCenter = playerPos` each frame — `BatchRenderer.ComputeDirectionalLightViewProj` automatically snaps the orthographic frustum to exact shadow texels so shadows stay rock-solid without swimming!

```csharp
SceneLighting lighting = SceneLighting.Default;
lighting.ShadowsEnabled = 1;           // Set to 0 to disable shadow pass completely
lighting.ShadowMapSize = 2048;         // 512..4096 (default 2048; use 1024 on low-power presets)
lighting.ShadowCenter = playerWorldPos;// Center of orthographic shadow coverage box
lighting.ShadowDistance = 28f;         // Half-extent in world units (28 => 56x56m coverage)
lighting.ShadowStrength = 0.72f;       // Shadow darkening intensity [0..1]
lighting.ShadowBias = 0.0018f;         // Depth comparison bias
lighting.ShadowNormalBias = 0.04f;     // World-space normal offset to eliminate slope acne
```
