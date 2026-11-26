# Agent Guide 07 — Advanced SDF Fonts, Text Layout, Animation & Shaders

## Baking SDF Font Atlases (`SdfFontLoader`)
Fonts are baked at bundle load time from TrueType (`.ttf`) files into single-channel `R8_UNORM` Signed Distance Field (SDF) textures inside `BundleArena` using `StbTrueTypeSharp`. `SdfFontLoader` uses a symmetric `onEdgeValue = 128` (`0.502`) with bounded derivative AA and Porter-Duff premultiplied fill-over-outline compositing in `sdf_text.hlsl` so glyphs render **100% opaque**, razor-sharp even at small HUD sizes, and free of outline RGB bleed when `OutlineColor.A == 0`:
```csharp
FontHandle font = SdfFontLoader.BakeSdfFromSystemOrFile(
    "Assets/font.ttf", // or null to auto-resolve OS TrueType font with procedural fallback
    ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle, StringId.Hash("main_font"),
    pixelSize: 64f, atlasSize: 1024, sdfPadding: 8);
```

## Creating 3D World-Space & 2D Screen-Space Text
```csharp
// 1. 3D World-Space billboarded damage number or sign
EntityId e = EntitySystem.Spawn(ref world.Entities, sceneId);
TransformSystem.Add(ref world.Transforms, e, new Vec3(0, 3, 0));
var td = RenderComponentSystem.DefaultWorldText(e, font, "CRIT +250!", sceneId);
td.Color = ColorF.Gold;
td.OutlineColor = new ColorF(0.15f, 0.02f, 0.02f, 1f);
td.OutlineWidth = 0.16f;
td.GlowColor = new ColorF(1f, 0.4f, 0.1f, 1f);
td.GlowIntensity = 0.9f;
td.AnimFlags = TextAnimFlags.Bounce | TextAnimFlags.Rainbow | TextAnimFlags.ScalePop;
TextHandle txt = RenderComponentSystem.AddText(ref world.RenderComponents, in td);

// 2. 2D HUD text (LayerMask = 1u << 31 for UI orthographic camera)
var hud = RenderComponentSystem.DefaultHudText(hudEntity, font, "SCORE: 1200", sceneId);
hud.AnimFlags = TextAnimFlags.Wave | TextAnimFlags.GlowPulse;
TextHandle hudTxt = RenderComponentSystem.AddText(ref world.RenderComponents, in hud);
```

## Tweening & Shadering Text
Every field on `TextData` (`Color`, `OutlineWidth`, `GlowIntensity`, `TypewriterProgress`, `FontScale`, `CustomParams`, `SortingOrder`) is plain unmanaged data — tween them directly with `TweenSystem`:
```csharp
TextData* ptr = RenderComponentSystem.TryGetText(in world.RenderComponents, txt);
TweenSystem.TweenFloatPtr(ref tweens, &ptr->TypewriterProgress, 0f, 1f, 0.8f, EaseType.Linear, scene: sceneId);
TweenSystem.TweenFloatPtr(ref tweens, &ptr->GlowIntensity, 0.2f, 1.5f, 0.6f, EaseType.InOutSine, loopMode: TweenLoopMode.PingPong, loops: -1, scene: sceneId);
```
You can also assign `ptr->CustomShader` (`ShaderVertexLayout.InstancedQuad`) to replace the built-in `sdf_text.hlsl` with a custom HLSL text shader; `TextData.CustomParams` is forwarded directly into each `GlyphGpuInstance` (`144` bytes, `PSInput.CustomParams : TEXCOORD6`).
