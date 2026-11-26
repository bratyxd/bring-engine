using System;
using System.Runtime.InteropServices;
using BringEngine.Assets;
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
/// Common, drop-in Bring Engine UI Profiler Overlay (Zero-OOP, Zero-GC, 100% Opaque High-Contrast SDF UI).
/// Renders on a dedicated persistent overlay layer (LayerMask = 1u << 30, DepthOrder = 1000, SceneId.Invalid)
/// using resolution-independent virtual 720p vertical scaling so text stays crisp and readable on 720p/1080p/1440p/4K.
/// Displays:
/// - Current FPS, Total Frame Time (ms), VSync state, FPS Cap, and 2-Thread Pipeline status
/// - THREE SEPARATE independent frame-budget fill bars (since Logic, Render, and GPU run pipelined 1 frame apart):
///   1. CPU (Logic Thread, Frame N)
///   2. RND (Render Thread, Frame N-1)
///   3. GPU (Hardware Fence, Frame N-1)
/// - Draw Calls, SetPass Calls, Passes, Objects, Triangles, Frame Arena KB, and Top CPU Scopes
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ProfilerOverlay
{
    public const uint OverlayLayerMask = 1u << 30;
    public const int OverlayDepthOrder = 1000;
    public const float VirtualHeight = 720f;
    public const float PanelWidth = 480f;
    public const float PanelHeight = 236f;
    public const float Margin = 14f;
    public const float BarOffsetX = 206f;
    public const float BarTrackWidth = 258f;
    public const float BarHeight = 13f;

    public CameraHandle Camera;
    public ProfilerAnchor Anchor;
    public byte Visible;
    public byte Initialized;

    // 100% opaque backdrop + solid frame borders (strictly distinct OrderInLayer = 0..5)
    public EntityId BgEntity;
    public SpriteHandle BgSprite;
    public EntityId AccentEntity;
    public SpriteHandle AccentSprite;
    public EntityId BorderBottomEntity;
    public SpriteHandle BorderBottomSprite;
    public EntityId BorderLeftEntity;
    public SpriteHandle BorderLeftSprite;
    public EntityId BorderRightEntity;
    public SpriteHandle BorderRightSprite;

    // 3 SEPARATE horizontal fill bars + tracks (strictly distinct OrderInLayer = 10..15)
    public EntityId LogicTrackEntity;
    public SpriteHandle LogicTrackSprite;
    public EntityId LogicBarEntity;
    public SpriteHandle LogicBarSprite;

    public EntityId RenderTrackEntity;
    public SpriteHandle RenderTrackSprite;
    public EntityId RenderBarEntity;
    public SpriteHandle RenderBarSprite;

    public EntityId GpuTrackEntity;
    public SpriteHandle GpuTrackSprite;
    public EntityId GpuBarEntity;
    public SpriteHandle GpuBarSprite;

    // 8 100% opaque high-contrast SDF telemetry rows
    public EntityId HeaderEntity;
    public TextHandle HeaderText;
    public EntityId FpsEntity;
    public TextHandle FpsText;
    public EntityId LogicRowEntity;
    public TextHandle LogicRowText;
    public EntityId RenderRowEntity;
    public TextHandle RenderRowText;
    public EntityId GpuRowEntity;
    public TextHandle GpuRowText;
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

        // Normalize SDF scale to exact virtual 720p pixel heights regardless of baked font pixel size
        FontAssetData* fnt = AssetSystem.TryGetFont(in world.Assets, font);
        float bakedPx = (fnt != null && fnt->PixelSize > 8f) ? fnt->PixelSize : 48f;
        float ScaleForPx(float px) => px / bakedPx;

        float uiH = ProfilerOverlay.VirtualHeight;
        float aspect = (screenHeight > 1f) ? (screenWidth / screenHeight) : (16f / 9f);
        float uiW = uiH * aspect;

        // Dedicated overlay camera (DepthOrder 1000, ClearFlags.None -> never touches 3D depth!)
        ov.Camera = CameraSystem.CreateOrthographicUI(
            ref world.Cameras, uiW, uiH,
            ownerScene: SceneId.Invalid,
            depthOrder: ProfilerOverlay.OverlayDepthOrder,
            layerMask: ProfilerOverlay.OverlayLayerMask);

        // 100% opaque solid dark card + crisp frame borders (strictly unique OrderInLayer -> zero sort flicker!)
        ColorF cardBg = new ColorF(0.04f, 0.06f, 0.11f, 1.0f);
        ColorF borderCol = new ColorF(0.22f, 0.44f, 0.66f, 1.0f);
        ColorF trackBg = new ColorF(0.14f, 0.19f, 0.29f, 1.0f);

        ov.BgEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.PanelWidth, ProfilerOverlay.PanelHeight), cardBg, 0, out ov.BgSprite);
        ov.AccentEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.PanelWidth, 4f), new ColorF(0.18f, 0.94f, 1.0f, 1.0f), 1, out ov.AccentSprite);
        ov.BorderBottomEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.PanelWidth, 2f), borderCol, 2, out ov.BorderBottomSprite);
        ov.BorderLeftEntity = SpawnSprite(ref world, whiteTexture, new Vec2(2f, ProfilerOverlay.PanelHeight), borderCol, 3, out ov.BorderLeftSprite);
        ov.BorderRightEntity = SpawnSprite(ref world, whiteTexture, new Vec2(2f, ProfilerOverlay.PanelHeight), borderCol, 4, out ov.BorderRightSprite);

        // Three separate tracks + fill bars (CPU Logic, Render Thread, GPU Hardware)
        ov.LogicTrackEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.BarTrackWidth, ProfilerOverlay.BarHeight), trackBg, 10, out ov.LogicTrackSprite);
        ov.LogicBarEntity = SpawnSprite(ref world, whiteTexture, new Vec2(4f, ProfilerOverlay.BarHeight), new ColorF(0.16f, 0.94f, 1.0f, 1.0f), 11, out ov.LogicBarSprite);

        ov.RenderTrackEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.BarTrackWidth, ProfilerOverlay.BarHeight), trackBg, 12, out ov.RenderTrackSprite);
        ov.RenderBarEntity = SpawnSprite(ref world, whiteTexture, new Vec2(4f, ProfilerOverlay.BarHeight), new ColorF(0.22f, 0.96f, 0.50f, 1.0f), 13, out ov.RenderBarSprite);

        ov.GpuTrackEntity = SpawnSprite(ref world, whiteTexture, new Vec2(ProfilerOverlay.BarTrackWidth, ProfilerOverlay.BarHeight), trackBg, 14, out ov.GpuTrackSprite);
        ov.GpuBarEntity = SpawnSprite(ref world, whiteTexture, new Vec2(4f, ProfilerOverlay.BarHeight), new ColorF(1.0f, 0.80f, 0.18f, 1.0f), 15, out ov.GpuBarSprite);

        // 100% opaque high-contrast SDF text rows
        ov.HeaderEntity = SpawnRowText(ref world, font, ScaleForPx(19f), new ColorF(0.28f, 0.96f, 1.0f, 1.0f), "PROFILER [F1]  VSYNC:ON  CAP:60", out ov.HeaderText);
        ov.FpsEntity = SpawnRowText(ref world, font, ScaleForPx(22f), new ColorF(1.0f, 0.96f, 0.50f, 1.0f), "FPS: 60.0   FRAME: 16.67 ms", out ov.FpsText);
        ov.LogicRowEntity = SpawnRowText(ref world, font, ScaleForPx(18f), new ColorF(0.32f, 0.96f, 1.0f, 1.0f), "LOGIC:  0.00 ms", out ov.LogicRowText);
        ov.RenderRowEntity = SpawnRowText(ref world, font, ScaleForPx(18f), new ColorF(0.36f, 0.98f, 0.60f, 1.0f), "RENDER: 0.00 ms", out ov.RenderRowText);
        ov.GpuRowEntity = SpawnRowText(ref world, font, ScaleForPx(18f), new ColorF(1.0f, 0.84f, 0.28f, 1.0f), "GPU:    0.00 ms", out ov.GpuRowText);
        ov.DrawsEntity = SpawnRowText(ref world, font, ScaleForPx(19f), ColorF.White, "DRAWS: 0  |  SETPASS: 0  |  PASSES: 0", out ov.DrawsText);
        ov.ObjectsEntity = SpawnRowText(ref world, font, ScaleForPx(18f), new ColorF(0.92f, 0.96f, 1.0f, 1.0f), "OBJS: 0  |  TRIS: 0  |  ARENA: 0KB", out ov.ObjectsText);
        ov.ScopesEntity = SpawnRowText(ref world, font, ScaleForPx(16f), new ColorF(0.84f, 0.92f, 1.0f, 1.0f), "SCOPES: --", out ov.ScopesText);

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
        SetSpriteVis(in world.RenderComponents, ov.BorderBottomSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.BorderLeftSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.BorderRightSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.LogicTrackSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.LogicBarSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.RenderTrackSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.RenderBarSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.GpuTrackSprite, vis);
        SetSpriteVis(in world.RenderComponents, ov.GpuBarSprite, vis);

        SetTextVis(in world.RenderComponents, ov.HeaderText, vis);
        SetTextVis(in world.RenderComponents, ov.FpsText, vis);
        SetTextVis(in world.RenderComponents, ov.LogicRowText, vis);
        SetTextVis(in world.RenderComponents, ov.RenderRowText, vis);
        SetTextVis(in world.RenderComponents, ov.GpuRowText, vis);
        SetTextVis(in world.RenderComponents, ov.DrawsText, vis);
        SetTextVis(in world.RenderComponents, ov.ObjectsText, vis);
        SetTextVis(in world.RenderComponents, ov.ScopesText, vis);
    }

    public static void Toggle(ref ProfilerOverlay ov, ref EngineWorld world)
        => SetVisible(ref ov, ref world, ov.Visible == 0);

    /// <summary>
    /// Updates all profiler UI text rows and 3 independent stage fill bars (Logic CPU, Render Thread, GPU) with ZERO GC allocations.
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

        char* buf = stackalloc char[160];
        Span<char> span = new Span<char>(buf, 160);

        // Concrete zero-GC span writer (uses direct int/double.TryFormat — 0 GC bytes even in CoreCLR Tier-0 QuickJIT!)
        // Row 0: Header + VSync + FPS Cap + Pipeline Mode
        bool vsyncOn = pacer.EffectiveVSync != 0;
        ReadOnlySpan<char> modeTag = profiler.PipelinedTwoThread != 0 ? "2T" : "1T";
        int n0 = 0;
        AppendStr(span, ref n0, "PROFILER [F1]  VSYNC:");
        AppendStr(span, ref n0, vsyncOn ? "ON" : "OFF");
        AppendStr(span, ref n0, "  CAP:");
        if (pacer.TargetFps > 0) AppendInt(span, ref n0, pacer.TargetFps);
        else AppendStr(span, ref n0, "MAX");
        AppendStr(span, ref n0, "  [");
        AppendStr(span, ref n0, modeTag);
        AppendStr(span, ref n0, "]");
        WriteText(in world.RenderComponents, ov.HeaderText, span.Slice(0, n0));

        // Row 1: Current FPS & frame total ms
        double fps = profiler.ProfiledFrames > 0 ? profiler.CurrentFps : 0.0;
        double totMs = profiler.ProfiledFrames > 0 ? profiler.SmoothedTotalMs : 0.0;
        int n1 = 0;
        AppendStr(span, ref n1, "FPS: ");
        AppendFixed(span, ref n1, fps, 1);
        AppendStr(span, ref n1, "   FRAME: ");
        AppendFixed(span, ref n1, totMs, 2);
        AppendStr(span, ref n1, " ms");
        WriteText(in world.RenderComponents, ov.FpsText, span.Slice(0, n1));

        // Rows 2, 3, 4: Independent pipelined stage timings (Logic CPU, Render Thread, GPU)
        double logicMs = profiler.ProfiledFrames > 0 ? profiler.SmoothedLogicMs : profiler.LogicCpuMs;
        double renderMs = profiler.ProfiledFrames > 0 ? profiler.SmoothedRenderMs : profiler.RenderCpuMs;
        double gpuMs = profiler.ProfiledFrames > 0 ? profiler.SmoothedGpuMs : profiler.GpuFrameMs;

        int nl = 0;
        AppendStr(span, ref nl, "LOGIC:  ");
        AppendFixedPadded(span, ref nl, logicMs, 2, 5);
        AppendStr(span, ref nl, " ms");
        WriteText(in world.RenderComponents, ov.LogicRowText, span.Slice(0, nl));

        int nr = 0;
        AppendStr(span, ref nr, "RENDER: ");
        AppendFixedPadded(span, ref nr, renderMs, 2, 5);
        AppendStr(span, ref nr, " ms");
        WriteText(in world.RenderComponents, ov.RenderRowText, span.Slice(0, nr));

        int ng = 0;
        AppendStr(span, ref ng, "GPU:    ");
        AppendFixedPadded(span, ref ng, gpuMs, 2, 5);
        AppendStr(span, ref ng, " ms");
        WriteText(in world.RenderComponents, ov.GpuRowText, span.Slice(0, ng));

        // Each pipelined stage has its OWN independent fill bar against the full target frame budget (e.g. 16.67ms at 60 FPS)!
        float budgetMs = pacer.TargetFps > 0 ? (1000f / pacer.TargetFps) : 16.6667f;
        float trackW = ProfilerOverlay.BarTrackWidth;

        UpdateStageBar(in world.RenderComponents, ov.LogicBarSprite, logicMs, budgetMs, trackW, new ColorF(0.16f, 0.94f, 1.0f, 1.0f));
        UpdateStageBar(in world.RenderComponents, ov.RenderBarSprite, renderMs, budgetMs, trackW, new ColorF(0.22f, 0.96f, 0.50f, 1.0f));
        UpdateStageBar(in world.RenderComponents, ov.GpuBarSprite, gpuMs, budgetMs, trackW, new ColorF(1.0f, 0.80f, 0.18f, 1.0f));

        // Row 5: Draw calls (+ shadow pass draws), SetPass calls, Passes
        int totalPasses = stats.RenderPasses + stats.CopyPasses;
        int n5 = 0;
        AppendStr(span, ref n5, "DRAWS: ");
        AppendInt(span, ref n5, stats.DrawCalls);
        if (stats.ShadowDrawCalls > 0)
        {
            AppendStr(span, ref n5, "+");
            AppendInt(span, ref n5, stats.ShadowDrawCalls);
            AppendStr(span, ref n5, "S");
        }
        AppendStr(span, ref n5, " | SETPASS: ");
        AppendInt(span, ref n5, stats.SetPassCalls);
        AppendStr(span, ref n5, " | PASS: ");
        AppendInt(span, ref n5, totalPasses);
        WriteText(in world.RenderComponents, ov.DrawsText, span.Slice(0, n5));

        // Row 6: Objects, Triangles, Frame Arena KB
        int objs = stats.MeshInstances + stats.SpriteInstances + stats.GlyphInstances + stats.ParticleInstances;
        int kb = (int)(stats.FrameArenaUsedBytes / 1024);
        int n6 = 0;
        AppendStr(span, ref n6, "OBJS: ");
        AppendInt(span, ref n6, objs);
        AppendStr(span, ref n6, "  |  TRIS: ");
        AppendInt(span, ref n6, stats.Triangles);
        AppendStr(span, ref n6, "  |  ARENA: ");
        AppendInt(span, ref n6, kb);
        AppendStr(span, ref n6, "KB");
        WriteText(in world.RenderComponents, ov.ObjectsText, span.Slice(0, n6));

        // Row 7: Top Logic/CPU scopes summary (zero allocation!)
        int pos = 0;
        if (profiler.LastFrameScopes != null && profiler.LastFrameScopeCount > 0)
        {
            int shown = 0;
            for (int i = 0; i < profiler.LastFrameScopeCount && shown < 4 && pos < 118; i++)
            {
                ProfileScopeSample* sc = profiler.LastFrameScopes + i;
                if (sc->Completed == 0) continue;
                if (shown > 0 && pos + 2 < 130) { span[pos++] = ' '; span[pos++] = ' '; }
                int nameChars = CopyUtf8ToChars(sc->Name.Data, sc->Name.Length, span.Slice(pos, Math.Min(8, 130 - pos)));
                pos += nameChars;
                AppendStr(span, ref pos, ":");
                AppendFixed(span, ref pos, sc->DurationMs, 2);
                shown++;
            }
        }
        if (pos > 0)
            WriteText(in world.RenderComponents, ov.ScopesText, span.Slice(0, pos));
    }

    private static void UpdateStageBar(in RenderComponentTables rc, SpriteHandle barHandle, double stageMs, float budgetMs, float trackW, ColorF normalColor)
    {
        SpriteData* sp = RenderComponentSystem.TryGetSprite(in rc, barHandle);
        if (sp == null) return;
        float fillW = DetMath.Clamp((float)(stageMs / budgetMs) * trackW, 3f, trackW);
        sp->Size = new Vec2(fillW, ProfilerOverlay.BarHeight);
        sp->Color = stageMs > budgetMs * 0.85 ? new ColorF(1.0f, 0.24f, 0.24f, 1.0f) : normalColor;
    }

    public static void Destroy(ref ProfilerOverlay ov, ref EngineWorld world)
    {
        if (ov.Initialized == 0) return;
        CameraSystem.Destroy(ref world.Cameras, ov.Camera);
        EngineWorldSystem.DestroyEntity(ref world, ov.BgEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.AccentEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.BorderBottomEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.BorderLeftEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.BorderRightEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.LogicTrackEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.LogicBarEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.RenderTrackEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.RenderBarEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.GpuTrackEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.GpuBarEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.HeaderEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.FpsEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.LogicRowEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.RenderRowEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.GpuRowEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.DrawsEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.ObjectsEntity);
        EngineWorldSystem.DestroyEntity(ref world, ov.ScopesEntity);
        ov = default;
    }

    private static void LayoutPanel(ref ProfilerOverlay ov, ref EngineWorld world, float screenW, float screenH)
    {
        ov.LastScreenW = screenW;
        ov.LastScreenH = screenH;

        float uiH = ProfilerOverlay.VirtualHeight;
        float aspect = (screenHeightValid(screenH)) ? (screenW / screenH) : (16f / 9f);
        float uiW = uiH * aspect;

        CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, ov.Camera);
        if (cam != null)
        {
            cam->OrthoHeight = uiH;
            cam->Position = new Vec3(uiW * 0.5f, uiH * 0.5f, 10f);
            cam->LookTarget = new Vec3(uiW * 0.5f, uiH * 0.5f, 0f);
        }

        GetPanelOrigin(ov.Anchor, uiW, uiH, out float px, out float py);
        float topY = py + ProfilerOverlay.PanelHeight;

        TransformSystem.SetLocalPosition(ref world.Transforms, ov.BgEntity, new Vec3(px, py, -2f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.AccentEntity, new Vec3(px, topY - 4f, -1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.BorderBottomEntity, new Vec3(px, py, -1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.BorderLeftEntity, new Vec3(px, py, -1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.BorderRightEntity, new Vec3(px + ProfilerOverlay.PanelWidth - 2f, py, -1f));

        float xText = px + 14f;
        float xBar = px + ProfilerOverlay.BarOffsetX;

        // Top header + FPS
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.HeaderEntity, new Vec3(xText, topY - 12f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.FpsEntity, new Vec3(xText, topY - 36f, 1f));

        // 3 separate pipelined stage rows (label on left, independent fill bar on right)
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.LogicRowEntity, new Vec3(xText, topY - 68f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.LogicTrackEntity, new Vec3(xBar, topY - 84f, -0.5f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.LogicBarEntity, new Vec3(xBar, topY - 84f, 0f));

        TransformSystem.SetLocalPosition(ref world.Transforms, ov.RenderRowEntity, new Vec3(xText, topY - 94f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.RenderTrackEntity, new Vec3(xBar, topY - 110f, -0.5f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.RenderBarEntity, new Vec3(xBar, topY - 110f, 0f));

        TransformSystem.SetLocalPosition(ref world.Transforms, ov.GpuRowEntity, new Vec3(xText, topY - 120f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.GpuTrackEntity, new Vec3(xBar, topY - 136f, -0.5f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.GpuBarEntity, new Vec3(xBar, topY - 136f, 0f));

        // Draw / SetPass / Objects / Scopes
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.DrawsEntity, new Vec3(xText, topY - 152f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.ObjectsEntity, new Vec3(xText, topY - 178f, 1f));
        TransformSystem.SetLocalPosition(ref world.Transforms, ov.ScopesEntity, new Vec3(xText, topY - 204f, 1f));
    }

    private static bool screenHeightValid(float h) => h > 1f;

    private static void GetPanelOrigin(ProfilerAnchor anchor, float uiW, float uiH, out float x, out float y)
    {
        float m = ProfilerOverlay.Margin;
        switch (anchor)
        {
            case ProfilerAnchor.TopLeft:
                x = m;
                y = uiH - ProfilerOverlay.PanelHeight - m;
                break;
            case ProfilerAnchor.BottomLeft:
                x = m;
                y = m;
                break;
            case ProfilerAnchor.BottomRight:
                x = uiW - ProfilerOverlay.PanelWidth - m;
                y = m;
                break;
            default: // TopRight
                x = uiW - ProfilerOverlay.PanelWidth - m;
                y = uiH - ProfilerOverlay.PanelHeight - m;
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
        td.Color = new ColorF(color.R, color.G, color.B, 1.0f);
        td.SecondaryColor = new ColorF(color.R, color.G, color.B, 1.0f);
        td.OutlineWidth = 0.06f;
        td.OutlineColor = new ColorF(0.0f, 0.0f, 0.0f, 1.0f);
        td.Softness = 0.01f;
        td.ShadowColor = ColorF.Transparent; // Clean razor-sharp opaque glyphs without fuzzy shadow blur
        td.ShadowOffset = Vec2.Zero;
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

    private static void AppendStr(Span<char> dst, ref int pos, ReadOnlySpan<char> s)
    {
        int avail = dst.Length - pos;
        int n = s.Length < avail ? s.Length : avail;
        if (n <= 0) return;
        s.Slice(0, n).CopyTo(dst.Slice(pos));
        pos += n;
    }

    private static void AppendInt(Span<char> dst, ref int pos, int value)
    {
        if (pos >= dst.Length) return;
        if (value < 0)
        {
            dst[pos++] = '-';
            if (value == int.MinValue) { AppendStr(dst, ref pos, "2147483648"); return; }
            value = -value;
        }
        char* tmp = stackalloc char[12];
        int len = 0;
        do
        {
            tmp[len++] = (char)('0' + (value % 10));
            value /= 10;
        } while (value > 0);
        for (int i = len - 1; i >= 0 && pos < dst.Length; i--)
            dst[pos++] = tmp[i];
    }

    private static void AppendFixed(Span<char> dst, ref int pos, double value, int decimals)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;
        if (value < 0.0)
        {
            if (pos < dst.Length) dst[pos++] = '-';
            value = -value;
        }
        long scale = decimals == 1 ? 10L : 100L;
        long scaled = (long)(value * scale + 0.5);
        long whole = scaled / scale;
        long frac = scaled % scale;
        AppendInt(dst, ref pos, (int)Math.Min(whole, int.MaxValue));
        if (pos < dst.Length) dst[pos++] = '.';
        if (decimals == 2 && frac < 10 && pos < dst.Length) dst[pos++] = '0';
        AppendInt(dst, ref pos, (int)frac);
    }

    private static void AppendFixedPadded(Span<char> dst, ref int pos, double value, int decimals, int minWidth)
    {
        char* tmp = stackalloc char[24];
        Span<char> numSpan = new Span<char>(tmp, 24);
        int len = 0;
        AppendFixed(numSpan, ref len, value, decimals);
        for (int p = len; p < minWidth && pos < dst.Length; p++)
            dst[pos++] = ' ';
        AppendStr(dst, ref pos, numSpan.Slice(0, len));
    }
}
