using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Simulation.Rig2D;

/// <summary>
/// Unmanaged coalescing range allocator backed directly by a LinearArena.
/// Used by Rig2DTable to allocate &amp; recycle contiguous bone/slot/spring/override slices with 0 GC and 0 leaks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Rig2DRangePool
{
    public int* FreeStarts;
    public int* FreeCounts;
    public int FreeRangeCount;
    public int FreeRangeCap;
    public int TotalCapacity;
    public int UsedCount;

    public static Rig2DRangePool Create(ref LinearArena arena, int totalCapacity, int maxFreeRanges)
    {
        if (totalCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCapacity));
        if (maxFreeRanges < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFreeRanges));

        int* starts = arena.AllocUninitialized<int>(maxFreeRanges, 16);
        int* counts = arena.AllocUninitialized<int>(maxFreeRanges, 16);
        int initCount = 0;
        if (totalCapacity > 0)
        {
            starts[0] = 0;
            counts[0] = totalCapacity;
            initCount = 1;
        }
        return new Rig2DRangePool
        {
            FreeStarts = starts,
            FreeCounts = counts,
            FreeRangeCount = initCount,
            FreeRangeCap = maxFreeRanges,
            TotalCapacity = totalCapacity,
            UsedCount = 0,
        };
    }

    public int Allocate(int count)
    {
        if (count <= 0) return 0;
        for (int i = 0; i < FreeRangeCount; i++)
        {
            int c = FreeCounts[i];
            if (c < count) continue;

            int start = FreeStarts[i];
            if (c == count)
            {
                int tail = FreeRangeCount - 1 - i;
                for (int k = 0; k < tail; k++)
                {
                    FreeStarts[i + k] = FreeStarts[i + k + 1];
                    FreeCounts[i + k] = FreeCounts[i + k + 1];
                }
                FreeRangeCount--;
            }
            else
            {
                FreeStarts[i] = start + count;
                FreeCounts[i] = c - count;
            }
            UsedCount += count;
            return start;
        }
        return -1;
    }

    public void Free(int start, int count)
    {
        if (count <= 0 || start < 0 || start > TotalCapacity - count) return;

        int pos = 0;
        while (pos < FreeRangeCount && FreeStarts[pos] < start)
            pos++;

        // Reject overlapping or already-free ranges (safe double-free no-op)
        if (pos > 0 && FreeStarts[pos - 1] + FreeCounts[pos - 1] > start)
            return;
        if (pos < FreeRangeCount && start + count > FreeStarts[pos])
            return;

        bool mergeLeft = pos > 0 && (FreeStarts[pos - 1] + FreeCounts[pos - 1] == start);
        bool mergeRight = pos < FreeRangeCount && (start + count == FreeStarts[pos]);

        bool freed = false;
        if (mergeLeft && mergeRight)
        {
            FreeCounts[pos - 1] += count + FreeCounts[pos];
            int tail = FreeRangeCount - 1 - pos;
            for (int k = 0; k < tail; k++)
            {
                FreeStarts[pos + k] = FreeStarts[pos + k + 1];
                FreeCounts[pos + k] = FreeCounts[pos + k + 1];
            }
            FreeRangeCount--;
            freed = true;
        }
        else if (mergeLeft)
        {
            FreeCounts[pos - 1] += count;
            freed = true;
        }
        else if (mergeRight)
        {
            FreeStarts[pos] = start;
            FreeCounts[pos] += count;
            freed = true;
        }
        else if (FreeRangeCount < FreeRangeCap)
        {
            for (int k = FreeRangeCount; k > pos; k--)
            {
                FreeStarts[k] = FreeStarts[k - 1];
                FreeCounts[k] = FreeCounts[k - 1];
            }
            FreeStarts[pos] = start;
            FreeCounts[pos] = count;
            FreeRangeCount++;
            freed = true;
        }

        if (freed)
        {
            UsedCount -= count;
            if (UsedCount < 0) UsedCount = 0;
        }
    }
}

/// <summary>
/// Central native-arena-backed storage for Rig2D assets, live instances, pooled bone/slot/spring/override slices,
/// per-frame FFD deform deltas (in FrameArena), and per-frame fired animation events.
/// Zero OOP, zero GC in hot loops, zero native leaks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Rig2DTable
{
    public const int DefaultOverridesPerInstance = 4;

    // Assets (fixed-slot generational table)
    public Rig2DAssetData* Assets;
    public uint* AssetGens;
    public byte* AssetAlive;
    public int MaxAssets;
    public int LiveAssetCount;
    public NativeHashMap<uint> AssetByName;

    // Instances (dense swap-back array + generational handle map)
    public Rig2DInstanceData* Instances;
    public int* InstanceSlotToDense;
    public uint* InstanceGens;
    public uint* InstanceFreeSlots;
    public int InstanceCount;
    public int MaxInstances;
    public int InstanceFreeCount;
    public int InstanceNextSlot;

    // Coalescing pooled instance storage (allocated inside persistentArena)
    public Rig2DAffine2D* BoneWorld;
    public Rig2DRangePool BonePool;

    public Rig2DRuntimeSlotState* SlotStates;
    public int* SlotOverrideAttachment; // -1 = no manual override; -2 = explicitly hidden; >=0 = attachmentIndex
    public int* DrawOrder;
    public Rig2DRangePool SlotPool;

    public Rig2DSpringRuntimeState* SpringStates;
    public Rig2DRangePool SpringPool;

    public Rig2DBoneOverride* BoneOverrides;
    public Rig2DRangePool OverridePool;

    // Per-frame fired animation events ring (cleared at start of StepAll)
    public Rig2DFiredEvent* FiredEvents;
    public int FiredEventCount;
    public int MaxFiredEvents;

    // Per-frame FFD vertex deform deltas (allocated inside FrameArena during StepAll)
    public NativeSlice<Vec2> FrameDeformDeltas;
    public byte* FrameDeformArenaBase;
    public uint FrameDeformArenaReset;

    public static Rig2DTable Create(
        ref LinearArena persistentArena,
        int maxAssets = 128,
        int maxInstances = 256,
        int maxPooledBones = 4096,
        int maxPooledSlots = 4096,
        int maxPooledSprings = 1024,
        int maxPooledOverrides = 512,
        int maxFiredEvents = 512)
    {
        const int maxCap = (int)(HandlePacking.IndexMask + 1u);
        if (maxAssets < 1 || maxAssets > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxAssets), maxAssets, $"Capacity must be in [1, {maxCap}].");
        if (maxInstances < 1 || maxInstances > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxInstances), maxInstances, $"Capacity must be in [1, {maxCap}].");
        if (maxPooledBones < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPooledBones));
        if (maxPooledSlots < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPooledSlots));
        if (maxPooledSprings < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPooledSprings));
        if (maxPooledOverrides < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPooledOverrides));
        if (maxFiredEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFiredEvents));

        ArenaMarker mark = persistentArena.SaveMarker();
        try
        {
            uint* assetGens = persistentArena.AllocUninitialized<uint>(maxAssets, 16);
            for (int i = 0; i < maxAssets; i++) assetGens[i] = 1u;

            int* slotToDense = persistentArena.AllocUninitialized<int>(maxInstances, 64);
            uint* instGens = persistentArena.AllocUninitialized<uint>(maxInstances, 64);
            for (int i = 0; i < maxInstances; i++)
            {
                slotToDense[i] = -1;
                instGens[i] = 1u;
            }

            int* slotOverrides = persistentArena.AllocUninitialized<int>(maxPooledSlots, 64);
            for (int i = 0; i < maxPooledSlots; i++) slotOverrides[i] = -1;

            int rangeCap = Math.Max(64, maxInstances + 1);

            return new Rig2DTable
            {
                Assets = persistentArena.Alloc<Rig2DAssetData>(maxAssets, 64),
                AssetGens = assetGens,
                AssetAlive = persistentArena.Alloc<byte>(maxAssets, 16),
                MaxAssets = maxAssets,
                LiveAssetCount = 0,
                AssetByName = NativeHashMap<uint>.Create(ref persistentArena, maxAssets),

                Instances = persistentArena.Alloc<Rig2DInstanceData>(maxInstances, 64),
                InstanceSlotToDense = slotToDense,
                InstanceGens = instGens,
                InstanceFreeSlots = persistentArena.AllocUninitialized<uint>(maxInstances, 64),
                InstanceCount = 0,
                MaxInstances = maxInstances,
                InstanceFreeCount = 0,
                InstanceNextSlot = 0,

                BoneWorld = persistentArena.Alloc<Rig2DAffine2D>(maxPooledBones, 64),
                BonePool = Rig2DRangePool.Create(ref persistentArena, maxPooledBones, rangeCap),

                SlotStates = persistentArena.Alloc<Rig2DRuntimeSlotState>(maxPooledSlots, 64),
                SlotOverrideAttachment = slotOverrides,
                DrawOrder = persistentArena.Alloc<int>(maxPooledSlots, 64),
                SlotPool = Rig2DRangePool.Create(ref persistentArena, maxPooledSlots, rangeCap),

                SpringStates = persistentArena.Alloc<Rig2DSpringRuntimeState>(maxPooledSprings, 64),
                SpringPool = Rig2DRangePool.Create(ref persistentArena, maxPooledSprings, rangeCap),

                BoneOverrides = persistentArena.Alloc<Rig2DBoneOverride>(maxPooledOverrides, 64),
                OverridePool = Rig2DRangePool.Create(ref persistentArena, maxPooledOverrides, rangeCap),

                FiredEvents = persistentArena.Alloc<Rig2DFiredEvent>(maxFiredEvents, 64),
                FiredEventCount = 0,
                MaxFiredEvents = maxFiredEvents,

                FrameDeformDeltas = default,
                FrameDeformArenaBase = null,
                FrameDeformArenaReset = 0,
            };
        }
        catch
        {
            persistentArena.Rollback(mark);
            throw;
        }
    }
}
