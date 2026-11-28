using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Simulation.Rig2D;

/// <summary>
/// Binary BRIG header layout (Version 1, Pack = 1).
/// Shared between Rig2DCompiler (writer) and Rig2DLoader (zero-GC runtime loader).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BrigHeader
{
    public const uint MagicValue = 0x47495242u; // "BRIG"
    public const ushort CurrentVersion = 1;

    public uint Magic;
    public ushort Version;
    public ushort Flags;
    public ulong NameHash;
    public FixedString64 NameText;
    public float PixelsPerUnit;
    public Vec2 OriginOffset;
    public Vec2 BoundsSize;

    public int BoneCount;
    public int SlotCount;
    public int AttachmentCount;
    public int MeshVertexCount;
    public int MeshIndexCount;
    public int SequenceFrameNameCount;
    public int SkinCount;
    public int SkinEntryCount;
    public int IkConstraintCount;
    public int TransformConstraintCount;
    public int SpringConstraintCount;
    public int ConstraintOrderCount;
    public int ClipCount;
    public int TimelineCount;
    public int KeyframeCount;
    public int EventKeyCount;
    public int DeformPoolCount;
    public int DrawOrderPoolCount;
}

/// <summary>
/// Declarative .rig2d.json -> compact binary BRIG compiler (zero reflection, Utf8JsonReader).
/// Topologically sorts bones (rejecting cycles), resolves atlas sprite region StringIds,
/// expands named easing curves, precomputes BoneInvBind, and serializes flat unmanaged binary data.
/// </summary>
public static unsafe class Rig2DCompiler
{
    private struct ParsedBone
    {
        public string Name;
        public string Parent;
        public float X, Y, RotDeg, ScaleX, ScaleY, ShearX, ShearY, Length;
        public Rig2DInheritMode Inherit;
    }

    private struct ParsedSlot
    {
        public string Name;
        public string Bone;
        public string DefaultAttachment;
        public ColorF LightColor;
        public ColorF DarkColor;
        public Rig2DBlendMode Blend;
    }

    private struct ParsedAttachment
    {
        public string SkinName;
        public string SlotName;
        public string Name;
        public Rig2DAttachmentKind Kind;
        public string ImagePath;
        public float X, Y, RotDeg, ScaleX, ScaleY, Width, Height;
        public List<Vec2> Vertices;
        public List<Vec2> Uvs;
        public List<uint> Triangles;
        public List<(string BoneName, float Weight)[]> Weights;
        public List<string> SeqFrames;
        public float SeqFps;
        public Rig2DSequenceLoop SeqLoop;
        public int SeqStartIndex;
    }

    private struct ParsedIk
    {
        public string Name;
        public int Order;
        public string BoneA;
        public string BoneB;
        public string TargetBone;
        public float Mix;
        public float Softness;
        public sbyte BendDirection;
        public byte Stretch;
        public byte Compress;
    }

    private struct ParsedTransformConstraint
    {
        public string Name;
        public int Order;
        public string Bone;
        public string TargetBone;
        public float MixRotate, MixX, MixY, MixScaleX, MixScaleY, MixShearY;
        public float OffsetRotateDeg, OffsetX, OffsetY, OffsetScaleX, OffsetScaleY, OffsetShearY;
        public byte Local, Relative;
    }

    private struct ParsedSpring
    {
        public string Name;
        public int Order;
        public string Bone;
        public float Inertia, Strength, Damping, Mass, Gravity, Wind, MaxAngleDeg, Mix;
        public float PosScaleX, PosScaleY, RotateScale;
    }

    private struct ParsedEventDef
    {
        public int IntVal;
        public float FloatVal;
        public string StringVal;
    }

    private struct ParsedTimeline
    {
        public Rig2DTimelineTarget Target;
        public string TargetName; // Bone, slot, constraint, or "skin|slot|attachment"
        public string ExtraSlot;
        public string ExtraSkin;
        public List<ParsedKey> Keys;
    }

    private struct ParsedKey
    {
        public float Time;
        public float V0, V1, V2, V3;
        public Rig2DCurveKind Curve;
        public float Cx1, Cy1, Cx2, Cy2;
        public int IntVal;
        public string StringVal;
        public List<Vec2>? DeformVerts;
        public List<string>? DrawOrderExplicit;
        public List<(string Slot, int Offset)>? DrawOrderOffsets;
    }

    private struct ParsedEventKey
    {
        public float Time;
        public string Name;
        public bool HasInt, HasFloat, HasString;
        public int IntVal;
        public float FloatVal;
        public string StringVal;
    }

    private struct ParsedAnim
    {
        public string Name;
        public float ExplicitDuration;
        public List<ParsedTimeline> Timelines;
        public List<ParsedEventKey> Events;
    }

    public static byte[] CompileFromFile(string rigJsonPath)
    {
        if (!File.Exists(rigJsonPath))
            throw new FileNotFoundException($"Rig2D JSON file not found: '{rigJsonPath}'.", rigJsonPath);
        byte[] raw = File.ReadAllBytes(rigJsonPath);
        string fallbackName = StripRig2DExtension(Path.GetFileName(rigJsonPath));
        return CompileFromJsonBytes(raw, fallbackName);
    }

    public static byte[] CompileFromJsonBytes(ReadOnlySpan<byte> jsonBytes, string fallbackName = "rig2d")
    {
        if (jsonBytes.Length >= 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB && jsonBytes[2] == 0xBF)
            jsonBytes = jsonBytes.Slice(3);

        var opts = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var r = new Utf8JsonReader(jsonBytes, isFinalBlock: true, state: new JsonReaderState(opts));

        if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("Rig2D JSON root must be an object.");

        string rigName = fallbackName;
        float ppu = 100f;
        Vec2 origin = Vec2.Zero;
        Vec2 boundsSize = new Vec2(512f, 512f);

        var rawBones = new List<ParsedBone>();
        var rawSlots = new List<ParsedSlot>();
        var rawSkinOrder = new List<string>();
        var rawAttachments = new List<ParsedAttachment>();
        var rawIks = new List<ParsedIk>();
        var rawTransforms = new List<ParsedTransformConstraint>();
        var rawSprings = new List<ParsedSpring>();
        var eventDefs = new Dictionary<string, ParsedEventDef>(StringComparer.Ordinal);
        var rawAnims = new List<ParsedAnim>();

        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string prop = r.GetString() ?? "";
            if (!r.Read()) throw new InvalidDataException($"Unexpected EOF after property '{prop}'.");

            switch (prop)
            {
                case "name":
                    if (r.TokenType == JsonTokenType.String)
                    {
                        string? s = r.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) rigName = s;
                    }
                    else r.Skip();
                    break;
                case "pixelsPerUnit":
                    ppu = ReadFloat(ref r, 100f);
                    break;
                case "origin":
                    origin = ReadVec2(ref r, Vec2.Zero);
                    break;
                case "size":
                    boundsSize = ReadVec2(ref r, new Vec2(512f, 512f));
                    break;
                case "bones":
                    ParseBones(ref r, rawBones);
                    break;
                case "slots":
                    ParseSlots(ref r, rawSlots);
                    break;
                case "skins":
                    ParseSkins(ref r, rawSkinOrder, rawAttachments);
                    break;
                case "constraints":
                    ParseConstraints(ref r, rawIks, rawTransforms, rawSprings);
                    break;
                case "events":
                    ParseEventDefs(ref r, eventDefs);
                    break;
                case "animations":
                    ParseAnimations(ref r, rawAnims);
                    break;
                default:
                    r.Skip();
                    break;
            }
        }

        if (rawBones.Count == 0)
            throw new InvalidDataException($"Rig2D '{rigName}' must define at least 1 bone.");
        if (rawBones.Count > Rig2DAssetData.MaxBonesPerRig)
            throw new InvalidDataException($"Rig2D '{rigName}' has {rawBones.Count} bones (max {Rig2DAssetData.MaxBonesPerRig}).");

        // 1. Topologically sort bones (parent < child) and reject cycles/missing parents
        var boneIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var origIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rawBones.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(rawBones[i].Name))
                throw new InvalidDataException($"Rig2D '{rigName}' bone #{i} has empty name.");
            if (!origIndexByName.TryAdd(rawBones[i].Name, i))
                throw new InvalidDataException($"Rig2D '{rigName}' duplicate bone name '{rawBones[i].Name}'.");
        }

        int nBones = rawBones.Count;
        int[] inDegree = new int[nBones];
        var children = new List<int>[nBones];
        for (int i = 0; i < nBones; i++) children[i] = new List<int>();

        for (int i = 0; i < nBones; i++)
        {
            string pName = rawBones[i].Parent;
            if (!string.IsNullOrEmpty(pName))
            {
                if (!origIndexByName.TryGetValue(pName, out int pOrig))
                    throw new InvalidDataException($"Rig2D '{rigName}' bone '{rawBones[i].Name}' references unknown parent '{pName}'.");
                if (pOrig == i)
                    throw new InvalidDataException($"Rig2D '{rigName}' bone '{rawBones[i].Name}' cannot parent to itself.");
                children[pOrig].Add(i);
                inDegree[i] = 1;
            }
        }

        // Preserve author order deterministically among ready bones
        var ready = new List<int>();
        for (int i = 0; i < nBones; i++)
            if (inDegree[i] == 0) ready.Add(i);

        var sortedBoneOrder = new List<int>(nBones);
        while (ready.Count > 0)
        {
            int cur = ready[0];
            ready.RemoveAt(0);
            sortedBoneOrder.Add(cur);
            for (int c = 0; c < children[cur].Count; c++)
            {
                int ch = children[cur][c];
                inDegree[ch]--;
                if (inDegree[ch] == 0)
                {
                    int insertPos = 0;
                    while (insertPos < ready.Count && ready[insertPos] < ch) insertPos++;
                    ready.Insert(insertPos, ch);
                }
            }
        }
        if (sortedBoneOrder.Count != nBones)
            throw new InvalidDataException($"Rig2D '{rigName}' bone hierarchy contains a cycle.");

        var bones = new Rig2DBoneSetupData[nBones];
        for (int i = 0; i < nBones; i++)
            boneIndexByName[rawBones[sortedBoneOrder[i]].Name] = i;

        for (int i = 0; i < nBones; i++)
        {
            ParsedBone pb = rawBones[sortedBoneOrder[i]];
            int parentIdx = string.IsNullOrEmpty(pb.Parent) ? -1 : boneIndexByName[pb.Parent];
            bones[i] = new Rig2DBoneSetupData
            {
                Name = StringId.Hash(pb.Name),
                NameText = FixedString64.From(pb.Name),
                ParentIndex = parentIdx,
                X = pb.X,
                Y = pb.Y,
                RotationDeg = pb.RotDeg,
                ScaleX = pb.ScaleX,
                ScaleY = pb.ScaleY,
                ShearX = pb.ShearX,
                ShearY = pb.ShearY,
                Length = pb.Length,
                Inherit = pb.Inherit,
            };
        }

        // Precompute setup world pose & inverse bind matrices
        var bindWorld = new Rig2DAffine2D[nBones];
        var boneInvBind = new Rig2DAffine2D[nBones];
        for (int i = 0; i < nBones; i++)
        {
            ref readonly Rig2DBoneSetupData bs = ref bones[i];
            int p = bs.ParentIndex;
            if (p < 0)
            {
                bindWorld[i] = Rig2DAffine2D.FromTrsShear(bs.X, bs.Y, bs.RotationDeg, bs.ScaleX, bs.ScaleY, bs.ShearX, bs.ShearY);
            }
            else
            {
                ref readonly Rig2DAffine2D pw = ref bindWorld[p];
                switch (bs.Inherit)
                {
                    case Rig2DInheritMode.OnlyTranslation:
                    {
                        float wx = pw.A * bs.X + pw.C * bs.Y + pw.Tx;
                        float wy = pw.B * bs.X + pw.D * bs.Y + pw.Ty;
                        bindWorld[i] = Rig2DAffine2D.FromTrsShear(wx, wy, bs.RotationDeg, bs.ScaleX, bs.ScaleY, bs.ShearX, bs.ShearY);
                        break;
                    }
                    case Rig2DInheritMode.NoRotation:
                    {
                        float wx = pw.A * bs.X + pw.C * bs.Y + pw.Tx;
                        float wy = pw.B * bs.X + pw.D * bs.Y + pw.Ty;
                        float psx = pw.WorldScaleX;
                        float psy = pw.WorldScaleY * (pw.Determinant < 0f ? -1f : 1f);
                        bindWorld[i] = Rig2DAffine2D.FromTrsShear(wx, wy, bs.RotationDeg, bs.ScaleX * psx, bs.ScaleY * psy, bs.ShearX, bs.ShearY);
                        break;
                    }
                    case Rig2DInheritMode.NoScale:
                    {
                        float wx = pw.A * bs.X + pw.C * bs.Y + pw.Tx;
                        float wy = pw.B * bs.X + pw.D * bs.Y + pw.Ty;
                        float pRot = pw.WorldRotationRad * DetMath.Rad2Deg;
                        float signY = pw.Determinant < 0f ? -1f : 1f;
                        bindWorld[i] = Rig2DAffine2D.FromTrsShear(wx, wy, pRot + bs.RotationDeg, bs.ScaleX, bs.ScaleY * signY, bs.ShearX, bs.ShearY);
                        break;
                    }
                    default:
                    {
                        Rig2DAffine2D loc = Rig2DAffine2D.FromTrsShear(bs.X, bs.Y, bs.RotationDeg, bs.ScaleX, bs.ScaleY, bs.ShearX, bs.ShearY);
                        bindWorld[i] = Rig2DAffine2D.Multiply(in pw, in loc);
                        break;
                    }
                }
            }
            boneInvBind[i] = Rig2DAffine2D.Invert(in bindWorld[i]);
        }

        // 2. Compile slots
        var slotIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var slots = new Rig2DSlotSetupData[rawSlots.Count];
        for (int i = 0; i < rawSlots.Count; i++)
        {
            ParsedSlot ps = rawSlots[i];
            if (!boneIndexByName.TryGetValue(ps.Bone, out int bi))
                throw new InvalidDataException($"Rig2D '{rigName}' slot '{ps.Name}' references unknown bone '{ps.Bone}'.");
            if (!slotIndexByName.TryAdd(ps.Name, i))
                throw new InvalidDataException($"Rig2D '{rigName}' duplicate slot name '{ps.Name}'.");
            slots[i] = new Rig2DSlotSetupData
            {
                Name = StringId.Hash(ps.Name),
                NameText = FixedString64.From(ps.Name),
                BoneIndex = bi,
                DefaultAttachmentName = string.IsNullOrEmpty(ps.DefaultAttachment) ? StringId.Empty : StringId.Hash(ps.DefaultAttachment),
                LightColor = ps.LightColor,
                DarkColor = ps.DarkColor,
                BlendMode = ps.Blend,
            };
        }

        // 3. Compile attachments, meshes, sequence frames, and skins
        // Ensure "default" skin is always index 0 when present
        int defIdx = rawSkinOrder.FindIndex(s => s.Equals("default", StringComparison.OrdinalIgnoreCase));
        if (defIdx > 0)
        {
            string defName = rawSkinOrder[defIdx];
            rawSkinOrder.RemoveAt(defIdx);
            rawSkinOrder.Insert(0, defName);
        }

        var attachments = new List<Rig2DAttachmentData>(rawAttachments.Count);
        var meshVertices = new List<Rig2DMeshVertex>();
        var meshIndices = new List<uint>();
        var seqFrameNames = new List<StringId>();
        var skinEntries = new List<Rig2DSkinEntry>();
        var skins = new Rig2DSkinData[rawSkinOrder.Count];

        // Lookup key: (skinName, slotName, attName) -> attachmentIndex
        var attachmentLookup = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int si = 0; si < rawSkinOrder.Count; si++)
        {
            string skinName = rawSkinOrder[si];
            int entryStart = skinEntries.Count;

            for (int ai = 0; ai < rawAttachments.Count; ai++)
            {
                ParsedAttachment pa = rawAttachments[ai];
                if (!pa.SkinName.Equals(skinName, StringComparison.Ordinal)) continue;
                if (!slotIndexByName.TryGetValue(pa.SlotName, out int slotIdx))
                    throw new InvalidDataException($"Rig2D '{rigName}' skin '{skinName}' references unknown slot '{pa.SlotName}'.");

                int attIdx = attachments.Count;
                int vStart = meshVertices.Count;
                int vCount = 0;
                int iStart = meshIndices.Count;
                int iCount = 0;
                int seqStart = seqFrameNames.Count;
                int seqCount = 0;

                if (pa.Kind == Rig2DAttachmentKind.WeightedMesh)
                {
                    int ownerBone = slots[slotIdx].BoneIndex;
                    if (pa.Vertices.Count >= 3 && pa.Triangles.Count >= 3)
                    {
                        vCount = pa.Vertices.Count;
                        for (int v = 0; v < vCount; v++)
                        {
                            Vec2 uv = v < pa.Uvs.Count ? pa.Uvs[v] : Vec2.Zero;
                            byte b0 = 0, b1 = 0, b2 = 0, b3 = 0;
                            float w0 = 0f, w1 = 0f, w2 = 0f, w3 = 0f;
                            if (v < pa.Weights.Count && pa.Weights[v].Length > 0)
                            {
                                var wArr = pa.Weights[v];
                                // Keep top 4 positive weights
                                int count = 0;
                                for (int k = 0; k < wArr.Length && count < 4; k++)
                                {
                                    float wt = wArr[k].Weight;
                                    if (wt <= 1e-6f) continue;
                                    if (!boneIndexByName.TryGetValue(wArr[k].BoneName, out int bIdx))
                                        throw new InvalidDataException($"Rig2D '{rigName}' mesh '{pa.Name}' vertex {v} references unknown bone '{wArr[k].BoneName}'.");
                                    switch (count)
                                    {
                                        case 0: b0 = (byte)bIdx; w0 = wt; break;
                                        case 1: b1 = (byte)bIdx; w1 = wt; break;
                                        case 2: b2 = (byte)bIdx; w2 = wt; break;
                                        case 3: b3 = (byte)bIdx; w3 = wt; break;
                                    }
                                    count++;
                                }
                            }
                            float wSum = w0 + w1 + w2 + w3;
                            if (wSum > 1e-5f)
                            {
                                float inv = 1f / wSum;
                                w0 *= inv; w1 *= inv; w2 *= inv; w3 *= inv;
                                wSum = 1f;
                            }
                            meshVertices.Add(new Rig2DMeshVertex
                            {
                                SetupPos = pa.Vertices[v],
                                Uv = uv,
                                PackedBoneIndices = Rig2DMeshVertex.PackBones(b0, b1, b2, b3),
                                BoneWeights = new Vec4(w0, w1, w2, w3),
                                WeightSum = wSum,
                            });
                        }

                        if ((pa.Triangles.Count % 3) != 0)
                            throw new InvalidDataException($"Rig2D '{rigName}' mesh '{pa.Name}' triangle index count ({pa.Triangles.Count}) must be a multiple of 3.");

                        iCount = pa.Triangles.Count;
                        for (int t = 0; t < iCount; t++)
                        {
                            uint idx = pa.Triangles[t];
                            if (idx >= (uint)vCount)
                                throw new InvalidDataException($"Rig2D '{rigName}' mesh '{pa.Name}' triangle index {idx} out of range [0, {vCount}).");
                            meshIndices.Add(idx);
                        }
                    }
                    else
                    {
                        // Synthesize a clean quad mesh in skeleton bind space bound to owner bone
                        float hw = pa.Width * 0.5f;
                        float hh = pa.Height * 0.5f;
                        Rig2DAffine2D attLocal = Rig2DAffine2D.FromTrsShear(pa.X, pa.Y, pa.RotDeg, pa.ScaleX, pa.ScaleY);
                        Rig2DAffine2D comb = Rig2DAffine2D.Multiply(in bindWorld[ownerBone], in attLocal);
                        Vec2 p0 = Rig2DAffine2D.TransformPoint(in comb, new Vec2(-hw, -hh));
                        Vec2 p1 = Rig2DAffine2D.TransformPoint(in comb, new Vec2(hw, -hh));
                        Vec2 p2 = Rig2DAffine2D.TransformPoint(in comb, new Vec2(hw, hh));
                        Vec2 p3 = Rig2DAffine2D.TransformPoint(in comb, new Vec2(-hw, hh));
                        uint pb = Rig2DMeshVertex.PackBones((byte)ownerBone, 0, 0, 0);
                        var bw = new Vec4(1f, 0f, 0f, 0f);
                        meshVertices.Add(new Rig2DMeshVertex { SetupPos = p0, Uv = new Vec2(0f, 1f), PackedBoneIndices = pb, BoneWeights = bw, WeightSum = 1f });
                        meshVertices.Add(new Rig2DMeshVertex { SetupPos = p1, Uv = new Vec2(1f, 1f), PackedBoneIndices = pb, BoneWeights = bw, WeightSum = 1f });
                        meshVertices.Add(new Rig2DMeshVertex { SetupPos = p2, Uv = new Vec2(1f, 0f), PackedBoneIndices = pb, BoneWeights = bw, WeightSum = 1f });
                        meshVertices.Add(new Rig2DMeshVertex { SetupPos = p3, Uv = new Vec2(0f, 0f), PackedBoneIndices = pb, BoneWeights = bw, WeightSum = 1f });
                        vCount = 4;
                        meshIndices.Add(0); meshIndices.Add(1); meshIndices.Add(2);
                        meshIndices.Add(0); meshIndices.Add(2); meshIndices.Add(3);
                        iCount = 6;
                    }
                }
                else if (pa.Kind == Rig2DAttachmentKind.Sequence)
                {
                    seqCount = pa.SeqFrames.Count;
                    for (int f = 0; f < seqCount; f++)
                        seqFrameNames.Add(StringId.Hash(NormalizeRegionKey(pa.SeqFrames[f])));
                }

                string regionKey = !string.IsNullOrEmpty(pa.ImagePath) ? NormalizeRegionKey(pa.ImagePath) : NormalizeRegionKey(pa.Name);
                attachments.Add(new Rig2DAttachmentData
                {
                    Name = StringId.Hash(pa.Name),
                    NameText = FixedString64.From(pa.Name),
                    SlotIndex = slotIdx,
                    Kind = pa.Kind,
                    X = pa.X,
                    Y = pa.Y,
                    RotationDeg = pa.RotDeg,
                    ScaleX = pa.ScaleX,
                    ScaleY = pa.ScaleY,
                    Width = pa.Width,
                    Height = pa.Height,
                    SpriteRegionName = StringId.Hash(regionKey),
                    FallbackUvRect = new Vec4(0f, 0f, 1f, 1f),
                    FallbackTexture = TextureHandle.Invalid,
                    VertexStart = vStart,
                    VertexCount = vCount,
                    IndexStart = iStart,
                    IndexCount = iCount,
                    SeqFrameStart = seqStart,
                    SeqFrameCount = seqCount,
                    SeqFps = pa.SeqFps > 0f ? pa.SeqFps : 12f,
                    SeqLoop = pa.SeqLoop,
                    SeqStartIndex = pa.SeqStartIndex,
                });

                skinEntries.Add(new Rig2DSkinEntry
                {
                    SlotIndex = slotIdx,
                    AttachmentName = StringId.Hash(pa.Name),
                    AttachmentIndex = attIdx,
                });

                attachmentLookup[$"{skinName}|{pa.SlotName}|{pa.Name}"] = attIdx;
                attachmentLookup.TryAdd($"*|{pa.SlotName}|{pa.Name}", attIdx);
                attachmentLookup.TryAdd($"*|*|{pa.Name}", attIdx);
            }

            skins[si] = new Rig2DSkinData
            {
                Name = StringId.Hash(skinName),
                NameText = FixedString64.From(skinName),
                EntryStart = entryStart,
                EntryCount = skinEntries.Count - entryStart,
            };
        }

        // 4. Compile constraints & sort unified ConstraintOrder
        var iks = new Rig2DIkConstraintData[rawIks.Count];
        var orderList = new List<(Rig2DConstraintOrderItem Item, int TieBreaker, string Name)>();

        for (int i = 0; i < rawIks.Count; i++)
        {
            ParsedIk pk = rawIks[i];
            if (!boneIndexByName.TryGetValue(pk.BoneA, out int bA))
                throw new InvalidDataException($"Rig2D '{rigName}' IK '{pk.Name}' references unknown bone '{pk.BoneA}'.");
            int bB = -1;
            if (!string.IsNullOrEmpty(pk.BoneB))
            {
                if (!boneIndexByName.TryGetValue(pk.BoneB, out bB))
                    throw new InvalidDataException($"Rig2D '{rigName}' IK '{pk.Name}' references unknown bone '{pk.BoneB}'.");
            }
            if (!boneIndexByName.TryGetValue(pk.TargetBone, out int tB))
                throw new InvalidDataException($"Rig2D '{rigName}' IK '{pk.Name}' references unknown target bone '{pk.TargetBone}'.");

            iks[i] = new Rig2DIkConstraintData
            {
                Name = StringId.Hash(pk.Name),
                Order = pk.Order,
                BoneA = bA,
                BoneB = bB,
                TargetBone = tB,
                Mix = DetMath.Clamp01(pk.Mix),
                Softness = DetMath.Max(0f, pk.Softness),
                BendDirection = pk.BendDirection >= 0 ? (sbyte)1 : (sbyte)-1,
                Stretch = pk.Stretch,
                Compress = pk.Compress,
            };
            orderList.Add((new Rig2DConstraintOrderItem { Kind = Rig2DConstraintKind.Ik, Index = i, Order = pk.Order }, orderList.Count, pk.Name));
        }

        var transConstraints = new Rig2DTransformConstraintData[rawTransforms.Count];
        for (int i = 0; i < rawTransforms.Count; i++)
        {
            ParsedTransformConstraint pt = rawTransforms[i];
            if (!boneIndexByName.TryGetValue(pt.Bone, out int bi))
                throw new InvalidDataException($"Rig2D '{rigName}' transform constraint '{pt.Name}' references unknown bone '{pt.Bone}'.");
            if (!boneIndexByName.TryGetValue(pt.TargetBone, out int ti))
                throw new InvalidDataException($"Rig2D '{rigName}' transform constraint '{pt.Name}' references unknown target '{pt.TargetBone}'.");
            transConstraints[i] = new Rig2DTransformConstraintData
            {
                Name = StringId.Hash(pt.Name),
                Order = pt.Order,
                BoneIndex = bi,
                TargetBone = ti,
                MixRotate = pt.MixRotate,
                MixX = pt.MixX,
                MixY = pt.MixY,
                MixScaleX = pt.MixScaleX,
                MixScaleY = pt.MixScaleY,
                MixShearY = pt.MixShearY,
                OffsetRotateDeg = pt.OffsetRotateDeg,
                OffsetX = pt.OffsetX,
                OffsetY = pt.OffsetY,
                OffsetScaleX = pt.OffsetScaleX,
                OffsetScaleY = pt.OffsetScaleY,
                OffsetShearY = pt.OffsetShearY,
                Local = pt.Local,
                Relative = pt.Relative,
            };
            orderList.Add((new Rig2DConstraintOrderItem { Kind = Rig2DConstraintKind.Transform, Index = i, Order = pt.Order }, orderList.Count, pt.Name));
        }

        var springs = new Rig2DSpringConstraintData[rawSprings.Count];
        for (int i = 0; i < rawSprings.Count; i++)
        {
            ParsedSpring ps = rawSprings[i];
            if (!boneIndexByName.TryGetValue(ps.Bone, out int bi))
                throw new InvalidDataException($"Rig2D '{rigName}' spring constraint '{ps.Name}' references unknown bone '{ps.Bone}'.");
            springs[i] = new Rig2DSpringConstraintData
            {
                Name = StringId.Hash(ps.Name),
                Order = ps.Order,
                BoneIndex = bi,
                Inertia = ps.Inertia,
                Strength = ps.Strength,
                Damping = ps.Damping,
                Mass = ps.Mass > 0f ? ps.Mass : 1f,
                Gravity = ps.Gravity,
                Wind = ps.Wind,
                MaxAngleDeg = ps.MaxAngleDeg > 0f ? ps.MaxAngleDeg : 45f,
                Mix = DetMath.Clamp01(ps.Mix),
                PosScaleX = ps.PosScaleX,
                PosScaleY = ps.PosScaleY,
                RotateScale = ps.RotateScale,
            };
            orderList.Add((new Rig2DConstraintOrderItem { Kind = Rig2DConstraintKind.Spring, Index = i, Order = ps.Order }, orderList.Count, ps.Name));
        }

        orderList.Sort((a, b) =>
        {
            int c = a.Item.Order.CompareTo(b.Item.Order);
            return c != 0 ? c : a.TieBreaker.CompareTo(b.TieBreaker);
        });

        var constraintOrder = new Rig2DConstraintOrderItem[orderList.Count];
        var constraintOrderIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < orderList.Count; i++)
        {
            constraintOrder[i] = orderList[i].Item;
            if (!string.IsNullOrEmpty(orderList[i].Name))
                constraintOrderIndexByName[orderList[i].Name] = i;
        }

        // 5. Compile animations, timelines, keyframes, deform pool, drawOrder pool, events
        var clips = new Rig2DAnimClipData[rawAnims.Count];
        var timelines = new List<Rig2DTimelineData>();
        var keyframes = new List<Rig2DKeyframeData>();
        var eventKeys = new List<Rig2DEventKeyData>();
        var deformPool = new List<Vec2>();
        var drawOrderPool = new List<int>();

        for (int ai = 0; ai < rawAnims.Count; ai++)
        {
            ParsedAnim pa = rawAnims[ai];
            int tlStart = timelines.Count;
            int evStart = eventKeys.Count;
            float maxTime = 0f;

            for (int ti = 0; ti < pa.Timelines.Count; ti++)
            {
                ParsedTimeline ptl = pa.Timelines[ti];
                if (ptl.Keys.Count == 0) continue;

                int targetIdx = -1;
                switch (ptl.Target)
                {
                    case Rig2DTimelineTarget.BoneRotate:
                    case Rig2DTimelineTarget.BoneTranslate:
                    case Rig2DTimelineTarget.BoneScale:
                    case Rig2DTimelineTarget.BoneShear:
                        if (!boneIndexByName.TryGetValue(ptl.TargetName, out targetIdx))
                            throw new InvalidDataException($"Rig2D '{rigName}' animation '{pa.Name}' references unknown bone '{ptl.TargetName}'.");
                        break;
                    case Rig2DTimelineTarget.SlotAttachment:
                    case Rig2DTimelineTarget.SlotColor:
                    case Rig2DTimelineTarget.SlotDarkColor:
                    case Rig2DTimelineTarget.SlotSequenceFrame:
                        if (!slotIndexByName.TryGetValue(ptl.TargetName, out targetIdx))
                            throw new InvalidDataException($"Rig2D '{rigName}' animation '{pa.Name}' references unknown slot '{ptl.TargetName}'.");
                        break;
                    case Rig2DTimelineTarget.ConstraintMix:
                        if (!constraintOrderIndexByName.TryGetValue(ptl.TargetName, out targetIdx))
                            throw new InvalidDataException($"Rig2D '{rigName}' animation '{pa.Name}' references unknown constraint '{ptl.TargetName}'.");
                        break;
                    case Rig2DTimelineTarget.MeshDeform:
                        if (!attachmentLookup.TryGetValue($"{ptl.ExtraSkin}|{ptl.ExtraSlot}|{ptl.TargetName}", out targetIdx) &&
                            !attachmentLookup.TryGetValue($"*|{ptl.ExtraSlot}|{ptl.TargetName}", out targetIdx) &&
                            !attachmentLookup.TryGetValue($"*|*|{ptl.TargetName}", out targetIdx))
                            throw new InvalidDataException($"Rig2D '{rigName}' animation '{pa.Name}' deform references unknown attachment '{ptl.TargetName}'.");
                        break;
                    case Rig2DTimelineTarget.DrawOrder:
                        targetIdx = 0;
                        break;
                }

                ptl.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                int keyStart = keyframes.Count;

                for (int ki = 0; ki < ptl.Keys.Count; ki++)
                {
                    ParsedKey pk = ptl.Keys[ki];
                    if (pk.Time > maxTime) maxTime = pk.Time;

                    int intVal = pk.IntVal;
                    ulong hashVal = 0UL;

                    if (ptl.Target == Rig2DTimelineTarget.SlotAttachment)
                    {
                        hashVal = string.IsNullOrEmpty(pk.StringVal) ? 0UL : StringId.Hash(pk.StringVal).Value;
                    }
                    else if (ptl.Target == Rig2DTimelineTarget.MeshDeform)
                    {
                        int vc = attachments[targetIdx].VertexCount;
                        intVal = deformPool.Count;
                        for (int v = 0; v < vc; v++)
                            deformPool.Add((pk.DeformVerts != null && v < pk.DeformVerts.Count) ? pk.DeformVerts[v] : Vec2.Zero);
                    }
                    else if (ptl.Target == Rig2DTimelineTarget.DrawOrder)
                    {
                        int sc = slots.Length;
                        if (pk.DrawOrderExplicit != null && pk.DrawOrderExplicit.Count > 0)
                        {
                            intVal = drawOrderPool.Count;
                            var used = new bool[sc];
                            for (int s = 0; s < pk.DrawOrderExplicit.Count; s++)
                            {
                                if (slotIndexByName.TryGetValue(pk.DrawOrderExplicit[s], out int idx) && !used[idx])
                                {
                                    used[idx] = true;
                                    drawOrderPool.Add(idx);
                                }
                            }
                            for (int s = 0; s < sc; s++)
                                if (!used[s]) drawOrderPool.Add(s);
                        }
                        else if (pk.DrawOrderOffsets != null && pk.DrawOrderOffsets.Count > 0)
                        {
                            intVal = drawOrderPool.Count;
                            int[] perm = new int[sc];
                            for (int s = 0; s < sc; s++) perm[s] = -1;
                            var unchanged = new List<int>(sc);
                            var shifted = new bool[sc];
                            for (int oi = 0; oi < pk.DrawOrderOffsets.Count; oi++)
                            {
                                if (!slotIndexByName.TryGetValue(pk.DrawOrderOffsets[oi].Slot, out int sIdx)) continue;
                                int targetPos = Math.Clamp(sIdx + pk.DrawOrderOffsets[oi].Offset, 0, sc - 1);
                                perm[targetPos] = sIdx;
                                shifted[sIdx] = true;
                            }
                            for (int s = 0; s < sc; s++)
                                if (!shifted[s]) unchanged.Add(s);
                            int u = 0;
                            for (int s = 0; s < sc; s++)
                            {
                                if (perm[s] < 0 && u < unchanged.Count) perm[s] = unchanged[u++];
                                drawOrderPool.Add(perm[s] >= 0 ? perm[s] : s);
                            }
                        }
                        else
                        {
                            intVal = -1; // Reset to setup order
                        }
                    }

                    keyframes.Add(new Rig2DKeyframeData
                    {
                        Time = pk.Time,
                        V0 = pk.V0,
                        V1 = pk.V1,
                        V2 = pk.V2,
                        V3 = pk.V3,
                        Curve = pk.Curve,
                        Cx1 = pk.Cx1,
                        Cy1 = pk.Cy1,
                        Cx2 = pk.Cx2,
                        Cy2 = pk.Cy2,
                        IntVal = intVal,
                        HashVal = hashVal,
                    });
                }

                timelines.Add(new Rig2DTimelineData
                {
                    Target = ptl.Target,
                    TargetIndex = targetIdx,
                    KeyStart = keyStart,
                    KeyCount = keyframes.Count - keyStart,
                });
            }

            pa.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
            for (int ei = 0; ei < pa.Events.Count; ei++)
            {
                ParsedEventKey pe = pa.Events[ei];
                if (pe.Time > maxTime) maxTime = pe.Time;
                eventDefs.TryGetValue(pe.Name, out ParsedEventDef def);
                int iv = pe.HasInt ? pe.IntVal : def.IntVal;
                float fv = pe.HasFloat ? pe.FloatVal : def.FloatVal;
                string sv = pe.HasString ? pe.StringVal : (def.StringVal ?? "");
                eventKeys.Add(new Rig2DEventKeyData
                {
                    Time = pe.Time,
                    EventName = StringId.Hash(pe.Name),
                    IntVal = iv,
                    FloatVal = fv,
                    StringVal = string.IsNullOrEmpty(sv) ? StringId.Empty : StringId.Hash(sv),
                });
            }

            float duration = pa.ExplicitDuration > 0f ? pa.ExplicitDuration : maxTime;
            clips[ai] = new Rig2DAnimClipData
            {
                Name = StringId.Hash(pa.Name),
                NameText = FixedString64.From(pa.Name),
                Duration = duration,
                TimelineStart = tlStart,
                TimelineCount = timelines.Count - tlStart,
                EventStart = evStart,
                EventCount = eventKeys.Count - evStart,
            };
        }

        // 6. Serialize flat binary BRIG payload
        var hdr = new BrigHeader
        {
            Magic = BrigHeader.MagicValue,
            Version = BrigHeader.CurrentVersion,
            Flags = 0,
            NameHash = StringId.Hash(rigName).Value,
            NameText = FixedString64.From(rigName),
            PixelsPerUnit = ppu > 0f && float.IsFinite(ppu) ? ppu : 100f,
            OriginOffset = origin,
            BoundsSize = boundsSize,

            BoneCount = bones.Length,
            SlotCount = slots.Length,
            AttachmentCount = attachments.Count,
            MeshVertexCount = meshVertices.Count,
            MeshIndexCount = meshIndices.Count,
            SequenceFrameNameCount = seqFrameNames.Count,
            SkinCount = skins.Length,
            SkinEntryCount = skinEntries.Count,
            IkConstraintCount = iks.Length,
            TransformConstraintCount = transConstraints.Length,
            SpringConstraintCount = springs.Length,
            ConstraintOrderCount = constraintOrder.Length,
            ClipCount = clips.Length,
            TimelineCount = timelines.Count,
            KeyframeCount = keyframes.Count,
            EventKeyCount = eventKeys.Count,
            DeformPoolCount = deformPool.Count,
            DrawOrderPoolCount = drawOrderPool.Count,
        };

        using var ms = new MemoryStream(4096);
        WriteStruct(ms, in hdr);
        WriteSpan(ms, bones.AsSpan());
        WriteSpan(ms, boneInvBind.AsSpan());
        WriteSpan(ms, slots.AsSpan());
        WriteSpan(ms, CollectionsMarshal.AsSpan(attachments));
        WriteSpan(ms, CollectionsMarshal.AsSpan(meshVertices));
        WriteSpan(ms, CollectionsMarshal.AsSpan(meshIndices));
        WriteSpan(ms, CollectionsMarshal.AsSpan(seqFrameNames));
        WriteSpan(ms, skins.AsSpan());
        WriteSpan(ms, CollectionsMarshal.AsSpan(skinEntries));
        WriteSpan(ms, iks.AsSpan());
        WriteSpan(ms, transConstraints.AsSpan());
        WriteSpan(ms, springs.AsSpan());
        WriteSpan(ms, constraintOrder.AsSpan());
        WriteSpan(ms, clips.AsSpan());
        WriteSpan(ms, CollectionsMarshal.AsSpan(timelines));
        WriteSpan(ms, CollectionsMarshal.AsSpan(keyframes));
        WriteSpan(ms, CollectionsMarshal.AsSpan(eventKeys));
        WriteSpan(ms, CollectionsMarshal.AsSpan(deformPool));
        WriteSpan(ms, CollectionsMarshal.AsSpan(drawOrderPool));
        return ms.ToArray();
    }

    public static string StripRig2DExtension(string relPath)
    {
        string norm = relPath.Replace('\\', '/');
        if (norm.EndsWith(".rig2d.json", StringComparison.OrdinalIgnoreCase))
            return norm.Substring(0, norm.Length - ".rig2d.json".Length);
        if (norm.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return norm.Substring(0, norm.Length - ".json".Length);
        return norm;
    }

    public static string NormalizeRegionKey(string path)
    {
        string s = path.Replace('\\', '/').Trim();
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s.Substring(2);
        while (s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(1);
        int dot = s.LastIndexOf('.');
        int slash = s.LastIndexOf('/');
        if (dot > slash)
        {
            string ext = s.Substring(dot).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga")
                s = s.Substring(0, dot);
        }
        return s;
    }

    // =========================================================================
    // JSON Section Parsers (Zero-Reflection Utf8JsonReader)
    // =========================================================================

    private static void ParseBones(ref Utf8JsonReader r, List<ParsedBone> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("'bones' must be a JSON array.");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Bone entry must be a JSON object.");
            var b = new ParsedBone
            {
                Name = "",
                Parent = "",
                ScaleX = 1f,
                ScaleY = 1f,
                Inherit = Rig2DInheritMode.Normal,
            };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string k = r.GetString() ?? "";
                if (!r.Read()) throw new InvalidDataException("Unexpected EOF in bone object.");
                switch (k)
                {
                    case "name": b.Name = ReadString(ref r); break;
                    case "parent": b.Parent = ReadString(ref r); break;
                    case "x": b.X = ReadFloat(ref r, 0f); break;
                    case "y": b.Y = ReadFloat(ref r, 0f); break;
                    case "rotation": b.RotDeg = ReadFloat(ref r, 0f); break;
                    case "scaleX": b.ScaleX = ReadFloat(ref r, 1f); break;
                    case "scaleY": b.ScaleY = ReadFloat(ref r, 1f); break;
                    case "shearX": b.ShearX = ReadFloat(ref r, 0f); break;
                    case "shearY": b.ShearY = ReadFloat(ref r, 0f); break;
                    case "length": b.Length = ReadFloat(ref r, 0f); break;
                    case "inherit": b.Inherit = ParseInheritMode(ReadString(ref r)); break;
                    default: r.Skip(); break;
                }
            }
            dst.Add(b);
        }
    }

    private static void ParseSlots(ref Utf8JsonReader r, List<ParsedSlot> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("'slots' must be a JSON array.");
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Slot entry must be a JSON object.");
            var s = new ParsedSlot
            {
                Name = "",
                Bone = "",
                DefaultAttachment = "",
                LightColor = ColorF.White,
                DarkColor = ColorF.Transparent,
                Blend = Rig2DBlendMode.AlphaBlend,
            };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string k = r.GetString() ?? "";
                if (!r.Read()) throw new InvalidDataException("Unexpected EOF in slot object.");
                switch (k)
                {
                    case "name": s.Name = ReadString(ref r); break;
                    case "bone": s.Bone = ReadString(ref r); break;
                    case "attachment": s.DefaultAttachment = ReadString(ref r); break;
                    case "color": s.LightColor = ReadColor(ref r, ColorF.White); break;
                    case "darkColor": s.DarkColor = ReadColor(ref r, ColorF.Transparent); break;
                    case "blend": s.Blend = ParseBlendMode(ReadString(ref r)); break;
                    default: r.Skip(); break;
                }
            }
            dst.Add(s);
        }
    }

    private static void ParseSkins(ref Utf8JsonReader r, List<string> skinOrder, List<ParsedAttachment> dst)
    {
        if (r.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("'skins' must be a JSON object mapping skinName -> slots.");
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string skinName = r.GetString() ?? "default";
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException($"Skin '{skinName}' must be an object.");
            skinOrder.Add(skinName);

            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string slotName = r.GetString() ?? "";
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                    throw new InvalidDataException($"Skin '{skinName}' slot '{slotName}' must be an object.");

                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    string attName = r.GetString() ?? "";
                    if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                        throw new InvalidDataException($"Attachment '{attName}' must be an object.");

                    var att = new ParsedAttachment
                    {
                        SkinName = skinName,
                        SlotName = slotName,
                        Name = attName,
                        Kind = Rig2DAttachmentKind.Region,
                        ImagePath = "",
                        ScaleX = 1f,
                        ScaleY = 1f,
                        Width = 64f,
                        Height = 64f,
                        Vertices = new List<Vec2>(),
                        Uvs = new List<Vec2>(),
                        Triangles = new List<uint>(),
                        Weights = new List<(string, float)[]>(),
                        SeqFrames = new List<string>(),
                        SeqFps = 12f,
                        SeqLoop = Rig2DSequenceLoop.Loop,
                        SeqStartIndex = 0,
                    };

                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string k = r.GetString() ?? "";
                        if (!r.Read()) throw new InvalidDataException("Unexpected EOF in attachment.");
                        switch (k)
                        {
                            case "type":
                                att.Kind = ParseAttachmentKind(ReadString(ref r));
                                break;
                            case "image":
                            case "path":
                                att.ImagePath = ReadString(ref r);
                                break;
                            case "x": att.X = ReadFloat(ref r, 0f); break;
                            case "y": att.Y = ReadFloat(ref r, 0f); break;
                            case "rotation": att.RotDeg = ReadFloat(ref r, 0f); break;
                            case "scaleX": att.ScaleX = ReadFloat(ref r, 1f); break;
                            case "scaleY": att.ScaleY = ReadFloat(ref r, 1f); break;
                            case "width": att.Width = ReadFloat(ref r, 64f); break;
                            case "height": att.Height = ReadFloat(ref r, 64f); break;
                            case "vertices": ReadVec2List(ref r, att.Vertices); break;
                            case "uvs": ReadVec2List(ref r, att.Uvs); break;
                            case "triangles": ReadUIntList(ref r, att.Triangles); break;
                            case "weights": ReadVertexWeights(ref r, att.Weights); break;
                            case "frames": ReadStringList(ref r, att.SeqFrames); break;
                            case "fps": att.SeqFps = ReadFloat(ref r, 12f); break;
                            case "loop": att.SeqLoop = ParseSequenceLoop(ReadString(ref r)); break;
                            case "startIndex": att.SeqStartIndex = ReadInt(ref r, 0); break;
                            default: r.Skip(); break;
                        }
                    }

                    if (att.Kind == Rig2DAttachmentKind.Region && att.SeqFrames.Count > 0)
                        att.Kind = Rig2DAttachmentKind.Sequence;

                    dst.Add(att);
                }
            }
        }
    }

    private static void ParseConstraints(
        ref Utf8JsonReader r,
        List<ParsedIk> iks,
        List<ParsedTransformConstraint> transforms,
        List<ParsedSpring> springs)
    {
        if (r.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("'constraints' must be a JSON object.");
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string group = r.GetString() ?? "";
            if (!r.Read()) throw new InvalidDataException("Unexpected EOF in constraints.");
            if (group == "ik" && r.TokenType == JsonTokenType.StartArray)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    var ik = new ParsedIk { Name = "", BoneA = "", BoneB = "", TargetBone = "", Mix = 1f, BendDirection = 1 };
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string k = r.GetString() ?? "";
                        if (!r.Read()) break;
                        switch (k)
                        {
                            case "name": ik.Name = ReadString(ref r); break;
                            case "order": ik.Order = ReadInt(ref r, 0); break;
                            case "bone":
                            case "boneA": ik.BoneA = ReadString(ref r); break;
                            case "boneB": ik.BoneB = ReadString(ref r); break;
                            case "bones":
                            {
                                var list = new List<string>();
                                ReadStringList(ref r, list);
                                if (list.Count >= 1) ik.BoneA = list[0];
                                if (list.Count >= 2) ik.BoneB = list[1];
                                break;
                            }
                            case "target": ik.TargetBone = ReadString(ref r); break;
                            case "mix": ik.Mix = ReadFloat(ref r, 1f); break;
                            case "softness": ik.Softness = ReadFloat(ref r, 0f); break;
                            case "bendDirection": ik.BendDirection = ReadInt(ref r, 1) >= 0 ? (sbyte)1 : (sbyte)-1; break;
                            case "stretch": ik.Stretch = ReadBoolByte(ref r, 0); break;
                            case "compress": ik.Compress = ReadBoolByte(ref r, 0); break;
                            default: r.Skip(); break;
                        }
                    }
                    iks.Add(ik);
                }
            }
            else if (group == "transform" && r.TokenType == JsonTokenType.StartArray)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    var tc = new ParsedTransformConstraint { Name = "", Bone = "", TargetBone = "" };
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string k = r.GetString() ?? "";
                        if (!r.Read()) break;
                        switch (k)
                        {
                            case "name": tc.Name = ReadString(ref r); break;
                            case "order": tc.Order = ReadInt(ref r, 0); break;
                            case "bone": tc.Bone = ReadString(ref r); break;
                            case "target": tc.TargetBone = ReadString(ref r); break;
                            case "mixRotate": tc.MixRotate = ReadFloat(ref r, 0f); break;
                            case "mixX": tc.MixX = ReadFloat(ref r, 0f); break;
                            case "mixY": tc.MixY = ReadFloat(ref r, 0f); break;
                            case "mixScaleX": tc.MixScaleX = ReadFloat(ref r, 0f); break;
                            case "mixScaleY": tc.MixScaleY = ReadFloat(ref r, 0f); break;
                            case "mixShearY": tc.MixShearY = ReadFloat(ref r, 0f); break;
                            case "offsetRotate": tc.OffsetRotateDeg = ReadFloat(ref r, 0f); break;
                            case "offsetX": tc.OffsetX = ReadFloat(ref r, 0f); break;
                            case "offsetY": tc.OffsetY = ReadFloat(ref r, 0f); break;
                            case "offsetScaleX": tc.OffsetScaleX = ReadFloat(ref r, 0f); break;
                            case "offsetScaleY": tc.OffsetScaleY = ReadFloat(ref r, 0f); break;
                            case "offsetShearY": tc.OffsetShearY = ReadFloat(ref r, 0f); break;
                            case "local": tc.Local = ReadBoolByte(ref r, 0); break;
                            case "relative": tc.Relative = ReadBoolByte(ref r, 0); break;
                            default: r.Skip(); break;
                        }
                    }
                    transforms.Add(tc);
                }
            }
            else if (group == "spring" && r.TokenType == JsonTokenType.StartArray)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    var sp = new ParsedSpring
                    {
                        Name = "", Bone = "", Inertia = 0.5f, Strength = 60f, Damping = 8f,
                        Mass = 1f, Gravity = 0f, Wind = 0f, MaxAngleDeg = 40f, Mix = 1f, RotateScale = 1f,
                    };
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string k = r.GetString() ?? "";
                        if (!r.Read()) break;
                        switch (k)
                        {
                            case "name": sp.Name = ReadString(ref r); break;
                            case "order": sp.Order = ReadInt(ref r, 0); break;
                            case "bone": sp.Bone = ReadString(ref r); break;
                            case "inertia": sp.Inertia = ReadFloat(ref r, 0.5f); break;
                            case "strength": sp.Strength = ReadFloat(ref r, 60f); break;
                            case "damping": sp.Damping = ReadFloat(ref r, 8f); break;
                            case "mass": sp.Mass = ReadFloat(ref r, 1f); break;
                            case "gravity": sp.Gravity = ReadFloat(ref r, 0f); break;
                            case "wind": sp.Wind = ReadFloat(ref r, 0f); break;
                            case "maxAngle":
                            case "maxAngleDeg": sp.MaxAngleDeg = ReadFloat(ref r, 40f); break;
                            case "mix": sp.Mix = ReadFloat(ref r, 1f); break;
                            case "posScaleX": sp.PosScaleX = ReadFloat(ref r, 0f); break;
                            case "posScaleY": sp.PosScaleY = ReadFloat(ref r, 0f); break;
                            case "rotateScale": sp.RotateScale = ReadFloat(ref r, 1f); break;
                            default: r.Skip(); break;
                        }
                    }
                    springs.Add(sp);
                }
            }
            else
            {
                r.Skip();
            }
        }
    }

    private static void ParseEventDefs(ref Utf8JsonReader r, Dictionary<string, ParsedEventDef> defs)
    {
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string name = r.GetString() ?? "";
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var def = new ParsedEventDef { StringVal = "" };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string k = r.GetString() ?? "";
                if (!r.Read()) break;
                switch (k)
                {
                    case "int": def.IntVal = ReadInt(ref r, 0); break;
                    case "float": def.FloatVal = ReadFloat(ref r, 0f); break;
                    case "string": def.StringVal = ReadString(ref r); break;
                    default: r.Skip(); break;
                }
            }
            defs[name] = def;
        }
    }

    private static void ParseAnimations(ref Utf8JsonReader r, List<ParsedAnim> anims)
    {
        if (r.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("'animations' must be a JSON object.");
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string animName = r.GetString() ?? "";
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException($"Animation '{animName}' must be an object.");

            var anim = new ParsedAnim
            {
                Name = animName,
                ExplicitDuration = 0f,
                Timelines = new List<ParsedTimeline>(),
                Events = new List<ParsedEventKey>(),
            };

            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string section = r.GetString() ?? "";
                if (!r.Read()) break;

                switch (section)
                {
                    case "duration":
                        anim.ExplicitDuration = ReadFloat(ref r, 0f);
                        break;
                    case "bones":
                        ParseBoneTimelines(ref r, anim.Timelines);
                        break;
                    case "slots":
                        ParseSlotTimelines(ref r, anim.Timelines);
                        break;
                    case "constraints":
                        ParseConstraintTimelines(ref r, anim.Timelines);
                        break;
                    case "deform":
                        ParseDeformTimelines(ref r, anim.Timelines);
                        break;
                    case "drawOrder":
                        ParseDrawOrderTimeline(ref r, anim.Timelines);
                        break;
                    case "events":
                        ParseAnimEvents(ref r, anim.Events);
                        break;
                    default:
                        r.Skip();
                        break;
                }
            }
            anims.Add(anim);
        }
    }

    private static void ParseBoneTimelines(ref Utf8JsonReader r, List<ParsedTimeline> dst)
    {
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string boneName = r.GetString() ?? "";
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string chan = r.GetString() ?? "";
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
                Rig2DTimelineTarget target;
                if (chan == "rotate") target = Rig2DTimelineTarget.BoneRotate;
                else if (chan == "translate") target = Rig2DTimelineTarget.BoneTranslate;
                else if (chan == "scale") target = Rig2DTimelineTarget.BoneScale;
                else if (chan == "shear") target = Rig2DTimelineTarget.BoneShear;
                else { r.Skip(); continue; }

                var keys = new List<ParsedKey>();
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    var pk = new ParsedKey
                    {
                        V0 = target == Rig2DTimelineTarget.BoneScale ? 1f : 0f,
                        V1 = target == Rig2DTimelineTarget.BoneScale ? 1f : 0f,
                        StringVal = "",
                    };
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string k = r.GetString() ?? "";
                        if (!r.Read()) break;
                        switch (k)
                        {
                            case "time": pk.Time = ReadFloat(ref r, 0f); break;
                            case "value":
                            case "angle":
                                if (r.TokenType == JsonTokenType.StartArray)
                                {
                                    Vec2 v = ReadVec2(ref r, Vec2.Zero);
                                    pk.V0 = v.X; pk.V1 = v.Y;
                                }
                                else pk.V0 = ReadFloat(ref r, 0f);
                                break;
                            case "x": pk.V0 = ReadFloat(ref r, pk.V0); break;
                            case "y": pk.V1 = ReadFloat(ref r, pk.V1); break;
                            case "curve": ReadCurve(ref r, ref pk); break;
                            default: r.Skip(); break;
                        }
                    }
                    keys.Add(pk);
                }
                dst.Add(new ParsedTimeline { Target = target, TargetName = boneName, ExtraSlot = "", ExtraSkin = "", Keys = keys });
            }
        }
    }

    private static void ParseSlotTimelines(ref Utf8JsonReader r, List<ParsedTimeline> dst)
    {
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string slotName = r.GetString() ?? "";
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string chan = r.GetString() ?? "";
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }

                Rig2DTimelineTarget target;
                if (chan == "attachment") target = Rig2DTimelineTarget.SlotAttachment;
                else if (chan == "color") target = Rig2DTimelineTarget.SlotColor;
                else if (chan == "darkColor") target = Rig2DTimelineTarget.SlotDarkColor;
                else if (chan == "sequence") target = Rig2DTimelineTarget.SlotSequenceFrame;
                else { r.Skip(); continue; }

                var keys = new List<ParsedKey>();
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    var pk = new ParsedKey { StringVal = "", V0 = 1f, V1 = 1f, V2 = 1f, V3 = 1f };
                    if (target == Rig2DTimelineTarget.SlotDarkColor) { pk.V0 = 0f; pk.V1 = 0f; pk.V2 = 0f; pk.V3 = 0f; }
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string k = r.GetString() ?? "";
                        if (!r.Read()) break;
                        switch (k)
                        {
                            case "time": pk.Time = ReadFloat(ref r, 0f); break;
                            case "name": pk.StringVal = ReadString(ref r); break;
                            case "frame": pk.IntVal = ReadInt(ref r, 0); break;
                            case "value":
                            case "color":
                                ColorF c = ReadColor(ref r, ColorF.White);
                                pk.V0 = c.R; pk.V1 = c.G; pk.V2 = c.B; pk.V3 = c.A;
                                break;
                            case "curve": ReadCurve(ref r, ref pk); break;
                            default: r.Skip(); break;
                        }
                    }
                    keys.Add(pk);
                }
                dst.Add(new ParsedTimeline { Target = target, TargetName = slotName, ExtraSlot = "", ExtraSkin = "", Keys = keys });
            }
        }
    }

    private static void ParseConstraintTimelines(ref Utf8JsonReader r, List<ParsedTimeline> dst)
    {
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string cName = r.GetString() ?? "";
            if (!r.Read() || r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
            var keys = new List<ParsedKey>();
            while (r.Read() && r.TokenType != JsonTokenType.EndArray)
            {
                if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                var pk = new ParsedKey { V0 = 1f, V1 = -1f, StringVal = "" };
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    string k = r.GetString() ?? "";
                    if (!r.Read()) break;
                    switch (k)
                    {
                        case "time": pk.Time = ReadFloat(ref r, 0f); break;
                        case "mix": pk.V0 = ReadFloat(ref r, 1f); break;
                        case "softness": pk.V1 = ReadFloat(ref r, -1f); break;
                        case "bendDirection": pk.IntVal = ReadInt(ref r, 0); break;
                        case "curve": ReadCurve(ref r, ref pk); break;
                        default: r.Skip(); break;
                    }
                }
                keys.Add(pk);
            }
            dst.Add(new ParsedTimeline { Target = Rig2DTimelineTarget.ConstraintMix, TargetName = cName, ExtraSlot = "", ExtraSkin = "", Keys = keys });
        }
    }

    private static void ParseDeformTimelines(ref Utf8JsonReader r, List<ParsedTimeline> dst)
    {
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string firstKey = r.GetString() ?? "";
            if (!r.Read()) break;

            if (r.TokenType == JsonTokenType.StartArray)
            {
                // Shorthand: deform.<attachmentName> = [ keys... ]
                var keys = ReadDeformKeyArray(ref r);
                dst.Add(new ParsedTimeline { Target = Rig2DTimelineTarget.MeshDeform, TargetName = firstKey, ExtraSlot = "*", ExtraSkin = "*", Keys = keys });
            }
            else if (r.TokenType == JsonTokenType.StartObject)
            {
                // Full: deform.<skin>.<slot>.<attachment> = [ keys... ]
                string skinName = firstKey;
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    string slotName = r.GetString() ?? "";
                    if (!r.Read() || r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string attName = r.GetString() ?? "";
                        if (!r.Read() || r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
                        var keys = ReadDeformKeyArray(ref r);
                        dst.Add(new ParsedTimeline { Target = Rig2DTimelineTarget.MeshDeform, TargetName = attName, ExtraSlot = slotName, ExtraSkin = skinName, Keys = keys });
                    }
                }
            }
            else r.Skip();
        }
    }

    private static List<ParsedKey> ReadDeformKeyArray(ref Utf8JsonReader r)
    {
        var keys = new List<ParsedKey>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var deformVerts = new List<Vec2>();
            var pk = new ParsedKey { StringVal = "", DeformVerts = deformVerts };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string k = r.GetString() ?? "";
                if (!r.Read()) break;
                switch (k)
                {
                    case "time": pk.Time = ReadFloat(ref r, 0f); break;
                    case "vertices": ReadVec2List(ref r, deformVerts); break;
                    case "curve": ReadCurve(ref r, ref pk); break;
                    default: r.Skip(); break;
                }
            }
            keys.Add(pk);
        }
        return keys;
    }

    private static void ParseDrawOrderTimeline(ref Utf8JsonReader r, List<ParsedTimeline> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        var keys = new List<ParsedKey>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var pk = new ParsedKey { StringVal = "" };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string k = r.GetString() ?? "";
                if (!r.Read()) break;
                if (k == "time") pk.Time = ReadFloat(ref r, 0f);
                else if (k == "order" && r.TokenType == JsonTokenType.StartArray)
                {
                    pk.DrawOrderExplicit = new List<string>();
                    ReadStringList(ref r, pk.DrawOrderExplicit);
                }
                else if (k == "offsets" && r.TokenType == JsonTokenType.StartArray)
                {
                    pk.DrawOrderOffsets = new List<(string, int)>();
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                        string sl = "";
                        int off = 0;
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            string ok = r.GetString() ?? "";
                            if (!r.Read()) break;
                            if (ok == "slot") sl = ReadString(ref r);
                            else if (ok == "offset") off = ReadInt(ref r, 0);
                            else r.Skip();
                        }
                        if (!string.IsNullOrEmpty(sl)) pk.DrawOrderOffsets.Add((sl, off));
                    }
                }
                else r.Skip();
            }
            keys.Add(pk);
        }
        if (keys.Count > 0)
            dst.Add(new ParsedTimeline { Target = Rig2DTimelineTarget.DrawOrder, TargetName = "", ExtraSlot = "", ExtraSkin = "", Keys = keys });
    }

    private static void ParseAnimEvents(ref Utf8JsonReader r, List<ParsedEventKey> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var ev = new ParsedEventKey { Name = "", StringVal = "" };
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                string k = r.GetString() ?? "";
                if (!r.Read()) break;
                switch (k)
                {
                    case "time": ev.Time = ReadFloat(ref r, 0f); break;
                    case "name": ev.Name = ReadString(ref r); break;
                    case "int": ev.IntVal = ReadInt(ref r, 0); ev.HasInt = true; break;
                    case "float": ev.FloatVal = ReadFloat(ref r, 0f); ev.HasFloat = true; break;
                    case "string": ev.StringVal = ReadString(ref r); ev.HasString = true; break;
                    default: r.Skip(); break;
                }
            }
            if (!string.IsNullOrEmpty(ev.Name)) dst.Add(ev);
        }
    }

    // =========================================================================
    // Low-Level Token Helpers
    // =========================================================================

    private static void ReadCurve(ref Utf8JsonReader r, ref ParsedKey pk)
    {
        if (r.TokenType == JsonTokenType.String)
        {
            string name = r.GetString() ?? "linear";
            switch (name.ToLowerInvariant())
            {
                case "step":
                case "stepped":
                    pk.Curve = Rig2DCurveKind.Step;
                    break;
                case "easein":
                    pk.Curve = Rig2DCurveKind.Bezier;
                    pk.Cx1 = 0.42f; pk.Cy1 = 0f; pk.Cx2 = 1f; pk.Cy2 = 1f;
                    break;
                case "easeout":
                    pk.Curve = Rig2DCurveKind.Bezier;
                    pk.Cx1 = 0f; pk.Cy1 = 0f; pk.Cx2 = 0.58f; pk.Cy2 = 1f;
                    break;
                case "easeinout":
                    pk.Curve = Rig2DCurveKind.Bezier;
                    pk.Cx1 = 0.42f; pk.Cy1 = 0f; pk.Cx2 = 0.58f; pk.Cy2 = 1f;
                    break;
                case "easeoutcubic":
                    pk.Curve = Rig2DCurveKind.Bezier;
                    pk.Cx1 = 0.33f; pk.Cy1 = 1f; pk.Cx2 = 0.68f; pk.Cy2 = 1f;
                    break;
                case "easeinoutsine":
                    pk.Curve = Rig2DCurveKind.Bezier;
                    pk.Cx1 = 0.37f; pk.Cy1 = 0f; pk.Cx2 = 0.63f; pk.Cy2 = 1f;
                    break;
                case "easeoutquad":
                    pk.Curve = Rig2DCurveKind.Bezier;
                    pk.Cx1 = 0.5f; pk.Cy1 = 1f; pk.Cx2 = 0.89f; pk.Cy2 = 1f;
                    break;
                default:
                    pk.Curve = Rig2DCurveKind.Linear;
                    break;
            }
        }
        else if (r.TokenType == JsonTokenType.StartArray)
        {
            float[] c = new float[4];
            int idx = 0;
            while (r.Read() && r.TokenType != JsonTokenType.EndArray)
            {
                if (idx < 4 && r.TokenType == JsonTokenType.Number && r.TryGetSingle(out float v))
                    c[idx++] = v;
            }
            if (idx == 4)
            {
                pk.Curve = Rig2DCurveKind.Bezier;
                pk.Cx1 = c[0]; pk.Cy1 = c[1]; pk.Cx2 = c[2]; pk.Cy2 = c[3];
            }
        }
        else
        {
            r.Skip();
        }
    }

    private static string ReadString(ref Utf8JsonReader r)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString() ?? "";
        if (r.TokenType == JsonTokenType.Null) return "";
        r.Skip();
        return "";
    }

    private static float ReadFloat(ref Utf8JsonReader r, float fallback)
    {
        if (r.TokenType == JsonTokenType.Number && r.TryGetSingle(out float v) && float.IsFinite(v))
            return v;
        if (r.TokenType == JsonTokenType.StartObject || r.TokenType == JsonTokenType.StartArray)
            r.Skip();
        return fallback;
    }

    private static int ReadInt(ref Utf8JsonReader r, int fallback)
    {
        if (r.TokenType == JsonTokenType.Number)
        {
            if (r.TryGetInt32(out int vi)) return vi;
            if (r.TryGetSingle(out float vf) && float.IsFinite(vf)) return (int)MathF.Round(vf);
        }
        if (r.TokenType == JsonTokenType.StartObject || r.TokenType == JsonTokenType.StartArray)
            r.Skip();
        return fallback;
    }

    private static byte ReadBoolByte(ref Utf8JsonReader r, byte fallback)
    {
        if (r.TokenType == JsonTokenType.True) return 1;
        if (r.TokenType == JsonTokenType.False) return 0;
        if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out int v)) return (byte)(v != 0 ? 1 : 0);
        r.Skip();
        return fallback;
    }

    private static Vec2 ReadVec2(ref Utf8JsonReader r, Vec2 fallback)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return fallback; }
        float x = fallback.X, y = fallback.Y;
        int idx = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.Number && r.TryGetSingle(out float v))
            {
                if (idx == 0) x = v;
                else if (idx == 1) y = v;
                idx++;
            }
        }
        return new Vec2(x, y);
    }

    private static ColorF ReadColor(ref Utf8JsonReader r, ColorF fallback)
    {
        if (r.TokenType == JsonTokenType.StartArray)
        {
            float c0 = fallback.R, c1 = fallback.G, c2 = fallback.B, c3 = fallback.A;
            int idx = 0;
            while (r.Read() && r.TokenType != JsonTokenType.EndArray)
            {
                if (r.TokenType == JsonTokenType.Number && r.TryGetSingle(out float v))
                {
                    if (idx == 0) c0 = v;
                    else if (idx == 1) c1 = v;
                    else if (idx == 2) c2 = v;
                    else if (idx == 3) c3 = v;
                    idx++;
                }
            }
            return new ColorF(c0, c1, c2, c3);
        }
        if (r.TokenType == JsonTokenType.String)
        {
            string hex = (r.GetString() ?? "").TrimStart('#');
            if ((hex.Length == 6 || hex.Length == 8) &&
                byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte rr) &&
                byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte gg) &&
                byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte bb))
            {
                byte aa = 255;
                if (hex.Length == 8)
                    byte.TryParse(hex.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out aa);
                return new ColorF(rr / 255f, gg / 255f, bb / 255f, aa / 255f);
            }
            return fallback;
        }
        r.Skip();
        return fallback;
    }

    private static void ReadVec2List(ref Utf8JsonReader r, List<Vec2> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        bool hasPending = false;
        float pendingX = 0f;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.StartArray)
            {
                dst.Add(ReadVec2(ref r, Vec2.Zero));
            }
            else if (r.TokenType == JsonTokenType.Number && r.TryGetSingle(out float v))
            {
                if (!hasPending) { pendingX = v; hasPending = true; }
                else { dst.Add(new Vec2(pendingX, v)); hasPending = false; }
            }
        }
    }

    private static void ReadUIntList(ref Utf8JsonReader r, List<uint> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.Number && r.TryGetUInt32(out uint v))
                dst.Add(v);
        }
    }

    private static void ReadStringList(ref Utf8JsonReader r, List<string> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.String)
                dst.Add(r.GetString() ?? "");
            else if (r.TokenType == JsonTokenType.StartObject || r.TokenType == JsonTokenType.StartArray)
                r.Skip();
        }
    }

    private static void ReadVertexWeights(ref Utf8JsonReader r, List<(string BoneName, float Weight)[]> dst)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
            var pairs = new List<(string, float)>();
            while (r.Read() && r.TokenType != JsonTokenType.EndArray)
            {
                if (r.TokenType == JsonTokenType.StartArray)
                {
                    string bn = "";
                    float wt = 0f;
                    int idx = 0;
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (idx == 0 && r.TokenType == JsonTokenType.String) bn = r.GetString() ?? "";
                        else if (idx == 1 && r.TokenType == JsonTokenType.Number && r.TryGetSingle(out float w)) wt = w;
                        idx++;
                    }
                    if (!string.IsNullOrEmpty(bn) && wt > 0f) pairs.Add((bn, wt));
                }
                else if (r.TokenType == JsonTokenType.StartObject)
                {
                    string bn = "";
                    float wt = 0f;
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        string k = r.GetString() ?? "";
                        if (!r.Read()) break;
                        if (k == "bone") bn = ReadString(ref r);
                        else if (k == "weight") wt = ReadFloat(ref r, 0f);
                        else r.Skip();
                    }
                    if (!string.IsNullOrEmpty(bn) && wt > 0f) pairs.Add((bn, wt));
                }
            }
            dst.Add(pairs.ToArray());
        }
    }

    private static Rig2DInheritMode ParseInheritMode(string s) => s.ToLowerInvariant() switch
    {
        "onlytranslation" or "only_translation" => Rig2DInheritMode.OnlyTranslation,
        "norotation" or "no_rotation" => Rig2DInheritMode.NoRotation,
        "noscale" or "no_scale" => Rig2DInheritMode.NoScale,
        _ => Rig2DInheritMode.Normal,
    };

    private static Rig2DBlendMode ParseBlendMode(string s) => s.ToLowerInvariant() switch
    {
        "additive" or "add" => Rig2DBlendMode.Additive,
        "multiply" or "mul" => Rig2DBlendMode.Multiply,
        _ => Rig2DBlendMode.AlphaBlend,
    };

    private static Rig2DAttachmentKind ParseAttachmentKind(string s) => s.ToLowerInvariant() switch
    {
        "mesh" or "weightedmesh" or "weighted_mesh" => Rig2DAttachmentKind.WeightedMesh,
        "sequence" or "seq" => Rig2DAttachmentKind.Sequence,
        "none" => Rig2DAttachmentKind.None,
        _ => Rig2DAttachmentKind.Region,
    };

    private static Rig2DSequenceLoop ParseSequenceLoop(string s) => s.ToLowerInvariant() switch
    {
        "hold" or "once" => Rig2DSequenceLoop.Hold,
        "pingpong" or "ping_pong" => Rig2DSequenceLoop.PingPong,
        _ => Rig2DSequenceLoop.Loop,
    };

    private static void WriteStruct<T>(Stream s, in T value) where T : unmanaged
    {
        fixed (T* ptr = &value)
            s.Write(new ReadOnlySpan<byte>(ptr, sizeof(T)));
    }

    private static void WriteSpan<T>(Stream s, ReadOnlySpan<T> span) where T : unmanaged
    {
        if (span.IsEmpty) return;
        s.Write(MemoryMarshal.AsBytes(span));
    }
}
