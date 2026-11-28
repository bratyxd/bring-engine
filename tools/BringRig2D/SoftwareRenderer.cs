using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Rig2D;
using BringEngine.Simulation.World;
using StbImageSharp;

namespace BringRig2D;

/// <summary>
/// Headless software rasterizer driven by the EXACT Bring Engine Rig2DSystem.StepAll solver.
/// Generates visual QA renders: bind_pose.png, bind_pose_overlay.png, mesh_weights.png,
/// strip_&lt;anim&gt;.png contact sheets (with timestamps, ground line &amp; event badges), and preview_&lt;anim&gt;.gif.
/// </summary>
public static unsafe class SoftwareRenderer
{
    public struct RenderOptions
    {
        public string? ImagesDir;
        public string AnimFilter; // "" or "all" = all animations; otherwise specific clip name
        public string? SkinName;
        public int Width;
        public int Height;
        public int StripFrames;
        public int GifFps;
        public bool EmitClean;
        public bool EmitOverlay;
        public bool EmitWeights;
        public bool EmitStrip;
        public bool EmitGif;
        public Color32 BgColor;
    }

    private struct LoadedImage
    {
        public byte[] Rgba;
        public int Width;
        public int Height;
    }

    private static readonly Color32[] BonePalette =
    {
        new(255, 82, 82, 255),
        new(64, 210, 255, 255),
        new(110, 240, 120, 255),
        new(255, 200, 60, 255),
        new(210, 110, 255, 255),
        new(255, 130, 60, 255),
        new(80, 240, 210, 255),
        new(255, 100, 185, 255),
    };

    public static int Run(string rigPath, string outDir, in RenderOptions opts)
    {
        if (!File.Exists(rigPath))
        {
            Console.Error.WriteLine($"[BringRig2D] ERROR: Rig file not found: '{rigPath}'.");
            return 1;
        }

        Directory.CreateDirectory(outDir);
        string rigDir = Path.GetDirectoryName(Path.GetFullPath(rigPath)) ?? ".";
        string imgBase = !string.IsNullOrEmpty(opts.ImagesDir) ? Path.GetFullPath(opts.ImagesDir) : rigDir;

        // 1. Load loose part PNGs keyed by normalized region StringId + event name lookup from .rig2d.json
        LoadRigJsonMetadata(rigPath, imgBase, rigDir, out var imageMap, out var eventNames);

        // 2. Compile .rig2d.json -> BRIG and load into a real Bring EngineWorld!
        byte[] brig = Rig2DCompiler.CompileFromFile(rigPath);
        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 1 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var bundleArena = LinearArena.Create((nuint)Math.Max(512 * 1024, brig.Length * 4));

        try
        {
            var world = EngineWorld.Create(ref alloc.Persistent, maxEntities: 64, maxParticles: 32);
            var bundleId = BundleId.Create(1, 1);
            Rig2DAssetHandle assetH = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref world.Rigs, bundleId);
            Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, assetH);
            if (asset == null) return 1;

            EntityId ent = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, ent, Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);

            Rig2DInstanceHandle instH = Rig2DSystem.AddInstance(ref world.Rigs, ent, assetH);
            if (!string.IsNullOrEmpty(opts.SkinName))
                Rig2DSystem.SetSkin(ref world.Rigs, instH, opts.SkinName);

            // Step once at dt=0 to establish clean setup pose
            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 0f, ref alloc.Frame, ref alloc.Scratch);

            // Compute auto-fit view framing covering asset bounds + actual posed geometry
            ComputeFraming(in world.Rigs, instH, asset, opts.Width, opts.Height, out float originPxX, out float originPxY, out float skelToImgScale);

            // 3. Clean bind pose
            if (opts.EmitClean)
            {
                byte[] buf = CreateBackdrop(opts.Width, opts.Height, opts.BgColor, checker: true);
                RasterizeRig(buf, opts.Width, opts.Height, in world.Rigs, instH, asset, imageMap, originPxX, originPxY, skelToImgScale);
                string p = Path.Combine(outDir, "bind_pose.png");
                ImagePrep.SavePng(p, buf, opts.Width, opts.Height);
                Console.WriteLine($"[BringRig2D] rendered -> '{p}'");
            }

            // 4. Bind pose + skeleton/pivot/grid/slot QA overlay
            if (opts.EmitOverlay)
            {
                byte[] buf = CreateBackdrop(opts.Width, opts.Height, opts.BgColor, checker: false);
                DrawGridOverlay(buf, opts.Width, opts.Height, originPxX, originPxY, skelToImgScale);
                RasterizeRig(buf, opts.Width, opts.Height, in world.Rigs, instH, asset, imageMap, originPxX, originPxY, skelToImgScale);
                DrawSkeletonOverlay(buf, opts.Width, opts.Height, in world.Rigs, instH, asset, originPxX, originPxY, skelToImgScale);
                string p = Path.Combine(outDir, "bind_pose_overlay.png");
                ImagePrep.SavePng(p, buf, opts.Width, opts.Height);
                Console.WriteLine($"[BringRig2D] rendered -> '{p}'");
            }

            // 5. Mesh wireframe + color-coded bone weight heatmap
            if (opts.EmitWeights)
            {
                byte[] buf = CreateBackdrop(opts.Width, opts.Height, opts.BgColor, checker: false);
                RasterizeRig(buf, opts.Width, opts.Height, in world.Rigs, instH, asset, imageMap, originPxX, originPxY, skelToImgScale, alphaMul: 0.45f);
                DrawMeshWeightsOverlay(buf, opts.Width, opts.Height, in world.Rigs, instH, asset, originPxX, originPxY, skelToImgScale);
                string p = Path.Combine(outDir, "mesh_weights.png");
                ImagePrep.SavePng(p, buf, opts.Width, opts.Height);
                Console.WriteLine($"[BringRig2D] rendered -> '{p}'");
            }

            // 6. Animation contact strips & animated GIF89a previews
            if (opts.EmitStrip || opts.EmitGif)
            {
                bool allAnims = string.IsNullOrEmpty(opts.AnimFilter) || opts.AnimFilter.Equals("all", StringComparison.OrdinalIgnoreCase);
                for (int ci = 0; ci < asset->ClipCount; ci++)
                {
                    string clipName = asset->Clips[ci].NameText.ToString();
                    if (!allAnims && !clipName.Equals(opts.AnimFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    float dur = asset->Clips[ci].Duration > 0.01f ? asset->Clips[ci].Duration : 1f;
                    string safeClip = SanitizeFileName(clipName);

                    if (opts.EmitStrip)
                    {
                        int nFrames = Math.Clamp(opts.StripFrames, 2, 16);
                        int cellW = Math.Clamp(opts.Width / 2, 160, 384);
                        int cellH = Math.Clamp(opts.Height / 2, 160, 384);
                        int headerH = 26;
                        int stripW = cellW * nFrames;
                        int stripH = cellH + headerH;
                        byte[] stripBuf = CreateBackdrop(stripW, stripH, opts.BgColor, checker: false);

                        ComputeFraming(in world.Rigs, instH, asset, cellW, cellH, out float cOx, out float cOy, out float cScale);

                        // Fresh instance per clip so springs start cleanly and deterministically
                        Rig2DInstanceHandle animInst = Rig2DSystem.AddInstance(ref world.Rigs, ent, assetH);
                        if (!string.IsNullOrEmpty(opts.SkinName))
                            Rig2DSystem.SetSkin(ref world.Rigs, animInst, opts.SkinName);
                        Rig2DSystem.SetAnimation(ref world.Rigs, animInst, ci, track: 0, loop: true, mixDuration: 0f);

                        // Warm up 1/4 cycle for springs if clip loops
                        float stepDt = dur / nFrames;
                        const float subDt = 1f / 60f;
                        for (int fi = 0; fi < nFrames; fi++)
                        {
                            float targetT = fi * stepDt;
                            var frameEvents = new List<string>();
                            int subSteps = fi == 0 ? 1 : Math.Max(1, (int)MathF.Round(stepDt / subDt));
                            float h = fi == 0 ? 0f : (stepDt / subSteps);

                            for (int ss = 0; ss < subSteps; ss++)
                            {
                                alloc.BeginFrame();
                                Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, h, ref alloc.Frame, ref alloc.Scratch);
                                var fired = Rig2DSystem.GetFiredEvents(in world.Rigs);
                                for (int e = 0; e < fired.Length; e++)
                                {
                                    if (fired[e].Instance == animInst)
                                    {
                                        string label = eventNames.TryGetValue(fired[e].EventName.Value, out string? evName) && !string.IsNullOrEmpty(evName)
                                            ? evName
                                            : $"EV:{fired[e].IntVal}";
                                        frameEvents.Add(label);
                                    }
                                }
                            }

                            byte[] cellBuf = CreateBackdrop(cellW, cellH, opts.BgColor, checker: false);
                            // Ground baseline at skeleton y=0
                            int groundY = Math.Clamp((int)MathF.Round(cOy), 0, cellH - 1);
                            DrawLine(cellBuf, cellW, cellH, 0, groundY, cellW - 1, groundY, new Color32(70, 95, 130, 255));
                            RasterizeRig(cellBuf, cellW, cellH, in world.Rigs, animInst, asset, imageMap, cOx, cOy, cScale);

                            // Copy cell into contact strip
                            int dstX = fi * cellW;
                            BlitBuffer(cellBuf, cellW, cellH, stripBuf, stripW, stripH, dstX, headerH);
                            DrawRectOutline(stripBuf, stripW, stripH, dstX, 0, cellW - 1, stripH - 1, new Color32(55, 70, 95, 255));
                            DrawText(stripBuf, stripW, stripH, dstX + 6, 8, $"F{fi} {targetT:F2}s", new Color32(220, 232, 248, 255));
                            if (frameEvents.Count > 0)
                            {
                                DrawFilledRect(stripBuf, stripW, stripH, dstX + 4, headerH + 4, cellW - 8, 13, new Color32(200, 65, 45, 220));
                                DrawText(stripBuf, stripW, stripH, dstX + 7, headerH + 7, string.Join(" ", frameEvents), new Color32(255, 255, 255, 255));
                            }
                        }

                        Rig2DSystem.RemoveInstance(ref world.Rigs, animInst);
                        string p = Path.Combine(outDir, $"strip_{safeClip}.png");
                        ImagePrep.SavePng(p, stripBuf, stripW, stripH);
                        Console.WriteLine($"[BringRig2D] rendered -> '{p}' ({stripW}x{stripH}, {nFrames} frames)");
                    }

                    if (opts.EmitGif)
                    {
                        int fps = Math.Clamp(opts.GifFps, 4, 30);
                        int gifW = Math.Clamp(opts.Width, 128, 512);
                        int gifH = Math.Clamp(opts.Height, 128, 512);
                        int totalFrames = Math.Clamp((int)MathF.Ceiling(dur * fps), 4, 120);
                        float dt = dur / totalFrames;

                        ComputeFraming(in world.Rigs, instH, asset, gifW, gifH, out float gOx, out float gOy, out float gScale);
                        Rig2DInstanceHandle gifInst = Rig2DSystem.AddInstance(ref world.Rigs, ent, assetH);
                        if (!string.IsNullOrEmpty(opts.SkinName))
                            Rig2DSystem.SetSkin(ref world.Rigs, gifInst, opts.SkinName);
                        Rig2DSystem.SetAnimation(ref world.Rigs, gifInst, ci, track: 0, loop: true, mixDuration: 0f);

                        var frames = new List<byte[]>(totalFrames);
                        for (int fi = 0; fi < totalFrames; fi++)
                        {
                            alloc.BeginFrame();
                            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, fi == 0 ? 0f : dt, ref alloc.Frame, ref alloc.Scratch);
                            byte[] fBuf = CreateBackdrop(gifW, gifH, opts.BgColor, checker: false);
                            int groundY = Math.Clamp((int)MathF.Round(gOy), 0, gifH - 1);
                            DrawLine(fBuf, gifW, gifH, 0, groundY, gifW - 1, groundY, new Color32(60, 80, 110, 255));
                            RasterizeRig(fBuf, gifW, gifH, in world.Rigs, gifInst, asset, imageMap, gOx, gOy, gScale);
                            DrawText(fBuf, gifW, gifH, 8, 8, $"{clipName} {fi * dt:F2}s", new Color32(215, 228, 245, 255));
                            frames.Add(fBuf);
                        }

                        Rig2DSystem.RemoveInstance(ref world.Rigs, gifInst);
                        string p = Path.Combine(outDir, $"preview_{safeClip}.gif");
                        GifEncoder.WriteAnimatedGif(p, frames, gifW, gifH, fps);
                        Console.WriteLine($"[BringRig2D] rendered -> '{p}' ({gifW}x{gifH}, {totalFrames} frames @ {fps}fps)");
                    }
                }
            }

            return 0;
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
    }

    private static void LoadRigJsonMetadata(
        string rigPath,
        string imgBase,
        string rigDir,
        out Dictionary<ulong, LoadedImage> imageMap,
        out Dictionary<ulong, string> eventNames)
    {
        imageMap = new Dictionary<ulong, LoadedImage>();
        eventNames = new Dictionary<ulong, string>();
        byte[] raw = File.ReadAllBytes(rigPath);
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) raw = raw[3..];
        if (JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
            return;

        var imgDict = imageMap;
        void TryLoad(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return;
            string key = Rig2DCompiler.NormalizeRegionKey(rel);
            ulong hash = StringId.Hash(key).Value;
            if (imgDict.ContainsKey(hash)) return;
            string? full = AutoMesher.ResolveImagePath(imgBase, rigDir, rel);
            if (full == null) return;
            ImageResult dec = ImageResult.FromMemory(File.ReadAllBytes(full), ColorComponents.RedGreenBlueAlpha);
            imgDict[hash] = new LoadedImage { Rgba = dec.Data, Width = dec.Width, Height = dec.Height };
        }

        if (root["skins"] is JsonObject skins)
        {
            foreach (var sk in skins)
            {
                if (sk.Value is not JsonObject slots) continue;
                foreach (var sl in slots)
                {
                    if (sl.Value is not JsonObject atts) continue;
                    foreach (var at in atts)
                    {
                        if (at.Value is not JsonObject att) continue;
                        string img = att["image"]?.GetValue<string>() ?? att["path"]?.GetValue<string>() ?? "";
                        TryLoad(img);
                        if (att["frames"] is JsonArray fArr)
                        {
                            foreach (var fn in fArr)
                                TryLoad(fn?.GetValue<string>() ?? "");
                        }
                    }
                }
            }
        }

        if (root["events"] is JsonObject evDefs)
        {
            foreach (var evKv in evDefs)
            {
                if (!string.IsNullOrEmpty(evKv.Key))
                    eventNames[StringId.Hash(evKv.Key).Value] = evKv.Key;
            }
        }

        if (root["animations"] is JsonObject anims)
        {
            foreach (var animKv in anims)
            {
                if (animKv.Value is not JsonObject animObj) continue;
                if (animObj["events"] is not JsonArray evArr) continue;
                foreach (var evNode in evArr)
                {
                    if (evNode is not JsonObject evObj) continue;
                    string? evName = evObj["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(evName))
                        eventNames[StringId.Hash(evName).Value] = evName;
                }
            }
        }
    }

    private static void ComputeFraming(
        in Rig2DTable table,
        Rig2DInstanceHandle instH,
        Rig2DAssetData* asset,
        int imgW,
        int imgH,
        out float originPxX,
        out float originPxY,
        out float scale)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in table, instH);
        if (inst != null)
        {
            Rig2DAffine2D* bw = table.BoneWorld + inst->BoneSliceStart;
            for (int b = 0; b < asset->BoneCount; b++)
            {
                Expand(bw[b].Tx, bw[b].Ty, ref minX, ref maxX, ref minY, ref maxY);
                if (asset->Bones[b].Length > 0f)
                {
                    Vec2 tip = Rig2DAffine2D.TransformPoint(in bw[b], new Vec2(asset->Bones[b].Length, 0f));
                    Expand(tip.X, tip.Y, ref minX, ref maxX, ref minY, ref maxY);
                }
            }
            for (int s = 0; s < inst->SlotSliceCount; s++)
            {
                int attIdx = table.SlotStates[inst->SlotSliceStart + s].ActiveAttachmentIndex;
                if ((uint)attIdx >= (uint)asset->AttachmentCount) continue;
                ref readonly Rig2DAttachmentData att = ref asset->Attachments[attIdx];
                if (att.Kind == Rig2DAttachmentKind.Region || att.Kind == Rig2DAttachmentKind.Sequence)
                {
                    int bi = asset->Slots[s].BoneIndex;
                    Rig2DSystem.ComputeRegionQuadCorners(in att, in bw[bi], out Vec2 bl, out Vec2 br, out Vec2 tr, out Vec2 tl);
                    Expand(bl.X, bl.Y, ref minX, ref maxX, ref minY, ref maxY);
                    Expand(br.X, br.Y, ref minX, ref maxX, ref minY, ref maxY);
                    Expand(tr.X, tr.Y, ref minX, ref maxX, ref minY, ref maxY);
                    Expand(tl.X, tl.Y, ref minX, ref maxX, ref minY, ref maxY);
                }
                else if (att.Kind == Rig2DAttachmentKind.WeightedMesh)
                {
                    int bi = asset->Slots[s].BoneIndex;
                    for (int v = 0; v < att.VertexCount; v++)
                    {
                        Vec2 p = Rig2DSystem.SkinMeshVertex(asset, bw, bi, in asset->MeshVertices[att.VertexStart + v], Vec2.Zero);
                        Expand(p.X, p.Y, ref minX, ref maxX, ref minY, ref maxY);
                    }
                }
            }
        }

        if (minX > maxX)
        {
            float bw = asset->BoundsSize.X > 1f ? asset->BoundsSize.X : 256f;
            float bh = asset->BoundsSize.Y > 1f ? asset->BoundsSize.Y : 256f;
            minX = -bw * 0.5f; maxX = bw * 0.5f;
            minY = 0f; maxY = bh;
        }

        float spanX = MathF.Max(64f, maxX - minX);
        float spanY = MathF.Max(64f, maxY - minY);
        float pad = 1.28f;
        scale = MathF.Min((imgW * 0.88f) / (spanX * pad), (imgH * 0.86f) / (spanY * pad));
        float cx = (minX + maxX) * 0.5f;
        float cy = (minY + maxY) * 0.5f;
        originPxX = imgW * 0.5f - cx * scale;
        originPxY = imgH * 0.52f + cy * scale;
    }

    private static void Expand(float x, float y, ref float minX, ref float maxX, ref float minY, ref float maxY)
    {
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
    }

    private static Vec2 SkelToImg(Vec2 s, float ox, float oy, float scale)
        => new Vec2(ox + s.X * scale, oy - s.Y * scale);

    private static void RasterizeRig(
        byte[] dst, int w, int h,
        in Rig2DTable table,
        Rig2DInstanceHandle instH,
        Rig2DAssetData* asset,
        Dictionary<ulong, LoadedImage> images,
        float ox, float oy, float scale,
        float alphaMul = 1f)
    {
        Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in table, instH);
        if (inst == null) return;

        Rig2DAffine2D* bw = table.BoneWorld + inst->BoneSliceStart;
        Rig2DRuntimeSlotState* states = table.SlotStates + inst->SlotSliceStart;
        int* drawOrder = table.DrawOrder + inst->SlotSliceStart;

        for (int di = 0; di < inst->SlotSliceCount; di++)
        {
            int slotIdx = drawOrder[di];
            if ((uint)slotIdx >= (uint)asset->SlotCount) continue;
            ref readonly Rig2DSlotSetupData slotSetup = ref asset->Slots[slotIdx];
            ref readonly Rig2DRuntimeSlotState st = ref states[slotIdx];
            int attIdx = st.ActiveAttachmentIndex;
            if ((uint)attIdx >= (uint)asset->AttachmentCount) continue;
            ref readonly Rig2DAttachmentData att = ref asset->Attachments[attIdx];
            if (att.Kind == Rig2DAttachmentKind.None) continue;

            StringId regionKey = att.SpriteRegionName;
            if (att.Kind == Rig2DAttachmentKind.Sequence && att.SeqFrameCount > 0 && asset->SequenceFrameNames != null)
            {
                int fi = Math.Clamp(st.SequenceFrame, 0, att.SeqFrameCount - 1);
                regionKey = asset->SequenceFrameNames[att.SeqFrameStart + fi];
            }

            bool hasImg = images.TryGetValue(regionKey.Value, out LoadedImage tex);
            ColorF light = new ColorF(
                st.LightColor.R * inst->Tint.R,
                st.LightColor.G * inst->Tint.G,
                st.LightColor.B * inst->Tint.B,
                st.LightColor.A * inst->Tint.A * alphaMul);
            ColorF dark = new ColorF(
                st.DarkColor.R + inst->DarkTint.R,
                st.DarkColor.G + inst->DarkTint.G,
                st.DarkColor.B + inst->DarkTint.B,
                inst->HitFlash);

            if (att.Kind == Rig2DAttachmentKind.Region || att.Kind == Rig2DAttachmentKind.Sequence)
            {
                Rig2DSystem.ComputeRegionQuadCorners(in att, in bw[slotSetup.BoneIndex], out Vec2 bl, out Vec2 br, out Vec2 tr, out Vec2 tl);
                Vec2 p0 = SkelToImg(bl, ox, oy, scale);
                Vec2 p1 = SkelToImg(br, ox, oy, scale);
                Vec2 p2 = SkelToImg(tr, ox, oy, scale);
                Vec2 p3 = SkelToImg(tl, ox, oy, scale);
                Vec2 uv0 = new(0f, 1f), uv1 = new(1f, 1f), uv2 = new(1f, 0f), uv3 = new(0f, 0f);
                ColorF fallbackCol = hasImg ? light : new ColorF(0.35f + (slotIdx % 4) * 0.15f, 0.45f + ((slotIdx + 1) % 3) * 0.15f, 0.65f, 0.85f * alphaMul);

                RasterizeTriangle(dst, w, h, p0, p1, p2, uv0, uv1, uv2, hasImg ? tex : default, hasImg, fallbackCol, dark, slotSetup.BlendMode);
                RasterizeTriangle(dst, w, h, p0, p2, p3, uv0, uv2, uv3, hasImg ? tex : default, hasImg, fallbackCol, dark, slotSetup.BlendMode);
            }
            else if (att.Kind == Rig2DAttachmentKind.WeightedMesh && att.VertexCount > 0 && att.IndexCount >= 3)
            {
                Vec2[] screenPts = new Vec2[att.VertexCount];
                Vec2* defPtr = (st.DeformVertexCount == att.VertexCount && table.FrameDeformDeltas.Ptr != null &&
                                st.DeformVertexOffset >= 0 && st.DeformVertexOffset + att.VertexCount <= table.FrameDeformDeltas.Length)
                    ? table.FrameDeformDeltas.Ptr + st.DeformVertexOffset
                    : null;

                for (int v = 0; v < att.VertexCount; v++)
                {
                    Vec2 d = defPtr != null ? defPtr[v] : Vec2.Zero;
                    Vec2 sp = Rig2DSystem.SkinMeshVertex(asset, bw, slotSetup.BoneIndex, in asset->MeshVertices[att.VertexStart + v], d);
                    screenPts[v] = SkelToImg(sp, ox, oy, scale);
                }

                ColorF fallbackCol = hasImg ? light : new ColorF(0.55f, 0.35f, 0.75f, 0.85f * alphaMul);
                for (int t = 0; t + 2 < att.IndexCount; t += 3)
                {
                    int i0 = (int)asset->MeshIndices[att.IndexStart + t];
                    int i1 = (int)asset->MeshIndices[att.IndexStart + t + 1];
                    int i2 = (int)asset->MeshIndices[att.IndexStart + t + 2];
                    Vec2 uv0 = asset->MeshVertices[att.VertexStart + i0].Uv;
                    Vec2 uv1 = asset->MeshVertices[att.VertexStart + i1].Uv;
                    Vec2 uv2 = asset->MeshVertices[att.VertexStart + i2].Uv;
                    RasterizeTriangle(dst, w, h, screenPts[i0], screenPts[i1], screenPts[i2], uv0, uv1, uv2, hasImg ? tex : default, hasImg, fallbackCol, dark, slotSetup.BlendMode);
                }
            }
        }
    }

    private static void RasterizeTriangle(
        byte[] dst, int w, int h,
        Vec2 p0, Vec2 p1, Vec2 p2,
        Vec2 uv0, Vec2 uv1, Vec2 uv2,
        in LoadedImage tex, bool hasTex,
        ColorF light, ColorF dark,
        Rig2DBlendMode blend)
    {
        float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X);
        if (MathF.Abs(area) < 1e-4f) return;
        float invArea = 1f / area;

        int minX = Math.Clamp((int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))), 0, w - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))), 0, w - 1);
        int minY = Math.Clamp((int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))), 0, h - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))), 0, h - 1);

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((p1.X - px) * (p2.Y - py) - (p1.Y - py) * (p2.X - px)) * invArea;
                float w1 = ((p2.X - px) * (p0.Y - py) - (p2.Y - py) * (p0.X - px)) * invArea;
                float w2 = 1f - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;

                float tr = 1f, tg = 1f, tb = 1f, ta = 1f;
                if (hasTex && tex.Rgba != null)
                {
                    float u = Math.Clamp(uv0.X * w0 + uv1.X * w1 + uv2.X * w2, 0f, 1f);
                    float v = Math.Clamp(uv0.Y * w0 + uv1.Y * w1 + uv2.Y * w2, 0f, 1f);
                    SampleBilinear(in tex, u, v, out tr, out tg, out tb, out ta);
                }

                float a = ta * light.A;
                if (a <= 0.002f) continue;

                float sr = tr * light.R + (1f - tr) * dark.R;
                float sg = tg * light.G + (1f - tg) * dark.G;
                float sb = tb * light.B + (1f - tb) * dark.B;
                if (dark.A > 1e-4f)
                {
                    float f = Math.Clamp(dark.A, 0f, 1f);
                    sr = DetMath.Lerp(sr, 1f, f);
                    sg = DetMath.Lerp(sg, 1f, f);
                    sb = DetMath.Lerp(sb, 1f, f);
                }

                int o = (y * w + x) * 4;
                float dr = dst[o + 0] / 255f;
                float dg = dst[o + 1] / 255f;
                float db = dst[o + 2] / 255f;

                float or_, og_, ob_;
                switch (blend)
                {
                    case Rig2DBlendMode.Additive:
                        or_ = dr + sr * a;
                        og_ = dg + sg * a;
                        ob_ = db + sb * a;
                        break;
                    case Rig2DBlendMode.Multiply:
                        or_ = dr * (sr * a + (1f - a));
                        og_ = dg * (sg * a + (1f - a));
                        ob_ = db * (sb * a + (1f - a));
                        break;
                    default:
                        or_ = sr * a + dr * (1f - a);
                        og_ = sg * a + dg * (1f - a);
                        ob_ = sb * a + db * (1f - a);
                        break;
                }

                dst[o + 0] = (byte)Math.Clamp((int)MathF.Round(or_ * 255f), 0, 255);
                dst[o + 1] = (byte)Math.Clamp((int)MathF.Round(og_ * 255f), 0, 255);
                dst[o + 2] = (byte)Math.Clamp((int)MathF.Round(ob_ * 255f), 0, 255);
                dst[o + 3] = 255;
            }
        }
    }

    private static void SampleBilinear(in LoadedImage tex, float u, float v, out float r, out float g, out float b, out float a)
    {
        float fx = u * (tex.Width - 1);
        float fy = v * (tex.Height - 1);
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, tex.Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(fy), 0, tex.Height - 1);
        int x1 = Math.Min(x0 + 1, tex.Width - 1);
        int y1 = Math.Min(y0 + 1, tex.Height - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        int o00 = (y0 * tex.Width + x0) * 4;
        int o10 = (y0 * tex.Width + x1) * 4;
        int o01 = (y1 * tex.Width + x0) * 4;
        int o11 = (y1 * tex.Width + x1) * 4;

        float w00 = (1f - tx) * (1f - ty);
        float w10 = tx * (1f - ty);
        float w01 = (1f - tx) * ty;
        float w11 = tx * ty;

        r = (tex.Rgba[o00 + 0] * w00 + tex.Rgba[o10 + 0] * w10 + tex.Rgba[o01 + 0] * w01 + tex.Rgba[o11 + 0] * w11) / 255f;
        g = (tex.Rgba[o00 + 1] * w00 + tex.Rgba[o10 + 1] * w10 + tex.Rgba[o01 + 1] * w01 + tex.Rgba[o11 + 1] * w11) / 255f;
        b = (tex.Rgba[o00 + 2] * w00 + tex.Rgba[o10 + 2] * w10 + tex.Rgba[o01 + 2] * w01 + tex.Rgba[o11 + 2] * w11) / 255f;
        a = (tex.Rgba[o00 + 3] * w00 + tex.Rgba[o10 + 3] * w10 + tex.Rgba[o01 + 3] * w01 + tex.Rgba[o11 + 3] * w11) / 255f;
    }

    // =========================================================================
    // Visual QA Overlays: Pixel Grid, Bone Sticks, Pivots, Mesh Weight Heatmap
    // =========================================================================

    private static void DrawGridOverlay(byte[] buf, int w, int h, float ox, float oy, float scale)
    {
        float gridStep = 50f;
        Color32 minor = new(34, 42, 60, 255);
        Color32 major = new(54, 68, 96, 255);
        Color32 axis = new(110, 145, 195, 255);

        int minGX = (int)MathF.Floor((-ox / scale) / gridStep);
        int maxGX = (int)MathF.Ceiling(((w - ox) / scale) / gridStep);
        for (int gx = minGX; gx <= maxGX; gx++)
        {
            int px = (int)MathF.Round(ox + gx * gridStep * scale);
            if ((uint)px >= (uint)w) continue;
            Color32 c = gx == 0 ? axis : ((gx % 2 == 0) ? major : minor);
            DrawLine(buf, w, h, px, 0, px, h - 1, c);
            if (gx % 2 == 0)
                DrawText(buf, w, h, px + 2, h - 11, $"{(int)(gx * gridStep)}", new Color32(120, 140, 170, 255));
        }

        int minGY = (int)MathF.Floor(((oy - h) / scale) / gridStep);
        int maxGY = (int)MathF.Ceiling((oy / scale) / gridStep);
        for (int gy = minGY; gy <= maxGY; gy++)
        {
            int py = (int)MathF.Round(oy - gy * gridStep * scale);
            if ((uint)py >= (uint)h) continue;
            Color32 c = gy == 0 ? axis : ((gy % 2 == 0) ? major : minor);
            DrawLine(buf, w, h, 0, py, w - 1, py, c);
            if (gy % 2 == 0)
                DrawText(buf, w, h, 4, py - 9, $"{(int)(gy * gridStep)}", new Color32(120, 140, 170, 255));
        }
    }

    private static void DrawSkeletonOverlay(byte[] buf, int w, int h, in Rig2DTable table, Rig2DInstanceHandle instH, Rig2DAssetData* asset, float ox, float oy, float scale)
    {
        Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in table, instH);
        if (inst == null) return;
        Rig2DAffine2D* bw = table.BoneWorld + inst->BoneSliceStart;

        // Draw slot bounding boxes (subtle teal)
        for (int s = 0; s < inst->SlotSliceCount; s++)
        {
            int attIdx = table.SlotStates[inst->SlotSliceStart + s].ActiveAttachmentIndex;
            if ((uint)attIdx >= (uint)asset->AttachmentCount) continue;
            ref readonly Rig2DAttachmentData att = ref asset->Attachments[attIdx];
            if (att.Kind == Rig2DAttachmentKind.Region || att.Kind == Rig2DAttachmentKind.Sequence)
            {
                Rig2DSystem.ComputeRegionQuadCorners(in att, in bw[asset->Slots[s].BoneIndex], out Vec2 bl, out Vec2 br, out Vec2 tr, out Vec2 tl);
                Vec2 p0 = SkelToImg(bl, ox, oy, scale), p1 = SkelToImg(br, ox, oy, scale);
                Vec2 p2 = SkelToImg(tr, ox, oy, scale), p3 = SkelToImg(tl, ox, oy, scale);
                Color32 boxC = new(70, 190, 200, 180);
                DrawLine(buf, w, h, (int)p0.X, (int)p0.Y, (int)p1.X, (int)p1.Y, boxC);
                DrawLine(buf, w, h, (int)p1.X, (int)p1.Y, (int)p2.X, (int)p2.Y, boxC);
                DrawLine(buf, w, h, (int)p2.X, (int)p2.Y, (int)p3.X, (int)p3.Y, boxC);
                DrawLine(buf, w, h, (int)p3.X, (int)p3.Y, (int)p0.X, (int)p0.Y, boxC);
            }
        }

        // Draw bone sticks with directional wedges & joint pivot crosshairs
        for (int b = 0; b < asset->BoneCount; b++)
        {
            Color32 col = BonePalette[b % BonePalette.Length];
            Vec2 start = SkelToImg(new Vec2(bw[b].Tx, bw[b].Ty), ox, oy, scale);
            float len = asset->Bones[b].Length;

            if (len > 2f)
            {
                Vec2 tipSkel = Rig2DAffine2D.TransformPoint(in bw[b], new Vec2(len, 0f));
                Vec2 tip = SkelToImg(tipSkel, ox, oy, scale);
                Vec2 dir = tip - start;
                float dl = dir.Length();
                if (dl > 1f)
                {
                    Vec2 perp = new Vec2(-dir.Y / dl, dir.X / dl) * MathF.Min(5.5f, dl * 0.18f);
                    Vec2 mid = start + dir * 0.16f;
                    Vec2 wL = mid + perp;
                    Vec2 wR = mid - perp;
                    DrawLine(buf, w, h, (int)start.X, (int)start.Y, (int)wL.X, (int)wL.Y, col);
                    DrawLine(buf, w, h, (int)start.X, (int)start.Y, (int)wR.X, (int)wR.Y, col);
                    DrawLine(buf, w, h, (int)wL.X, (int)wL.Y, (int)tip.X, (int)tip.Y, col);
                    DrawLine(buf, w, h, (int)wR.X, (int)wR.Y, (int)tip.X, (int)tip.Y, col);
                }
            }
            else if (asset->Bones[b].ParentIndex >= 0)
            {
                int p = asset->Bones[b].ParentIndex;
                Vec2 pStart = SkelToImg(new Vec2(bw[p].Tx, bw[p].Ty), ox, oy, scale);
                DrawLine(buf, w, h, (int)pStart.X, (int)pStart.Y, (int)start.X, (int)start.Y, new Color32(150, 150, 165, 255));
            }

            // Pivot crosshair + center dot
            int sx = (int)MathF.Round(start.X), sy = (int)MathF.Round(start.Y);
            DrawLine(buf, w, h, sx - 5, sy, sx + 5, sy, Color32.White);
            DrawLine(buf, w, h, sx, sy - 5, sx, sy + 5, Color32.White);
            DrawFilledRect(buf, w, h, sx - 2, sy - 2, 5, 5, col);
            DrawText(buf, w, h, sx + 6, sy - 4, asset->Bones[b].NameText.ToString(), Color32.White);
        }
    }

    private static void DrawMeshWeightsOverlay(byte[] buf, int w, int h, in Rig2DTable table, Rig2DInstanceHandle instH, Rig2DAssetData* asset, float ox, float oy, float scale)
    {
        Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in table, instH);
        if (inst == null) return;
        Rig2DAffine2D* bw = table.BoneWorld + inst->BoneSliceStart;
        var legendBones = new HashSet<int>();

        for (int s = 0; s < inst->SlotSliceCount; s++)
        {
            int attIdx = table.SlotStates[inst->SlotSliceStart + s].ActiveAttachmentIndex;
            if ((uint)attIdx >= (uint)asset->AttachmentCount) continue;
            ref readonly Rig2DAttachmentData att = ref asset->Attachments[attIdx];
            if (att.Kind != Rig2DAttachmentKind.WeightedMesh || att.VertexCount <= 0) continue;

            int ownerBone = asset->Slots[s].BoneIndex;
            Vec2[] pts = new Vec2[att.VertexCount];
            Color32[] vCols = new Color32[att.VertexCount];

            for (int v = 0; v < att.VertexCount; v++)
            {
                ref readonly Rig2DMeshVertex mv = ref asset->MeshVertices[att.VertexStart + v];
                Vec2 sp = Rig2DSystem.SkinMeshVertex(asset, bw, ownerBone, in mv, Vec2.Zero);
                pts[v] = SkelToImg(sp, ox, oy, scale);

                float rr = 0f, gg = 0f, bb = 0f;
                if (mv.WeightSum <= 1e-5f)
                {
                    Color32 c = BonePalette[ownerBone % BonePalette.Length];
                    rr = c.R; gg = c.G; bb = c.B;
                    legendBones.Add(ownerBone);
                }
                else
                {
                    uint packed = mv.PackedBoneIndices;
                    for (int k = 0; k < 4; k++)
                    {
                        float wt = k switch { 0 => mv.BoneWeights.X, 1 => mv.BoneWeights.Y, 2 => mv.BoneWeights.Z, _ => mv.BoneWeights.W };
                        if (wt <= 1e-4f) continue;
                        int bi = (int)((packed >> (k * 8)) & 0xFFu);
                        Color32 c = BonePalette[bi % BonePalette.Length];
                        rr += c.R * wt; gg += c.G * wt; bb += c.B * wt;
                        legendBones.Add(bi);
                    }
                }
                vCols[v] = new Color32((byte)Math.Clamp((int)rr, 0, 255), (byte)Math.Clamp((int)gg, 0, 255), (byte)Math.Clamp((int)bb, 0, 255), 255);
            }

            // Wireframe edges
            Color32 wire = new(215, 228, 245, 190);
            for (int t = 0; t + 2 < att.IndexCount; t += 3)
            {
                int i0 = (int)asset->MeshIndices[att.IndexStart + t];
                int i1 = (int)asset->MeshIndices[att.IndexStart + t + 1];
                int i2 = (int)asset->MeshIndices[att.IndexStart + t + 2];
                DrawLine(buf, w, h, (int)pts[i0].X, (int)pts[i0].Y, (int)pts[i1].X, (int)pts[i1].Y, wire);
                DrawLine(buf, w, h, (int)pts[i1].X, (int)pts[i1].Y, (int)pts[i2].X, (int)pts[i2].Y, wire);
                DrawLine(buf, w, h, (int)pts[i2].X, (int)pts[i2].Y, (int)pts[i0].X, (int)pts[i0].Y, wire);
            }

            // Weight-colored vertex dots
            for (int v = 0; v < att.VertexCount; v++)
                DrawFilledRect(buf, w, h, (int)pts[v].X - 2, (int)pts[v].Y - 2, 5, 5, vCols[v]);
        }

        // Draw compact bone color legend at top-left
        int ly = 8;
        DrawText(buf, w, h, 8, ly, "MESH WEIGHTS", Color32.White);
        ly += 12;
        foreach (int bi in legendBones)
        {
            if (bi < 0 || bi >= asset->BoneCount) continue;
            DrawFilledRect(buf, w, h, 8, ly, 8, 8, BonePalette[bi % BonePalette.Length]);
            DrawText(buf, w, h, 20, ly, asset->Bones[bi].NameText.ToString(), Color32.White);
            ly += 11;
            if (ly > h - 16) break;
        }
    }

    // =========================================================================
    // 2D Primitives & Crisp 5x7 Pixel Bitmap Font
    // =========================================================================

    private static byte[] CreateBackdrop(int w, int h, Color32 bg, bool checker)
    {
        byte[] buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            int delta = checker && (((x >> 4) + (y >> 4)) & 1) != 0 ? 10 : 0;
            buf[o + 0] = (byte)Math.Min(255, bg.R + delta);
            buf[o + 1] = (byte)Math.Min(255, bg.G + delta);
            buf[o + 2] = (byte)Math.Min(255, bg.B + delta);
            buf[o + 3] = 255;
        }
        return buf;
    }

    private static void BlitBuffer(byte[] src, int sw, int sh, byte[] dst, int dw, int dh, int dx, int dy)
    {
        for (int y = 0; y < sh; y++)
        {
            int ty = dy + y;
            if ((uint)ty >= (uint)dh) continue;
            for (int x = 0; x < sw; x++)
            {
                int tx = dx + x;
                if ((uint)tx >= (uint)dw) continue;
                int so = (y * sw + x) * 4;
                int doff = (ty * dw + tx) * 4;
                dst[doff + 0] = src[so + 0];
                dst[doff + 1] = src[so + 1];
                dst[doff + 2] = src[so + 2];
                dst[doff + 3] = 255;
            }
        }
    }

    private static void PlotBlend(byte[] buf, int w, int h, int x, int y, Color32 c)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int o = (y * w + x) * 4;
        float a = c.A / 255f;
        buf[o + 0] = (byte)(c.R * a + buf[o + 0] * (1f - a));
        buf[o + 1] = (byte)(c.G * a + buf[o + 1] * (1f - a));
        buf[o + 2] = (byte)(c.B * a + buf[o + 2] * (1f - a));
        buf[o + 3] = 255;
    }

    private static void DrawLine(byte[] buf, int w, int h, int x0, int y0, int x1, int y1, Color32 c)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            PlotBlend(buf, w, h, x0, y0, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void DrawFilledRect(byte[] buf, int w, int h, int rx, int ry, int rw, int rh, Color32 c)
    {
        for (int y = ry; y < ry + rh; y++)
        for (int x = rx; x < rx + rw; x++)
            PlotBlend(buf, w, h, x, y, c);
    }

    private static void DrawRectOutline(byte[] buf, int w, int h, int rx, int ry, int rw, int rh, Color32 c)
    {
        DrawLine(buf, w, h, rx, ry, rx + rw, ry, c);
        DrawLine(buf, w, h, rx + rw, ry, rx + rw, ry + rh, c);
        DrawLine(buf, w, h, rx + rw, ry + rh, rx, ry + rh, c);
        DrawLine(buf, w, h, rx, ry + rh, rx, ry, c);
    }

    private static void DrawText(byte[] buf, int w, int h, int x, int y, string text, Color32 c)
    {
        int cx = x;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = char.ToUpperInvariant(text[i]);
            ulong glyph = GetGlyph5x7(ch);
            for (int row = 0; row < 7; row++)
            {
                int bits = (int)((glyph >> (row * 5)) & 0x1F);
                for (int col = 0; col < 5; col++)
                {
                    if (((bits >> (4 - col)) & 1) != 0)
                    {
                        PlotBlend(buf, w, h, cx + col + 1, y + row + 1, new Color32(0, 0, 0, 170));
                        PlotBlend(buf, w, h, cx + col, y + row, c);
                    }
                }
            }
            cx += 6;
        }
    }

    private static ulong Pack5x7(byte r0, byte r1, byte r2, byte r3, byte r4, byte r5, byte r6)
        => (ulong)r0 | ((ulong)r1 << 5) | ((ulong)r2 << 10) | ((ulong)r3 << 15) | ((ulong)r4 << 20) | ((ulong)r5 << 25) | ((ulong)r6 << 30);

    private static ulong GetGlyph5x7(char c) => c switch
    {
        '0' => Pack5x7(0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E),
        '1' => Pack5x7(0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E),
        '2' => Pack5x7(0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F),
        '3' => Pack5x7(0x1E, 0x01, 0x01, 0x0E, 0x01, 0x01, 0x1E),
        '4' => Pack5x7(0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02),
        '5' => Pack5x7(0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E),
        '6' => Pack5x7(0x0E, 0x10, 0x1E, 0x11, 0x11, 0x11, 0x0E),
        '7' => Pack5x7(0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08),
        '8' => Pack5x7(0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E),
        '9' => Pack5x7(0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C),
        'A' => Pack5x7(0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11),
        'B' => Pack5x7(0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E),
        'C' => Pack5x7(0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E),
        'D' => Pack5x7(0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C),
        'E' => Pack5x7(0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F),
        'F' => Pack5x7(0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10),
        'G' => Pack5x7(0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F),
        'H' => Pack5x7(0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11),
        'I' => Pack5x7(0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E),
        'J' => Pack5x7(0x07, 0x02, 0x02, 0x02, 0x12, 0x12, 0x0C),
        'K' => Pack5x7(0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11),
        'L' => Pack5x7(0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F),
        'M' => Pack5x7(0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11),
        'N' => Pack5x7(0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11),
        'O' => Pack5x7(0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E),
        'P' => Pack5x7(0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10),
        'Q' => Pack5x7(0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D),
        'R' => Pack5x7(0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11),
        'S' => Pack5x7(0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E),
        'T' => Pack5x7(0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04),
        'U' => Pack5x7(0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E),
        'V' => Pack5x7(0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04),
        'W' => Pack5x7(0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11),
        'X' => Pack5x7(0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11),
        'Y' => Pack5x7(0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04),
        'Z' => Pack5x7(0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F),
        '-' => Pack5x7(0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00),
        '_' => Pack5x7(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F),
        '.' => Pack5x7(0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C),
        ':' => Pack5x7(0x00, 0x04, 0x04, 0x00, 0x04, 0x04, 0x00),
        '/' => Pack5x7(0x01, 0x02, 0x02, 0x04, 0x08, 0x08, 0x10),
        _ => 0UL,
    };

    private static string SanitizeFileName(string name)
    {
        char[] arr = name.ToCharArray();
        for (int i = 0; i < arr.Length; i++)
            if (!char.IsLetterOrDigit(arr[i]) && arr[i] != '_' && arr[i] != '-') arr[i] = '_';
        return new string(arr);
    }

    public static Color32 ParseBgHex(string hex, Color32 fallback)
    {
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, null, out byte b))
            return new Color32(r, g, b, 255);
        return fallback;
    }
}
