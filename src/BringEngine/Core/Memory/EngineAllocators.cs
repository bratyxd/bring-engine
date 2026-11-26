using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BringEngine.Core.Memory;

[StructLayout(LayoutKind.Sequential)]
public struct MemoryTelemetry
{
    public nuint PersistentUsed;
    public nuint PersistentCapacity;
    public nuint FrameUsed;
    public nuint FramePeak;
    public nuint FrameCapacity;
    public nuint ScratchUsed;
    public nuint ScratchPeak;
    public nuint DynamicUsed;
    public nuint DynamicCapacity;
    public long GlobalLiveBytes;
    public long GlobalPeakBytes;
    public long GlobalLiveBlocks;
}

/// <summary>
/// Central allocator set created at engine startup.
/// - Persistent: lives for the entire lifetime of the application.
/// - Frame: reset in O(1) at the beginning of every frame (render queues, sorting, temp vertices, queries).
/// - Scratch: scoped SaveMarker/Rollback arena for decoding, parsing, and packing.
/// - Dynamic: coalescing native free-list allocator for variable-lived data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct EngineAllocators
{
    public LinearArena Persistent;
    public LinearArena Frame;      // Current Logic-thread write frame arena
    public LinearArena FrameBack;  // Previous frame's immutable read arena (for 2-thread Render pipeline)
    public LinearArena Scratch;
    public FreeListAllocator Dynamic;
    public int FrameSwapCount;

    public static EngineAllocators Create(
        nuint persistentBytes = 16 * 1024 * 1024,
        nuint frameBytes = 8 * 1024 * 1024,
        nuint scratchBytes = 16 * 1024 * 1024,
        nuint dynamicBytes = 16 * 1024 * 1024)
    {
        EngineAllocators alloc = default;
        try
        {
            alloc.Persistent = LinearArena.Create(persistentBytes, StringId.Hash("Persistent").Value);
            alloc.Frame = LinearArena.Create(frameBytes, StringId.Hash("FrameA").Value);
            alloc.FrameBack = LinearArena.Create(frameBytes, StringId.Hash("FrameB").Value);
            alloc.Scratch = LinearArena.Create(scratchBytes, StringId.Hash("Scratch").Value);
            alloc.Dynamic = FreeListAllocator.Create(dynamicBytes);
            alloc.FrameSwapCount = 0;
            return alloc;
        }
        catch
        {
            alloc.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginFrame()
    {
        Frame.Reset();
    }

    /// <summary>
    /// Swaps double-buffered Frame <-> FrameBack arenas at the 2-thread pipeline barrier and resets the new write arena in O(1).
    /// Logic Thread writes into Frame while Render Thread reads immutable RenderPacket data from FrameBack with zero locks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SwapFrameArenas()
    {
        LinearArena prevWrite = Frame;
        Frame = FrameBack;
        FrameBack = prevWrite;
        Frame.Reset();
        FrameSwapCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LinearArena CreateScopedArena(nuint bytes, ulong nameHash)
    {
        return LinearArena.Create(bytes, nameHash);
    }

    public readonly MemoryTelemetry GetTelemetry() => new MemoryTelemetry
    {
        PersistentUsed = Persistent.Offset,
        PersistentCapacity = Persistent.Capacity,
        FrameUsed = Frame.Offset,
        FramePeak = Frame.PeakOffset > FrameBack.PeakOffset ? Frame.PeakOffset : FrameBack.PeakOffset,
        FrameCapacity = Frame.Capacity,
        ScratchUsed = Scratch.Offset,
        ScratchPeak = Scratch.PeakOffset,
        DynamicUsed = Dynamic.UsedBytes,
        DynamicCapacity = Dynamic.Capacity,
        GlobalLiveBytes = NativeMemoryTracker.LiveBytes,
        GlobalPeakBytes = NativeMemoryTracker.PeakLiveBytes,
        GlobalLiveBlocks = NativeMemoryTracker.LiveBlocks,
    };

    public void Dispose()
    {
        Dynamic.Dispose();
        Scratch.Dispose();
        FrameBack.Dispose();
        Frame.Dispose();
        Persistent.Dispose();
    }
}
