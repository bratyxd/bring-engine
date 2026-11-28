using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BringEngine.Core.Math;
using BringEngine.Simulation.Rig2D;
using StbImageSharp;

namespace BringRig2D;

/// <summary>
/// Pure-C# silhouette auto-mesher &amp; bone-weight generator for .rig2d.json:
/// Alpha silhouette dilation -> boundary contour tracing -> RDP simplification -> interior Steiner grid ->
/// deterministic Bowyer-Watson Delaunay triangulation -> smooth inverse-distance bone weights + weightPins +
/// Laplacian adjacency smoothing -> top-4 bone pruning &amp; normalization.
/// </summary>
public static class AutoMesher
{
    private struct BoneInfo
    {
        public string Name;
        public int ParentIdx;
        public float Length;
        public Rig2DAffine2D BindWorld;
    }

    private struct WeightPin
    {
        public string Bone;
        public string Shape; // "circle", "box", "polygon"
        public float X, Y, Radius, Width, Height, Weight;
        public List<Vec2>? Polygon;
    }

    public static int Run(string rigPath, string? imagesDir, float defaultDensity, int defaultMargin, int defaultSmooth, string? outPath)
    {
        if (!File.Exists(rigPath))
        {
            Console.Error.WriteLine($"[BringRig2D] ERROR: Rig file not found: '{rigPath}'.");
            return 1;
        }

        string rigDir = Path.GetDirectoryName(Path.GetFullPath(rigPath)) ?? ".";
        string imgBase = !string.IsNullOrEmpty(imagesDir) ? Path.GetFullPath(imagesDir) : rigDir;

        byte[] jsonBytes = File.ReadAllBytes(rigPath);
        if (jsonBytes.Length >= 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB && jsonBytes[2] == 0xBF)
            jsonBytes = jsonBytes[3..];

        JsonNode? rootNode = JsonNode.Parse(jsonBytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (rootNode is not JsonObject root)
        {
            Console.Error.WriteLine("[BringRig2D] ERROR: Rig JSON root must be an object.");
            return 1;
        }

        // 1. Compute bind-world bone poses
        var boneList = ComputeBindBones(root, out var boneByName);
        var slotBoneByName = ParseSlotBones(root);

        if (root["skins"] is not JsonObject skinsObj)
        {
            Console.WriteLine("[BringRig2D] No skins found to auto-mesh.");
            return 0;
        }

        int meshedCount = 0;
        foreach (var skinKv in skinsObj)
        {
            if (skinKv.Value is not JsonObject skinSlots) continue;
            foreach (var slotKv in skinSlots)
            {
                if (slotKv.Value is not JsonObject attsObj) continue;
                string ownerBoneName = slotBoneByName.TryGetValue(slotKv.Key, out string? ob) ? ob : (boneList.Count > 0 ? boneList[0].Name : "");

                foreach (var attKv in attsObj)
                {
                    if (attKv.Value is not JsonObject att) continue;
                    string type = att["type"]?.GetValue<string>() ?? "region";
                    bool hasAutoMesh = att["autoMesh"] is JsonObject;
                    if (!type.Equals("mesh", StringComparison.OrdinalIgnoreCase) && !type.Equals("weightedMesh", StringComparison.OrdinalIgnoreCase) && !hasAutoMesh)
                        continue;

                    string imgRel = att["image"]?.GetValue<string>() ?? att["path"]?.GetValue<string>() ?? attKv.Key;
                    string? imgPath = ResolveImagePath(imgBase, rigDir, imgRel);
                    if (imgPath == null)
                    {
                        Console.Error.WriteLine($"[BringRig2D] ERROR: Mesh attachment '{attKv.Key}' image not found: '{imgRel}'.");
                        return 1;
                    }

                    JsonObject? am = att["autoMesh"] as JsonObject;
                    float density = am?["density"]?.GetValue<float>() ?? defaultDensity;
                    int margin = am?["outlineMargin"]?.GetValue<int>() ?? defaultMargin;
                    int smoothIters = am?["smoothIterations"]?.GetValue<int>() ?? defaultSmooth;

                    var candidateBones = new List<string>();
                    if (am?["bones"] is JsonArray bArr)
                    {
                        foreach (var bn in bArr)
                        {
                            string? s = bn?.GetValue<string>();
                            if (!string.IsNullOrEmpty(s) && boneByName.ContainsKey(s))
                                candidateBones.Add(s);
                        }
                    }
                    if (candidateBones.Count == 0 && !string.IsNullOrEmpty(ownerBoneName) && boneByName.ContainsKey(ownerBoneName))
                        candidateBones.Add(ownerBoneName);

                    var pins = ParseWeightPins(am?["weightPins"] as JsonArray);

                    float attX = att["x"]?.GetValue<float>() ?? 0f;
                    float attY = att["y"]?.GetValue<float>() ?? 0f;
                    float attRot = att["rotation"]?.GetValue<float>() ?? 0f;
                    float attSx = att["scaleX"]?.GetValue<float>() ?? 1f;
                    float attSy = att["scaleY"]?.GetValue<float>() ?? 1f;

                    byte[] imgRaw = File.ReadAllBytes(imgPath);
                    ImageResult dec = ImageResult.FromMemory(imgRaw, ColorComponents.RedGreenBlueAlpha);
                    float attW = att["width"]?.GetValue<float>() ?? dec.Width;
                    float attH = att["height"]?.GetValue<float>() ?? dec.Height;

                    Rig2DAffine2D ownerBind = boneByName.TryGetValue(ownerBoneName, out int obIdx)
                        ? boneList[obIdx].BindWorld
                        : Rig2DAffine2D.Identity;
                    Rig2DAffine2D attLocal = Rig2DAffine2D.FromTrsShear(attX, attY, attRot, attSx, attSy);
                    Rig2DAffine2D attToBind = Rig2DAffine2D.Multiply(in ownerBind, in attLocal);

                    GenerateMeshFromAlpha(
                        dec.Data, dec.Width, dec.Height,
                        attW, attH, in attToBind,
                        density, margin, smoothIters,
                        boneList, boneByName, candidateBones, pins,
                        out var verts, out var uvs, out var tris, out var weights);

                    att["type"] = "mesh";
                    att["width"] = MathF.Round(attW, 2);
                    att["height"] = MathF.Round(attH, 2);

                    var vJson = new JsonArray();
                    for (int i = 0; i < verts.Count; i++)
                        vJson.Add(new JsonArray(MathF.Round(verts[i].X, 2), MathF.Round(verts[i].Y, 2)));
                    att["vertices"] = vJson;

                    var uvJson = new JsonArray();
                    for (int i = 0; i < uvs.Count; i++)
                        uvJson.Add(new JsonArray(MathF.Round(uvs[i].X, 4), MathF.Round(uvs[i].Y, 4)));
                    att["uvs"] = uvJson;

                    var tJson = new JsonArray();
                    for (int i = 0; i < tris.Count; i++)
                        tJson.Add(tris[i]);
                    att["triangles"] = tJson;

                    var wJson = new JsonArray();
                    for (int i = 0; i < weights.Count; i++)
                    {
                        var vw = new JsonArray();
                        foreach (var (bn, wt) in weights[i])
                            vw.Add(new JsonArray(bn, MathF.Round(wt, 4)));
                        wJson.Add(vw);
                    }
                    att["weights"] = wJson;

                    meshedCount++;
                    Console.WriteLine($"[BringRig2D] mesh '{attKv.Key}': {verts.Count} verts, {tris.Count / 3} tris, {candidateBones.Count} bone(s) (density={density}px, margin={margin}px)");
                }
            }
        }

        string savePath = !string.IsNullOrEmpty(outPath) ? outPath : rigPath;
        var writeOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(savePath, root.ToJsonString(writeOpts), new UTF8Encoding(false));
        Console.WriteLine($"[BringRig2D] Auto-meshed {meshedCount} attachment(s) -> '{savePath}'.");
        return 0;
    }

    private static void GenerateMeshFromAlpha(
        byte[] rgba, int w, int h,
        float attW, float attH,
        in Rig2DAffine2D attToBind,
        float density, int margin, int smoothIters,
        List<BoneInfo> bones,
        Dictionary<string, int> boneByName,
        List<string> candidateBones,
        List<WeightPin> pins,
        out List<Vec2> outVerts,
        out List<Vec2> outUvs,
        out List<int> outTris,
        out List<List<(string Bone, float Weight)>> outWeights)
    {
        float step = MathF.Max(8f, density);
        bool[] mask = BuildDilatedAlphaMask(rgba, w, h, Math.Clamp(margin, 0, 12));
        bool[] contourMask = FilterConnectedComponents(mask, w, h);

        // 1. Extract outer boundary contour points (pixel space) from largest silhouette component and simplify with RDP
        var pointsPx = new List<Vec2>();
        var contour = ExtractOuterContour(contourMask, w, h);
        if (contour.Count >= 3)
        {
            float eps = MathF.Max(1.8f, step * 0.14f);
            var simplified = SimplifyClosedContour(contour, eps);
            // Subdivide overly long contour edges to keep triangle aspect ratios crisp
            for (int i = 0; i < simplified.Count; i++)
            {
                Vec2 a = simplified[i];
                Vec2 b = simplified[(i + 1) % simplified.Count];
                pointsPx.Add(a);
                float dist = (b - a).Length();
                int subs = (int)MathF.Floor(dist / (step * 1.15f));
                for (int s = 1; s <= subs; s++)
                    pointsPx.Add(Vec2.Lerp(a, b, (float)s / (subs + 1)));
            }
        }

        // Fallback to padded bounding rectangle if silhouette is empty or degenerate
        if (pointsPx.Count < 3)
        {
            pointsPx.Clear();
            pointsPx.Add(new Vec2(0, 0));
            pointsPx.Add(new Vec2(w - 1, 0));
            pointsPx.Add(new Vec2(w - 1, h - 1));
            pointsPx.Add(new Vec2(0, h - 1));
        }

        // 2. Seed interior staggered grid Steiner points inside dilated alpha mask
        float minBoundaryDistSq = (step * 0.42f) * (step * 0.42f);
        int boundaryCount = pointsPx.Count;
        int rowIdx = 0;
        for (float y = step * 0.5f; y < h - step * 0.25f; y += step * 0.866f)
        {
            float xOff = (rowIdx & 1) != 0 ? step * 0.5f : 0f;
            for (float x = step * 0.5f + xOff; x < w - step * 0.25f; x += step)
            {
                int ix = Math.Clamp((int)MathF.Round(x), 0, w - 1);
                int iy = Math.Clamp((int)MathF.Round(y), 0, h - 1);
                if (!mask[iy * w + ix]) continue;

                bool tooClose = false;
                for (int bi = 0; bi < boundaryCount; bi++)
                {
                    float dx = pointsPx[bi].X - x;
                    float dy = pointsPx[bi].Y - y;
                    if (dx * dx + dy * dy < minBoundaryDistSq) { tooClose = true; break; }
                }
                if (!tooClose)
                    pointsPx.Add(new Vec2(x, y));
            }
            rowIdx++;
        }

        // Deduplicate near-identical points
        var cleanPts = new List<Vec2>(pointsPx.Count);
        for (int i = 0; i < pointsPx.Count; i++)
        {
            Vec2 p = new Vec2(Math.Clamp(pointsPx[i].X, 0f, w), Math.Clamp(pointsPx[i].Y, 0f, h));
            bool dup = false;
            for (int j = 0; j < cleanPts.Count; j++)
            {
                float dx = cleanPts[j].X - p.X;
                float dy = cleanPts[j].Y - p.Y;
                if (dx * dx + dy * dy < 1.5f) { dup = true; break; }
            }
            if (!dup) cleanPts.Add(p);
        }

        // 3. Deterministic Bowyer-Watson Delaunay Triangulation
        var rawTris = DelaunayTriangulate(cleanPts);

        // 4. Filter triangles whose centroid falls inside dilated silhouette mask & ensure CCW in skeleton space
        var keptTris = new List<(int A, int B, int C)>(rawTris.Count);
        for (int i = 0; i < rawTris.Count; i++)
        {
            var (a, b, c) = rawTris[i];
            Vec2 pa = cleanPts[a], pb = cleanPts[b], pc = cleanPts[c];
            float cx = (pa.X + pb.X + pc.X) / 3f;
            float cy = (pa.Y + pb.Y + pc.Y) / 3f;
            int ix = Math.Clamp((int)MathF.Round(cx), 0, w - 1);
            int iy = Math.Clamp((int)MathF.Round(cy), 0, h - 1);
            if (!mask[iy * w + ix]) continue;

            // Check non-degenerate area
            float area2 = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            if (MathF.Abs(area2) < 1e-2f) continue;

            // Note: Y flips from image top-down to skeleton Y-up, so CW in image space == CCW in skeleton space!
            if (area2 > 0f)
                keptTris.Add((a, c, b));
            else
                keptTris.Add((a, b, c));
        }

        if (keptTris.Count == 0 && rawTris.Count > 0)
            keptTris.AddRange(rawTris);

        // Compact only referenced vertices
        var remap = new int[cleanPts.Count];
        Array.Fill(remap, -1);
        outVerts = new List<Vec2>();
        outUvs = new List<Vec2>();
        outTris = new List<int>(keptTris.Count * 3);

        for (int i = 0; i < keptTris.Count; i++)
        {
            int[] idx = { keptTris[i].A, keptTris[i].B, keptTris[i].C };
            for (int k = 0; k < 3; k++)
            {
                int orig = idx[k];
                if (remap[orig] < 0)
                {
                    remap[orig] = outVerts.Count;
                    float u = Math.Clamp(cleanPts[orig].X / w, 0f, 1f);
                    float v = Math.Clamp(cleanPts[orig].Y / h, 0f, 1f);
                    float lx = (u - 0.5f) * attW;
                    float ly = (0.5f - v) * attH;
                    Vec2 bindP = Rig2DAffine2D.TransformPoint(in attToBind, new Vec2(lx, ly));
                    outVerts.Add(bindP);
                    outUvs.Add(new Vec2(u, v));
                }
                outTris.Add(remap[orig]);
            }
        }

        // 5. Compute bone weights (inverse distance to bone segments + weightPins + Laplacian smoothing)
        int vc = outVerts.Count;
        int nb = candidateBones.Count;
        outWeights = new List<List<(string, float)>>(vc);
        if (nb == 0)
        {
            for (int v = 0; v < vc; v++) outWeights.Add(new List<(string, float)>());
            return;
        }

        var wMat = new float[vc, nb];
        var segStart = new Vec2[nb];
        var segEnd = new Vec2[nb];
        for (int b = 0; b < nb; b++)
        {
            BoneInfo bi = bones[boneByName[candidateBones[b]]];
            segStart[b] = new Vec2(bi.BindWorld.Tx, bi.BindWorld.Ty);
            float len = bi.Length > 1f ? bi.Length : 1f;
            segEnd[b] = Rig2DAffine2D.TransformPoint(in bi.BindWorld, new Vec2(len, 0f));
        }

        float falloffEps = MathF.Max(16f, step * step * 0.5f);
        for (int v = 0; v < vc; v++)
        {
            Vec2 p = outVerts[v];
            float sum = 0f;
            for (int b = 0; b < nb; b++)
            {
                float dSq = PointToSegmentDistSq(p, segStart[b], segEnd[b]);
                float wt = 1f / (dSq + falloffEps);
                wMat[v, b] = wt;
                sum += wt;
            }
            if (sum > 1e-8f)
                for (int b = 0; b < nb; b++) wMat[v, b] /= sum;

            // Apply explicit weightPins
            for (int pi = 0; pi < pins.Count; pi++)
            {
                var pin = pins[pi];
                int targetCol = candidateBones.IndexOf(pin.Bone);
                if (targetCol < 0) continue;
                if (!PointInPin(p, in pin)) continue;

                float pw = Math.Clamp(pin.Weight, 0f, 1f);
                for (int b = 0; b < nb; b++)
                    wMat[v, b] = b == targetCol ? DetMath.Lerp(wMat[v, b], 1f, pw) : wMat[v, b] * (1f - pw);
            }
        }

        // Build vertex adjacency for Laplacian smoothing
        var adj = new List<int>[vc];
        for (int v = 0; v < vc; v++) adj[v] = new List<int>(6);
        for (int t = 0; t + 2 < outTris.Count; t += 3)
        {
            int a = outTris[t], b = outTris[t + 1], c = outTris[t + 2];
            AddUnique(adj[a], b); AddUnique(adj[a], c);
            AddUnique(adj[b], a); AddUnique(adj[b], c);
            AddUnique(adj[c], a); AddUnique(adj[c], b);
        }

        int iters = Math.Clamp(smoothIters, 0, 16);
        var nextMat = new float[vc, nb];
        for (int it = 0; it < iters; it++)
        {
            for (int v = 0; v < vc; v++)
            {
                var nbrs = adj[v];
                if (nbrs.Count == 0)
                {
                    for (int b = 0; b < nb; b++) nextMat[v, b] = wMat[v, b];
                    continue;
                }
                float invN = 1f / nbrs.Count;
                for (int b = 0; b < nb; b++)
                {
                    float avg = 0f;
                    for (int k = 0; k < nbrs.Count; k++) avg += wMat[nbrs[k], b];
                    nextMat[v, b] = wMat[v, b] * 0.5f + (avg * invN) * 0.5f;
                }
            }
            var tmp = wMat; wMat = nextMat; nextMat = tmp;
        }

        // Prune to top 4 bones per vertex & normalize
        for (int v = 0; v < vc; v++)
        {
            var list = new List<(string Bone, float Weight)>(nb);
            for (int b = 0; b < nb; b++)
                if (wMat[v, b] > 0.005f) list.Add((candidateBones[b], wMat[v, b]));
            list.Sort((x, y) => y.Weight.CompareTo(x.Weight));
            if (list.Count > 4) list.RemoveRange(4, list.Count - 4);
            float s = 0f;
            for (int k = 0; k < list.Count; k++) s += list[k].Weight;
            if (s > 1e-6f)
            {
                for (int k = 0; k < list.Count; k++)
                    list[k] = (list[k].Bone, list[k].Weight / s);
            }
            else if (nb > 0)
            {
                list.Clear();
                list.Add((candidateBones[0], 1f));
            }
            outWeights.Add(list);
        }
    }

    private static bool[] BuildDilatedAlphaMask(byte[] rgba, int w, int h, int radius)
    {
        bool[] raw = new bool[w * h];
        for (int i = 0; i < w * h; i++) raw[i] = rgba[i * 4 + 3] > 16;
        if (radius <= 0) return raw;

        bool[] dst = new bool[w * h];
        int r2 = radius * radius;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (raw[y * w + x]) { dst[y * w + x] = true; continue; }
                bool hit = false;
                for (int dy = -radius; dy <= radius && !hit; dy++)
                {
                    int ny = y + dy;
                    if ((uint)ny >= (uint)h) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dy * dy > r2) continue;
                        int nx = x + dx;
                        if ((uint)nx >= (uint)w) continue;
                        if (raw[ny * w + nx]) { hit = true; break; }
                    }
                }
                dst[y * w + x] = hit;
            }
        }
        return dst;
    }

    private static bool[] FilterConnectedComponents(bool[] mask, int w, int h)
    {
        int n = w * h;
        int[] labels = new int[n];
        var compAreas = new List<int> { 0 }; // 1-based component labels
        var queue = new Queue<int>();
        int totalOpaque = 0;

        for (int i = 0; i < n; i++)
        {
            if (!mask[i] || labels[i] != 0) continue;
            int label = compAreas.Count;
            int area = 0;
            labels[i] = label;
            queue.Enqueue(i);

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                area++;
                int cx = cur % w;
                int cy = cur / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if ((uint)ny >= (uint)h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx;
                        if ((uint)nx >= (uint)w) continue;
                        int nIdx = ny * w + nx;
                        if (mask[nIdx] && labels[nIdx] == 0)
                        {
                            labels[nIdx] = label;
                            queue.Enqueue(nIdx);
                        }
                    }
                }
            }

            compAreas.Add(area);
            totalOpaque += area;
        }

        if (compAreas.Count <= 1) return mask;

        int largestLabel = 1;
        int largestArea = compAreas[1];
        for (int l = 2; l < compAreas.Count; l++)
        {
            if (compAreas[l] > largestArea)
            {
                largestArea = compAreas[l];
                largestLabel = l;
            }
        }

        // Remove tiny stray speckles (< 1% of opaque area) from mask, and trace only the largest component contour
        int minKeepArea = Math.Max(4, totalOpaque / 100);
        bool[] largestMask = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int l = labels[i];
            if (l == 0) continue;
            if (l == largestLabel)
                largestMask[i] = true;
            else if (compAreas[l] < minKeepArea)
                mask[i] = false;
        }
        return largestMask;
    }

    private static List<Vec2> ExtractOuterContour(bool[] mask, int w, int h)
    {
        // Moore-Neighbor boundary tracing starting at topmost-leftmost opaque pixel
        int startX = -1, startY = -1;
        for (int y = 0; y < h && startX < 0; y++)
        for (int x = 0; x < w; x++)
            if (mask[y * w + x]) { startX = x; startY = y; break; }

        var pts = new List<Vec2>();
        if (startX < 0) return pts;

        // 8-neighbor clockwise offsets starting from West (-1, 0)
        int[] dx = { -1, -1, 0, 1, 1, 1, 0, -1 };
        int[] dy = { 0, -1, -1, -1, 0, 1, 1, 1 };

        int cx = startX, cy = startY;
        int backDir = 0; // came from West (empty)
        int maxSteps = w * h * 4;
        pts.Add(new Vec2(cx + 0.5f, cy + 0.5f));

        for (int step = 0; step < maxSteps; step++)
        {
            int foundDir = -1;
            for (int k = 1; k <= 8; k++)
            {
                int dir = (backDir + k) & 7;
                int nx = cx + dx[dir];
                int ny = cy + dy[dir];
                if ((uint)nx < (uint)w && (uint)ny < (uint)h && mask[ny * w + nx])
                {
                    foundDir = dir;
                    break;
                }
            }
            if (foundDir < 0) break;

            cx += dx[foundDir];
            cy += dy[foundDir];
            backDir = (foundDir + 4) & 7;
            if (cx == startX && cy == startY && pts.Count > 2) break;
            pts.Add(new Vec2(cx + 0.5f, cy + 0.5f));
        }

        return pts;
    }

    private static List<Vec2> SimplifyClosedContour(List<Vec2> pts, float eps)
    {
        if (pts.Count <= 4) return new List<Vec2>(pts);

        // Split closed ring at farthest pair so RDP preserves overall silhouette cleanly
        int idxA = 0, idxB = pts.Count / 2;
        float maxD = -1f;
        for (int i = 1; i < pts.Count; i++)
        {
            float d = (pts[i] - pts[0]).Length();
            if (d > maxD) { maxD = d; idxB = i; }
        }

        var half1 = pts.GetRange(idxA, idxB - idxA + 1);
        var half2 = new List<Vec2>(pts.Count - idxB + 1);
        for (int i = idxB; i < pts.Count; i++) half2.Add(pts[i]);
        half2.Add(pts[idxA]);

        var r1 = Rdp(half1, eps);
        var r2 = Rdp(half2, eps);
        var combined = new List<Vec2>(r1.Count + r2.Count);
        for (int i = 0; i < r1.Count - 1; i++) combined.Add(r1[i]);
        for (int i = 0; i < r2.Count - 1; i++) combined.Add(r2[i]);
        return combined;
    }

    private static List<Vec2> Rdp(List<Vec2> pts, float eps)
    {
        if (pts.Count <= 2) return new List<Vec2>(pts);
        float maxDistSq = -1f;
        int splitIdx = 0;
        Vec2 a = pts[0], b = pts[pts.Count - 1];
        for (int i = 1; i < pts.Count - 1; i++)
        {
            float dSq = PointToSegmentDistSq(pts[i], a, b);
            if (dSq > maxDistSq) { maxDistSq = dSq; splitIdx = i; }
        }
        if (maxDistSq > eps * eps)
        {
            var left = Rdp(pts.GetRange(0, splitIdx + 1), eps);
            var right = Rdp(pts.GetRange(splitIdx, pts.Count - splitIdx), eps);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }
        return new List<Vec2> { a, b };
    }

    private static List<(int A, int B, int C)> DelaunayTriangulate(List<Vec2> pts)
    {
        int n = pts.Count;
        if (n < 3) return new List<(int, int, int)>();

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (pts[i].X < minX) minX = pts[i].X;
            if (pts[i].Y < minY) minY = pts[i].Y;
            if (pts[i].X > maxX) maxX = pts[i].X;
            if (pts[i].Y > maxY) maxY = pts[i].Y;
        }
        float dx = maxX - minX, dy = maxY - minY;
        float dMax = MathF.Max(dx, dy) * 4f + 100f;
        float midX = (minX + maxX) * 0.5f, midY = (minY + maxY) * 0.5f;

        var allPts = new List<Vec2>(n + 3);
        allPts.AddRange(pts);
        int s0 = n, s1 = n + 1, s2 = n + 2;
        allPts.Add(new Vec2(midX - 2f * dMax, midY - dMax));
        allPts.Add(new Vec2(midX, midY + 2f * dMax));
        allPts.Add(new Vec2(midX + 2f * dMax, midY - dMax));

        var tris = new List<(int A, int B, int C)> { (s0, s1, s2) };
        var bad = new List<(int A, int B, int C)>();
        var edges = new List<(int U, int V)>();

        for (int pi = 0; pi < n; pi++)
        {
            Vec2 p = allPts[pi];
            bad.Clear();
            for (int ti = tris.Count - 1; ti >= 0; ti--)
            {
                var t = tris[ti];
                if (InCircumcircle(allPts[t.A], allPts[t.B], allPts[t.C], p))
                {
                    bad.Add(t);
                    tris.RemoveAt(ti);
                }
            }

            edges.Clear();
            for (int bi = 0; bi < bad.Count; bi++)
            {
                var t = bad[bi];
                ToggleBoundaryEdge(edges, t.A, t.B);
                ToggleBoundaryEdge(edges, t.B, t.C);
                ToggleBoundaryEdge(edges, t.C, t.A);
            }

            for (int ei = 0; ei < edges.Count; ei++)
                tris.Add((edges[ei].U, edges[ei].V, pi));
        }

        tris.RemoveAll(t => t.A >= n || t.B >= n || t.C >= n);
        return tris;
    }

    private static void ToggleBoundaryEdge(List<(int U, int V)> edges, int u, int v)
    {
        for (int i = 0; i < edges.Count; i++)
        {
            if ((edges[i].U == u && edges[i].V == v) || (edges[i].U == v && edges[i].V == u))
            {
                edges.RemoveAt(i);
                return;
            }
        }
        edges.Add((u, v));
    }

    private static bool InCircumcircle(Vec2 a, Vec2 b, Vec2 c, Vec2 p)
    {
        double ax = a.X - p.X, ay = a.Y - p.Y;
        double bx = b.X - p.X, by = b.Y - p.Y;
        double cx = c.X - p.X, cy = c.Y - p.Y;

        double orient = (b.X - a.X) * (double)(c.Y - a.Y) - (b.Y - a.Y) * (double)(c.X - a.X);
        double det = (ax * ax + ay * ay) * (bx * cy - cx * by)
                   - (bx * bx + by * by) * (ax * cy - cx * ay)
                   + (cx * cx + cy * cy) * (ax * by - bx * ay);
        return orient >= 0.0 ? det > 1e-6 : det < -1e-6;
    }

    private static float PointToSegmentDistSq(Vec2 p, Vec2 a, Vec2 b)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y;
        float lenSq = abx * abx + aby * aby;
        if (lenSq < 1e-6f)
        {
            float dx = p.X - a.X, dy = p.Y - a.Y;
            return dx * dx + dy * dy;
        }
        float t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq, 0f, 1f);
        float px = a.X + abx * t - p.X;
        float py = a.Y + aby * t - p.Y;
        return px * px + py * py;
    }

    private static bool PointInPin(Vec2 p, in WeightPin pin)
    {
        switch (pin.Shape.ToLowerInvariant())
        {
            case "circle":
            {
                float dx = p.X - pin.X, dy = p.Y - pin.Y;
                return dx * dx + dy * dy <= pin.Radius * pin.Radius;
            }
            case "box":
            {
                float hw = pin.Width * 0.5f, hh = pin.Height * 0.5f;
                return p.X >= pin.X - hw && p.X <= pin.X + hw && p.Y >= pin.Y - hh && p.Y <= pin.Y + hh;
            }
            case "polygon":
                if (pin.Polygon == null || pin.Polygon.Count < 3) return false;
                bool inside = false;
                for (int i = 0, j = pin.Polygon.Count - 1; i < pin.Polygon.Count; j = i++)
                {
                    Vec2 pi = pin.Polygon[i], pj = pin.Polygon[j];
                    if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                        (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-7f) + pi.X))
                        inside = !inside;
                }
                return inside;
            default:
                return false;
        }
    }

    private static void AddUnique(List<int> list, int val)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == val) return;
        list.Add(val);
    }

    private static List<BoneInfo> ComputeBindBones(JsonObject root, out Dictionary<string, int> byName)
    {
        byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var list = new List<BoneInfo>();
        if (root["bones"] is not JsonArray arr) return list;

        var raw = new List<(string Name, string Parent, float X, float Y, float Rot, float Sx, float Sy, float Shx, float Shy, float Len, Rig2DInheritMode Inherit)>();
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            raw.Add((
                o["name"]?.GetValue<string>() ?? "",
                o["parent"]?.GetValue<string>() ?? "",
                o["x"]?.GetValue<float>() ?? 0f,
                o["y"]?.GetValue<float>() ?? 0f,
                o["rotation"]?.GetValue<float>() ?? 0f,
                o["scaleX"]?.GetValue<float>() ?? 1f,
                o["scaleY"]?.GetValue<float>() ?? 1f,
                o["shearX"]?.GetValue<float>() ?? 0f,
                o["shearY"]?.GetValue<float>() ?? 0f,
                o["length"]?.GetValue<float>() ?? 0f,
                Rig2DCompiler.ParseInheritMode(o["inherit"]?.GetValue<string>())));
        }

        // Resolve iteratively so parents always precede children, honoring all 4 Rig2DInheritModes
        var resolved = new bool[raw.Count];
        for (int pass = 0; pass < raw.Count; pass++)
        {
            bool progress = false;
            for (int i = 0; i < raw.Count; i++)
            {
                if (resolved[i]) continue;
                var b = raw[i];
                int pIdx = -1;
                if (!string.IsNullOrEmpty(b.Parent))
                {
                    if (!byName.TryGetValue(b.Parent, out pIdx)) continue;
                }
                Rig2DAffine2D bw;
                if (pIdx >= 0)
                {
                    Rig2DAffine2D parentBind = list[pIdx].BindWorld;
                    bw = Rig2DSystem.ComputeBoneWorldFromParent(in parentBind, b.Inherit, b.X, b.Y, b.Rot, b.Sx, b.Sy, b.Shx, b.Shy);
                }
                else
                {
                    bw = Rig2DAffine2D.FromTrsShear(b.X, b.Y, b.Rot, b.Sx, b.Sy, b.Shx, b.Shy);
                }
                byName[b.Name] = list.Count;
                list.Add(new BoneInfo { Name = b.Name, ParentIdx = pIdx, Length = b.Len, BindWorld = bw });
                resolved[i] = true;
                progress = true;
            }
            if (!progress) break;
        }
        return list;
    }

    private static Dictionary<string, string> ParseSlotBones(JsonObject root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["slots"] is not JsonArray arr) return map;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            string? name = o["name"]?.GetValue<string>();
            string? bone = o["bone"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(bone))
                map[name] = bone;
        }
        return map;
    }

    private static List<WeightPin> ParseWeightPins(JsonArray? arr)
    {
        var list = new List<WeightPin>();
        if (arr == null) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var pin = new WeightPin
            {
                Bone = o["bone"]?.GetValue<string>() ?? "",
                Shape = o["shape"]?.GetValue<string>() ?? "circle",
                X = o["x"]?.GetValue<float>() ?? 0f,
                Y = o["y"]?.GetValue<float>() ?? 0f,
                Radius = o["radius"]?.GetValue<float>() ?? 32f,
                Width = o["width"]?.GetValue<float>() ?? 64f,
                Height = o["height"]?.GetValue<float>() ?? 64f,
                Weight = o["weight"]?.GetValue<float>() ?? 1f,
            };
            if (o["points"] is JsonArray ptsArr)
            {
                pin.Polygon = new List<Vec2>();
                foreach (var p in ptsArr)
                {
                    if (p is JsonArray pair && pair.Count >= 2)
                        pin.Polygon.Add(new Vec2(pair[0]!.GetValue<float>(), pair[1]!.GetValue<float>()));
                }
            }
            list.Add(pin);
        }
        return list;
    }

    public static string? ResolveImagePath(string imgBase, string rigDir, string rel)
    {
        string norm = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string[] bases = { imgBase, rigDir };
        string[] exts = { "", ".png", ".jpg", ".jpeg", ".bmp", ".tga" };
        foreach (string b in bases)
        {
            foreach (string ext in exts)
            {
                string cand = Path.Combine(b, norm + ext);
                if (File.Exists(cand)) return cand;
            }
        }
        return null;
    }
}
