# Agent Guide 07 — Advanced SDF Fonts, Text Layout, Animation & Shaders

## Baking SDF Font Atlases (`SdfFontLoader`)
Fonts are baked at bundle load time from TrueType (`.ttf`) files into single-channel `R8_UNORM` Signed Distance Field (SDF) textures inside `BundleArena` using `StbTrueTypeSharp`. `SdfFontLoader` uses a symmetric `onEdgeValue = 128` (`0.502`) with bounded derivative AA and Porter-Duff premultiplied fill-over-outline compositing in `sdf_text.hlsl` so glyphs render **100% opaque**, razor-sharp even at small HUD sizes, and free of outline RGB bleed when `OutlineColor.A == 0`:
```csharp
FontHandle font = SdfFontLoader.BakeSdfFromSystemOrFile(
    "Assets/font.ttf", // or null to auto-resolve OS TrueType font with procedural fallback
    ref *ctx.BundleArena, ref ctx.World->Assets, ctx.Bundle, StringId.Hash("main_font"),
    pixelSize: 64f, atlasSize: 1024, sdfPadding: 8);
```

## Default: Static, Readable Text (`TextAnimFlags.None`)
Keep text **static, crisp, and readable by default** across HUD counters, menus, buttons, dialogue, tooltips, and world labels (`TextAnimFlags.None`).

> **Rule — Do NOT Animate Text Unless Explicitly Requested**: Leave `TextData.AnimFlags = TextAnimFlags.None` and skip text animations (`Wave`, `Rainbow`, `Bounce`, `Shake`, `ScalePop`, `GlowPulse`, `ShineSweep`, typewriter loops, or pulsing glow tweens) unless the user or design specifically asks for an animated text effect. Applying text animations broadly across HUD, menus, and world labels hurts readability and feels visually noisy.

```csharp
// 1. 3D World-Space billboarded label or callout (static by default)
EntityId e = EntitySystem.Spawn(ref world.Entities, sceneId);
TransformSystem.Add(ref world.Transforms, e, new Vec3(0, 3, 0));
var td = RenderComponentSystem.DefaultWorldText(e, font, "CRIT +250", sceneId);
td.Color = ColorF.Gold;
td.OutlineColor = new ColorF(0.08f, 0.08f, 0.1f, 1f);
td.OutlineWidth = 0.12f;
td.GlowColor = new ColorF(1f, 0.85f, 0.4f, 1f);
td.GlowIntensity = 0.2f;
td.AnimFlags = TextAnimFlags.None; // keep static unless animation is explicitly requested
TextHandle txt = RenderComponentSystem.AddText(ref world.RenderComponents, in td);

// 2. 2D HUD text (LayerMask = 1u << 31 for UI orthographic camera; static & crisp)
var hud = RenderComponentSystem.DefaultHudText(hudEntity, font, "SCORE: 1200", sceneId);
hud.OutlineColor = new ColorF(0.05f, 0.05f, 0.08f, 1f);
hud.OutlineWidth = 0.1f;
hud.AnimFlags = TextAnimFlags.None;
TextHandle hudTxt = RenderComponentSystem.AddText(ref world.RenderComponents, in hud);
```

## Optional Text Effects (Only When Explicitly Requested)
When a specific screen or moment **explicitly calls for** animated text (for example, a requested dialogue typewriter reveal or a specifically requested stylized title effect), you may opt into per-character `TextAnimFlags` (`Wave`, `Rainbow`, `Bounce`, `Shake`, `ScalePop`, `GlowPulse`, `ShineSweep`) or **handle-safe `TweenSystem` channels** that survive swap-back table compactions:
```csharp
// Only apply when explicitly requested — never as a default on HUD/menu/label text:
// td.AnimFlags = TextAnimFlags.ScalePop;
TweenSystem.TextTypewriter(ref tweens, txt, 0f, 1f, 0.6f, EaseType.Linear, scene: sceneId);
TweenSystem.TextGlowIntensity(ref tweens, txt, 0.15f, 0.55f, 0.8f, EaseType.InOutSine, loopMode: TweenLoopMode.PingPong, loops: -1, scene: sceneId);
TweenSystem.TextColor(ref tweens, txt, ColorF.White, ColorF.Gold, 0.4f, EaseType.OutCubic, scene: sceneId);
```
> **Warning**: Never pass raw pointers into `RenderComponentTables.Texts` (such as `&ptr->TypewriterProgress`) to `TweenSystem.TweenFloatPtr` — `RenderComponentTables.Texts` uses swap-back dense compaction whenever any text is removed, which would relocate the element and cause a raw-pointer tween to write into a different text's memory!

You can also assign `ptr->CustomShader` (`ShaderVertexLayout.InstancedQuad`) to replace the built-in `sdf_text.hlsl` with a custom HLSL text shader; `TextData.CustomParams` is forwarded directly into each `GlyphGpuInstance` (`144` bytes, `PSInput.CustomParams : TEXCOORD6`).
