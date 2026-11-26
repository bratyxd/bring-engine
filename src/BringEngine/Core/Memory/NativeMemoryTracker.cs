using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace BringEngine.Core.Memory;

/// <summary>
/// Thread-safe native memory allocation tracker.
/// Every native allocation in Bring Engine goes through this tracker so memory usage,
/// peak usage, and leak checks are exact.
/// </summary>
public static unsafe class NativeMemoryTracker
{
    private static long s_totalAllocatedBytes;
    private static long s_totalFreedBytes;
    private static long s_liveBytes;
    private static long s_peakLiveBytes;
    private static long s_liveBlocks;

    public static long TotalAllocatedBytes => Volatile.Read(ref s_totalAllocatedBytes);
    public static long TotalFreedBytes => Volatile.Read(ref s_totalFreedBytes);
    public static long LiveBytes => Volatile.Read(ref s_liveBytes);
    public static long PeakLiveBytes => Volatile.Read(ref s_peakLiveBytes);
    public static long LiveBlocks => Volatile.Read(ref s_liveBlocks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* AlignedAlloc(nuint byteCount, nuint alignment = 64)
    {
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a non-zero power of 2.");
        if ((ulong)byteCount > (ulong)long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "Allocation size exceeds Int64.MaxValue.");

        nuint nativeSize = byteCount == 0 ? alignment : byteCount;
        void* ptr = NativeMemory.AlignedAlloc(nativeSize, alignment);
        long size = (long)byteCount;
        Interlocked.Add(ref s_totalAllocatedBytes, size);
        long live = Interlocked.Add(ref s_liveBytes, size);
        Interlocked.Increment(ref s_liveBlocks);

        // Update peak atomically
        long peak = Volatile.Read(ref s_peakLiveBytes);
        while (live > peak)
        {
            long prev = Interlocked.CompareExchange(ref s_peakLiveBytes, live, peak);
            if (prev == peak) break;
            peak = prev;
        }

        return ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* AlignedAllocZeroed(nuint byteCount, nuint alignment = 64)
    {
        void* ptr = AlignedAlloc(byteCount, alignment);
        NativeMemory.Clear(ptr, byteCount == 0 ? alignment : byteCount);
        return ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AlignedFree(void* ptr, nuint byteCount)
    {
        if (ptr == null) return;
        NativeMemory.AlignedFree(ptr);
        long size = (long)byteCount;
        Interlocked.Add(ref s_totalFreedBytes, size);
        Interlocked.Add(ref s_liveBytes, -size);
        Interlocked.Decrement(ref s_liveBlocks);
    }

    public static void ResetStatsForTest()
    {
        Volatile.Write(ref s_totalAllocatedBytes, 0);
        Volatile.Write(ref s_totalFreedBytes, 0);
        Volatile.Write(ref s_liveBytes, 0);
        Volatile.Write(ref s_peakLiveBytes, 0);
        Volatile.Write(ref s_liveBlocks, 0);
    }
}
