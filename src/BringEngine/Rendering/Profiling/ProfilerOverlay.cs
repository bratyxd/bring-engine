using System;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;

namespace BringEngine.Rendering.Profiling;

public enum ProfilerAnchor : byte
{
    TopRight = 0,
    TopLeft = 1,
    BottomRight = 2,
    BottomLeft = 3,
}

/// <summary>
/// Common, drop-in Bring Engine UI Profiler Overlay (Zero-OOP, Zero-GC).
/// Renders on a dedicated persistent overlay layer (LayerMask = 1u << 30, DepthOrder = 1000, SceneId.Invalid)
/// so it automatically survives Single & Additive scene transitions and displays:
/// - Current FPS & total frame time (ms)
/// - CPU frame time (ms) & GPU frame time (ms) + visual frame-budget bar
/// - VSync state & FPS Cap
/// - Draw Calls, SetPass Calls, Render/Copy Passes, Objects, Triangles, Frame Arena KB
/// - Top named CPU scopes from the previous frame
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ProfilerOverlay
{
    public const uint OverlayLayerMask = 1u << 30;
    public const int OverlayDepthOrder = 1000;
    public const float PanelWidth = 392f;
    public const float PanelHeight = 160f;
    public const float Margin = 12f;

    public CameraHandle Camera;
    public ProfilerAnchor Anchor;
    public byte Visible;
    public byte Initialized;

    // Backdrop + accent + CPU/GPU budget bar sprites
    public EntityId BgEntity;
    public SpriteHandle BgSprite;
    public EntityId AccentEntity;
    public SpriteHandle AccentSprite;
    public EntityId BarTrackEntity;
    public SpriteHandle BarTrackSprite;
    public EntityId CpuBarEntity;
    public SpriteHandle CpuBarSprite;
    public EntityId GpuBarEntity;
    public SpriteHandle GpuBarSprite;

    // 6 SDF telemetry rows
    public EntityId HeaderEntity;
    public TextHandle HeaderText;
    public EntityId FpsEntity;
    public TextHandle FpsText;
    public EntityId TimingEntity;
    public TextHandle TimingText;
    public EntityId DrawsEntity;
    public TextHandle DrawsText;
    public EntityId ObjectsEntity;
    public TextHandle ObjectsText;
    public EntityId ScopesEntity;
    public TextHandle ScopesText;

    public float LastScreenW;
    public float LastScreenH;
}

public static unsafe class ProfilerOverlaySystem
{
    /// <summary>
    /// Creates the common profiler UI overlay on LayerMask (1u << 30) with OwnerScene = SceneId.Invalid
    /// so it persists seamlessly across all scene loads/unloads.
    /// </summary>
    public static ProfilerOverlay Create(
        ref EngineWorld world,
        FontHandle font,
        TextureHandle whiteTexture,
        float screenWidth,
        float screenHeight,
        ProfilerAnchor anchor = ProfilerAnchor.TopRight,
        bool startVisible = true)
    {
        var ov = new ProfilerOverlay
        {
            Anchor = anchor,
            Visible = (byte)(startVisible ? 1 : 0),
            Initialized = 1,
            LastScreenW = screenWidth,
            LastScreenH = screenHeight,
        };

        // Dedicated overlay camera (DepthOrder 1000 -> renders cleanly above both 3D world and game HUD)
        ov.Camera = CameraSystem.CreateOrthographicUI(
            ref world.Cameras, screenWidth, screenHeight,
            ownerScene: SceneId.Invalid,
            depthOrder: ProfilerOverlay.OverlayDepthOrder,
            layerMask: ProfilerOverlay.OverlayLayerMask);

        ov.BgEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.PanelWidth, ProfilerOverlay.PanelHeight),
            new ColorF(0.03f, 0.05f, 0.10f, 0.86f), 0, out ov.BgSprite);
        ov.AccentEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.PanelWidth, 3f),
            new ColorF(0.15f, 0.92f, 1.0f, 0.95f), 1, out ov.AccentSprite);
        ov.BarTrackEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.PanelWidth - 24f, 8f),
            new ColorF(0.12f, 0.16f, 0.24f, 0.95f), 1, out ov.BarTrackSprite);
        ov.CpuBarEntity = SpawnSprite(ref world, whiteTexture, new Vec2(4f, 8f),
            new ColorF(0.15f, 0.92f, 1.0f, 1.0f), 2, out ov.CpuBarSprite);
        ov.GpuBarEntity = SpawnSprite(ref world, whiteTexture, new Vec2(4f, 8f),
            new ColorF(1.0f, 0.78f, 0.18f, 1.0f), 2, out ov.GpuBarSprite);

        ov.HeaderEntity = SpawnRowText(ref world, font, 0.34f, new ColorF(0.22f, 0.95f, 1.0f, 1f), "PROFILER [F1]  VSYNC:ON  CAP:60", out ov.HeaderText);
        ov.FpsEntity = SpawnRowText(ref world, font, 0.38f, new ColorF(1.0f, 0.95f, 0.55f, 1f), "FPS: 60.0  (16.67 ms)", out ov.FpsText);
        ov.TimingEntity = SpawnRowText(ref world, font, 0.35f, new ColorF(0.95f, 0.98f, 1.0f, 1f), "CPU: 0.00 ms  |  GPU: 0.00 ms", out ov.TimingText);
        ov.DrawsEntity = SpawnRowText(ref world, font, 0.34f, new ColorF(0.65f, 0.92f, 1.0f, 1f), "DRAWS: 0  |  SETPASS: 0  |  PASSES: 0", out ov.DrawsText);
        ov.ObjectsEntity = SpawnRowText(ref world, font, 0.32f, new ColorF(0.80f, 0.86f, 0.96f, 0.95f), "OBJS: 0  |  TRIS: 0  |  ARENA: 0KB", out ov.ObjectsText);
        ov.ScopesEntity = SpawnRowText(ref world, font, 0.28f, new ColorF(0.62f, 0.74f, 0.88f, 0.92f), "SCOPES: --", out ov.ScopesText);

        LayoutPanel(ref ov, ref world, screenWidth, screenHeight);
        SetVisible(ref ov, ref world, startVisible);
        return ov;
    }

    public static void SetVisible(ref ProfilerOverlay ov, ref EngineWorld world, bool visible)
    {
        if (ov.Initialized == 0) return;
        byte vis = (byte)(visible ? 1 : 0);
        ov.Visible = vis;

        CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, ov.Camera);
        if (cam != null) cam->Enabled = vis;

        SetSpriteVis(in world.RenderComponents, ov.BgSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.AccentSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.BarTrackSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.CpuBarSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.GpuBarSprite, vis);

        SetTextVis(in world.RenderComponents, ov.HeaderText, vis);
        SetTextVis(in world.RenderComponents, ov.FpsText, vis);
        SetTextVis(in world.RenderComponents, ov.TimingText, vis);
        SetTextVis(in world.RenderComponents, ov.DrawsText, vis);
        SetTextVis(in world.RenderComponents, ov.ObjectsText, vis);
        SetTextVis(in world.RenderComponents, ov.ScopesText, vis);
    }

    public static void Toggle(ref ProfilerOverlay ov, ref EngineWorld world)
        => SetVisible(ref ov, ref world, ov.Visible == 0);

    /// <summary>
    /// Updates all profiler UI text rows and visual CPU/GPU budget bars with ZERO managed heap allocations.
    /// </summary>
    public static void Update(
        ref ProfilerOverlay ov,
        ref EngineWorld world,
        in FrameProfiler profiler,
        in RenderStats stats,
        in FramePacer pacer,
        float screenWidth,
        float screenHeight)
    {
        if (ov.Initialized == 0 || ov.Visible == 0) return;

        if (DetMath.Abs(screenWidth - ov.LastScreenW) > 1f || DetMath.Abs(screenHeight - ov.LastScreenH) > 1f)
            LayoutPanel(ref ov, ref world, screenWidth, screenHeight);

        // Zero-GC formatting into stackalloc char buffer!
        char* buf = stackalloc char[160];
        Span<char> span = new Span<char>(buf, 160);

        // Row 0: Header + VSync + FPS Cap
        bool vsyncOn = pacer.EffectiveVSync != 0;
        if (pacer.TargetFps > 0)
        {
            if (span.TryWrite($"PROFILER [F1]   VSYNC:{(vsyncOn ? "ON" : "OFF")}  CAP:{pacer.TargetFps}", out int n0))
                WriteText(in world.RenderComponents, ov.HeaderText, span.Slice(0, n0));
        }
        else
        {
            if (span.TryWrite($"PROFILER [F1]   VSYNC:{(vsyncOn ? "ON" : "OFF")}  CAP:MAX", out int n0))
                WriteText(in world.RenderComponents, ov.HeaderText, span.Slice(0, n0));
        }

        // Row 1: Current FPS & frame total ms
        double fps = profiler.ProfiledFrames > 0 ? profiler.CurrentFps : 0.0;
        double totMs = profiler.ProfiledFrames > 0 ? profiler.SmoothedTotalMs : 0.0;
        if (span.TryWrite($"FPS: {fps:F1}   FRAME: {totMs:F2} ms", out int n1))
            WriteText(in world.RenderComponents, ov.FpsText, span.Slice(0, n1));

        // Row 2: CPU ms & GPU ms
        double cpuMs = profiler.ProfiledFrames > 0 ? profiler.SmoothedCpuMs : profiler.CpuFrameMs;
        double gpuMs = profiler.ProfiledFrames > 0 ? profiler.SmoothedGpuMs : profiler.GpuFrameMs;
        if (span.TryWrite($"CPU: {cpuMs:F2} ms  |  GPU: {gpuMs:F2} ms", out int n2))
            WriteText(in world.RenderComponents, ov.TimingText, span.Slice(0, n2));

        // Row 3: Draw calls, SetPass calls, Passes
        int totalPasses = stats.RenderPasses + stats.CopyPasses;
        if (span.TryWrite($"DRAWS: {stats.DrawCalls}  |  SETPASS: {stats.SetPassCalls}  |  PASSES: {totalPasses}", out int n3))
            WriteText(in world.RenderComponents, ov.DrawsText, span.Slice(0, n3));

        // Row 4: Objects, Triangles, Frame Arena KB
        int objs = stats.MeshInstances + stats.SpriteInstances + stats.GlyphInstances + stats.ParticleInstances;
        int kb = (int)(stats.FrameArenaUsedBytes / 1024);
        if (span.TryWrite($"OBJS: {objs}  |  TRIS: {stats.Triangles}  |  ARENA: {kb}KB", out int n4))
            WriteText(in world.RenderComponents, ov.ObjectsText, span.Slice(0, n4));

        // Row 5: Top CPU scopes summary (zero allocation!)
        int pos = 0;
        if (profiler.LastFrameScopes != null && profiler.LastFrameScopeCount > 0)
        {
            int shown = 0;
            for (int i = 0; i < profiler.LastFrameScopeCount && shown < 4 && pos < 115; i++)
            {
                ProfileScopeSample* sc = profiler.LastFrameScopes + i;
                if (sc->Completed == 0) continue;
                if (shown > 0 && pos + 2 < 128) { span[pos++] = ' '; span[pos++] = ' '; }
                int nameChars = CopyUtf8ToChars(sc->Name.Data, sc->Name.Length, span.Slice(pos, Math.Min(10, 128 - pos)));
                pos += nameChars;
                if (span.Slice(pos).TryWrite($":{sc->DurationMs:F2}", out int written))
                    pos += written;
                shown++;
            }
        }
        if (pos > 0)
            WriteText(in world.RenderComponents, ov.ScopesText, span.Slice(0, pos));

        // Update CPU (cyan) & GPU (gold) visual frame-budget bars against target budget (default 16.67ms)
        float budgetMs = pacer.TargetFps > 0 ? (1000f / pacer.TargetFps) : 16.6667f;
        float trackW = ProfilerOverlay.PanelWidth - 24f;
        float cpuW = DetMath.Clamp((float)(cpuMs / budgetMs) * trackW, 2f, trackW);
        float gpuW = DetMath.Clamp((float)(gpuMs / budgetMs) * trackW, 2f, DetMath.Max(2f, trackW - cpuW));

        GetPanelOrigin(ov.Anchor, screenWidth, screenHeight, out float px, out float py);
        float barY = py + 12f;

        SpriteData* cpuSp = RenderComponentSystem.TryGetSprite(in world.RenderComponents, ov.CpuBarSprite);
        if (cpuSp != null)
        {
            cpuSp->Size = new Vec2(cpuW, 8f);
            cpuSp->Color = cpuMs > budgetMs * 0.85 ? ColorF.Red : new ColorF(0.15f, 0.92f, 1.0f, 1.0f);
        }

        SpriteData* gpuSp = RenderComponentSystem.TryGetSprite(in world.RenderComponents, ov.GpuBarSprite);
        if (gpuSp != null)
        {
            gpuSp->Size = new Vec2(gpuW, 8f);
            gpuSp->Color = (cpuMs + gpuMs) > budgetMs ? new ColorF(1f, 0.35f, 0.2f, 1f) : ColorF.Gold;
        }
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.GpuBarEntity, new Vec3(px + 12f + cpuW, barY, 0f));
    }

    public static void Destroy(ref ProfilerOverlay ov, ref EngineWorld world)
    {
        if (ov.Initialized == 0) return;
        CameraSystem.Destroy(ref world.Cameras, ov.Camera);
        EngineWorldSystem.DestroyEntity(ref world, ov.BgEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.AccentEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.BarTrackEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.CpuBarEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.GpuBarEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.HeaderEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.FpsEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.TimingEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.DrawsEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.ObjectsEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.ScopesEntity);
        ov = default;
    }

    private static void LayoutPanel(ref ProfilerOverlay ov, ref EngineWorld world, float screenW, float screenH)
    {
        ov.LastScreenW = screenW;
        ov.LastScreenH = screenH;

        CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, ov.Camera);
        if (cam != null)
        {
            cam->OrthoHeight = screenH;
            cam->Position = new Vec3(screenW * 0.5f, screenH * 0.5f, 10f);
            cam->LookTarget = new Vec3(screenW * 0.5f, screenH * 0.5f, 0f);
        }

        GetPanelOrigin(ov.Anchor, screenW, screenH, out float px, out float py);
        float topY = py + ProfilerOverlay.PanelHeight;

        TransformSystem.SetLocalPosition(ref world.Transforms, ov.BgEntity, new Vec3(px, py, -2f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.AccentEntity, new Vec3(px, topY - 3f, -1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.BarTrackEntity, new Vec3(px + 12f, py + 12f, -1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.CpuBarEntity, new Vec3(px + 12f, py + 12f, 0f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.GpuBarEntity, new Vec3(px + 20f, py + 12f, 0f));

        float xText = px + 14f;
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.HeaderEntity, new Vec3(xText, topY - 10f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.FpsEntity, new Vec3(xText, topY - 32f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.TimingEntity, new Vec3(xText, topY - 56f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.DrawsEntity, new Vec3(xText, topY - 78f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.ObjectsEntity, new Vec3(xText, topY - 100f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.ScopesEntity, new Vec3(xText, topY - 122f, 1f));
    }

    private static void GetPanelOrigin(ProfilerAnchor anchor, float screenW, float screenH, out float x, out float y)
    {
        float m = ProfilerOverlay.Margin;
        switch (anchor)
        {
            case ProfilerAnchor.TopLeft:
                x = m;
                y = screenH - ProfilerOverlay.PanelHeight - m;
                break;
            case ProfilerAnchor.BottomLeft:
                x = m;
                y = m;
                break;
            case ProfilerAnchor.BottomRight:
                x = screenW - ProfilerOverlay.PanelWidth - m;
                y = m;
                break;
            default: // TopRight
                x = screenW - ProfilerOverlay.PanelWidth - m;
                y = screenH - ProfilerOverlay.PanelHeight - m;
                break;
        }
    }

    private static EntityId SpawnSprite(ref EngineWorld world, TextureHandle whiteTex, Vec2 size, ColorF color, short order, out SpriteHandle handle)
    {
        EntityId e = EntitySystem.Spawn(ref world.Entities, SceneId.Invalid);
        TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
        handle = RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData
        {
            Entity = e,
            OwnerScene = SceneId.Invalid,
            Texture = whiteTex,
            Size = size,
            Pivot = Vec2.Zero,
            Color = color,
            LayerMask = ProfilerOverlay.OverlayLayerMask,
            SortingLayer = 100,
            OrderInLayer = order,
            DepthTest = 0,
            Visible = 1,
        });
        return e;
    }

    private static EntityId SpawnRowText(ref EngineWorld world, FontHandle font, float scale, ColorF color, ReadOnlySpan<char> initial, out TextHandle handle)
    {
        EntityId e = EntitySystem.Spawn(ref world.Entities, SceneId.Invalid);
        TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
        var td = RenderComponentSystem.DefaultHudText(e, font, initial, SceneId.Invalid);
        td.FontScale = scale;
        td.Color = color;
        td.SecondaryColor = color;
        td.OutlineWidth = 0.12f;
        td.OutlineColor = new ColorF(0.02f, 0.03f, 0.06f, 0.95f);
        td.ShadowOffset = new Vec2(1.2f, -1.2f);
        td.LayerMask = ProfilerOverlay.OverlayLayerMask;
        td.DepthTest = 0;
        handle = RenderComponentSystem.AddText(ref world.RenderComponents, in td);
        return e;
    }

    private static void SetSpriteVis(in RenderComponentTables rc, SpriteHandle h, byte vis)
    {
        SpriteData* p = RenderComponentSystem.TryGetSprite(in rc, h);
        if (p != null) p->Visible = vis;
    }

    private static void SetTextVis(in RenderComponentTables rc, TextHandle h, byte vis)
    {
        TextData* p = RenderComponentSystem.TryGetText(in rc, h);
        if (p != null) p->Visible = vis;
    }

    private static void WriteText(in RenderComponentTables rc, TextHandle h, ReadOnlySpan<char> text)
    {
        TextData* p = RenderComponentSystem.TryGetText(in rc, h);
        if (p != null) p->Content = FixedString256.From(text);
    }

    private static int CopyUtf8ToChars(byte* utf8, int byteLen, Span<char> dst)
    {
        int n = byteLen < dst.Length ? byteLen : dst.Length;
        for (int i = 0; i < n; i++) dst[i] = (char)utf8[i];
        return n;
    }
}
