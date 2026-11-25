using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

namespace BringEngine.Simulation.World;

/// <summary>
/// Structure-of-Arrays (SoA) 3D Transform table with parent-child hierarchy support.
/// Dense arrays guarantee sequential cache hits during hierarchy propagation, culling, and rendering.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TransformTable
{
    public int* EntityToDense;     // Sparse map: entityIndex -> denseIndex (-1 if none)
    public EntityId* DenseEntity;  // Parallel to SoA arrays
    public Vec3* LocalPosition;
    public Quat* LocalRotation;
    public Vec3* LocalScale;
    public EntityId* Parent;
    public byte* Depth;            // Hierarchy depth (0 = root)
    public byte* Dirty;
    public Mat4* WorldMatrix;      // 64-byte aligned world transform
    public Vec3* WorldPosition;    // Cached world translation for fast spatial queries

    public int MaxEntities;
    public int Capacity;
    public int Count;
    public byte MaxDepth;

    public static TransformTable Create(ref LinearArena arena, int maxEntities, int transformCapacity)
    {
        int* sparse = arena.AllocUninitialized<int>(maxEntities, 64);
        for (int i = 0; i < maxEntities; i++) sparse[i] = -1;

        return new TransformTable
        {
            EntityToDense = sparse,
            DenseEntity = arena.AllocUninitialized<EntityId>(transformCapacity, 64),
            LocalPosition = arena.Alloc<Vec3>(transformCapacity, 64),
            LocalRotation = arena.Alloc<Quat>(transformCapacity, 64),
            LocalScale = arena.Alloc<Vec3>(transformCapacity, 64),
            Parent = arena.Alloc<EntityId>(transformCapacity, 64),
            Depth = arena.Alloc<byte>(transformCapacity, 64),
            Dirty = arena.Alloc<byte>(transformCapacity, 64),
            WorldMatrix = arena.Alloc<Mat4>(transformCapacity, 64),
            WorldPosition = arena.Alloc<Vec3>(transformCapacity, 64),
            MaxEntities = maxEntities,
            Capacity = transformCapacity,
            Count = 0,
            MaxDepth = 0,
        };
    }
}

public static unsafe class TransformSystem
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDenseIndex(in TransformTable table, EntityId entity)
    {
        if (!entity.IsValid) return -1;
        uint idx = entity.Index;
        if (idx >= (uint)table.MaxEntities) return -1;
        int d = table.EntityToDense[idx];
        return (d >= 0 && d < table.Count && table.DenseEntity[d] == entity) ? d : -1;
    }

    public static int Add(
        ref TransformTable table,
        EntityId entity,
        Vec3 localPosition,
        Quat localRotation,
        Vec3 localScale,
        EntityId parent = default)
    {
        int existing = GetDenseIndex(in table, entity);
        if (existing >= 0)
        {
            table.LocalPosition[existing] = localPosition;
            table.LocalRotation[existing] = localRotation;
            table.LocalScale[existing] = localScale;
            table.Parent[existing] = parent;
            table.Dirty[existing] = 1;
            RecomputeDepth(ref table, existing);
            return existing;
        }

        if (table.Count >= table.Capacity)
            throw new OutOfMemoryException($"TransformTable capacity ({table.Capacity}) exceeded.");

        int d = table.Count++;
        table.EntityToDense[entity.Index] = d;
        table.DenseEntity[d] = entity;
        table.LocalPosition[d] = localPosition;
        table.LocalRotation[d] = localRotation;
        table.LocalScale[d] = localScale;
        table.Parent[d] = parent;
        table.Dirty[d] = 1;
        table.WorldMatrix[d] = Mat4.CreateTRS(localPosition, localRotation, localScale);
        table.WorldPosition[d] = localPosition;
        RecomputeDepth(ref table, d);
        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Add(ref TransformTable table, EntityId entity, Vec3 position)
        => Add(ref table, entity, position, Quat.Identity, Vec3.One, EntityId.Invalid);

    public static bool Remove(ref TransformTable table, EntityId entity)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return false;

        int last = --table.Count;
        if (d != last)
        {
            EntityId moved = table.DenseEntity[last];
            table.DenseEntity[d] = moved;
            table.LocalPosition[d] = table.LocalPosition[last];
            table.LocalRotation[d] = table.LocalRotation[last];
            table.LocalScale[d] = table.LocalScale[last];
            table.Parent[d] = table.Parent[last];
            table.Depth[d] = table.Depth[last];
            table.Dirty[d] = table.Dirty[last];
            table.WorldMatrix[d] = table.WorldMatrix[last];
            table.WorldPosition[d] = table.WorldPosition[last];
            table.EntityToDense[moved.Index] = d;
        }
        table.EntityToDense[entity.Index] = -1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocalPosition(ref TransformTable table, EntityId entity, Vec3 pos)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return;
        table.LocalPosition[d] = pos;
        table.Dirty[d] = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocalRotation(ref TransformTable table, EntityId entity, Quat rot)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return;
        table.LocalRotation[d] = rot;
        table.Dirty[d] = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocalScale(ref TransformTable table, EntityId entity, Vec3 scale)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return;
        table.LocalScale[d] = scale;
        table.Dirty[d] = 1;
    }

    public static void SetParent(ref TransformTable table, EntityId entity, EntityId parent)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return;
        table.Parent[d] = parent;
        table.Dirty[d] = 1;
        RecomputeAllDepths(ref table);
    }

    private static void RecomputeDepth(ref TransformTable table, int denseIdx)
    {
        byte depth = 0;
        EntityId p = table.Parent[denseIdx];
        int guard = 0;
        while (p.IsValid && guard < 32)
        {
            int pd = GetDenseIndex(in table, p);
            if (pd < 0) break;
            depth++;
            p = table.Parent[pd];
            guard++;
        }
        table.Depth[denseIdx] = depth;
        if (depth > table.MaxDepth) table.MaxDepth = depth;
    }

    public static void RecomputeAllDepths(ref TransformTable table)
    {
        table.MaxDepth = 0;
        for (int i = 0; i < table.Count; i++)
            RecomputeDepth(ref table, i);
    }

    /// <summary>
    /// Minimum transform count before PropagateHierarchy fans out depth passes across JobSystem workers.
    /// Below this threshold, single-threaded iteration runs inline with zero synchronization overhead.
    /// </summary>
    public const int ParallelThreshold = 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct TransformLevelJobCtx
    {
        public TransformTable* Table;
        public byte Level;
    }

    private static void PropagateRootRange(void* userData, int begin, int end, int workerIndex)
    {
        _ = workerIndex;
        ref TransformTable table = ref *((TransformLevelJobCtx*)userData)->Table;
        for (int i = begin; i < end; i++)
        {
            if (table.Depth[i] != 0) continue;
            Mat4 local = Mat4.CreateTRS(table.LocalPosition[i], table.LocalRotation[i], table.LocalScale[i]);
            table.WorldMatrix[i] = local;
            table.WorldPosition[i] = new Vec3(local.C3.X, local.C3.Y, local.C3.Z);
            table.Dirty[i] = 0;
        }
    }

    private static void PropagateChildRange(void* userData, int begin, int end, int workerIndex)
    {
        _ = workerIndex;
        TransformLevelJobCtx* ctx = (TransformLevelJobCtx*)userData;
        ref TransformTable table = ref *ctx->Table;
        byte level = ctx->Level;

        for (int i = begin; i < end; i++)
        {
            if (table.Depth[i] != level) continue;
            Mat4 local = Mat4.CreateTRS(table.LocalPosition[i], table.LocalRotation[i], table.LocalScale[i]);
            int pd = GetDenseIndex(in table, table.Parent[i]);
            Mat4 world = pd >= 0 ? Mat4.Multiply(in table.WorldMatrix[pd], in local) : local;
            table.WorldMatrix[i] = world;
            table.WorldPosition[i] = new Vec3(world.C3.X, world.C3.Y, world.C3.Z);
            table.Dirty[i] = 0;
        }
    }

    /// <summary>
    /// Cache-linear, non-recursive hierarchy propagation by depth level (roots first, then children).
    /// Optionally parallelizes each depth pass when Count >= ParallelThreshold and jobs != null;
    /// produces bit-exact identical matrices to single-threaded execution. Zero heap allocations.
    /// </summary>
    public static void PropagateHierarchy(ref TransformTable table, JobScheduler* jobs = null)
    {
        int count = table.Count;
        if (count == 0) return;

        fixed (TransformTable* tPtr = &table)
        {
            var ctx = new TransformLevelJobCtx { Table = tPtr, Level = 0 };
            JobSystem.ParallelFor(
                jobs,
                count,
                batchSize: 256,
                userData: &ctx,
                callback: &PropagateRootRange,
                minParallelCount: ParallelThreshold);

            for (byte level = 1; level <= table.MaxDepth; level++)
            {
                ctx.Level = level;
                JobSystem.ParallelFor(
                    jobs,
                    count,
                    batchSize: 256,
                    userData: &ctx,
                    callback: &PropagateChildRange,
                    minParallelCount: ParallelThreshold);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Mat4 GetWorldMatrix(in TransformTable table, EntityId entity)
    {
        int d = GetDenseIndex(in table, entity);
        return d >= 0 ? table.WorldMatrix[d] : Mat4.Identity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 GetWorldPosition(in TransformTable table, EntityId entity)
    {
        int d = GetDenseIndex(in table, entity);
        return d >= 0 ? table.WorldPosition[d] : Vec3.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 GetWorldForward(in TransformTable table, EntityId entity)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return Vec3.Forward;
        Vec4 c2 = table.WorldMatrix[d].C2;
        return new Vec3(-c2.X, -c2.Y, -c2.Z).Normalized();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 GetWorldRight(in TransformTable table, EntityId entity)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return Vec3.Right;
        Vec4 c0 = table.WorldMatrix[d].C0;
        return new Vec3(c0.X, c0.Y, c0.Z).Normalized();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 GetWorldUp(in TransformTable table, EntityId entity)
    {
        int d = GetDenseIndex(in table, entity);
        if (d < 0) return Vec3.Up;
        Vec4 c1 = table.WorldMatrix[d].C1;
        return new Vec3(c1.X, c1.Y, c1.Z).Normalized();
    }
}
