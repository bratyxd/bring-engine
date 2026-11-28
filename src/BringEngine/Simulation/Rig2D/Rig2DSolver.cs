using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.World;

namespace BringEngine.Simulation.Rig2D;

/// <summary>
/// Pure unmanaged, Zero-OOP, Zero-GC, headless-deterministic 2D skeletal rig &amp; deformable mesh solver.
/// Supports bones + 4 inherit modes, slots, skins + runtime overrides, regions / weighted meshes + FFD / sequence slots,
/// 1 &amp; 2-bone analytic IK (bend, softness, stretch, compress), transform constraints, deterministic spring physics,
/// 8-track animation mixing (crossfade + additive + Bezier/stepped curves), draw-order keys, events, and world bone sockets.
/// </summary>
public static unsafe class Rig2DSystem
{
    private const int TrackCount = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct BoneLocalPose
    {
        public float X, Y, RotDeg, ScaleX, ScaleY, ShearX, ShearY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClipPoseSample
    {
        public float X, Y, RotDeg, ScaleX, ScaleY, ShearX, ShearY;
        public byte HasTranslate, HasRotate, HasScale, HasShear;
    }

    // =========================================================================
    // Asset Registry APIs
    // =========================================================================

    public static Rig2DAssetHandle RegisterAsset(ref Rig2DTable table, in Rig2DAssetData data)
    {
        if (data.BoneCount < 0 || data.BoneCount > Rig2DAssetData.MaxBonesPerRig)
            throw new ArgumentOutOfRangeException(nameof(data), data.BoneCount, $"Rig2D BoneCount must be in [0, {Rig2DAssetData.MaxBonesPerRig}].");
        if (data.SlotCount < 0)
            throw new ArgumentOutOfRangeException(nameof(data), data.SlotCount, "Rig2D SlotCount must be >= 0.");
        if (data.BoneCount > 0 && (data.Bones == null || data.BoneInvBind == null))
            throw new ArgumentNullException(nameof(data), "Rig2D Bones and BoneInvBind must not be null when BoneCount > 0.");
        if (data.SlotCount > 0 && data.Slots == null)
            throw new ArgumentNullException(nameof(data), "Rig2D Slots must not be null when SlotCount > 0.");

        for (int i = 0; i < table.MaxAssets; i++)
        {
            if (table.AssetAlive[i] == 0)
            {
                var h = Rig2DAssetHandle.Create((uint)i, table.AssetGens[i]);
                table.AssetAlive[i] = 1;
                table.LiveAssetCount++;
                table.Assets[i] = data;
                table.Assets[i].Handle = h;
                if (table.Assets[i].PixelsPerUnit <= 0f || !float.IsFinite(table.Assets[i].PixelsPerUnit))
                    table.Assets[i].PixelsPerUnit = 100f;
                if (data.Name.IsValid)
                    table.AssetByName.Set(data.Name.Value, h.Raw);
                return h;
            }
        }
        throw new OutOfMemoryException("Rig2DTable asset slots exhausted.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rig2DAssetData* TryGetAsset(in Rig2DTable table, Rig2DAssetHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)table.MaxAssets) return null;
        if (table.AssetAlive[handle.Index] == 0 || table.AssetGens[handle.Index] != handle.Generation) return null;
        return table.Assets + handle.Index;
    }

    public static Rig2DAssetHandle FindAsset(in Rig2DTable table, StringId name)
    {
        if (!name.IsValid || !table.AssetByName.TryGet(name.Value, out uint raw))
            return Rig2DAssetHandle.Invalid;
        var h = new Rig2DAssetHandle(raw);
        return TryGetAsset(in table, h) != null ? h : Rig2DAssetHandle.Invalid;
    }

    public static Rig2DAssetHandle FindAsset(in Rig2DTable table, ReadOnlySpan<char> name)
        => FindAsset(in table, StringId.Hash(name));

    public static void UnregisterByBundle(ref Rig2DTable table, BundleId bundle)
    {
        if (!bundle.IsValid) return;
        for (int i = 0; i < table.MaxAssets; i++)
        {
            if (table.AssetAlive[i] != 0 && table.Assets[i].OwnerBundle == bundle)
            {
                if (table.Assets[i].Name.IsValid &&
                    table.AssetByName.TryGet(table.Assets[i].Name.Value, out uint cur) &&
                    cur == table.Assets[i].Handle.Raw)
                {
                    table.AssetByName.Remove(table.Assets[i].Name.Value);
                }
                table.Assets[i] = default;
                table.AssetAlive[i] = 0;
                table.LiveAssetCount--;
                table.AssetGens[i] = HandlePacking.NextGeneration(table.AssetGens[i]);
            }
        }
    }

    public static int FindBoneIndex(Rig2DAssetData* asset, StringId name)
    {
        if (asset == null || !name.IsValid || asset->Bones == null) return -1;
        for (int i = 0; i < asset->BoneCount; i++)
            if (asset->Bones[i].Name == name) return i;
        return -1;
    }

    public static int FindBoneIndex(Rig2DAssetData* asset, ReadOnlySpan<char> name)
        => FindBoneIndex(asset, StringId.Hash(name));

    public static int FindSlotIndex(Rig2DAssetData* asset, StringId name)
    {
        if (asset == null || !name.IsValid || asset->Slots == null) return -1;
        for (int i = 0; i < asset->SlotCount; i++)
            if (asset->Slots[i].Name == name) return i;
        return -1;
    }

    public static int FindSlotIndex(Rig2DAssetData* asset, ReadOnlySpan<char> name)
        => FindSlotIndex(asset, StringId.Hash(name));

    public static int FindClipIndex(Rig2DAssetData* asset, StringId name)
    {
        if (asset == null || !name.IsValid || asset->Clips == null) return -1;
        for (int i = 0; i < asset->ClipCount; i++)
            if (asset->Clips[i].Name == name) return i;
        return -1;
    }

    public static int FindClipIndex(Rig2DAssetData* asset, ReadOnlySpan<char> name)
        => FindClipIndex(asset, StringId.Hash(name));

    public static int FindSkinIndex(Rig2DAssetData* asset, StringId name)
    {
        if (asset == null || !name.IsValid || asset->Skins == null) return -1;
        for (int i = 0; i < asset->SkinCount; i++)
            if (asset->Skins[i].Name == name) return i;
        return -1;
    }

    public static int FindSkinIndex(Rig2DAssetData* asset, ReadOnlySpan<char> name)
        => FindSkinIndex(asset, StringId.Hash(name));

    public static int FindAttachmentIndex(Rig2DAssetData* asset, int slotIndex, StringId attachmentName, int activeSkinIndex = 0)
    {
        if (asset == null || !attachmentName.IsValid || slotIndex < 0 || slotIndex >= asset->SlotCount)
            return -1;

        if (activeSkinIndex >= 0 && activeSkinIndex < asset->SkinCount && asset->Skins != null && asset->SkinEntries != null)
        {
            ref readonly Rig2DSkinData skin = ref asset->Skins[activeSkinIndex];
            for (int i = 0; i < skin.EntryCount; i++)
            {
                ref readonly Rig2DSkinEntry e = ref asset->SkinEntries[skin.EntryStart + i];
                if (e.SlotIndex == slotIndex && e.AttachmentName == attachmentName)
                    return e.AttachmentIndex;
            }
        }

        if (activeSkinIndex != 0 && asset->SkinCount > 0 && asset->Skins != null && asset->SkinEntries != null)
        {
            ref readonly Rig2DSkinData defSkin = ref asset->Skins[0];
            for (int i = 0; i < defSkin.EntryCount; i++)
            {
                ref readonly Rig2DSkinEntry e = ref asset->SkinEntries[defSkin.EntryStart + i];
                if (e.SlotIndex == slotIndex && e.AttachmentName == attachmentName)
                    return e.AttachmentIndex;
            }
        }

        if (asset->Attachments != null)
        {
            for (int i = 0; i < asset->AttachmentCount; i++)
            {
                if (asset->Attachments[i].SlotIndex == slotIndex && asset->Attachments[i].Name == attachmentName)
                    return i;
            }
        }
        return -1;
    }

    // =========================================================================
    // Instance Lifecycle APIs
    // =========================================================================

    public static Rig2DInstanceHandle AddInstance(
        ref Rig2DTable table,
        EntityId entity,
        Rig2DAssetHandle assetHandle,
        SceneId scene = default,
        uint layerMask = 1u,
        short sortingLayer = 0,
        short orderInLayer = 0,
        SpriteBillboardMode billboard = SpriteBillboardMode.None,
        bool depthTest = true)
    {
        Rig2DAssetData* asset = TryGetAsset(in table, assetHandle);
        if (asset == null)
            throw new InvalidOperationException($"Cannot add Rig2D instance: invalid Rig2DAssetHandle {assetHandle}.");

        if (table.InstanceFreeCount <= 0 && table.InstanceNextSlot >= table.MaxInstances)
            throw new OutOfMemoryException("Rig2DTable instance slots exhausted.");

        int boneCount = asset->BoneCount;
        int slotCount = asset->SlotCount;
        int springCount = asset->SpringConstraintCount;
        int overrideCount = Rig2DTable.DefaultOverridesPerInstance;

        int boneStart = -1, slotStart = -1, springStart = -1, overrideStart = -1;
        bool allocated = false;
        try
        {
            boneStart = table.BonePool.Allocate(boneCount);
            if (boneCount > 0 && boneStart < 0)
                throw new OutOfMemoryException("Rig2DTable pooled bone capacity exhausted.");

            slotStart = table.SlotPool.Allocate(slotCount);
            if (slotCount > 0 && slotStart < 0)
                throw new OutOfMemoryException("Rig2DTable pooled slot capacity exhausted.");

            springStart = table.SpringPool.Allocate(springCount);
            if (springCount > 0 && springStart < 0)
                throw new OutOfMemoryException("Rig2DTable pooled spring capacity exhausted.");

            overrideStart = table.OverridePool.Allocate(overrideCount);
            if (overrideStart < 0)
            {
                // Graceful fallback: 0 overrides if override pool is full while bones/slots succeeded
                overrideCount = 0;
                overrideStart = 0;
            }
            allocated = true;
        }
        finally
        {
            if (!allocated)
            {
                if (boneCount > 0 && boneStart >= 0) table.BonePool.Free(boneStart, boneCount);
                if (slotCount > 0 && slotStart >= 0) table.SlotPool.Free(slotStart, slotCount);
                if (springCount > 0 && springStart >= 0) table.SpringPool.Free(springStart, springCount);
                if (overrideCount > 0 && overrideStart >= 0) table.OverridePool.Free(overrideStart, overrideCount);
            }
        }

        uint slot = table.InstanceFreeCount > 0
            ? table.InstanceFreeSlots[--table.InstanceFreeCount]
            : (uint)table.InstanceNextSlot++;

        var handle = Rig2DInstanceHandle.Create(slot, table.InstanceGens[slot]);
        int denseIdx = table.InstanceCount++;
        table.InstanceSlotToDense[slot] = denseIdx;

        int defaultSkin = asset->SkinCount > 0 ? 0 : -1;
        var inst = new Rig2DInstanceData
        {
            Handle = handle,
            Entity = entity,
            OwnerScene = scene,
            Asset = assetHandle,
            ActiveSkinIndex = defaultSkin,
            CustomShader = ShaderHandle.Invalid,
            Tint = ColorF.White,
            DarkTint = ColorF.Transparent,
            HitFlash = 0f,
            CustomParams = Vec4.Zero,
            Scale = Vec2.One,
            LayerMask = layerMask,
            SortingLayer = sortingLayer,
            OrderInLayer = orderInLayer,
            Billboard = billboard,
            FlipX = 0,
            FlipY = 0,
            Visible = 1,
            DepthTest = (byte)(depthTest ? 1 : 0),
            BoneSliceStart = boneStart,
            BoneSliceCount = boneCount,
            SlotSliceStart = slotStart,
            SlotSliceCount = slotCount,
            SpringSliceStart = springStart,
            SpringSliceCount = springCount,
            OverrideSliceStart = overrideStart,
            OverrideSliceCount = overrideCount,
            PrevEntityPos = Vec3.Zero,
            HasPrevEntityPos = 0,
            PosedOnce = 0,
        };

        for (int t = 0; t < TrackCount; t++)
        {
            inst.Tracks[t] = new Rig2DTrackState
            {
                ClipIndex = -1,
                FromClipIndex = -1,
                Speed = 1f,
                Alpha = 1f,
                Loop = 1,
                FromLoop = 1,
                Active = 0,
            };
        }

        table.Instances[denseIdx] = inst;

        // Initialize setup pose immediately so queries/renders before first StepAll are valid
        if (boneCount > 0)
        {
            Rig2DAffine2D* bw = table.BoneWorld + boneStart;
            ComputeSetupWorldPose(asset, bw);
        }
        for (int s = 0; s < slotCount; s++)
        {
            ref readonly Rig2DSlotSetupData ss = ref asset->Slots[s];
            int attIdx = FindAttachmentIndex(asset, s, ss.DefaultAttachmentName, defaultSkin);
            int seqStart = (attIdx >= 0 && asset->Attachments[attIdx].Kind == Rig2DAttachmentKind.Sequence)
                ? asset->Attachments[attIdx].SeqStartIndex
                : 0;
            table.SlotStates[slotStart + s] = new Rig2DRuntimeSlotState
            {
                ActiveAttachmentIndex = attIdx,
                LightColor = ss.LightColor,
                DarkColor = ss.DarkColor,
                SequenceTime = 0f,
                SequenceFrame = seqStart,
                DeformVertexOffset = 0,
                DeformVertexCount = 0,
            };
            table.SlotOverrideAttachment[slotStart + s] = -1;
            table.DrawOrder[slotStart + s] = s;
        }
        for (int sp = 0; sp < springCount; sp++)
            table.SpringStates[springStart + sp] = default;
        for (int ov = 0; ov < overrideCount; ov++)
            table.BoneOverrides[overrideStart + ov] = new Rig2DBoneOverride { BoneIndex = -1, Mode = Rig2DBoneOverrideMode.None };

        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rig2DInstanceData* TryGetInstance(in Rig2DTable table, Rig2DInstanceHandle handle)
    {
        if (!handle.IsValid) return null;
        uint slot = handle.Index;
        if (slot >= (uint)table.MaxInstances) return null;
        if (table.InstanceGens[slot] != handle.Generation) return null;
        int d = table.InstanceSlotToDense[slot];
        return (d >= 0 && d < table.InstanceCount) ? table.Instances + d : null;
    }

    public static bool RemoveInstance(ref Rig2DTable table, Rig2DInstanceHandle handle)
    {
        Rig2DInstanceData* ptr = TryGetInstance(in table, handle);
        if (ptr == null) return false;
        RemoveInstanceDenseAt(ref table, (int)(ptr - table.Instances));
        return true;
    }

    private static void RemoveInstanceDenseAt(ref Rig2DTable table, int denseIdx)
    {
        ref Rig2DInstanceData inst = ref table.Instances[denseIdx];
        if (inst.BoneSliceCount > 0) table.BonePool.Free(inst.BoneSliceStart, inst.BoneSliceCount);
        if (inst.SlotSliceCount > 0) table.SlotPool.Free(inst.SlotSliceStart, inst.SlotSliceCount);
        if (inst.SpringSliceCount > 0) table.SpringPool.Free(inst.SpringSliceStart, inst.SpringSliceCount);
        if (inst.OverrideSliceCount > 0) table.OverridePool.Free(inst.OverrideSliceStart, inst.OverrideSliceCount);

        uint slot = inst.Handle.Index;
        table.InstanceGens[slot] = HandlePacking.NextGeneration(table.InstanceGens[slot]);
        table.InstanceSlotToDense[slot] = -1;
        table.InstanceFreeSlots[table.InstanceFreeCount++] = slot;

        int last = --table.InstanceCount;
        if (denseIdx != last)
        {
            table.Instances[denseIdx] = table.Instances[last];
            table.InstanceSlotToDense[table.Instances[denseIdx].Handle.Index] = denseIdx;
        }
    }

    public static void RemoveByEntity(ref Rig2DTable table, EntityId entity)
    {
        for (int i = table.InstanceCount - 1; i >= 0; i--)
        {
            if (table.Instances[i].Entity == entity)
                RemoveInstanceDenseAt(ref table, i);
        }
    }

    public static void RemoveByScene(ref Rig2DTable table, SceneId scene)
    {
        for (int i = table.InstanceCount - 1; i >= 0; i--)
        {
            if (table.Instances[i].OwnerScene == scene)
                RemoveInstanceDenseAt(ref table, i);
        }
    }

    public static Rig2DInstanceHandle FindInstanceByEntity(in Rig2DTable table, EntityId entity)
    {
        for (int i = 0; i < table.InstanceCount; i++)
            if (table.Instances[i].Entity == entity) return table.Instances[i].Handle;
        return Rig2DInstanceHandle.Invalid;
    }

    // =========================================================================
    // Playback & Runtime Control APIs (Zero-GC)
    // =========================================================================

    public static bool SetAnimation(
        ref Rig2DTable table,
        Rig2DInstanceHandle handle,
        ReadOnlySpan<char> clipName,
        int track = 0,
        bool loop = true,
        float mixDuration = 0.15f,
        float speed = 1f,
        float alpha = 1f,
        bool additive = false)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null) return false;
        int clipIdx = FindClipIndex(asset, clipName);
        if (clipIdx < 0) return false;
        return SetAnimation(ref table, handle, clipIdx, track, loop, mixDuration, speed, alpha, additive);
    }

    public static bool SetAnimation(
        ref Rig2DTable table,
        Rig2DInstanceHandle handle,
        int clipIndex,
        int track = 0,
        bool loop = true,
        float mixDuration = 0.15f,
        float speed = 1f,
        float alpha = 1f,
        bool additive = false)
    {
        if ((uint)track >= TrackCount) return false;
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null || (uint)clipIndex >= (uint)asset->ClipCount) return false;

        ref Rig2DTrackState tr = ref inst->Tracks[track];
        bool canMix = tr.Active != 0 && tr.ClipIndex >= 0 && tr.ClipIndex != clipIndex && float.IsFinite(mixDuration) && mixDuration > 1e-5f;
        if (canMix)
        {
            tr.FromClipIndex = tr.ClipIndex;
            tr.FromTime = tr.Time;
            tr.FromLoop = tr.Loop;
            tr.MixElapsed = 0f;
            tr.MixDuration = mixDuration;
        }
        else
        {
            tr.FromClipIndex = -1;
            tr.FromTime = 0f;
            tr.FromLoop = (byte)(loop ? 1 : 0);
            tr.MixElapsed = 0f;
            tr.MixDuration = 0f;
        }

        tr.FadeOutElapsed = 0f;
        tr.FadeOutDuration = 0f;
        tr.FadeOutStartAlpha = 0f;

        tr.ClipIndex = clipIndex;
        tr.Time = 0f;
        tr.Speed = float.IsFinite(speed) ? speed : 1f;
        tr.Alpha = DetMath.Clamp01(float.IsFinite(alpha) ? alpha : 1f);
        tr.Loop = (byte)(loop ? 1 : 0);
        tr.Additive = (byte)(additive ? 1 : 0);
        tr.Active = 1;
        tr.JustStarted = 1;
        return true;
    }

    public static void ClearTrack(ref Rig2DTable table, Rig2DInstanceHandle handle, int track, float fadeOutDuration = 0f)
    {
        if ((uint)track >= TrackCount) return;
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return;
        ref Rig2DTrackState tr = ref inst->Tracks[track];
        if (tr.Active == 0 || !float.IsFinite(fadeOutDuration) || fadeOutDuration <= 1e-5f || !float.IsFinite(tr.Alpha) || tr.Alpha <= 1e-5f)
        {
            tr = new Rig2DTrackState
            {
                ClipIndex = -1,
                FromClipIndex = -1,
                Speed = 1f,
                Alpha = 0f,
                Loop = 1,
                FromLoop = 1,
                Active = 0,
            };
            return;
        }

        tr.FadeOutStartAlpha = DetMath.Clamp01(tr.Alpha);
        tr.FadeOutElapsed = 0f;
        tr.FadeOutDuration = fadeOutDuration;
    }

    public static void SetTrackAlpha(ref Rig2DTable table, Rig2DInstanceHandle handle, int track, float alpha)
    {
        if ((uint)track >= TrackCount) return;
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return;
        ref Rig2DTrackState tr = ref inst->Tracks[track];
        tr.Alpha = DetMath.Clamp01(float.IsFinite(alpha) ? alpha : 0f);
        tr.FadeOutElapsed = 0f;
        tr.FadeOutDuration = 0f;
        tr.FadeOutStartAlpha = 0f;
    }

    public static void SetTrackSpeed(ref Rig2DTable table, Rig2DInstanceHandle handle, int track, float speed)
    {
        if ((uint)track >= TrackCount) return;
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return;
        inst->Tracks[track].Speed = float.IsFinite(speed) ? speed : 1f;
    }

    public static bool SetSkin(ref Rig2DTable table, Rig2DInstanceHandle handle, ReadOnlySpan<char> skinName)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null) return false;
        int idx = FindSkinIndex(asset, skinName);
        if (idx < 0) return false;
        return SetSkin(ref table, handle, idx);
    }

    public static bool SetSkin(ref Rig2DTable table, Rig2DInstanceHandle handle, int skinIndex)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null || skinIndex < -1 || skinIndex >= asset->SkinCount) return false;
        inst->ActiveSkinIndex = skinIndex;

        // Immediately refresh unoverridden slot attachments for instant visual feedback
        for (int s = 0; s < inst->SlotSliceCount; s++)
        {
            if (table.SlotOverrideAttachment[inst->SlotSliceStart + s] == -1)
            {
                table.SlotStates[inst->SlotSliceStart + s].ActiveAttachmentIndex =
                    FindAttachmentIndex(asset, s, asset->Slots[s].DefaultAttachmentName, skinIndex);
            }
        }
        return true;
    }

    public static bool SetSlotAttachment(ref Rig2DTable table, Rig2DInstanceHandle handle, ReadOnlySpan<char> slotName, ReadOnlySpan<char> attachmentName)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null) return false;
        int slotIdx = FindSlotIndex(asset, slotName);
        if (slotIdx < 0) return false;
        return SetSlotAttachment(ref table, handle, slotIdx, attachmentName);
    }

    public static bool SetSlotAttachment(ref Rig2DTable table, Rig2DInstanceHandle handle, int slotIndex, ReadOnlySpan<char> attachmentName)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null || (uint)slotIndex >= (uint)inst->SlotSliceCount) return false;

        if (attachmentName.IsEmpty)
        {
            table.SlotOverrideAttachment[inst->SlotSliceStart + slotIndex] = -2;
            table.SlotStates[inst->SlotSliceStart + slotIndex].ActiveAttachmentIndex = -1;
            return true;
        }

        int attIdx = FindAttachmentIndex(asset, slotIndex, StringId.Hash(attachmentName), inst->ActiveSkinIndex);
        if (attIdx < 0) return false;
        table.SlotOverrideAttachment[inst->SlotSliceStart + slotIndex] = attIdx;
        table.SlotStates[inst->SlotSliceStart + slotIndex].ActiveAttachmentIndex = attIdx;
        return true;
    }

    public static void ClearSlotAttachmentOverride(ref Rig2DTable table, Rig2DInstanceHandle handle, int slotIndex)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null || (uint)slotIndex >= (uint)inst->SlotSliceCount) return;
        table.SlotOverrideAttachment[inst->SlotSliceStart + slotIndex] = -1;
    }

    public static void ClearSlotAttachmentOverride(ref Rig2DTable table, Rig2DInstanceHandle handle, ReadOnlySpan<char> slotName)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null) return;
        int slotIdx = FindSlotIndex(asset, slotName);
        if (slotIdx >= 0) ClearSlotAttachmentOverride(ref table, handle, slotIdx);
    }

    public static void SetHitFlash(ref Rig2DTable table, Rig2DInstanceHandle handle, float amount)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst != null) inst->HitFlash = DetMath.Clamp01(amount);
    }

    public static void SetTint(ref Rig2DTable table, Rig2DInstanceHandle handle, ColorF tint)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst != null) inst->Tint = tint;
    }

    public static void SetDarkTint(ref Rig2DTable table, Rig2DInstanceHandle handle, ColorF darkTint)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst != null) inst->DarkTint = darkTint;
    }

    public static void SetFlip(ref Rig2DTable table, Rig2DInstanceHandle handle, bool flipX, bool flipY)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return;
        inst->FlipX = (byte)(flipX ? 1 : 0);
        inst->FlipY = (byte)(flipY ? 1 : 0);
    }

    public static bool SetBoneOverride(
        ref Rig2DTable table,
        Rig2DInstanceHandle handle,
        ReadOnlySpan<char> boneName,
        float addX = 0f,
        float addY = 0f,
        float addRotDeg = 0f,
        float weight = 1f)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null || inst->OverrideSliceCount <= 0) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null) return false;
        int boneIdx = FindBoneIndex(asset, boneName);
        if (boneIdx < 0) return false;

        return UpsertBoneOverride(ref table, inst, new Rig2DBoneOverride
        {
            BoneIndex = boneIdx,
            AddX = addX,
            AddY = addY,
            AddRotDeg = addRotDeg,
            Mode = Rig2DBoneOverrideMode.AdditiveLocal,
            Weight = DetMath.Clamp01(weight),
        });
    }

    public static bool SetIkTargetWorld(
        ref Rig2DTable table,
        Rig2DInstanceHandle handle,
        ReadOnlySpan<char> targetBoneName,
        in TransformTable transforms,
        Vec3 worldTarget,
        float weight = 1f)
    {
        _ = transforms; // Evaluated live against entity world transform inside StepAll
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null || inst->OverrideSliceCount <= 0) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null) return false;
        int boneIdx = FindBoneIndex(asset, targetBoneName);
        if (boneIdx < 0) return false;

        return UpsertBoneOverride(ref table, inst, new Rig2DBoneOverride
        {
            BoneIndex = boneIdx,
            SetWorldTargetX = worldTarget.X,
            SetWorldTargetY = worldTarget.Y,
            SetWorldTargetZ = worldTarget.Z,
            Mode = Rig2DBoneOverrideMode.WorldPositionTarget,
            Weight = DetMath.Clamp01(weight),
        });
    }

    private static bool UpsertBoneOverride(ref Rig2DTable table, Rig2DInstanceData* inst, in Rig2DBoneOverride ov)
    {
        int start = inst->OverrideSliceStart;
        int count = inst->OverrideSliceCount;
        int freeSlot = -1;
        for (int i = 0; i < count; i++)
        {
            ref Rig2DBoneOverride cur = ref table.BoneOverrides[start + i];
            if (cur.Mode != Rig2DBoneOverrideMode.None && cur.BoneIndex == ov.BoneIndex)
            {
                cur = ov;
                return true;
            }
            if (cur.Mode == Rig2DBoneOverrideMode.None && freeSlot < 0)
                freeSlot = i;
        }
        if (freeSlot < 0) return false;
        table.BoneOverrides[start + freeSlot] = ov;
        return true;
    }

    public static void ClearBoneOverrides(ref Rig2DTable table, Rig2DInstanceHandle handle)
    {
        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return;
        for (int i = 0; i < inst->OverrideSliceCount; i++)
            table.BoneOverrides[inst->OverrideSliceStart + i] = new Rig2DBoneOverride { BoneIndex = -1, Mode = Rig2DBoneOverrideMode.None };
    }

    /// <summary>
    /// Queries a posed bone's world-space socket position, rotation (radians), and scale.
    /// Zero GC allocations.
    /// </summary>
    public static bool TryGetBoneWorldPose(
        in Rig2DTable table,
        Rig2DInstanceHandle handle,
        ReadOnlySpan<char> boneName,
        in TransformTable transforms,
        out Vec3 worldPos,
        out float worldRotRad,
        out Vec2 worldScale)
    {
        worldPos = Vec3.Zero;
        worldRotRad = 0f;
        worldScale = Vec2.One;

        Rig2DInstanceData* inst = TryGetInstance(in table, handle);
        if (inst == null) return false;
        Rig2DAssetData* asset = TryGetAsset(in table, inst->Asset);
        if (asset == null) return false;
        int boneIdx = FindBoneIndex(asset, boneName);
        if (boneIdx < 0 || boneIdx >= inst->BoneSliceCount) return false;

        ref readonly Rig2DAffine2D bw = ref table.BoneWorld[inst->BoneSliceStart + boneIdx];
        Mat4 entMat = TransformSystem.GetWorldMatrix(in transforms, inst->Entity);

        float invPpu = asset->PixelsPerUnit > 1e-5f ? (1f / asset->PixelsPerUnit) : 0.01f;
        float sx = inst->Scale.X * (inst->FlipX != 0 ? -1f : 1f);
        float sy = inst->Scale.Y * (inst->FlipY != 0 ? -1f : 1f);

        float lx = (bw.Tx - asset->OriginOffset.X) * invPpu * sx;
        float ly = (bw.Ty - asset->OriginOffset.Y) * invPpu * sy;
        worldPos = Mat4.TransformPoint(in entMat, new Vec3(lx, ly, 0f));

        Vec3 ax = Mat4.TransformVector(in entMat, new Vec3(bw.A * sx, bw.B * sy, 0f));
        Vec3 ay = Mat4.TransformVector(in entMat, new Vec3(bw.C * sx, bw.D * sy, 0f));
        worldRotRad = DetMath.Atan2(ax.Y, ax.X);
        worldScale = new Vec2(ax.Length(), ay.Length());
        return true;
    }

    /// <summary>
    /// Returns all animation events fired during the most recent Rig2DSystem.StepAll tick (Zero-GC).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<Rig2DFiredEvent> GetFiredEvents(in Rig2DTable table)
        => table.FiredEvents == null || table.FiredEventCount <= 0
            ? ReadOnlySpan<Rig2DFiredEvent>.Empty
            : new ReadOnlySpan<Rig2DFiredEvent>(table.FiredEvents, table.FiredEventCount);

    // =========================================================================
    // Core Deterministic Frame Solver (Zero-GC, Headless-Safe)
    // =========================================================================

    public static void StepAll(
        ref Rig2DTable table,
        in TransformTable transforms,
        float dt,
        ref LinearArena frameArena,
        ref LinearArena scratchArena)
    {
        if (!float.IsFinite(dt) || dt < 0f)
            dt = 0f;

        table.FiredEventCount = 0;

        // 1. Pre-count exact FFD deform vertices needed this frame across all instances
        int totalDeformVerts = CountFrameDeformVertices(in table);
        if (totalDeformVerts > 0 && (ulong)totalDeformVerts * (ulong)sizeof(Vec2) + 64UL <= frameArena.Remaining)
        {
            table.FrameDeformDeltas = frameArena.AllocSlice<Vec2>(totalDeformVerts, 64, zeroed: true);
            table.FrameDeformArenaBase = frameArena.BasePtr;
            table.FrameDeformArenaReset = frameArena.ResetCount;
        }
        else
        {
            table.FrameDeformDeltas = default;
            table.FrameDeformArenaBase = null;
            table.FrameDeformArenaReset = 0;
        }

        int deformCursor = 0;

        // 2. Solve each live instance deterministically
        for (int ii = 0; ii < table.InstanceCount; ii++)
        {
            ref Rig2DInstanceData inst = ref table.Instances[ii];
            Rig2DAssetData* asset = TryGetAsset(in table, inst.Asset);
            if (asset == null || asset->BoneCount <= 0) continue;

            StepSingleInstance(ref table, ref inst, asset, in transforms, dt, ref deformCursor, ref scratchArena);
        }
    }

    private static int CountFrameDeformVertices(in Rig2DTable table)
    {
        long total = 0;
        for (int ii = 0; ii < table.InstanceCount; ii++)
        {
            ref readonly Rig2DInstanceData inst = ref table.Instances[ii];
            Rig2DAssetData* asset = TryGetAsset(in table, inst.Asset);
            if (asset == null || asset->ClipCount == 0 || asset->DeformPoolCount == 0) continue;

            for (int t = 0; t < TrackCount; t++)
            {
                Rig2DTrackState tr = inst.Tracks[t];
                if (tr.Active == 0 || tr.Alpha <= 1e-5f) continue;

                if ((uint)tr.ClipIndex < (uint)asset->ClipCount)
                    total += CountClipDeformVerts(asset, tr.ClipIndex);
                if (tr.FromClipIndex >= 0 && (uint)tr.FromClipIndex < (uint)asset->ClipCount && tr.MixDuration > 0f)
                    total += CountClipDeformVerts(asset, tr.FromClipIndex);
            }
        }
        return total > int.MaxValue ? 0 : (int)total;
    }

    private static long CountClipDeformVerts(Rig2DAssetData* asset, int clipIdx)
    {
        long sum = 0;
        ref readonly Rig2DAnimClipData clip = ref asset->Clips[clipIdx];
        for (int i = 0; i < clip.TimelineCount; i++)
        {
            ref readonly Rig2DTimelineData tl = ref asset->Timelines[clip.TimelineStart + i];
            if (tl.Target == Rig2DTimelineTarget.MeshDeform && (uint)tl.TargetIndex < (uint)asset->AttachmentCount)
                sum += asset->Attachments[tl.TargetIndex].VertexCount;
        }
        return sum;
    }

    private static void StepSingleInstance(
        ref Rig2DTable table,
        ref Rig2DInstanceData inst,
        Rig2DAssetData* asset,
        in TransformTable transforms,
        float dt,
        ref int deformCursor,
        ref LinearArena scratchArena)
    {
        int bc = asset->BoneCount;
        int sc = asset->SlotCount;
        int cc = asset->ConstraintOrderCount;
        int ikc = asset->IkConstraintCount;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            BoneLocalPose* localPose = scratchArena.AllocUninitialized<BoneLocalPose>(bc, 16);
            ClipPoseSample* sampleA = scratchArena.AllocUninitialized<ClipPoseSample>(bc, 16);
            ClipPoseSample* sampleB = scratchArena.AllocUninitialized<ClipPoseSample>(bc, 16);
            byte* dirtyBones = scratchArena.Alloc<byte>(bc, 16);

            float* constraintMix = cc > 0 ? scratchArena.AllocUninitialized<float>(cc, 16) : null;
            float* ikSoftness = ikc > 0 ? scratchArena.AllocUninitialized<float>(ikc, 16) : null;
            sbyte* ikBend = ikc > 0 ? scratchArena.AllocUninitialized<sbyte>(ikc, 16) : null;
            byte* seqKeyed = sc > 0 ? scratchArena.Alloc<byte>(sc, 16) : null;

            // 1. Reset working pose to setup pose
            for (int b = 0; b < bc; b++)
            {
                ref readonly Rig2DBoneSetupData bs = ref asset->Bones[b];
                localPose[b] = new BoneLocalPose
                {
                    X = bs.X,
                    Y = bs.Y,
                    RotDeg = bs.RotationDeg,
                    ScaleX = bs.ScaleX,
                    ScaleY = bs.ScaleY,
                    ShearX = bs.ShearX,
                    ShearY = bs.ShearY,
                };
            }

            Rig2DRuntimeSlotState* slotStates = sc > 0 ? table.SlotStates + inst.SlotSliceStart : null;
            int* drawOrder = sc > 0 ? table.DrawOrder + inst.SlotSliceStart : null;
            for (int s = 0; s < sc; s++)
            {
                ref readonly Rig2DSlotSetupData ss = ref asset->Slots[s];
                slotStates[s].ActiveAttachmentIndex = FindAttachmentIndex(asset, s, ss.DefaultAttachmentName, inst.ActiveSkinIndex);
                slotStates[s].LightColor = ss.LightColor;
                slotStates[s].DarkColor = ss.DarkColor;
                slotStates[s].DeformVertexOffset = 0;
                slotStates[s].DeformVertexCount = 0;
                drawOrder[s] = s;
            }

            for (int ci = 0; ci < cc; ci++)
            {
                ref readonly Rig2DConstraintOrderItem ord = ref asset->ConstraintOrder[ci];
                constraintMix[ci] = ord.Kind switch
                {
                    Rig2DConstraintKind.Ik => asset->IkConstraints[ord.Index].Mix,
                    Rig2DConstraintKind.Transform => 1f,
                    Rig2DConstraintKind.Spring => asset->SpringConstraints[ord.Index].Mix,
                    _ => 1f,
                };
            }
            for (int i = 0; i < ikc; i++)
            {
                ikSoftness[i] = asset->IkConstraints[i].Softness;
                ikBend[i] = asset->IkConstraints[i].BendDirection >= 0 ? (sbyte)1 : (sbyte)-1;
            }

            // 2. Advance & sample tracks 0..7
            for (int ti = 0; ti < TrackCount; ti++)
            {
                ref Rig2DTrackState tr = ref inst.Tracks[ti];
                if (tr.Active == 0) continue;
                if ((uint)tr.ClipIndex >= (uint)asset->ClipCount)
                {
                    tr.Active = 0;
                    continue;
                }

                if (tr.FadeOutDuration > 1e-5f)
                {
                    if (!float.IsFinite(tr.FadeOutElapsed) || tr.FadeOutElapsed < 0f) tr.FadeOutElapsed = 0f;
                    if (!float.IsFinite(tr.FadeOutStartAlpha)) tr.FadeOutStartAlpha = DetMath.Clamp01(tr.Alpha);
                    if (dt > 0f)
                        tr.FadeOutElapsed += dt;
                    if (tr.FadeOutElapsed >= tr.FadeOutDuration)
                    {
                        tr = new Rig2DTrackState
                        {
                            ClipIndex = -1,
                            FromClipIndex = -1,
                            Speed = 1f,
                            Alpha = 0f,
                            Loop = 1,
                            FromLoop = 1,
                            Active = 0,
                        };
                        continue;
                    }
                    float fade = 1f - DetMath.Clamp01(tr.FadeOutElapsed / tr.FadeOutDuration);
                    tr.Alpha = tr.FadeOutStartAlpha * fade;
                }

                ref readonly Rig2DAnimClipData clip = ref asset->Clips[tr.ClipIndex];
                if (!float.IsFinite(tr.Time)) tr.Time = 0f;
                if (!float.IsFinite(tr.FromTime)) tr.FromTime = 0f;

                float prevTime = tr.Time;
                bool justStarted = tr.JustStarted != 0;
                tr.JustStarted = 0;

                if (dt > 0f && float.IsFinite(tr.Speed) && clip.Duration > 0f)
                {
                    AdvanceTrackTimeAndFireEvents(ref table, in inst, asset, in clip, ref tr.Time, prevTime, dt * tr.Speed, tr.Loop != 0, justStarted);
                }
                else if (justStarted && clip.EventCount > 0)
                {
                    FireEventsInRange(ref table, in inst, asset, in clip, 0f, 0f, inclusiveStart: true);
                }

                bool hasCrossfade = tr.FromClipIndex >= 0 && (uint)tr.FromClipIndex < (uint)asset->ClipCount && tr.MixDuration > 1e-5f;
                float mixT = 1f;
                if (hasCrossfade)
                {
                    ref readonly Rig2DAnimClipData fromClip = ref asset->Clips[tr.FromClipIndex];
                    if (dt > 0f)
                    {
                        if (fromClip.Duration > 0f && float.IsFinite(tr.Speed))
                        {
                            tr.FromTime += dt * tr.Speed;
                            if (!float.IsFinite(tr.FromTime)) tr.FromTime = 0f;
                            if (tr.FromLoop != 0)
                            {
                                tr.FromTime %= fromClip.Duration;
                                if (tr.FromTime < 0f) tr.FromTime += fromClip.Duration;
                            }
                            else
                            {
                                tr.FromTime = DetMath.Clamp(tr.FromTime, 0f, fromClip.Duration);
                            }
                        }
                        tr.MixElapsed += dt;
                    }
                    mixT = DetMath.Clamp01(tr.MixElapsed / tr.MixDuration);
                    if (mixT >= 1f)
                    {
                        tr.FromClipIndex = -1;
                        tr.MixDuration = 0f;
                        hasCrossfade = false;
                    }
                }

                float alpha = DetMath.Clamp01(tr.Alpha);
                if (alpha <= 1e-5f) continue;

                bool additive = tr.Additive != 0;

                if (hasCrossfade)
                {
                    ref readonly Rig2DAnimClipData fromClip = ref asset->Clips[tr.FromClipIndex];
                    SampleClipBones(asset, in fromClip, tr.FromTime, sampleA, bc);
                    SampleClipBones(asset, in clip, tr.Time, sampleB, bc);
                    ApplyCrossfadedBones(asset, localPose, sampleA, sampleB, bc, mixT, alpha, additive);

                    // Non-bone timelines: crossfade continuous channels in scratch first (no setup bleed), step discrete at mixT >= 0.5
                    ApplyCrossfadedNonBoneTimelines(
                        ref table, in inst, asset,
                        in fromClip, tr.FromTime,
                        in clip, tr.Time,
                        slotStates, drawOrder, seqKeyed, constraintMix, ikSoftness, ikBend,
                        mixT, alpha, additive, ref deformCursor, ref scratchArena);
                }
                else
                {
                    SampleClipBones(asset, in clip, tr.Time, sampleB, bc);
                    ApplyClipBones(asset, localPose, sampleB, bc, alpha, additive);
                    ApplyClipNonBoneTimelines(
                        ref table, in inst, asset, in clip, tr.Time,
                        slotStates, drawOrder, seqKeyed, constraintMix, ikSoftness, ikBend,
                        alpha, additive, applyDiscrete: true, ref deformCursor);
                }
            }

            // 3. Apply manual slot overrides & advance unkeyed Sequence slots
            for (int s = 0; s < sc; s++)
            {
                int ov = table.SlotOverrideAttachment[inst.SlotSliceStart + s];
                if (ov == -2)
                    slotStates[s].ActiveAttachmentIndex = -1;
                else if (ov >= 0 && ov < asset->AttachmentCount)
                    slotStates[s].ActiveAttachmentIndex = ov;

                int attIdx = slotStates[s].ActiveAttachmentIndex;
                if (attIdx >= 0 && attIdx < asset->AttachmentCount && asset->Attachments[attIdx].Kind == Rig2DAttachmentKind.Sequence)
                {
                    ref readonly Rig2DAttachmentData att = ref asset->Attachments[attIdx];
                    if (seqKeyed[s] == 0)
                    {
                        if (dt > 0f && inst.PosedOnce != 0)
                            slotStates[s].SequenceTime += dt;
                        slotStates[s].SequenceFrame = EvaluateSequenceFrame(in att, slotStates[s].SequenceTime);
                    }
                }
            }

            // 4. Apply AdditiveLocal bone overrides
            for (int ov = 0; ov < inst.OverrideSliceCount; ov++)
            {
                ref readonly Rig2DBoneOverride bo = ref table.BoneOverrides[inst.OverrideSliceStart + ov];
                if (bo.Mode != Rig2DBoneOverrideMode.AdditiveLocal) continue;
                if ((uint)bo.BoneIndex >= (uint)bc || bo.Weight <= 1e-5f) continue;
                float w = DetMath.Clamp01(bo.Weight);
                localPose[bo.BoneIndex].X += bo.AddX * w;
                localPose[bo.BoneIndex].Y += bo.AddY * w;
                localPose[bo.BoneIndex].RotDeg += bo.AddRotDeg * w;
            }

            // 5. Propagate 2D bone hierarchy
            Rig2DAffine2D* worldBones = table.BoneWorld + inst.BoneSliceStart;
            for (int b = 0; b < bc; b++)
                ComputeBoneWorld(asset, localPose, worldBones, b);

            // 6. Apply WorldPositionTarget overrides (e.g. world-space IK targets) BEFORE constraints run
            Mat4 entMat = TransformSystem.GetWorldMatrix(in transforms, inst.Entity);
            Vec3 entWorldPos = new Vec3(entMat.C3.X, entMat.C3.Y, entMat.C3.Z);
            bool hasWorldOverride = false;
            for (int ov = 0; ov < inst.OverrideSliceCount; ov++)
            {
                if (table.BoneOverrides[inst.OverrideSliceStart + ov].Mode == Rig2DBoneOverrideMode.WorldPositionTarget)
                {
                    hasWorldOverride = true;
                    break;
                }
            }
            if (hasWorldOverride && Mat4.TryInvert(in entMat, out Mat4 invEntMat))
            {
                float ppu = asset->PixelsPerUnit > 1e-5f ? asset->PixelsPerUnit : 100f;
                float sx = inst.Scale.X * (inst.FlipX != 0 ? -1f : 1f);
                float sy = inst.Scale.Y * (inst.FlipY != 0 ? -1f : 1f);
                float invSx = DetMath.Abs(sx) > 1e-5f ? (1f / sx) : 1f;
                float invSy = DetMath.Abs(sy) > 1e-5f ? (1f / sy) : 1f;

                for (int ov = 0; ov < inst.OverrideSliceCount; ov++)
                {
                    ref readonly Rig2DBoneOverride bo = ref table.BoneOverrides[inst.OverrideSliceStart + ov];
                    if (bo.Mode != Rig2DBoneOverrideMode.WorldPositionTarget) continue;
                    if ((uint)bo.BoneIndex >= (uint)bc || bo.Weight <= 1e-5f) continue;

                    Vec3 local3 = Mat4.TransformPoint(in invEntMat, new Vec3(bo.SetWorldTargetX, bo.SetWorldTargetY, bo.SetWorldTargetZ));
                    float skelX = local3.X * ppu * invSx + asset->OriginOffset.X;
                    float skelY = local3.Y * ppu * invSy + asset->OriginOffset.Y;
                    float w = DetMath.Clamp01(bo.Weight);

                    int bi = bo.BoneIndex;
                    float targetTx = DetMath.Lerp(worldBones[bi].Tx, skelX, w);
                    float targetTy = DetMath.Lerp(worldBones[bi].Ty, skelY, w);

                    int parentIdx = asset->Bones[bi].ParentIndex;
                    if (parentIdx >= 0 && parentIdx < bc)
                    {
                        Rig2DAffine2D invParent = Rig2DAffine2D.Invert(in worldBones[parentIdx]);
                        Vec2 lp = Rig2DAffine2D.TransformPoint(in invParent, new Vec2(targetTx, targetTy));
                        localPose[bi].X = lp.X;
                        localPose[bi].Y = lp.Y;
                    }
                    else
                    {
                        localPose[bi].X = targetTx;
                        localPose[bi].Y = targetTy;
                    }
                    UpdateBoneSubtree(asset, localPose, worldBones, dirtyBones, bi);
                }
            }

            // Compute entity displacement in skeleton pixel space for spring inertia (accounting for Flip & Scale)
            Vec2 entDeltaSkel = Vec2.Zero;
            if (inst.HasPrevEntityPos != 0 && dt > 1e-6f)
            {
                Vec3 worldDelta = entWorldPos - inst.PrevEntityPos;
                float ppu = asset->PixelsPerUnit > 1e-5f ? asset->PixelsPerUnit : 100f;
                float sx = inst.Scale.X * (inst.FlipX != 0 ? -1f : 1f);
                float sy = inst.Scale.Y * (inst.FlipY != 0 ? -1f : 1f);
                float invSx = (float.IsFinite(sx) && DetMath.Abs(sx) > 1e-5f) ? (1f / sx) : 1f;
                float invSy = (float.IsFinite(sy) && DetMath.Abs(sy) > 1e-5f) ? (1f / sy) : 1f;
                float dxSkel = worldDelta.X * ppu * invSx;
                float dySkel = worldDelta.Y * ppu * invSy;
                entDeltaSkel = new Vec2(float.IsFinite(dxSkel) ? dxSkel : 0f, float.IsFinite(dySkel) ? dySkel : 0f);
            }
            inst.PrevEntityPos = entWorldPos;
            inst.HasPrevEntityPos = 1;

            // 7. Solve constraints in ConstraintOrder
            for (int ci = 0; ci < cc; ci++)
            {
                ref readonly Rig2DConstraintOrderItem item = ref asset->ConstraintOrder[ci];
                float mix = DetMath.Clamp01(constraintMix[ci]);
                switch (item.Kind)
                {
                    case Rig2DConstraintKind.Ik:
                        if ((uint)item.Index < (uint)asset->IkConstraintCount && mix > 1e-5f)
                        {
                            ref readonly Rig2DIkConstraintData ik = ref asset->IkConstraints[item.Index];
                            SolveIkConstraint(asset, in ik, mix, ikSoftness[item.Index], ikBend[item.Index], localPose, worldBones, dirtyBones);
                        }
                        break;

                    case Rig2DConstraintKind.Transform:
                        if ((uint)item.Index < (uint)asset->TransformConstraintCount)
                        {
                            ref readonly Rig2DTransformConstraintData tc = ref asset->TransformConstraints[item.Index];
                            SolveTransformConstraint(asset, in tc, mix, localPose, worldBones, dirtyBones);
                        }
                        break;

                    case Rig2DConstraintKind.Spring:
                        if ((uint)item.Index < (uint)asset->SpringConstraintCount && item.Index < inst.SpringSliceCount)
                        {
                            ref readonly Rig2DSpringConstraintData sp = ref asset->SpringConstraints[item.Index];
                            ref Rig2DSpringRuntimeState st = ref table.SpringStates[inst.SpringSliceStart + item.Index];
                            SolveSpringConstraint(asset, in sp, ref st, mix, dt, entDeltaSkel, localPose, worldBones, dirtyBones);
                        }
                        break;
                }
            }

            inst.PosedOnce = 1;
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    // =========================================================================
    // Bone Hierarchy Computation (with all 4 Rig2DInheritModes)
    // =========================================================================

    /// <summary>
    /// Computes a child bone's 2D world affine transform from its parent's world transform and inherit mode.
    /// Shared across Rig2DSystem (runtime solver), Rig2DCompiler (bind pose), and BringRig2D AutoMesher.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rig2DAffine2D ComputeBoneWorldFromParent(
        in Rig2DAffine2D parentWorld,
        Rig2DInheritMode inherit,
        float x,
        float y,
        float rotDeg,
        float sx,
        float sy,
        float shearX,
        float shearY)
    {
        switch (inherit)
        {
            case Rig2DInheritMode.OnlyTranslation:
            {
                float wx = parentWorld.A * x + parentWorld.C * y + parentWorld.Tx;
                float wy = parentWorld.B * x + parentWorld.D * y + parentWorld.Ty;
                return Rig2DAffine2D.FromTrsShear(wx, wy, rotDeg, sx, sy, shearX, shearY);
            }
            case Rig2DInheritMode.NoRotation:
            {
                float wx = parentWorld.A * x + parentWorld.C * y + parentWorld.Tx;
                float wy = parentWorld.B * x + parentWorld.D * y + parentWorld.Ty;
                float psx = parentWorld.WorldScaleX;
                float psy = parentWorld.WorldScaleY * (parentWorld.Determinant < 0f ? -1f : 1f);
                return Rig2DAffine2D.FromTrsShear(wx, wy, rotDeg, sx * psx, sy * psy, shearX, shearY);
            }
            case Rig2DInheritMode.NoScale:
            {
                float wx = parentWorld.A * x + parentWorld.C * y + parentWorld.Tx;
                float wy = parentWorld.B * x + parentWorld.D * y + parentWorld.Ty;
                float pRotDeg = parentWorld.WorldRotationRad * DetMath.Rad2Deg;
                float signY = parentWorld.Determinant < 0f ? -1f : 1f;
                return Rig2DAffine2D.FromTrsShear(wx, wy, pRotDeg + rotDeg, sx, sy * signY, shearX, shearY);
            }
            default:
            {
                Rig2DAffine2D local = Rig2DAffine2D.FromTrsShear(x, y, rotDeg, sx, sy, shearX, shearY);
                return Rig2DAffine2D.Multiply(in parentWorld, in local);
            }
        }
    }

    private static void ComputeSetupWorldPose(Rig2DAssetData* asset, Rig2DAffine2D* worldBones)
    {
        for (int b = 0; b < asset->BoneCount; b++)
        {
            ref readonly Rig2DBoneSetupData bs = ref asset->Bones[b];
            var pose = new BoneLocalPose
            {
                X = bs.X, Y = bs.Y, RotDeg = bs.RotationDeg,
                ScaleX = bs.ScaleX, ScaleY = bs.ScaleY,
                ShearX = bs.ShearX, ShearY = bs.ShearY,
            };
            ComputeBoneWorldSingle(in bs, in pose, worldBones, b);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeBoneWorld(Rig2DAssetData* asset, BoneLocalPose* localPose, Rig2DAffine2D* worldBones, int b)
        => ComputeBoneWorldSingle(in asset->Bones[b], in localPose[b], worldBones, b);

    private static void ComputeBoneWorldSingle(in Rig2DBoneSetupData bs, in BoneLocalPose lp, Rig2DAffine2D* worldBones, int b)
    {
        int p = bs.ParentIndex;
        if (p < 0 || p >= b)
        {
            worldBones[b] = Rig2DAffine2D.FromTrsShear(lp.X, lp.Y, lp.RotDeg, lp.ScaleX, lp.ScaleY, lp.ShearX, lp.ShearY);
            return;
        }

        worldBones[b] = ComputeBoneWorldFromParent(in worldBones[p], bs.Inherit, lp.X, lp.Y, lp.RotDeg, lp.ScaleX, lp.ScaleY, lp.ShearX, lp.ShearY);
    }

    private static void UpdateBoneSubtree(Rig2DAssetData* asset, BoneLocalPose* localPose, Rig2DAffine2D* worldBones, byte* dirty, int rootBone)
    {
        int bc = asset->BoneCount;
        if ((uint)rootBone >= (uint)bc) return;
        for (int i = rootBone; i < bc; i++) dirty[i] = 0;
        dirty[rootBone] = 1;
        ComputeBoneWorld(asset, localPose, worldBones, rootBone);
        for (int i = rootBone + 1; i < bc; i++)
        {
            int p = asset->Bones[i].ParentIndex;
            if (p >= rootBone && dirty[p] != 0)
            {
                dirty[i] = 1;
                ComputeBoneWorld(asset, localPose, worldBones, i);
            }
        }
    }

    // =========================================================================
    // Timeline Sampling, Curves & Event Detection
    // =========================================================================

    private static void AdvanceTrackTimeAndFireEvents(
        ref Rig2DTable table,
        in Rig2DInstanceData inst,
        Rig2DAssetData* asset,
        in Rig2DAnimClipData clip,
        ref float trackTime,
        float prevTime,
        float delta,
        bool loop,
        bool justStarted)
    {
        float dur = clip.Duration;
        if (dur <= 0f) return;

        if (justStarted && delta >= 0f)
            FireEventsInRange(ref table, in inst, asset, in clip, 0f, 0f, inclusiveStart: true);

        float nextUnwrapped = prevTime + delta;
        if (!float.IsFinite(nextUnwrapped)) nextUnwrapped = 0f;

        if (delta >= 0f)
        {
            if (loop)
            {
                if (nextUnwrapped < dur)
                {
                    FireEventsInRange(ref table, in inst, asset, in clip, prevTime, nextUnwrapped, inclusiveStart: false);
                    trackTime = nextUnwrapped;
                }
                else
                {
                    // Fire tail (prevTime, dur], then wrap and fire [0, wrapped]
                    FireEventsInRange(ref table, in inst, asset, in clip, prevTime, dur, inclusiveStart: false);
                    float wrapped = nextUnwrapped % dur;
                    if (wrapped < 0f) wrapped += dur;
                    FireEventsInRange(ref table, in inst, asset, in clip, 0f, wrapped, inclusiveStart: true);
                    trackTime = wrapped;
                }
            }
            else
            {
                float clamped = DetMath.Clamp(nextUnwrapped, 0f, dur);
                if (clamped > prevTime)
                    FireEventsInRange(ref table, in inst, asset, in clip, prevTime, clamped, inclusiveStart: false);
                trackTime = clamped;
            }
        }
        else
        {
            // Reverse playback
            if (loop)
            {
                if (nextUnwrapped >= 0f)
                {
                    FireEventsInRangeReverse(ref table, in inst, asset, in clip, prevTime, nextUnwrapped);
                    trackTime = nextUnwrapped;
                }
                else
                {
                    FireEventsInRangeReverse(ref table, in inst, asset, in clip, prevTime, 0f);
                    float wrapped = ((nextUnwrapped % dur) + dur) % dur;
                    FireEventsInRangeReverse(ref table, in inst, asset, in clip, dur, wrapped);
                    trackTime = wrapped;
                }
            }
            else
            {
                float clamped = DetMath.Clamp(nextUnwrapped, 0f, dur);
                if (clamped < prevTime)
                    FireEventsInRangeReverse(ref table, in inst, asset, in clip, prevTime, clamped);
                trackTime = clamped;
            }
        }
    }

    private static void FireEventsInRange(
        ref Rig2DTable table,
        in Rig2DInstanceData inst,
        Rig2DAssetData* asset,
        in Rig2DAnimClipData clip,
        float startTime,
        float endTime,
        bool inclusiveStart)
    {
        if (clip.EventCount <= 0 || asset->EventKeys == null || table.FiredEvents == null) return;
        for (int i = 0; i < clip.EventCount; i++)
        {
            ref readonly Rig2DEventKeyData ev = ref asset->EventKeys[clip.EventStart + i];
            bool hit = inclusiveStart
                ? (ev.Time >= startTime && ev.Time <= endTime)
                : (ev.Time > startTime && ev.Time <= endTime);
            if (!hit) continue;
            if (table.FiredEventCount >= table.MaxFiredEvents) return;
            table.FiredEvents[table.FiredEventCount++] = new Rig2DFiredEvent
            {
                Instance = inst.Handle,
                Entity = inst.Entity,
                EventName = ev.EventName,
                IntVal = ev.IntVal,
                FloatVal = ev.FloatVal,
                StringVal = ev.StringVal,
                Time = ev.Time,
            };
        }
    }

    private static void FireEventsInRangeReverse(
        ref Rig2DTable table,
        in Rig2DInstanceData inst,
        Rig2DAssetData* asset,
        in Rig2DAnimClipData clip,
        float highTime,
        float lowTime)
    {
        if (clip.EventCount <= 0 || asset->EventKeys == null || table.FiredEvents == null) return;
        for (int i = clip.EventCount - 1; i >= 0; i--)
        {
            ref readonly Rig2DEventKeyData ev = ref asset->EventKeys[clip.EventStart + i];
            if (ev.Time < highTime && ev.Time >= lowTime)
            {
                if (table.FiredEventCount >= table.MaxFiredEvents) return;
                table.FiredEvents[table.FiredEventCount++] = new Rig2DFiredEvent
                {
                    Instance = inst.Handle,
                    Entity = inst.Entity,
                    EventName = ev.EventName,
                    IntVal = ev.IntVal,
                    FloatVal = ev.FloatVal,
                    StringVal = ev.StringVal,
                    Time = ev.Time,
                };
            }
        }
    }

    private static void SampleClipBones(Rig2DAssetData* asset, in Rig2DAnimClipData clip, float time, ClipPoseSample* dst, int boneCount)
    {
        for (int b = 0; b < boneCount; b++)
        {
            dst[b] = new ClipPoseSample { ScaleX = 1f, ScaleY = 1f };
        }

        if (clip.TimelineCount <= 0 || asset->Timelines == null || asset->Keyframes == null) return;

        for (int i = 0; i < clip.TimelineCount; i++)
        {
            ref readonly Rig2DTimelineData tl = ref asset->Timelines[clip.TimelineStart + i];
            if (tl.KeyCount <= 0) continue;
            int bi = tl.TargetIndex;
            if ((uint)bi >= (uint)boneCount) continue;

            switch (tl.Target)
            {
                case Rig2DTimelineTarget.BoneRotate:
                {
                    SampleScalar(asset->Keyframes + tl.KeyStart, tl.KeyCount, time, out float rot);
                    dst[bi].RotDeg = rot;
                    dst[bi].HasRotate = 1;
                    break;
                }
                case Rig2DTimelineTarget.BoneTranslate:
                {
                    SampleVec2(asset->Keyframes + tl.KeyStart, tl.KeyCount, time, out float tx, out float ty);
                    dst[bi].X = tx;
                    dst[bi].Y = ty;
                    dst[bi].HasTranslate = 1;
                    break;
                }
                case Rig2DTimelineTarget.BoneScale:
                {
                    SampleVec2(asset->Keyframes + tl.KeyStart, tl.KeyCount, time, out float sx, out float sy);
                    dst[bi].ScaleX = sx;
                    dst[bi].ScaleY = sy;
                    dst[bi].HasScale = 1;
                    break;
                }
                case Rig2DTimelineTarget.BoneShear:
                {
                    SampleVec2(asset->Keyframes + tl.KeyStart, tl.KeyCount, time, out float shx, out float shy);
                    dst[bi].ShearX = shx;
                    dst[bi].ShearY = shy;
                    dst[bi].HasShear = 1;
                    break;
                }
            }
        }
    }

    private static void ApplyClipBones(
        Rig2DAssetData* asset,
        BoneLocalPose* pose,
        ClipPoseSample* sample,
        int boneCount,
        float alpha,
        bool additive)
    {
        for (int b = 0; b < boneCount; b++)
        {
            ref readonly Rig2DBoneSetupData bs = ref asset->Bones[b];
            ref readonly ClipPoseSample s = ref sample[b];
            ref BoneLocalPose p = ref pose[b];

            if (s.HasTranslate != 0)
            {
                if (additive)
                {
                    p.X += s.X * alpha;
                    p.Y += s.Y * alpha;
                }
                else
                {
                    p.X = DetMath.Lerp(p.X, bs.X + s.X, alpha);
                    p.Y = DetMath.Lerp(p.Y, bs.Y + s.Y, alpha);
                }
            }
            if (s.HasRotate != 0)
            {
                if (additive)
                {
                    p.RotDeg += s.RotDeg * alpha;
                }
                else
                {
                    float target = bs.RotationDeg + s.RotDeg;
                    p.RotDeg += WrapAngleDeg(target - p.RotDeg) * alpha;
                }
            }
            if (s.HasScale != 0)
            {
                if (additive)
                {
                    p.ScaleX *= 1f + (s.ScaleX - 1f) * alpha;
                    p.ScaleY *= 1f + (s.ScaleY - 1f) * alpha;
                }
                else
                {
                    p.ScaleX = DetMath.Lerp(p.ScaleX, bs.ScaleX * s.ScaleX, alpha);
                    p.ScaleY = DetMath.Lerp(p.ScaleY, bs.ScaleY * s.ScaleY, alpha);
                }
            }
            if (s.HasShear != 0)
            {
                if (additive)
                {
                    p.ShearX += s.ShearX * alpha;
                    p.ShearY += s.ShearY * alpha;
                }
                else
                {
                    p.ShearX = DetMath.Lerp(p.ShearX, bs.ShearX + s.ShearX, alpha);
                    p.ShearY = DetMath.Lerp(p.ShearY, bs.ShearY + s.ShearY, alpha);
                }
            }
        }
    }

    private static void ApplyCrossfadedBones(
        Rig2DAssetData* asset,
        BoneLocalPose* pose,
        ClipPoseSample* sampleA,
        ClipPoseSample* sampleB,
        int boneCount,
        float mixT,
        float alpha,
        bool additive)
    {
        for (int b = 0; b < boneCount; b++)
        {
            ref readonly Rig2DBoneSetupData bs = ref asset->Bones[b];
            ref readonly ClipPoseSample a = ref sampleA[b];
            ref readonly ClipPoseSample sb = ref sampleB[b];
            ref BoneLocalPose p = ref pose[b];

            if (a.HasTranslate != 0 || sb.HasTranslate != 0)
            {
                float ax = a.HasTranslate != 0 ? a.X : 0f;
                float ay = a.HasTranslate != 0 ? a.Y : 0f;
                float bx = sb.HasTranslate != 0 ? sb.X : 0f;
                float by = sb.HasTranslate != 0 ? sb.Y : 0f;
                float mx = DetMath.Lerp(ax, bx, mixT);
                float my = DetMath.Lerp(ay, by, mixT);
                if (additive) { p.X += mx * alpha; p.Y += my * alpha; }
                else { p.X = DetMath.Lerp(p.X, bs.X + mx, alpha); p.Y = DetMath.Lerp(p.Y, bs.Y + my, alpha); }
            }
            if (a.HasRotate != 0 || sb.HasRotate != 0)
            {
                float ar = a.HasRotate != 0 ? a.RotDeg : 0f;
                float br = sb.HasRotate != 0 ? sb.RotDeg : 0f;
                float mr = ar + WrapAngleDeg(br - ar) * mixT;
                if (additive) { p.RotDeg += mr * alpha; }
                else { float target = bs.RotationDeg + mr; p.RotDeg += WrapAngleDeg(target - p.RotDeg) * alpha; }
            }
            if (a.HasScale != 0 || sb.HasScale != 0)
            {
                float asx = a.HasScale != 0 ? a.ScaleX : 1f;
                float asy = a.HasScale != 0 ? a.ScaleY : 1f;
                float bsx = sb.HasScale != 0 ? sb.ScaleX : 1f;
                float bsy = sb.HasScale != 0 ? sb.ScaleY : 1f;
                float msx = DetMath.Lerp(asx, bsx, mixT);
                float msy = DetMath.Lerp(asy, bsy, mixT);
                if (additive)
                {
                    p.ScaleX *= 1f + (msx - 1f) * alpha;
                    p.ScaleY *= 1f + (msy - 1f) * alpha;
                }
                else
                {
                    p.ScaleX = DetMath.Lerp(p.ScaleX, bs.ScaleX * msx, alpha);
                    p.ScaleY = DetMath.Lerp(p.ScaleY, bs.ScaleY * msy, alpha);
                }
            }
            if (a.HasShear != 0 || sb.HasShear != 0)
            {
                float ashx = a.HasShear != 0 ? a.ShearX : 0f;
                float ashy = a.HasShear != 0 ? a.ShearY : 0f;
                float bshx = sb.HasShear != 0 ? sb.ShearX : 0f;
                float bshy = sb.HasShear != 0 ? sb.ShearY : 0f;
                float mshx = DetMath.Lerp(ashx, bshx, mixT);
                float mshy = DetMath.Lerp(ashy, bshy, mixT);
                if (additive) { p.ShearX += mshx * alpha; p.ShearY += mshy * alpha; }
                else { p.ShearX = DetMath.Lerp(p.ShearX, bs.ShearX + mshx, alpha); p.ShearY = DetMath.Lerp(p.ShearY, bs.ShearY + mshy, alpha); }
            }
        }
    }

    private static float GetSetupConstraintMix(Rig2DAssetData* asset, int constraintOrderIndex)
    {
        ref readonly Rig2DConstraintOrderItem ord = ref asset->ConstraintOrder[constraintOrderIndex];
        return ord.Kind switch
        {
            Rig2DConstraintKind.Ik => asset->IkConstraints[ord.Index].Mix,
            Rig2DConstraintKind.Transform => 1f,
            Rig2DConstraintKind.Spring => asset->SpringConstraints[ord.Index].Mix,
            _ => 1f,
        };
    }

    private static bool ClipHasNonBoneTimelines(Rig2DAssetData* asset, in Rig2DAnimClipData clip)
    {
        if (clip.TimelineCount <= 0 || asset->Timelines == null || asset->Keyframes == null) return false;
        for (int i = 0; i < clip.TimelineCount; i++)
        {
            if (asset->Timelines[clip.TimelineStart + i].Target >= Rig2DTimelineTarget.SlotAttachment)
                return true;
        }
        return false;
    }

    private static void ApplyCrossfadedNonBoneTimelines(
        ref Rig2DTable table,
        in Rig2DInstanceData inst,
        Rig2DAssetData* asset,
        in Rig2DAnimClipData fromClip,
        float fromTime,
        in Rig2DAnimClipData toClip,
        float toTime,
        Rig2DRuntimeSlotState* slotStates,
        int* drawOrder,
        byte* seqKeyed,
        float* constraintMix,
        float* ikSoftness,
        sbyte* ikBend,
        float mixT,
        float alpha,
        bool additive,
        ref int deformCursor,
        ref LinearArena scratchArena)
    {
        if (!ClipHasNonBoneTimelines(asset, in fromClip) && !ClipHasNonBoneTimelines(asset, in toClip))
            return;

        // 1. Apply discrete channels (SlotAttachment, SlotSequenceFrame, DrawOrder, ikBend) from active half first
        if (mixT < 0.5f)
            ApplyClipDiscreteNonBoneTimelines(in inst, asset, in fromClip, fromTime, slotStates, drawOrder, seqKeyed, ikBend);
        else
            ApplyClipDiscreteNonBoneTimelines(in inst, asset, in toClip, toTime, slotStates, drawOrder, seqKeyed, ikBend);

        int sc = asset->SlotCount;
        int cc = asset->ConstraintOrderCount;
        int ikc = asset->IkConstraintCount;

        ArenaMarker mark = scratchArena.SaveMarker();
        try
        {
            ColorF* lightA = sc > 0 ? scratchArena.AllocUninitialized<ColorF>(sc, 16) : null;
            ColorF* lightB = sc > 0 ? scratchArena.AllocUninitialized<ColorF>(sc, 16) : null;
            byte* hasLightA = sc > 0 ? scratchArena.Alloc<byte>(sc, 16) : null;
            byte* hasLightB = sc > 0 ? scratchArena.Alloc<byte>(sc, 16) : null;

            ColorF* darkA = sc > 0 ? scratchArena.AllocUninitialized<ColorF>(sc, 16) : null;
            ColorF* darkB = sc > 0 ? scratchArena.AllocUninitialized<ColorF>(sc, 16) : null;
            byte* hasDarkA = sc > 0 ? scratchArena.Alloc<byte>(sc, 16) : null;
            byte* hasDarkB = sc > 0 ? scratchArena.Alloc<byte>(sc, 16) : null;

            float* mixA = cc > 0 ? scratchArena.AllocUninitialized<float>(cc, 16) : null;
            float* mixB = cc > 0 ? scratchArena.AllocUninitialized<float>(cc, 16) : null;
            byte* hasMixA = cc > 0 ? scratchArena.Alloc<byte>(cc, 16) : null;
            byte* hasMixB = cc > 0 ? scratchArena.Alloc<byte>(cc, 16) : null;

            float* softA = ikc > 0 ? scratchArena.AllocUninitialized<float>(ikc, 16) : null;
            float* softB = ikc > 0 ? scratchArena.AllocUninitialized<float>(ikc, 16) : null;
            byte* hasSoftA = ikc > 0 ? scratchArena.Alloc<byte>(ikc, 16) : null;
            byte* hasSoftB = ikc > 0 ? scratchArena.Alloc<byte>(ikc, 16) : null;

            int* deformTlA = sc > 0 ? scratchArena.AllocUninitialized<int>(sc, 16) : null;
            int* deformTlB = sc > 0 ? scratchArena.AllocUninitialized<int>(sc, 16) : null;
            for (int s = 0; s < sc; s++)
            {
                deformTlA[s] = -1;
                deformTlB[s] = -1;
            }

            // 2. Sample continuous channels from both clips into zero-GC scratch buffers
            SampleClipContinuousNonBone(asset, in fromClip, fromTime, slotStates,
                lightA, hasLightA, darkA, hasDarkA, mixA, hasMixA, softA, hasSoftA, deformTlA);
            SampleClipContinuousNonBone(asset, in toClip, toTime, slotStates,
                lightB, hasLightB, darkB, hasDarkB, mixB, hasMixB, softB, hasSoftB, deformTlB);

            // 3. Crossfade SlotColor & SlotDarkColor (unkeyed side falls back to setup pose; zero setup bleed!)
            if (slotStates != null)
            {
                for (int s = 0; s < sc; s++)
                {
                    if (hasLightA[s] != 0 || hasLightB[s] != 0)
                    {
                        ColorF setupLight = asset->Slots[s].LightColor;
                        ColorF ca = hasLightA[s] != 0 ? lightA[s] : setupLight;
                        ColorF cb = hasLightB[s] != 0 ? lightB[s] : setupLight;
                        ColorF mixed = ColorF.Lerp(ca, cb, mixT);
                        slotStates[s].LightColor = ColorF.Lerp(slotStates[s].LightColor, mixed, alpha);
                    }
                    if (hasDarkA[s] != 0 || hasDarkB[s] != 0)
                    {
                        ColorF setupDark = asset->Slots[s].DarkColor;
                        ColorF ca = hasDarkA[s] != 0 ? darkA[s] : setupDark;
                        ColorF cb = hasDarkB[s] != 0 ? darkB[s] : setupDark;
                        ColorF mixed = ColorF.Lerp(ca, cb, mixT);
                        slotStates[s].DarkColor = ColorF.Lerp(slotStates[s].DarkColor, mixed, alpha);
                    }
                }
            }

            // 4. Crossfade ConstraintMix & ikSoftness (unkeyed side falls back to setup pose)
            if (constraintMix != null)
            {
                for (int ci = 0; ci < cc; ci++)
                {
                    if (hasMixA[ci] != 0 || hasMixB[ci] != 0)
                    {
                        float setupMix = GetSetupConstraintMix(asset, ci);
                        float ma = hasMixA[ci] != 0 ? mixA[ci] : setupMix;
                        float mb = hasMixB[ci] != 0 ? mixB[ci] : setupMix;
                        float mixed = DetMath.Lerp(ma, mb, mixT);
                        constraintMix[ci] = DetMath.Lerp(constraintMix[ci], mixed, alpha);
                    }
                }
            }
            if (ikSoftness != null)
            {
                for (int ii = 0; ii < ikc; ii++)
                {
                    if (hasSoftA[ii] != 0 || hasSoftB[ii] != 0)
                    {
                        float setupSoft = asset->IkConstraints[ii].Softness;
                        float sa = hasSoftA[ii] != 0 ? softA[ii] : setupSoft;
                        float sb = hasSoftB[ii] != 0 ? softB[ii] : setupSoft;
                        float mixed = DetMath.Lerp(sa, sb, mixT);
                        ikSoftness[ii] = DetMath.Lerp(ikSoftness[ii], mixed, alpha);
                    }
                }
            }

            // 5. Crossfade MeshDeform per slot (unkeyed side falls back to Vec2.Zero)
            if (asset->DeformPool != null && table.FrameDeformDeltas.Ptr != null && slotStates != null)
            {
                for (int s = 0; s < sc; s++)
                {
                    int tlIdxA = deformTlA[s];
                    int tlIdxB = deformTlB[s];
                    if (tlIdxA < 0 && tlIdxB < 0) continue;

                    int attIdx = slotStates[s].ActiveAttachmentIndex;
                    if ((uint)attIdx >= (uint)asset->AttachmentCount) continue;
                    int vc = asset->Attachments[attIdx].VertexCount;
                    if (vc <= 0) continue;

                    int dstOff = slotStates[s].DeformVertexOffset;
                    bool freshSlot = slotStates[s].DeformVertexCount != vc;
                    if (freshSlot)
                    {
                        if (deformCursor + vc > table.FrameDeformDeltas.Length) continue;
                        dstOff = deformCursor;
                        deformCursor += vc;
                        slotStates[s].DeformVertexOffset = dstOff;
                        slotStates[s].DeformVertexCount = vc;
                        for (int v = 0; v < vc; v++)
                            table.FrameDeformDeltas.Ptr[dstOff + v] = Vec2.Zero;
                    }

                    ArenaMarker defMark = scratchArena.SaveMarker();
                    try
                    {
                        Vec2* tmpA = scratchArena.Alloc<Vec2>(vc, 16);
                        Vec2* tmpB = scratchArena.Alloc<Vec2>(vc, 16);

                        if (tlIdxA >= 0)
                        {
                            ref readonly Rig2DTimelineData tlA = ref asset->Timelines[tlIdxA];
                            SampleDeformTimeline(asset->Keyframes + tlA.KeyStart, tlA.KeyCount, fromTime,
                                asset->DeformPool, asset->DeformPoolCount, tmpA, vc, 1f, accumulate: false);
                        }
                        if (tlIdxB >= 0)
                        {
                            ref readonly Rig2DTimelineData tlB = ref asset->Timelines[tlIdxB];
                            SampleDeformTimeline(asset->Keyframes + tlB.KeyStart, tlB.KeyCount, toTime,
                                asset->DeformPool, asset->DeformPoolCount, tmpB, vc, 1f, accumulate: false);
                        }

                        Vec2* dst = table.FrameDeformDeltas.Ptr + dstOff;
                        for (int v = 0; v < vc; v++)
                        {
                            Vec2 mixed = Vec2.Lerp(tmpA[v], tmpB[v], mixT);
                            if (additive)
                                dst[v] = dst[v] + mixed * alpha;
                            else
                                dst[v] = Vec2.Lerp(dst[v], mixed, alpha);
                        }
                    }
                    finally
                    {
                        scratchArena.Rollback(defMark);
                    }
                }
            }
        }
        finally
        {
            scratchArena.Rollback(mark);
        }
    }

    private static void ApplyClipDiscreteNonBoneTimelines(
        in Rig2DInstanceData inst,
        Rig2DAssetData* asset,
        in Rig2DAnimClipData clip,
        float time,
        Rig2DRuntimeSlotState* slotStates,
        int* drawOrder,
        byte* seqKeyed,
        sbyte* ikBend)
    {
        if (clip.TimelineCount <= 0 || asset->Timelines == null || asset->Keyframes == null) return;

        for (int i = 0; i < clip.TimelineCount; i++)
        {
            ref readonly Rig2DTimelineData tl = ref asset->Timelines[clip.TimelineStart + i];
            if (tl.KeyCount <= 0) continue;
            Rig2DKeyframeData* keys = asset->Keyframes + tl.KeyStart;

            switch (tl.Target)
            {
                case Rig2DTimelineTarget.SlotAttachment:
                    if ((uint)tl.TargetIndex < (uint)asset->SlotCount && slotStates != null)
                    {
                        int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                        var attName = new StringId(keys[kIdx].HashVal);
                        slotStates[tl.TargetIndex].ActiveAttachmentIndex = attName.IsValid
                            ? FindAttachmentIndex(asset, tl.TargetIndex, attName, inst.ActiveSkinIndex)
                            : -1;
                    }
                    break;

                case Rig2DTimelineTarget.SlotSequenceFrame:
                    if ((uint)tl.TargetIndex < (uint)asset->SlotCount && slotStates != null)
                    {
                        int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                        slotStates[tl.TargetIndex].SequenceFrame = keys[kIdx].IntVal;
                        seqKeyed[tl.TargetIndex] = 1;
                    }
                    break;

                case Rig2DTimelineTarget.ConstraintMix:
                    if ((uint)tl.TargetIndex < (uint)asset->ConstraintOrderCount && ikBend != null)
                    {
                        ref readonly Rig2DConstraintOrderItem ord = ref asset->ConstraintOrder[tl.TargetIndex];
                        if (ord.Kind == Rig2DConstraintKind.Ik && (uint)ord.Index < (uint)asset->IkConstraintCount)
                        {
                            int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                            if (keys[kIdx].IntVal != 0)
                                ikBend[ord.Index] = keys[kIdx].IntVal > 0 ? (sbyte)1 : (sbyte)-1;
                        }
                    }
                    break;

                case Rig2DTimelineTarget.DrawOrder:
                    if (drawOrder != null && asset->SlotCount > 0)
                    {
                        int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                        int poolOff = keys[kIdx].IntVal;
                        if (poolOff < 0)
                        {
                            for (int s = 0; s < asset->SlotCount; s++) drawOrder[s] = s;
                        }
                        else if (asset->DrawOrderPool != null && poolOff + asset->SlotCount <= asset->DrawOrderPoolCount)
                        {
                            for (int s = 0; s < asset->SlotCount; s++)
                                drawOrder[s] = asset->DrawOrderPool[poolOff + s];
                        }
                    }
                    break;
            }
        }
    }

    private static void SampleClipContinuousNonBone(
        Rig2DAssetData* asset,
        in Rig2DAnimClipData clip,
        float time,
        Rig2DRuntimeSlotState* slotStates,
        ColorF* lightDst,
        byte* hasLight,
        ColorF* darkDst,
        byte* hasDark,
        float* mixDst,
        byte* hasMix,
        float* softDst,
        byte* hasSoft,
        int* deformTlDst)
    {
        if (clip.TimelineCount <= 0 || asset->Timelines == null || asset->Keyframes == null) return;

        for (int i = 0; i < clip.TimelineCount; i++)
        {
            int absTlIdx = clip.TimelineStart + i;
            ref readonly Rig2DTimelineData tl = ref asset->Timelines[absTlIdx];
            if (tl.KeyCount <= 0) continue;
            Rig2DKeyframeData* keys = asset->Keyframes + tl.KeyStart;

            switch (tl.Target)
            {
                case Rig2DTimelineTarget.SlotColor:
                    if ((uint)tl.TargetIndex < (uint)asset->SlotCount && lightDst != null)
                    {
                        SampleVec4(keys, tl.KeyCount, time, out float r, out float g, out float b, out float a);
                        lightDst[tl.TargetIndex] = new ColorF(r, g, b, a);
                        hasLight[tl.TargetIndex] = 1;
                    }
                    break;

                case Rig2DTimelineTarget.SlotDarkColor:
                    if ((uint)tl.TargetIndex < (uint)asset->SlotCount && darkDst != null)
                    {
                        SampleVec4(keys, tl.KeyCount, time, out float r, out float g, out float b, out float a);
                        darkDst[tl.TargetIndex] = new ColorF(r, g, b, a);
                        hasDark[tl.TargetIndex] = 1;
                    }
                    break;

                case Rig2DTimelineTarget.ConstraintMix:
                    if ((uint)tl.TargetIndex < (uint)asset->ConstraintOrderCount && mixDst != null)
                    {
                        SampleVec2(keys, tl.KeyCount, time, out float mixVal, out float softVal);
                        mixDst[tl.TargetIndex] = DetMath.Clamp01(mixVal);
                        hasMix[tl.TargetIndex] = 1;

                        ref readonly Rig2DConstraintOrderItem ord = ref asset->ConstraintOrder[tl.TargetIndex];
                        if (ord.Kind == Rig2DConstraintKind.Ik && (uint)ord.Index < (uint)asset->IkConstraintCount && softDst != null)
                        {
                            int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                            if (softVal >= 0f && keys[kIdx].V1 >= 0f)
                            {
                                softDst[ord.Index] = softVal;
                                hasSoft[ord.Index] = 1;
                            }
                        }
                    }
                    break;

                case Rig2DTimelineTarget.MeshDeform:
                    if ((uint)tl.TargetIndex < (uint)asset->AttachmentCount && slotStates != null && deformTlDst != null)
                    {
                        int slotIdx = asset->Attachments[tl.TargetIndex].SlotIndex;
                        if ((uint)slotIdx < (uint)asset->SlotCount && slotStates[slotIdx].ActiveAttachmentIndex == tl.TargetIndex)
                            deformTlDst[slotIdx] = absTlIdx;
                    }
                    break;
            }
        }
    }

    private static void ApplyClipNonBoneTimelines(
        ref Rig2DTable table,
        in Rig2DInstanceData inst,
        Rig2DAssetData* asset,
        in Rig2DAnimClipData clip,
        float time,
        Rig2DRuntimeSlotState* slotStates,
        int* drawOrder,
        byte* seqKeyed,
        float* constraintMix,
        float* ikSoftness,
        sbyte* ikBend,
        float alpha,
        bool additive,
        bool applyDiscrete,
        ref int deformCursor)
    {
        if (clip.TimelineCount <= 0 || asset->Timelines == null || asset->Keyframes == null) return;

        for (int i = 0; i < clip.TimelineCount; i++)
        {
            ref readonly Rig2DTimelineData tl = ref asset->Timelines[clip.TimelineStart + i];
            if (tl.KeyCount <= 0) continue;
            Rig2DKeyframeData* keys = asset->Keyframes + tl.KeyStart;

            switch (tl.Target)
            {
                case Rig2DTimelineTarget.SlotAttachment:
                    if (applyDiscrete && (uint)tl.TargetIndex < (uint)asset->SlotCount && slotStates != null)
                    {
                        int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                        var attName = new StringId(keys[kIdx].HashVal);
                        slotStates[tl.TargetIndex].ActiveAttachmentIndex = attName.IsValid
                            ? FindAttachmentIndex(asset, tl.TargetIndex, attName, inst.ActiveSkinIndex)
                            : -1;
                    }
                    break;

                case Rig2DTimelineTarget.SlotColor:
                    if ((uint)tl.TargetIndex < (uint)asset->SlotCount && slotStates != null)
                    {
                        SampleVec4(keys, tl.KeyCount, time, out float r, out float g, out float b, out float a);
                        slotStates[tl.TargetIndex].LightColor = ColorF.Lerp(slotStates[tl.TargetIndex].LightColor, new ColorF(r, g, b, a), alpha);
                    }
                    break;

                case Rig2DTimelineTarget.SlotDarkColor:
                    if ((uint)tl.TargetIndex < (uint)asset->SlotCount && slotStates != null)
                    {
                        SampleVec4(keys, tl.KeyCount, time, out float r, out float g, out float b, out float a);
                        slotStates[tl.TargetIndex].DarkColor = ColorF.Lerp(slotStates[tl.TargetIndex].DarkColor, new ColorF(r, g, b, a), alpha);
                    }
                    break;

                case Rig2DTimelineTarget.SlotSequenceFrame:
                    if (applyDiscrete && (uint)tl.TargetIndex < (uint)asset->SlotCount && slotStates != null)
                    {
                        int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                        slotStates[tl.TargetIndex].SequenceFrame = keys[kIdx].IntVal;
                        seqKeyed[tl.TargetIndex] = 1;
                    }
                    break;

                case Rig2DTimelineTarget.ConstraintMix:
                    if ((uint)tl.TargetIndex < (uint)asset->ConstraintOrderCount && constraintMix != null)
                    {
                        SampleVec2(keys, tl.KeyCount, time, out float mixVal, out float softVal);
                        constraintMix[tl.TargetIndex] = DetMath.Lerp(constraintMix[tl.TargetIndex], DetMath.Clamp01(mixVal), alpha);
                        ref readonly Rig2DConstraintOrderItem ord = ref asset->ConstraintOrder[tl.TargetIndex];
                        if (ord.Kind == Rig2DConstraintKind.Ik && (uint)ord.Index < (uint)asset->IkConstraintCount)
                        {
                            int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                            if (softVal >= 0f && keys[kIdx].V1 >= 0f)
                                ikSoftness[ord.Index] = DetMath.Lerp(ikSoftness[ord.Index], softVal, alpha);
                            if (applyDiscrete && keys[kIdx].IntVal != 0)
                                ikBend[ord.Index] = keys[kIdx].IntVal > 0 ? (sbyte)1 : (sbyte)-1;
                        }
                    }
                    break;

                case Rig2DTimelineTarget.DrawOrder:
                    if (applyDiscrete && drawOrder != null && asset->SlotCount > 0)
                    {
                        int kIdx = FindStepKeyIndex(keys, tl.KeyCount, time);
                        int poolOff = keys[kIdx].IntVal;
                        if (poolOff < 0)
                        {
                            for (int s = 0; s < asset->SlotCount; s++) drawOrder[s] = s;
                        }
                        else if (asset->DrawOrderPool != null && poolOff + asset->SlotCount <= asset->DrawOrderPoolCount)
                        {
                            for (int s = 0; s < asset->SlotCount; s++)
                                drawOrder[s] = asset->DrawOrderPool[poolOff + s];
                        }
                    }
                    break;

                case Rig2DTimelineTarget.MeshDeform:
                    if ((uint)tl.TargetIndex < (uint)asset->AttachmentCount && slotStates != null &&
                        asset->DeformPool != null && table.FrameDeformDeltas.Ptr != null)
                    {
                        ref readonly Rig2DAttachmentData att = ref asset->Attachments[tl.TargetIndex];
                        int slotIdx = att.SlotIndex;
                        if ((uint)slotIdx >= (uint)asset->SlotCount) break;
                        if (slotStates[slotIdx].ActiveAttachmentIndex != tl.TargetIndex) break;
                        int vc = att.VertexCount;
                        if (vc <= 0) break;

                        int dstOff = slotStates[slotIdx].DeformVertexOffset;
                        bool freshSlot = slotStates[slotIdx].DeformVertexCount != vc;
                        if (freshSlot)
                        {
                            if (deformCursor + vc > table.FrameDeformDeltas.Length) break;
                            dstOff = deformCursor;
                            deformCursor += vc;
                            slotStates[slotIdx].DeformVertexOffset = dstOff;
                            slotStates[slotIdx].DeformVertexCount = vc;
                            for (int v = 0; v < vc; v++)
                                table.FrameDeformDeltas.Ptr[dstOff + v] = Vec2.Zero;
                        }

                        SampleDeformTimeline(keys, tl.KeyCount, time, asset->DeformPool, asset->DeformPoolCount,
                            table.FrameDeformDeltas.Ptr + dstOff, vc, alpha, accumulate: additive);
                    }
                    break;
            }
        }
    }

    private static void SampleDeformTimeline(
        Rig2DKeyframeData* keys,
        int keyCount,
        float time,
        Vec2* deformPool,
        int deformPoolCount,
        Vec2* dst,
        int vertCount,
        float alpha,
        bool accumulate)
    {
        if (keyCount <= 0) return;

        int k0, k1;
        float t;
        FindKeySegment(keys, keyCount, time, out k0, out k1, out t);

        int off0 = keys[k0].IntVal;
        int off1 = keys[k1].IntVal;
        bool valid0 = off0 >= 0 && off0 + vertCount <= deformPoolCount;
        bool valid1 = off1 >= 0 && off1 + vertCount <= deformPoolCount;

        for (int v = 0; v < vertCount; v++)
        {
            Vec2 d0 = valid0 ? deformPool[off0 + v] : Vec2.Zero;
            Vec2 d1 = valid1 ? deformPool[off1 + v] : Vec2.Zero;
            Vec2 val = (k0 == k1) ? d0 : Vec2.Lerp(d0, d1, t);
            if (accumulate)
                dst[v] = dst[v] + val * alpha;
            else
                dst[v] = Vec2.Lerp(dst[v], val, alpha);
        }
    }

    private static int FindStepKeyIndex(Rig2DKeyframeData* keys, int count, float time)
    {
        if (count <= 1 || time <= keys[0].Time) return 0;
        if (time >= keys[count - 1].Time) return count - 1;
        int lo = 0, hi = count - 1;
        while (lo + 1 < hi)
        {
            int mid = (lo + hi) >> 1;
            if (keys[mid].Time <= time) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    private static void FindKeySegment(Rig2DKeyframeData* keys, int count, float time, out int k0, out int k1, out float factor)
    {
        if (count <= 1 || time <= keys[0].Time)
        {
            k0 = k1 = 0;
            factor = 0f;
            return;
        }
        if (time >= keys[count - 1].Time)
        {
            k0 = k1 = count - 1;
            factor = 0f;
            return;
        }

        int lo = 0, hi = count - 1;
        while (lo + 1 < hi)
        {
            int mid = (lo + hi) >> 1;
            if (keys[mid].Time <= time) lo = mid;
            else hi = mid;
        }
        k0 = lo;
        k1 = hi;

        if (keys[k0].Curve == Rig2DCurveKind.Step)
        {
            k1 = k0;
            factor = 0f;
            return;
        }

        float span = keys[k1].Time - keys[k0].Time;
        if (span <= 1e-6f)
        {
            factor = 0f;
            return;
        }
        float u = DetMath.Clamp01((time - keys[k0].Time) / span);
        factor = keys[k0].Curve == Rig2DCurveKind.Bezier
            ? EvaluateCubicBezierY(u, keys[k0].Cx1, keys[k0].Cy1, keys[k0].Cx2, keys[k0].Cy2)
            : u;
    }

    private static void SampleScalar(Rig2DKeyframeData* keys, int count, float time, out float v0)
    {
        FindKeySegment(keys, count, time, out int k0, out int k1, out float f);
        v0 = k0 == k1 ? keys[k0].V0 : DetMath.Lerp(keys[k0].V0, keys[k1].V0, f);
    }

    private static void SampleVec2(Rig2DKeyframeData* keys, int count, float time, out float v0, out float v1)
    {
        FindKeySegment(keys, count, time, out int k0, out int k1, out float f);
        if (k0 == k1)
        {
            v0 = keys[k0].V0;
            v1 = keys[k0].V1;
            return;
        }
        v0 = DetMath.Lerp(keys[k0].V0, keys[k1].V0, f);
        v1 = DetMath.Lerp(keys[k0].V1, keys[k1].V1, f);
    }

    private static void SampleVec4(Rig2DKeyframeData* keys, int count, float time, out float v0, out float v1, out float v2, out float v3)
    {
        FindKeySegment(keys, count, time, out int k0, out int k1, out float f);
        if (k0 == k1)
        {
            v0 = keys[k0].V0; v1 = keys[k0].V1; v2 = keys[k0].V2; v3 = keys[k0].V3;
            return;
        }
        v0 = DetMath.Lerp(keys[k0].V0, keys[k1].V0, f);
        v1 = DetMath.Lerp(keys[k0].V1, keys[k1].V1, f);
        v2 = DetMath.Lerp(keys[k0].V2, keys[k1].V2, f);
        v3 = DetMath.Lerp(keys[k0].V3, keys[k1].V3, f);
    }

    /// <summary>
    /// Deterministic monotonic Cubic Bezier curve solver (Newton-Raphson + bisection fallback, 0 GC).
    /// </summary>
    public static float EvaluateCubicBezierY(float u, float cx1, float cy1, float cx2, float cy2)
    {
        if (u <= 0f) return 0f;
        if (u >= 1f) return 1f;

        cx1 = DetMath.Clamp01(cx1);
        cx2 = DetMath.Clamp01(cx2);

        float t = u;
        for (int iter = 0; iter < 6; iter++)
        {
            float omt = 1f - t;
            float x = 3f * omt * omt * t * cx1 + 3f * omt * t * t * cx2 + t * t * t;
            float err = x - u;
            if (DetMath.Abs(err) < 1e-5f) break;
            float dx = 3f * omt * omt * cx1 + 6f * omt * t * (cx2 - cx1) + 3f * t * t * (1f - cx2);
            if (DetMath.Abs(dx) < 1e-6f) break;
            t = DetMath.Clamp01(t - err / dx);
        }

        // Verify accuracy; fallback to guaranteed-convergent bisection if needed
        float omtCheck = 1f - t;
        float xCheck = 3f * omtCheck * omtCheck * t * cx1 + 3f * omtCheck * t * t * cx2 + t * t * t;
        if (DetMath.Abs(xCheck - u) > 1e-4f)
        {
            float lo = 0f, hi = 1f;
            for (int iter = 0; iter < 14; iter++)
            {
                t = 0.5f * (lo + hi);
                float omt = 1f - t;
                float x = 3f * omt * omt * t * cx1 + 3f * omt * t * t * cx2 + t * t * t;
                if (x < u) lo = t;
                else hi = t;
            }
        }

        float om = 1f - t;
        return 3f * om * om * t * cy1 + 3f * om * t * t * cy2 + t * t * t;
    }

    public static int EvaluateSequenceFrame(in Rig2DAttachmentData att, float seqTime)
    {
        int count = att.SeqFrameCount;
        if (count <= 1) return 0;
        float fps = att.SeqFps > 0f ? att.SeqFps : 12f;
        int raw = att.SeqStartIndex + (int)MathF.Floor(DetMath.Max(0f, seqTime) * fps + 1e-5f);
        switch (att.SeqLoop)
        {
            case Rig2DSequenceLoop.Hold:
                return Math.Clamp(raw, 0, count - 1);
            case Rig2DSequenceLoop.Loop:
                return ((raw % count) + count) % count;
            case Rig2DSequenceLoop.PingPong:
            {
                int period = (count - 1) * 2;
                int m = ((raw % period) + period) % period;
                return m < count ? m : period - m;
            }
            default:
                return Math.Clamp(raw, 0, count - 1);
        }
    }

    // =========================================================================
    // Constraints: 1 & 2-Bone IK, Transform, and Deterministic Spring Physics
    // =========================================================================

    private static void SolveIkConstraint(
        Rig2DAssetData* asset,
        in Rig2DIkConstraintData ik,
        float mix,
        float softness,
        sbyte bendDir,
        BoneLocalPose* localPose,
        Rig2DAffine2D* worldBones,
        byte* dirtyBones)
    {
        int bc = asset->BoneCount;
        if ((uint)ik.BoneA >= (uint)bc || (uint)ik.TargetBone >= (uint)bc) return;

        ref readonly Rig2DAffine2D wT = ref worldBones[ik.TargetBone];
        float tx = wT.Tx;
        float ty = wT.Ty;

        if (ik.BoneB < 0)
        {
            // 1-Bone IK Aim (+ optional stretch/compress)
            int a = ik.BoneA;
            float ax = worldBones[a].Tx;
            float ay = worldBones[a].Ty;
            float dx = tx - ax;
            float dy = ty - ay;
            float dist = DetMath.Sqrt(dx * dx + dy * dy);
            if (dist < 1e-5f) return;

            float targetRad = DetMath.Atan2(dy, dx);
            float curRad = worldBones[a].WorldRotationRad;
            float deltaDeg = WrapAngleDeg((targetRad - curRad) * DetMath.Rad2Deg) * mix;
            localPose[a].RotDeg += deltaDeg;

            float lenA = asset->Bones[a].Length * worldBones[a].WorldScaleX;
            if (lenA > 1e-4f)
            {
                if (ik.Stretch != 0 && dist > lenA)
                    localPose[a].ScaleX *= DetMath.Lerp(1f, dist / lenA, mix);
                else if (ik.Compress != 0 && dist < lenA)
                    localPose[a].ScaleX *= DetMath.Lerp(1f, dist / lenA, mix);
            }
            UpdateBoneSubtree(asset, localPose, worldBones, dirtyBones, a);
            return;
        }

        // 2-Bone Analytic IK (Law of Cosines + Softness + BendDirection + Stretch + Compress)
        int boneA = ik.BoneA;
        int boneB = ik.BoneB;
        if ((uint)boneB >= (uint)bc) return;

        float pAx = worldBones[boneA].Tx;
        float pAy = worldBones[boneA].Ty;
        float pBx = worldBones[boneB].Tx;
        float pBy = worldBones[boneB].Ty;

        float l1 = DetMath.Sqrt((pBx - pAx) * (pBx - pAx) + (pBy - pAy) * (pBy - pAy));
        if (l1 < 1e-5f) l1 = asset->Bones[boneA].Length * worldBones[boneA].WorldScaleX;
        float l2 = asset->Bones[boneB].Length * worldBones[boneB].WorldScaleX;
        if (l2 < 1e-5f) l2 = l1;
        if (l1 < 1e-5f || l2 < 1e-5f) return;

        float dx2 = tx - pAx;
        float dy2 = ty - pAy;
        float d = DetMath.Sqrt(dx2 * dx2 + dy2 * dy2);
        if (d < 1e-5f) return;

        float maxReach = l1 + l2;
        float minReach = DetMath.Abs(l1 - l2);
        float dEff = d;

        if (softness > 1e-5f && maxReach > 1e-5f)
        {
            float softClamp = DetMath.Min(softness, maxReach * 0.45f);
            float softStart = maxReach - softClamp;
            if (dEff > softStart)
            {
                float excess = dEff - softStart;
                dEff = softStart + softClamp * (1f - DetMath.Exp(-excess / softClamp));
            }
        }

        float scaleFactor = 1f;
        if (ik.Stretch != 0 && d > maxReach && maxReach > 1e-5f)
        {
            scaleFactor = DetMath.Lerp(1f, d / maxReach, mix);
            dEff = maxReach;
        }
        else if (ik.Compress != 0 && d < minReach && minReach > 1e-5f)
        {
            scaleFactor = DetMath.Lerp(1f, d / minReach, mix);
            dEff = minReach;
        }

        float clampedD = DetMath.Clamp(dEff, minReach + 1e-5f, maxReach - 1e-5f);
        float cosA = DetMath.Clamp((l1 * l1 + clampedD * clampedD - l2 * l2) / (2f * l1 * clampedD), -1f, 1f);
        float cosB = DetMath.Clamp((clampedD * clampedD - l1 * l1 - l2 * l2) / (2f * l1 * l2), -1f, 1f);

        float baseAngle = DetMath.Atan2(dy2, dx2);
        float sign = bendDir >= 0 ? 1f : -1f;
        float desiredWorldA = baseAngle - sign * DetMath.Acos(cosA);
        float desiredWorldB = desiredWorldA + sign * DetMath.Acos(cosB);

        // Apply rotation & stretch to Bone A
        float curWorldA = worldBones[boneA].WorldRotationRad;
        localPose[boneA].RotDeg += WrapAngleDeg((desiredWorldA - curWorldA) * DetMath.Rad2Deg) * mix;
        if (DetMath.Abs(scaleFactor - 1f) > 1e-5f)
        {
            localPose[boneA].ScaleX *= scaleFactor;
            // Only scale boneB directly if it does NOT inherit boneA's scale;
            // otherwise boneA.ScaleX already scales both l1 and collinear l2.
            Rig2DInheritMode bInherit = asset->Bones[boneB].Inherit;
            if (bInherit == Rig2DInheritMode.NoScale || bInherit == Rig2DInheritMode.OnlyTranslation)
                localPose[boneB].ScaleX *= scaleFactor;
        }
        UpdateBoneSubtree(asset, localPose, worldBones, dirtyBones, boneA);

        // Apply rotation to Bone B
        float curWorldB = worldBones[boneB].WorldRotationRad;
        localPose[boneB].RotDeg += WrapAngleDeg((desiredWorldB - curWorldB) * DetMath.Rad2Deg) * mix;
        UpdateBoneSubtree(asset, localPose, worldBones, dirtyBones, boneB);
    }

    private static void SolveTransformConstraint(
        Rig2DAssetData* asset,
        in Rig2DTransformConstraintData tc,
        float globalMix,
        BoneLocalPose* localPose,
        Rig2DAffine2D* worldBones,
        byte* dirtyBones)
    {
        int bc = asset->BoneCount;
        int bi = tc.BoneIndex;
        int ti = tc.TargetBone;
        if ((uint)bi >= (uint)bc || (uint)ti >= (uint)bc) return;

        float mRot = DetMath.Clamp01(tc.MixRotate * globalMix);
        float mX = DetMath.Clamp01(tc.MixX * globalMix);
        float mY = DetMath.Clamp01(tc.MixY * globalMix);
        float mSx = DetMath.Clamp01(tc.MixScaleX * globalMix);
        float mSy = DetMath.Clamp01(tc.MixScaleY * globalMix);
        float mShY = DetMath.Clamp01(tc.MixShearY * globalMix);

        if (mRot <= 1e-5f && mX <= 1e-5f && mY <= 1e-5f && mSx <= 1e-5f && mSy <= 1e-5f && mShY <= 1e-5f)
            return;

        if (tc.Local != 0)
        {
            BoneLocalPose tPose = localPose[ti];
            ref BoneLocalPose bPose = ref localPose[bi];
            if (tc.Relative != 0)
            {
                if (mRot > 0f) bPose.RotDeg += (tPose.RotDeg + tc.OffsetRotateDeg) * mRot;
                if (mX > 0f) bPose.X += (tPose.X + tc.OffsetX) * mX;
                if (mY > 0f) bPose.Y += (tPose.Y + tc.OffsetY) * mY;
                if (mSx > 0f) bPose.ScaleX *= 1f + (tPose.ScaleX - 1f + tc.OffsetScaleX) * mSx;
                if (mSy > 0f) bPose.ScaleY *= 1f + (tPose.ScaleY - 1f + tc.OffsetScaleY) * mSy;
                if (mShY > 0f) bPose.ShearY += (tPose.ShearY + tc.OffsetShearY) * mShY;
            }
            else
            {
                if (mRot > 0f) bPose.RotDeg += WrapAngleDeg((tPose.RotDeg + tc.OffsetRotateDeg) - bPose.RotDeg) * mRot;
                if (mX > 0f) bPose.X = DetMath.Lerp(bPose.X, tPose.X + tc.OffsetX, mX);
                if (mY > 0f) bPose.Y = DetMath.Lerp(bPose.Y, tPose.Y + tc.OffsetY, mY);
                if (mSx > 0f) bPose.ScaleX = DetMath.Lerp(bPose.ScaleX, tPose.ScaleX + tc.OffsetScaleX, mSx);
                if (mSy > 0f) bPose.ScaleY = DetMath.Lerp(bPose.ScaleY, tPose.ScaleY + tc.OffsetScaleY, mSy);
                if (mShY > 0f) bPose.ShearY = DetMath.Lerp(bPose.ShearY, tPose.ShearY + tc.OffsetShearY, mShY);
            }
            UpdateBoneSubtree(asset, localPose, worldBones, dirtyBones, bi);
            return;
        }

        // World-space transform constraint
        ref readonly Rig2DAffine2D tw = ref worldBones[ti];
        ref readonly Rig2DAffine2D bw = ref worldBones[bi];

        float tRotDeg = tw.WorldRotationRad * DetMath.Rad2Deg;
        float bRotDeg = bw.WorldRotationRad * DetMath.Rad2Deg;
        if (mRot > 0f)
        {
            float dRot = tc.Relative != 0
                ? (tRotDeg + tc.OffsetRotateDeg) * mRot
                : WrapAngleDeg((tRotDeg + tc.OffsetRotateDeg) - bRotDeg) * mRot;
            localPose[bi].RotDeg += dRot;
        }

        if (mX > 0f || mY > 0f)
        {
            float targetWx = tc.Relative != 0
                ? bw.Tx + (tw.Tx + tc.OffsetX) * mX
                : DetMath.Lerp(bw.Tx, tw.Tx + tc.OffsetX, mX);
            float targetWy = tc.Relative != 0
                ? bw.Ty + (tw.Ty + tc.OffsetY) * mY
                : DetMath.Lerp(bw.Ty, tw.Ty + tc.OffsetY, mY);

            int p = asset->Bones[bi].ParentIndex;
            if (p >= 0 && p < bc)
            {
                Rig2DAffine2D invP = Rig2DAffine2D.Invert(in worldBones[p]);
                Vec2 lp = Rig2DAffine2D.TransformPoint(in invP, new Vec2(targetWx, targetWy));
                localPose[bi].X = lp.X;
                localPose[bi].Y = lp.Y;
            }
            else
            {
                localPose[bi].X = targetWx;
                localPose[bi].Y = targetWy;
            }
        }

        if (mSx > 0f)
        {
            float tsx = tw.WorldScaleX + tc.OffsetScaleX;
            float bsx = bw.WorldScaleX;
            if (bsx > 1e-5f)
            {
                float ratio = tc.Relative != 0 ? (1f + (tsx - 1f) * mSx) : DetMath.Lerp(1f, tsx / bsx, mSx);
                localPose[bi].ScaleX *= ratio;
            }
        }
        if (mSy > 0f)
        {
            float tsy = tw.WorldScaleY + tc.OffsetScaleY;
            float bsy = bw.WorldScaleY;
            if (bsy > 1e-5f)
            {
                float ratio = tc.Relative != 0 ? (1f + (tsy - 1f) * mSy) : DetMath.Lerp(1f, tsy / bsy, mSy);
                localPose[bi].ScaleY *= ratio;
            }
        }
        if (mShY > 0f)
        {
            float tShY = (DetMath.Atan2(tw.D, tw.C) - DetMath.Atan2(tw.B, tw.A) - DetMath.HalfPi) * DetMath.Rad2Deg;
            localPose[bi].ShearY += (tShY + tc.OffsetShearY) * mShY;
        }

        UpdateBoneSubtree(asset, localPose, worldBones, dirtyBones, bi);
    }

    private static void SolveSpringConstraint(
        Rig2DAssetData* asset,
        in Rig2DSpringConstraintData sp,
        ref Rig2DSpringRuntimeState st,
        float mix,
        float dt,
        Vec2 entDeltaSkel,
        BoneLocalPose* localPose,
        Rig2DAffine2D* worldBones,
        byte* dirtyBones)
    {
        int bc = asset->BoneCount;
        int bi = sp.BoneIndex;
        if ((uint)bi >= (uint)bc) return;

        float curX = worldBones[bi].Tx;
        float curY = worldBones[bi].Ty;
        float curAngleDeg = worldBones[bi].WorldRotationRad * DetMath.Rad2Deg;

        if (st.Initialized == 0)
        {
            st.PrevWorldX = curX;
            st.PrevWorldY = curY;
            st.PrevWorldAngleDeg = curAngleDeg;
            st.AngleOffsetDeg = 0f;
            st.AngleVelDeg = 0f;
            st.PosOffsetX = 0f;
            st.PosOffsetY = 0f;
            st.VelX = 0f;
            st.VelY = 0f;
            st.Initialized = 1;
            return;
        }

        // Guard against any non-finite spring runtime state before integrating
        if (!float.IsFinite(st.AngleOffsetDeg)) st.AngleOffsetDeg = 0f;
        if (!float.IsFinite(st.AngleVelDeg)) st.AngleVelDeg = 0f;
        if (!float.IsFinite(st.PosOffsetX)) st.PosOffsetX = 0f;
        if (!float.IsFinite(st.PosOffsetY)) st.PosOffsetY = 0f;
        if (!float.IsFinite(st.VelX)) st.VelX = 0f;
        if (!float.IsFinite(st.VelY)) st.VelY = 0f;

        if (dt > 1e-6f && mix > 1e-5f)
        {
            float dx = (curX - st.PrevWorldX) + entDeltaSkel.X;
            float dy = (curY - st.PrevWorldY) + entDeltaSkel.Y;
            float dAngle = WrapAngleDeg(curAngleDeg - st.PrevWorldAngleDeg);
            if (!float.IsFinite(dx)) dx = 0f;
            if (!float.IsFinite(dy)) dy = 0f;
            if (!float.IsFinite(dAngle)) dAngle = 0f;

            float invDt = 1f / DetMath.Max(dt, 1f / 240f);
            float boneAngleRad = (curAngleDeg + st.AngleOffsetDeg) * DetMath.Deg2Rad;
            float nx = -DetMath.Sin(boneAngleRad);
            float ny = DetMath.Cos(boneAngleRad);

            // External torque from linear movement inertia + angular inertia + gravity (-Y) + wind (+X)
            float linearImpulse = -(dx * nx + dy * ny) * invDt * sp.Inertia;
            float angularImpulse = -dAngle * invDt * sp.Inertia * 0.5f;
            float gravityTorque = (-sp.Gravity * ny) * 0.08f;
            float windTorque = (sp.Wind * nx) * 0.08f;
            float extTorque = linearImpulse + angularImpulse + gravityTorque + windTorque;
            if (!float.IsFinite(extTorque)) extTorque = 0f;

            float k = DetMath.Max(0.1f, sp.Strength);
            float damp = DetMath.Max(0.1f, sp.Damping);
            float invMass = 1f / DetMath.Max(0.05f, sp.Mass);
            float maxAng = sp.MaxAngleDeg > 0f && float.IsFinite(sp.MaxAngleDeg) ? sp.MaxAngleDeg : 45f;
            float maxVel = DetMath.Max(1000f, maxAng * 40f);

            // Cap effective spring dt to 8/60s so substep h <= 1/60s always (prevents Euler divergence on hitch/tab-out spikes)
            float simDt = DetMath.Min(dt, 8f / 60f);
            int steps = simDt > (1f / 60f) ? Math.Min(8, (int)MathF.Ceiling(simDt * 60f)) : 1;
            float h = simDt / steps;

            for (int step = 0; step < steps; step++)
            {
                if (DetMath.Abs(sp.RotateScale) > 1e-5f)
                {
                    float acc = (-k * st.AngleOffsetDeg - damp * st.AngleVelDeg + extTorque) * invMass;
                    float nextVel = st.AngleVelDeg + acc * h;
                    st.AngleVelDeg = float.IsFinite(nextVel) ? DetMath.Clamp(nextVel, -maxVel, maxVel) : 0f;
                    float nextAng = st.AngleOffsetDeg + st.AngleVelDeg * h;
                    st.AngleOffsetDeg = float.IsFinite(nextAng) ? nextAng : 0f;
                    if (st.AngleOffsetDeg > maxAng)
                    {
                        st.AngleOffsetDeg = maxAng;
                        if (st.AngleVelDeg > 0f) st.AngleVelDeg = 0f;
                    }
                    else if (st.AngleOffsetDeg < -maxAng)
                    {
                        st.AngleOffsetDeg = -maxAng;
                        if (st.AngleVelDeg < 0f) st.AngleVelDeg = 0f;
                    }
                }

                if (DetMath.Abs(sp.PosScaleX) > 1e-5f || DetMath.Abs(sp.PosScaleY) > 1e-5f)
                {
                    float fx = (-dx * invDt * sp.Inertia + sp.Wind * 0.1f) * sp.PosScaleX;
                    float fy = (-dy * invDt * sp.Inertia - sp.Gravity * 0.1f) * sp.PosScaleY;
                    if (!float.IsFinite(fx)) fx = 0f;
                    if (!float.IsFinite(fy)) fy = 0f;

                    float nextVx = st.VelX + ((-k * st.PosOffsetX - damp * st.VelX + fx) * invMass) * h;
                    float nextVy = st.VelY + ((-k * st.PosOffsetY - damp * st.VelY + fy) * invMass) * h;
                    st.VelX = float.IsFinite(nextVx) ? DetMath.Clamp(nextVx, -maxVel, maxVel) : 0f;
                    st.VelY = float.IsFinite(nextVy) ? DetMath.Clamp(nextVy, -maxVel, maxVel) : 0f;

                    float nextPx = st.PosOffsetX + st.VelX * h;
                    float nextPy = st.PosOffsetY + st.VelY * h;
                    st.PosOffsetX = float.IsFinite(nextPx) ? nextPx : 0f;
                    st.PosOffsetY = float.IsFinite(nextPy) ? nextPy : 0f;
                    if (st.PosOffsetX > maxAng) { st.PosOffsetX = maxAng; if (st.VelX > 0f) st.VelX = 0f; }
                    else if (st.PosOffsetX < -maxAng) { st.PosOffsetX = -maxAng; if (st.VelX < 0f) st.VelX = 0f; }
                    if (st.PosOffsetY > maxAng) { st.PosOffsetY = maxAng; if (st.VelY > 0f) st.VelY = 0f; }
                    else if (st.PosOffsetY < -maxAng) { st.PosOffsetY = -maxAng; if (st.VelY < 0f) st.VelY = 0f; }
                }
            }
        }

        st.PrevWorldX = curX;
        st.PrevWorldY = curY;
        st.PrevWorldAngleDeg = curAngleDeg;

        if (mix > 1e-5f)
        {
            localPose[bi].RotDeg += st.AngleOffsetDeg * mix * sp.RotateScale;
            localPose[bi].X += st.PosOffsetX * mix * sp.PosScaleX;
            localPose[bi].Y += st.PosOffsetY * mix * sp.PosScaleY;
            UpdateBoneSubtree(asset, localPose, worldBones, dirtyBones, bi);
        }
    }

    // =========================================================================
    // Shared Skinning & Vertex Evaluation Helper (used by BatchRenderer & BringRig2D)
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 SkinMeshVertex(
        Rig2DAssetData* asset,
        Rig2DAffine2D* worldBones,
        int ownerBoneIdx,
        in Rig2DMeshVertex v,
        Vec2 deformDelta)
    {
        Vec2 setupP = v.SetupPos + deformDelta;
        if (v.WeightSum <= 1e-5f)
        {
            if ((uint)ownerBoneIdx < (uint)asset->BoneCount)
            {
                Vec2 invP = Rig2DAffine2D.TransformPoint(in asset->BoneInvBind[ownerBoneIdx], setupP);
                return Rig2DAffine2D.TransformPoint(in worldBones[ownerBoneIdx], invP);
            }
            return setupP;
        }

        float rx = 0f, ry = 0f;
        uint packed = v.PackedBoneIndices;
        float invSum = DetMath.Abs(v.WeightSum - 1f) > 1e-4f ? (1f / v.WeightSum) : 1f;

        for (int k = 0; k < 4; k++)
        {
            float w = k switch
            {
                0 => v.BoneWeights.X,
                1 => v.BoneWeights.Y,
                2 => v.BoneWeights.Z,
                _ => v.BoneWeights.W,
            };
            if (w <= 1e-6f) continue;
            w *= invSum;
            int bi = (int)((packed >> (k * 8)) & 0xFFu);
            if ((uint)bi >= (uint)asset->BoneCount) bi = ownerBoneIdx;
            if ((uint)bi < (uint)asset->BoneCount)
            {
                Vec2 invP = Rig2DAffine2D.TransformPoint(in asset->BoneInvBind[bi], setupP);
                Vec2 wp = Rig2DAffine2D.TransformPoint(in worldBones[bi], invP);
                rx += wp.X * w;
                ry += wp.Y * w;
            }
            else
            {
                rx += setupP.X * w;
                ry += setupP.Y * w;
            }
        }
        return new Vec2(rx, ry);
    }

    /// <summary>
    /// Computes the 4 skeleton-space corner positions (BL, BR, TR, TL) for a Region or Sequence attachment.
    /// </summary>
    public static void ComputeRegionQuadCorners(
        in Rig2DAttachmentData att,
        in Rig2DAffine2D boneWorld,
        out Vec2 bl,
        out Vec2 br,
        out Vec2 tr,
        out Vec2 tl)
    {
        float hw = att.Width * 0.5f;
        float hh = att.Height * 0.5f;
        Rig2DAffine2D attLocal = Rig2DAffine2D.FromTrsShear(att.X, att.Y, att.RotationDeg, att.ScaleX, att.ScaleY);
        Rig2DAffine2D combined = Rig2DAffine2D.Multiply(in boneWorld, in attLocal);

        bl = Rig2DAffine2D.TransformPoint(in combined, new Vec2(-hw, -hh));
        br = Rig2DAffine2D.TransformPoint(in combined, new Vec2(hw, -hh));
        tr = Rig2DAffine2D.TransformPoint(in combined, new Vec2(hw, hh));
        tl = Rig2DAffine2D.TransformPoint(in combined, new Vec2(-hw, hh));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float WrapAngleDeg(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        else if (deg < -180f) deg += 360f;
        return deg;
    }
}
