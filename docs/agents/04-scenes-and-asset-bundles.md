# Agent Guide 04 — Asset Bundles & Unity-Like Scene Management

## Design Philosophy: Minimal Memory Footprint
Assets live in **ref-counted Asset Bundles** (`BundleManager`), and gameplay/view lifecycles live in **Unity-like Scenes** (`SceneManager`).
- Every loaded bundle owns a dedicated `LinearArena` (`BundleArena`).
- When a bundle's `RefCount` drops to `0`, all Meshes, Textures, Materials, Fonts, glTF Models, Skeletons, and Animation Clips owned by that bundle are immediately unregistered (releasing GPU VRAM via hooks if rendering is active) and `BundleArena.Dispose()` returns 100% of native RAM to the OS.
- Every active scene owns a dedicated `SceneArena` and tags every entity/component/tween/emitter/camera with its `SceneId`, enabling instant, zero-leak cleanup on unload.

## Registering & Using Asset Bundles
```csharp
// Option A: Programmatic loader function pointer (zero OOP/delegates)
static unsafe void LoadArenaBundle(ref BundleLoadContext ctx)
{
    TextureLoader.CreateCheckerGrid(256, 8, ..., ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle, "arena_floor");
    GltfLoader.LoadFromFile("Assets/hero.glb", ref *ctx.BundleArena, ref *ctx.ScratchArena,
        ref ctx.World->Assets, ref ctx.World->Animations, ctx.Bundle, "hero_model");
}

BundleId commonBundle = BundleSystem.Register(ref bundles, "common", 4 * 1024 * 1024, &LoadCommonBundle);
BundleId arenaBundle  = BundleSystem.Register(ref bundles, "arena",  8 * 1024 * 1024, &LoadArenaBundle);

// Option B: Zero-reflection JSON manifest bundle
BundleId level2 = BundleSystem.RegisterManifest(ref bundles, "level2", 8 * 1024 * 1024, "Assets/level2_bundle.json");
```

## Unity-Like Scene Loading (`Single` vs `Additive`)
```csharp
ReadOnlySpan<BundleId> arenaDeps = stackalloc BundleId[] { commonBundle, arenaBundle };
SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "Arena", arenaDeps,
    sceneArenaBytes: 2 * 1024 * 1024,
    onLoad: &OnArenaLoad,
    onUnload: &OnArenaUnload,
    onFixedUpdate: &OnArenaFixedUpdate,
    onViewSync: &OnArenaViewSync);

// Single mode: keeps shared bundles ('common') alive, unloads previous scene + unshared bundles FIRST (minimum peak RAM!), then loads 'Arena'
SceneId arenaScene = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, "Arena", SceneLoadMode.Single);

// Additive mode: load HUD scene alongside Arena
SceneId hudScene = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, "HUD", SceneLoadMode.Additive);

// Unload HUD independently
SceneSystem.UnloadScene(ref scenes, ref world, ref alloc, ref bundles, hudScene);
```
