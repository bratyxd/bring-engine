using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;

namespace BringEngine.Simulation.World;

/// <summary>
/// Contiguous generational entity storage.
/// Supports O(1) spawn, O(1) destroy with generation bump, and dense linear iteration.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct EntityTable
{
    public uint* Generations;
    public byte* AliveFlags;
    public SceneId* OwnerScenes;
    public uint* FreeStack;
    public EntityId* DenseAlive;
    public int* SlotToDense;
    public int Capacity;
    public int FreeCount;
    public int NextSlot;
    public int AliveCount;

    public static EntityTable Create(ref LinearArena arena, int maxEntities)
    {
        uint* gens = arena.AllocUninitialized<uint>(maxEntities, 64);
        byte* alive = arena.Alloc<byte>(maxEntities, 64);
        SceneId* scenes = arena.Alloc<SceneId>(maxEntities, 64);
        uint* freeStack = arena.AllocUninitialized<uint>(maxEntities, 64);
        EntityId* denseAlive = arena.AllocUninitialized<EntityId>(maxEntities, 64);
        int* slotToDense = arena.AllocUninitialized<int>(maxEntities, 64);

        for (int i = 0; i < maxEntities; i++)
        {
            gens[i] = 1;
            slotToDense[i] = -1;
        }

        return new EntityTable
        {
            Generations = gens,
            AliveFlags = alive,
            OwnerScenes = scenes,
            FreeStack = freeStack,
            DenseAlive = denseAlive,
            SlotToDense = slotToDense,
            Capacity = maxEntities,
            FreeCount = 0,
            NextSlot = 0,
            AliveCount = 0,
        };
    }
}

public static unsafe class EntitySystem
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityId Spawn(ref EntityTable table, SceneId ownerScene = default)
    {
        uint slot;
        if (table.FreeCount > 0)
        {
            slot = table.FreeStack[--table.FreeCount];
        }
        else
        {
            if (table.NextSlot >= table.Capacity)
                throw new OutOfMemoryException($"EntityTable capacity ({table.Capacity}) exceeded.");
            slot = (uint)table.NextSlot++;
        }

        uint gen = table.Generations[slot];
        EntityId id = EntityId.Create(slot, gen);
        table.AliveFlags[slot] = 1;
        table.OwnerScenes[slot] = ownerScene;

        int denseIdx = table.AliveCount++;
        table.DenseAlive[denseIdx] = id;
        table.SlotToDense[slot] = denseIdx;
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlive(in EntityTable table, EntityId id)
    {
        if (!id.IsValid) return false;
        uint slot = id.Index;
        if (slot >= (uint)table.Capacity) return false;
        return table.AliveFlags[slot] != 0 && table.Generations[slot] == id.Generation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SceneId GetOwnerScene(in EntityTable table, EntityId id)
    {
        if (!IsAlive(in table, id)) return SceneId.Invalid;
        return table.OwnerScenes[id.Index];
    }

    public static bool Destroy(ref EntityTable table, EntityId id)
    {
        if (!IsAlive(in table, id)) return false;
        uint slot = id.Index;
        table.AliveFlags[slot] = 0;
        table.OwnerScenes[slot] = SceneId.Invalid;

        uint nextGen = (table.Generations[slot] + 1) & HandlePacking.GenerationMask;
        table.Generations[slot] = nextGen == 0 ? 1u : nextGen;

        int denseIdx = table.SlotToDense[slot];
        int lastDense = --table.AliveCount;
        if (denseIdx != lastDense)
        {
            EntityId moved = table.DenseAlive[lastDense];
            table.DenseAlive[denseIdx] = moved;
            table.SlotToDense[moved.Index] = denseIdx;
        }
        table.SlotToDense[slot] = -1;
        table.FreeStack[table.FreeCount++] = slot;
        return true;
    }

    /// <summary>
    /// Collects all alive entities owned by a specific scene into a frame/scratch arena slice.
    /// </summary>
    public static NativeSlice<EntityId> CollectByScene(in EntityTable table, SceneId scene, ref LinearArena tempArena)
    {
        int count = 0;
        for (int i = 0; i < table.AliveCount; i++)
        {
            if (table.OwnerScenes[table.DenseAlive[i].Index] == scene)
                count++;
        }

        if (count == 0) return default;
        var result = tempArena.AllocSlice<EntityId>(count, 16, zeroed: false);
        int write = 0;
        for (int i = 0; i < table.AliveCount; i++)
        {
            EntityId e = table.DenseAlive[i];
            if (table.OwnerScenes[e.Index] == scene)
                result[write++] = e;
        }
        return result;
    }
}
