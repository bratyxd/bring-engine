# Agent Guide 06 — Unity-Style 2D Sprites in 3D World & GPU Batching

## 2D Sprites in 3D World (`SpriteData`)
Bring Engine renders 2D sprites natively inside the 3D world with full depth testing, 3D transforms, sorting layers, sprite-sheet UVs, and billboard modes — just like Unity's `SpriteRenderer`, but 100% data-oriented and GPU-instanced!

```csharp
EntityId rune = EntitySystem.Spawn(ref world.Entities, sceneId);
TransformSystem.Add(ref world.Transforms, rune, new Vec3(0, 0.05f, 0),
    Quat.FromAxisAngle(Vec3.UnitX, -DetMath.HalfPi), Vec3.One); // flat on 3D ground

SpriteHandle sh = RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
{
    Entity = rune,
    OwnerScene = sceneId,
    Texture = spriteSheetTex,
    Size = new Vec2(2.5f, 2.5f),
    Pivot = new Vec2(0.5f, 0.5f),
    UvRect = new Vec4(0f, 0.5f, 0.25f, 0.5f), // frame from 4x2 atlas
    Color = ColorF.Cyan,
    LayerMask = 1u,
    Billboard = SpriteBillboardMode.None,      // or Spherical / CylindricalY!
    BlendMode = SpriteBlendMode.Additive,      // AlphaBlend / AlphaCutout / Additive
    DepthTest = 1,
    Visible = 1,
});
```

## 2D HUD / Screen-Space Sprites
Spawn a sprite with `LayerMask = 1u << 31` (matching your Orthographic UI camera) and `DepthTest = 0`. Use `SortingLayer` and `OrderInLayer` to control exact draw order.

## GPU Batching & Performance Rules
`BatchRenderer.RenderFrame` automatically minimizes draw calls and GPU state changes:
1. **3D Meshes**: Frustum-culled on CPU, sorted by `(AlphaMode, Shader, Texture, Mesh)` in `FrameArena`, and merged into **instanced draw calls** (`DrawGPUIndexedPrimitives` with `num_instances = batchCount`).
2. **2D Sprites**: Sorted by `(SortingLayer, OrderInLayer, DepthTest, BlendMode, Shader, Texture)` and drawn in 1 instanced quad call per state group.
3. **SDF Text**: Adjacent text objects sharing a font atlas + shader + depth mode merge into 1 instanced glyph draw call.
4. **Particles**: All live particles sharing `(Texture, Shader, BlendMode)` draw in 1 instanced quad call.
5. **Single Copy Pass**: All per-frame instance, joint, sprite, glyph, and particle storage buffers upload in ONE `SDL_GPUCopyPass` before any `SDL_GPURenderPass`.
6. Read `gpu.LastFrameStats` (`DrawCalls`, `PipelineBinds`, `MeshInstances`, `SpriteInstances`, `GlyphInstances`, `ParticleInstances`, `Triangles`, `CulledMeshes`) to audit performance!

## Writing Custom HLSL Shaders
Follow `SDL_shadercross` register spaces:
- Vertex StorageBuffers: `space0` (`t0`, `t1`), UniformBuffers: `space1` (`b0` camera, `b1` `BaseInstance`).
- Fragment Samplers/Textures: `space2` (`s0`, `t0`), UniformBuffers: `space3` (`b0`).
- Always start with `#pragma pack_matrix(column_major)` and index `Instances[BaseInstance + instanceId]`.
- For `ShaderVertexLayout.Mesh3D` custom shaders, keep all 7 `VSInput` attributes (`TEXCOORD0..TEXCOORD6`) and `AlbedoTex`/`JointPalette` referenced so SPIR-V/DXIL reflection preserves the exact vertex layout on D3D12, Vulkan, and Metal (see `src/ShowcaseGame/Assets/Shaders/hologram_energy.hlsl` as the canonical template).
- Compile with `ShaderSystem.CompileFromFile(ref gpu, "Assets/my_shader.hlsl", "my_shader", ShaderVertexLayout.Mesh3D)`.
