using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Rig2D;

namespace BringRig2D;

/// <summary>
/// Structural, asset, mesh-topology, constraint, animation, and geometric continuity validator for .rig2d.json.
/// </summary>
public static unsafe class RigValidator
{
    public static int Run(string rigPath, string? imagesDir, out int errorCount, out int warningCount)
    {
        errorCount = 0;
        warningCount = 0;
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(rigPath))
        {
            Console.Error.WriteLine($"[BringRig2D] [ERROR] Rig file not found: '{rigPath}'.");
            errorCount = 1;
            return 1;
        }

        string rigDir = Path.GetDirectoryName(Path.GetFullPath(rigPath)) ?? ".";
        string imgBase = !string.IsNullOrEmpty(imagesDir) ? Path.GetFullPath(imagesDir) : rigDir;

        // 1. JSON parse & image asset existence checks
        JsonObject? root = null;
        try
        {
            byte[] raw = File.ReadAllBytes(rigPath);
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) raw = raw[3..];
            root = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            if (root == null) errors.Add("JSON root must be an object.");
        }
        catch (Exception ex)
        {
            errors.Add($"JSON parse failure: {ex.Message}");
        }

        if (root != null)
        {
            ValidateJsonAttachmentsAndImages(root, imgBase, rigDir, errors, warnings);
        }

        // 2. Full Rig2DCompiler + Rig2DLoader dry-run check
        byte[]? brig = null;
        try
        {
            brig = Rig2DCompiler.CompileFromFile(rigPath);
        }
        catch (Exception ex)
        {
            errors.Add($"Compile failed: {ex.Message}");
        }

        if (brig != null)
        {
            var alloc = EngineAllocators.Create(2 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
            var bundleArena = LinearArena.Create((nuint)Math.Max(256 * 1024, brig.Length * 4));
            try
            {
                var rigs = Rig2DTable.Create(ref alloc.Persistent, maxAssets: 8, maxInstances: 8, maxPooledBones: 512, maxPooledSlots: 512, maxPooledSprings: 128, maxPooledOverrides: 64, maxFiredEvents: 64);
                var bundle = BundleId.Create(1, 1);
                Rig2DAssetHandle h = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref rigs, bundle);
                Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in rigs, h);
                if (asset == null)
                {
                    errors.Add("Rig2DLoader returned invalid handle.");
                }
                else
                {
                    ValidateCompiledAsset(asset, errors, warnings);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"BRIG binary validation failed: {ex.Message}");
            }
            finally
            {
                bundleArena.Dispose();
                alloc.Dispose();
            }
        }

        errorCount = errors.Count;
        warningCount = warnings.Count;

        foreach (string w in warnings) Console.WriteLine($"[BringRig2D] [WARN]  {w}");
        foreach (string e in errors) Console.Error.WriteLine($"[BringRig2D] [ERROR] {e}");

        if (errors.Count == 0)
        {
            Console.WriteLine($"[BringRig2D] VALID: '{rigPath}' (0 errors, {warnings.Count} warning(s)).");
            return 0;
        }

        Console.Error.WriteLine($"[BringRig2D] INVALID: '{rigPath}' ({errors.Count} error(s), {warnings.Count} warning(s)).");
        return 1;
    }

    private static void ValidateJsonAttachmentsAndImages(JsonObject root, string imgBase, string rigDir, List<string> errors, List<string> warnings)
    {
        if (root["skins"] is not JsonObject skins) return;
        foreach (var skinKv in skins)
        {
            if (skinKv.Value is not JsonObject slots) continue;
            foreach (var slotKv in slots)
            {
                if (slotKv.Value is not JsonObject atts) continue;
                foreach (var attKv in atts)
                {
                    if (attKv.Value is not JsonObject att) continue;
                    string type = att["type"]?.GetValue<string>() ?? "region";
                    if (type.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;

                    if (type.Equals("sequence", StringComparison.OrdinalIgnoreCase))
                    {
                        if (att["frames"] is not JsonArray fArr || fArr.Count == 0)
                        {
                            errors.Add($"Sequence attachment '{skinKv.Key}/{slotKv.Key}/{attKv.Key}' has no frames.");
                        }
                        else
                        {
                            foreach (var fn in fArr)
                            {
                                string fPath = fn?.GetValue<string>() ?? "";
                                if (string.IsNullOrEmpty(fPath) || AutoMesher.ResolveImagePath(imgBase, rigDir, fPath) == null)
                                    warnings.Add($"Sequence frame image not found on disk: '{fPath}' (slot '{slotKv.Key}').");
                            }
                        }
                    }
                    else
                    {
                        string imgRel = att["image"]?.GetValue<string>() ?? att["path"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrEmpty(imgRel) && AutoMesher.ResolveImagePath(imgBase, rigDir, imgRel) == null)
                            warnings.Add($"Attachment image not found on disk: '{imgRel}' (attachment '{skinKv.Key}/{slotKv.Key}/{attKv.Key}').");

                        if (type.Equals("mesh", StringComparison.OrdinalIgnoreCase) || type.Equals("weightedMesh", StringComparison.OrdinalIgnoreCase))
                        {
                            if (att["vertices"] is not JsonArray vArr || vArr.Count < 3)
                                warnings.Add($"Mesh attachment '{attKv.Key}' has no baked vertices yet (run 'BringRig2D mesh' to auto-triangulate).");
                            else if (att["uvs"] is JsonArray uvArr && uvArr.Count != vArr.Count)
                                errors.Add($"Mesh attachment '{attKv.Key}' UV count ({uvArr.Count}) != vertex count ({vArr.Count}).");
                        }
                    }
                }
            }
        }
    }

    private static void ValidateCompiledAsset(Rig2DAssetData* asset, List<string> errors, List<string> warnings)
    {
        // 1. Bones: NaN/zero-scale & geometric joint continuity
        for (int i = 0; i < asset->BoneCount; i++)
        {
            ref readonly Rig2DBoneSetupData b = ref asset->Bones[i];
            if (!float.IsFinite(b.X) || !float.IsFinite(b.Y) || !float.IsFinite(b.RotationDeg))
                errors.Add($"Bone '{b.NameText}' has non-finite transform.");
            if (MathF.Abs(b.ScaleX) < 1e-4f || MathF.Abs(b.ScaleY) < 1e-4f)
                warnings.Add($"Bone '{b.NameText}' has near-zero setup scale ({b.ScaleX}, {b.ScaleY}).");

            if (b.ParentIndex >= 0)
            {
                ref readonly Rig2DBoneSetupData p = ref asset->Bones[b.ParentIndex];
                if (p.Length > 10f)
                {
                    float distFromParent = MathF.Sqrt(b.X * b.X + b.Y * b.Y);
                    if (distFromParent > p.Length * 2.2f)
                        warnings.Add($"Joint continuity: bone '{b.NameText}' origin ({b.X:F1},{b.Y:F1}) is {distFromParent:F1}px from parent '{p.NameText}' (length={p.Length:F1}px).");
                }
            }
        }

        // 2. Slots: check default attachment exists in default skin
        for (int s = 0; s < asset->SlotCount; s++)
        {
            ref readonly Rig2DSlotSetupData sl = ref asset->Slots[s];
            if (sl.DefaultAttachmentName.IsValid && Rig2DSystem.FindAttachmentIndex(asset, s, sl.DefaultAttachmentName, 0) < 0)
                warnings.Add($"Slot '{sl.NameText}' default attachment not found in default skin.");
        }

        // 3. Mesh topology: degenerate/inverted triangles, UV bounds, unweighted vertices
        for (int a = 0; a < asset->AttachmentCount; a++)
        {
            ref readonly Rig2DAttachmentData att = ref asset->Attachments[a];
            if (att.Kind != Rig2DAttachmentKind.WeightedMesh) continue;

            int unweighted = 0;
            int badUvs = 0;
            for (int v = 0; v < att.VertexCount; v++)
            {
                ref readonly Rig2DMeshVertex mv = ref asset->MeshVertices[att.VertexStart + v];
                if (mv.WeightSum <= 1e-5f) unweighted++;
                if (mv.Uv.X < -0.02f || mv.Uv.X > 1.02f || mv.Uv.Y < -0.02f || mv.Uv.Y > 1.02f) badUvs++;
            }
            if (unweighted > 0)
                warnings.Add($"Mesh '{att.NameText}' has {unweighted}/{att.VertexCount} unweighted vertices (will fall back to rigid slot bone).");
            if (badUvs > 0)
                warnings.Add($"Mesh '{att.NameText}' has {badUvs} vertices with UVs outside [0, 1].");

            int degenTris = 0;
            int invertedTris = 0;
            for (int t = 0; t + 2 < att.IndexCount; t += 3)
            {
                uint i0 = asset->MeshIndices[att.IndexStart + t];
                uint i1 = asset->MeshIndices[att.IndexStart + t + 1];
                uint i2 = asset->MeshIndices[att.IndexStart + t + 2];
                Vec2 p0 = asset->MeshVertices[att.VertexStart + i0].SetupPos;
                Vec2 p1 = asset->MeshVertices[att.VertexStart + i1].SetupPos;
                Vec2 p2 = asset->MeshVertices[att.VertexStart + i2].SetupPos;
                float cross = (p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X);
                if (MathF.Abs(cross) < 1e-3f) degenTris++;
                else if (cross < 0f) invertedTris++;
            }
            if (degenTris > 0) warnings.Add($"Mesh '{att.NameText}' has {degenTris} degenerate (zero-area) triangle(s).");
            if (invertedTris > 0) warnings.Add($"Mesh '{att.NameText}' has {invertedTris} clockwise/inverted triangle(s).");
        }

        // 4. Constraints sanity
        for (int i = 0; i < asset->IkConstraintCount; i++)
        {
            ref readonly Rig2DIkConstraintData ik = ref asset->IkConstraints[i];
            if (ik.BoneB >= 0 && ik.BoneA == ik.BoneB)
                errors.Add($"IK constraint #{i} has BoneA == BoneB.");
            if (ik.BoneA == ik.TargetBone || ik.BoneB == ik.TargetBone)
                errors.Add($"IK constraint #{i} targets one of its own constrained bones.");
        }

        // 5. Animations sanity
        for (int c = 0; c < asset->ClipCount; c++)
        {
            ref readonly Rig2DAnimClipData clip = ref asset->Clips[c];
            if (clip.Duration < 0f || !float.IsFinite(clip.Duration))
                errors.Add($"Animation '{clip.NameText}' has invalid duration {clip.Duration}.");
            for (int t = 0; t < clip.TimelineCount; t++)
            {
                ref readonly Rig2DTimelineData tl = ref asset->Timelines[clip.TimelineStart + t];
                for (int k = 0; k < tl.KeyCount; k++)
                {
                    float kt = asset->Keyframes[tl.KeyStart + k].Time;
                    if (kt < 0f || !float.IsFinite(kt))
                        errors.Add($"Animation '{clip.NameText}' has negative/non-finite keyframe time {kt}.");
                }
            }
        }
    }
}
