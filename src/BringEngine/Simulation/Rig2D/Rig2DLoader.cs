using System;
using System.IO;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Simulation.Rig2D;

/// <summary>
/// Runtime zero-GC binary BRIG loader into a Bundle LinearArena.
/// Validates header, exact payload bounds, topological bone order, and every cross-array index before registering.
/// </summary>
public static unsafe class Rig2DLoader
{
    public static Rig2DAssetHandle LoadFromMemory(
        ReadOnlySpan<byte> brigBytes,
        ref LinearArena bundleArena,
        ref Rig2DTable rigs,
        BundleId bundle,
        StringId nameOverride = default,
        TextureHandle defaultAtlas = default)
    {
        if (brigBytes.Length < sizeof(BrigHeader))
            throw new InvalidDataException("Truncated BRIG header.");

        BrigHeader hdr = MemoryMarshal.Read<BrigHeader>(brigBytes);
        if (hdr.Magic != BrigHeader.MagicValue)
            throw new InvalidDataException($"Invalid BRIG magic 0x{hdr.Magic:X8}.");
        if (hdr.Version != BrigHeader.CurrentVersion)
            throw new InvalidDataException($"Unsupported BRIG version {hdr.Version}.");

        if (hdr.BoneCount < 0 || hdr.BoneCount > Rig2DAssetData.MaxBonesPerRig ||
            hdr.SlotCount < 0 || hdr.AttachmentCount < 0 ||
            hdr.MeshVertexCount < 0 || hdr.MeshIndexCount < 0 ||
            hdr.SequenceFrameNameCount < 0 || hdr.SkinCount < 0 || hdr.SkinEntryCount < 0 ||
            hdr.IkConstraintCount < 0 || hdr.TransformConstraintCount < 0 || hdr.SpringConstraintCount < 0 ||
            hdr.ConstraintOrderCount < 0 || hdr.ClipCount < 0 || hdr.TimelineCount < 0 ||
            hdr.KeyframeCount < 0 || hdr.EventKeyCount < 0 ||
            hdr.DeformPoolCount < 0 || hdr.DrawOrderPoolCount < 0)
        {
            throw new InvalidDataException("Invalid negative or out-of-range counts in BRIG header.");
        }

        ulong expectedBytes = (ulong)sizeof(BrigHeader)
            + (ulong)hdr.BoneCount * (ulong)sizeof(Rig2DBoneSetupData)
            + (ulong)hdr.BoneCount * (ulong)sizeof(Rig2DAffine2D)
            + (ulong)hdr.SlotCount * (ulong)sizeof(Rig2DSlotSetupData)
            + (ulong)hdr.AttachmentCount * (ulong)sizeof(Rig2DAttachmentData)
            + (ulong)hdr.MeshVertexCount * (ulong)sizeof(Rig2DMeshVertex)
            + (ulong)hdr.MeshIndexCount * (ulong)sizeof(uint)
            + (ulong)hdr.SequenceFrameNameCount * (ulong)sizeof(StringId)
            + (ulong)hdr.SkinCount * (ulong)sizeof(Rig2DSkinData)
            + (ulong)hdr.SkinEntryCount * (ulong)sizeof(Rig2DSkinEntry)
            + (ulong)hdr.IkConstraintCount * (ulong)sizeof(Rig2DIkConstraintData)
            + (ulong)hdr.TransformConstraintCount * (ulong)sizeof(Rig2DTransformConstraintData)
            + (ulong)hdr.SpringConstraintCount * (ulong)sizeof(Rig2DSpringConstraintData)
            + (ulong)hdr.ConstraintOrderCount * (ulong)sizeof(Rig2DConstraintOrderItem)
            + (ulong)hdr.ClipCount * (ulong)sizeof(Rig2DAnimClipData)
            + (ulong)hdr.TimelineCount * (ulong)sizeof(Rig2DTimelineData)
            + (ulong)hdr.KeyframeCount * (ulong)sizeof(Rig2DKeyframeData)
            + (ulong)hdr.EventKeyCount * (ulong)sizeof(Rig2DEventKeyData)
            + (ulong)hdr.DeformPoolCount * (ulong)sizeof(Vec2)
            + (ulong)hdr.DrawOrderPoolCount * (ulong)sizeof(int);

        if (expectedBytes != (ulong)brigBytes.Length)
            throw new InvalidDataException($"Corrupt BRIG payload length ({brigBytes.Length}B != expected {expectedBytes}B).");

        ArenaMarker mark = bundleArena.SaveMarker();
        try
        {
            int cursor = sizeof(BrigHeader);

            Rig2DBoneSetupData* bones = ReadArray<Rig2DBoneSetupData>(brigBytes, ref cursor, hdr.BoneCount, ref bundleArena);
            Rig2DAffine2D* invBind = ReadArray<Rig2DAffine2D>(brigBytes, ref cursor, hdr.BoneCount, ref bundleArena);
            Rig2DSlotSetupData* slots = ReadArray<Rig2DSlotSetupData>(brigBytes, ref cursor, hdr.SlotCount, ref bundleArena);
            Rig2DAttachmentData* attachments = ReadArray<Rig2DAttachmentData>(brigBytes, ref cursor, hdr.AttachmentCount, ref bundleArena);
            Rig2DMeshVertex* meshVerts = ReadArray<Rig2DMeshVertex>(brigBytes, ref cursor, hdr.MeshVertexCount, ref bundleArena);
            uint* meshIndices = ReadArray<uint>(brigBytes, ref cursor, hdr.MeshIndexCount, ref bundleArena);
            StringId* seqNames = ReadArray<StringId>(brigBytes, ref cursor, hdr.SequenceFrameNameCount, ref bundleArena);
            Rig2DSkinData* skins = ReadArray<Rig2DSkinData>(brigBytes, ref cursor, hdr.SkinCount, ref bundleArena);
            Rig2DSkinEntry* skinEntries = ReadArray<Rig2DSkinEntry>(brigBytes, ref cursor, hdr.SkinEntryCount, ref bundleArena);
            Rig2DIkConstraintData* iks = ReadArray<Rig2DIkConstraintData>(brigBytes, ref cursor, hdr.IkConstraintCount, ref bundleArena);
            Rig2DTransformConstraintData* tcs = ReadArray<Rig2DTransformConstraintData>(brigBytes, ref cursor, hdr.TransformConstraintCount, ref bundleArena);
            Rig2DSpringConstraintData* springs = ReadArray<Rig2DSpringConstraintData>(brigBytes, ref cursor, hdr.SpringConstraintCount, ref bundleArena);
            Rig2DConstraintOrderItem* order = ReadArray<Rig2DConstraintOrderItem>(brigBytes, ref cursor, hdr.ConstraintOrderCount, ref bundleArena);
            Rig2DAnimClipData* clips = ReadArray<Rig2DAnimClipData>(brigBytes, ref cursor, hdr.ClipCount, ref bundleArena);
            Rig2DTimelineData* timelines = ReadArray<Rig2DTimelineData>(brigBytes, ref cursor, hdr.TimelineCount, ref bundleArena);
            Rig2DKeyframeData* keyframes = ReadArray<Rig2DKeyframeData>(brigBytes, ref cursor, hdr.KeyframeCount, ref bundleArena);
            Rig2DEventKeyData* events = ReadArray<Rig2DEventKeyData>(brigBytes, ref cursor, hdr.EventKeyCount, ref bundleArena);
            Vec2* deformPool = ReadArray<Vec2>(brigBytes, ref cursor, hdr.DeformPoolCount, ref bundleArena);
            int* drawOrderPool = ReadArray<int>(brigBytes, ref cursor, hdr.DrawOrderPoolCount, ref bundleArena);

            // Validate every cross-reference & topological invariant
            for (int i = 0; i < hdr.BoneCount; i++)
            {
                int p = bones[i].ParentIndex;
                if (p < -1 || p >= i)
                    throw new InvalidDataException($"Corrupt BRIG bone {i} parent index {p} (must be in [-1, {i - 1}]).");
            }
            for (int i = 0; i < hdr.SlotCount; i++)
            {
                if ((uint)slots[i].BoneIndex >= (uint)hdr.BoneCount)
                    throw new InvalidDataException($"Corrupt BRIG slot {i} bone index {slots[i].BoneIndex}.");
            }
            for (int i = 0; i < hdr.AttachmentCount; i++)
            {
                ref readonly Rig2DAttachmentData a = ref attachments[i];
                if ((uint)a.SlotIndex >= (uint)hdr.SlotCount)
                    throw new InvalidDataException($"Corrupt BRIG attachment {i} slot index {a.SlotIndex}.");
                if (a.VertexStart < 0 || a.VertexCount < 0 || a.VertexStart + a.VertexCount > hdr.MeshVertexCount)
                    throw new InvalidDataException($"Corrupt BRIG attachment {i} vertex range.");
                if (a.IndexStart < 0 || a.IndexCount < 0 || (a.IndexCount % 3) != 0 || a.IndexStart + a.IndexCount > hdr.MeshIndexCount)
                    throw new InvalidDataException($"Corrupt BRIG attachment {i} index range.");
                for (int k = 0; k < a.IndexCount; k++)
                {
                    if (meshIndices[a.IndexStart + k] >= (uint)a.VertexCount)
                        throw new InvalidDataException($"Corrupt BRIG attachment {i} triangle index out of range.");
                }
                if (a.SeqFrameStart < 0 || a.SeqFrameCount < 0 || a.SeqFrameStart + a.SeqFrameCount > hdr.SequenceFrameNameCount)
                    throw new InvalidDataException($"Corrupt BRIG attachment {i} sequence range.");
            }
            for (int i = 0; i < hdr.SkinCount; i++)
            {
                if (skins[i].EntryStart < 0 || skins[i].EntryCount < 0 || skins[i].EntryStart + skins[i].EntryCount > hdr.SkinEntryCount)
                    throw new InvalidDataException($"Corrupt BRIG skin {i} entry range.");
            }
            for (int i = 0; i < hdr.SkinEntryCount; i++)
            {
                if ((uint)skinEntries[i].SlotIndex >= (uint)hdr.SlotCount || (uint)skinEntries[i].AttachmentIndex >= (uint)hdr.AttachmentCount)
                    throw new InvalidDataException($"Corrupt BRIG skin entry {i}.");
            }
            for (int i = 0; i < hdr.IkConstraintCount; i++)
            {
                if ((uint)iks[i].BoneA >= (uint)hdr.BoneCount ||
                    (iks[i].BoneB != -1 && (uint)iks[i].BoneB >= (uint)hdr.BoneCount) ||
                    (uint)iks[i].TargetBone >= (uint)hdr.BoneCount)
                    throw new InvalidDataException($"Corrupt BRIG IK constraint {i}.");
            }
            for (int i = 0; i < hdr.TransformConstraintCount; i++)
            {
                if ((uint)tcs[i].BoneIndex >= (uint)hdr.BoneCount || (uint)tcs[i].TargetBone >= (uint)hdr.BoneCount)
                    throw new InvalidDataException($"Corrupt BRIG transform constraint {i}.");
            }
            for (int i = 0; i < hdr.SpringConstraintCount; i++)
            {
                if ((uint)springs[i].BoneIndex >= (uint)hdr.BoneCount)
                    throw new InvalidDataException($"Corrupt BRIG spring constraint {i}.");
            }
            for (int i = 0; i < hdr.ConstraintOrderCount; i++)
            {
                bool ok = order[i].Kind switch
                {
                    Rig2DConstraintKind.Ik => (uint)order[i].Index < (uint)hdr.IkConstraintCount,
                    Rig2DConstraintKind.Transform => (uint)order[i].Index < (uint)hdr.TransformConstraintCount,
                    Rig2DConstraintKind.Spring => (uint)order[i].Index < (uint)hdr.SpringConstraintCount,
                    _ => false,
                };
                if (!ok) throw new InvalidDataException($"Corrupt BRIG constraint order entry {i}.");
            }
            for (int i = 0; i < hdr.ClipCount; i++)
            {
                if (clips[i].TimelineStart < 0 || clips[i].TimelineCount < 0 || clips[i].TimelineStart + clips[i].TimelineCount > hdr.TimelineCount)
                    throw new InvalidDataException($"Corrupt BRIG clip {i} timeline range.");
                if (clips[i].EventStart < 0 || clips[i].EventCount < 0 || clips[i].EventStart + clips[i].EventCount > hdr.EventKeyCount)
                    throw new InvalidDataException($"Corrupt BRIG clip {i} event range.");
            }
            for (int i = 0; i < hdr.TimelineCount; i++)
            {
                ref readonly Rig2DTimelineData tl = ref timelines[i];
                if (tl.KeyStart < 0 || tl.KeyCount < 0 || tl.KeyStart + tl.KeyCount > hdr.KeyframeCount)
                    throw new InvalidDataException($"Corrupt BRIG timeline {i} keyframe range.");

                if (tl.Target == Rig2DTimelineTarget.DrawOrder)
                {
                    for (int k = 0; k < tl.KeyCount; k++)
                    {
                        int off = keyframes[tl.KeyStart + k].IntVal;
                        if (off != -1 && (off < 0 || off + hdr.SlotCount > hdr.DrawOrderPoolCount))
                            throw new InvalidDataException($"Corrupt BRIG drawOrder keyframe pool offset {off}.");
                        if (off >= 0)
                        {
                            for (int s = 0; s < hdr.SlotCount; s++)
                                if ((uint)drawOrderPool[off + s] >= (uint)hdr.SlotCount)
                                    throw new InvalidDataException($"Corrupt BRIG drawOrder slot index.");
                        }
                    }
                }
                else if (tl.Target == Rig2DTimelineTarget.MeshDeform)
                {
                    if ((uint)tl.TargetIndex >= (uint)hdr.AttachmentCount)
                        throw new InvalidDataException($"Corrupt BRIG deform timeline {i} attachment index {tl.TargetIndex}.");
                    int vc = attachments[tl.TargetIndex].VertexCount;
                    for (int k = 0; k < tl.KeyCount; k++)
                    {
                        int off = keyframes[tl.KeyStart + k].IntVal;
                        if (off < 0 || off + vc > hdr.DeformPoolCount)
                            throw new InvalidDataException($"Corrupt BRIG deform keyframe pool offset {off}.");
                    }
                }
            }

            var asset = new Rig2DAssetData
            {
                OwnerBundle = bundle,
                Name = nameOverride.IsValid ? nameOverride : new StringId(hdr.NameHash),
                NameText = hdr.NameText,
                PixelsPerUnit = hdr.PixelsPerUnit > 0f && float.IsFinite(hdr.PixelsPerUnit) ? hdr.PixelsPerUnit : 100f,
                OriginOffset = hdr.OriginOffset,
                BoundsSize = hdr.BoundsSize,
                DefaultAtlasTexture = defaultAtlas,
                Bones = bones,
                BoneInvBind = invBind,
                BoneCount = hdr.BoneCount,
                Slots = slots,
                SlotCount = hdr.SlotCount,
                Attachments = attachments,
                AttachmentCount = hdr.AttachmentCount,
                MeshVertices = meshVerts,
                MeshVertexCount = hdr.MeshVertexCount,
                MeshIndices = meshIndices,
                MeshIndexCount = hdr.MeshIndexCount,
                SequenceFrameNames = seqNames,
                SequenceFrameNameCount = hdr.SequenceFrameNameCount,
                Skins = skins,
                SkinCount = hdr.SkinCount,
                SkinEntries = skinEntries,
                SkinEntryCount = hdr.SkinEntryCount,
                IkConstraints = iks,
                IkConstraintCount = hdr.IkConstraintCount,
                TransformConstraints = tcs,
                TransformConstraintCount = hdr.TransformConstraintCount,
                SpringConstraints = springs,
                SpringConstraintCount = hdr.SpringConstraintCount,
                ConstraintOrder = order,
                ConstraintOrderCount = hdr.ConstraintOrderCount,
                Clips = clips,
                ClipCount = hdr.ClipCount,
                Timelines = timelines,
                TimelineCount = hdr.TimelineCount,
                Keyframes = keyframes,
                KeyframeCount = hdr.KeyframeCount,
                EventKeys = events,
                EventKeyCount = hdr.EventKeyCount,
                DeformPool = deformPool,
                DeformPoolCount = hdr.DeformPoolCount,
                DrawOrderPool = drawOrderPool,
                DrawOrderPoolCount = hdr.DrawOrderPoolCount,
            };

            return Rig2DSystem.RegisterAsset(ref rigs, in asset);
        }
        catch
        {
            bundleArena.Rollback(mark);
            throw;
        }
    }

    private static T* ReadArray<T>(ReadOnlySpan<byte> src, ref int cursor, int count, ref LinearArena arena) where T : unmanaged
    {
        if (count <= 0) return null;
        int bytes = checked(count * sizeof(T));
        T* dst = arena.AllocUninitialized<T>(count, 64);
        src.Slice(cursor, bytes).CopyTo(new Span<byte>((byte*)dst, bytes));
        cursor += bytes;
        return dst;
    }
}
