using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BringEngine.Core.Memory;

/// <summary>
/// Saved offset inside a LinearArena for O(1) scoped rollbacks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ArenaMarker
{
    public readonly nuint Offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArenaMarker(nuint offset) => Offset = offset;
}

/// <summary>
/// 64-byte aligned bump-pointer native memory arena.
/// Supports O(1) allocation, O(1) scoped rollback, and O(1) full reset.
/// Zero managed heap allocations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct LinearArena
{
    public byte* BasePtr;
    public nuint Capacity;
    public nuint Offset;
    public nuint PeakOffset;
    public uint AllocCount;
    public uint ResetCount;
    public ulong NameHash;

    public readonly bool IsValid => BasePtr != null;
    public readonly nuint Remaining => Capacity - Offset;

    public static LinearArena Create(nuint capacityBytes, ulong nameHash = 0)
    {
        nuint alignedCap = AlignUp(capacityBytes == 0 ? 64u : capacityBytes, 64);
        byte* ptr = (byte*)NativeMemoryTracker.AlignedAllocZeroed(alignedCap, 64);
        return new LinearArena
        {
            BasePtr = ptr,
            Capacity = alignedCap,
            Offset = 0,
            PeakOffset = 0,
            AllocCount = 0,
            ResetCount = 0,
            NameHash = nameHash,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint AlignUp(nuint value, nuint alignment)
    {
        nuint mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void* AllocBytes(nuint byteCount, nuint alignment = 16, bool zeroed = true)
    {
        if (BasePtr == null)
            throw new InvalidOperationException("Attempted to allocate from an uninitialized or disposed LinearArena.");

        if (alignment < 1) alignment = 1;
        nuint alignedOffset = AlignUp(Offset, alignment);
        nuint newOffset = alignedOffset + byteCount;

        if (newOffset > Capacity)
            throw new OutOfMemoryException($"LinearArena overflow: requested {byteCount} bytes (align {alignment}), used {Offset}/{Capacity}.");

        byte* result = BasePtr + alignedOffset;
        Offset = newOffset;
        if (newOffset > PeakOffset) PeakOffset = newOffset;
        AllocCount++;

        if (zeroed && byteCount > 0)
            NativeMemory.Clear(result, byteCount);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* Alloc<T>(int count = 1, nuint alignment = 16) where T : unmanaged
    {
        if (count <= 0) return null;
        return (T*)AllocBytes((nuint)sizeof(T) * (nuint)count, alignment, zeroed: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* AllocUninitialized<T>(int count = 1, nuint alignment = 16) where T : unmanaged
    {
        if (count <= 0) return null;
        return (T*)AllocBytes((nuint)sizeof(T) * (nuint)count, alignment, zeroed: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeSlice<T> AllocSlice<T>(int count, nuint alignment = 16, bool zeroed = true) where T : unmanaged
    {
        if (count <= 0) return default;
        T* ptr = (T*)AllocBytes((nuint)sizeof(T) * (nuint)count, alignment, zeroed);
        return new NativeSlice<T>(ptr, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ArenaMarker SaveMarker() => new ArenaMarker(Offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Rollback(ArenaMarker marker)
    {
        if (marker.Offset > Offset)
            throw new InvalidOperationException("Cannot rollback LinearArena forward.");
        Offset = marker.Offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Offset = 0;
        ResetCount++;
    }

    public void Dispose()
    {
        if (BasePtr != null)
        {
            NativeMemoryTracker.AlignedFree(BasePtr, Capacity);
            BasePtr = null;
            Capacity = 0;
            Offset = 0;
        }
    }
}
