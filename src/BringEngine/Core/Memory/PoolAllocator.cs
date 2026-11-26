using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BringEngine.Core.Memory;

/// <summary>
/// Fixed-capacity O(1) slot pool allocator backed by a LinearArena or dedicated native memory.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PoolAllocator<T> where T : unmanaged
{
    public T* Slots;
    public int* FreeStack;
    public byte* InUse;
    public int Capacity;
    public int FreeCount;
    public int ActiveCount;
    private byte _ownsMemory;

    public readonly bool IsValid => Slots != null;

    public static PoolAllocator<T> Create(ref LinearArena arena, int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "PoolAllocator capacity must be positive.");

        T* slots = arena.Alloc<T>(capacity, 64);
        int* freeStack = arena.AllocUninitialized<int>(capacity, 16);
        byte* inUse = arena.Alloc<byte>(capacity, 16);
        for (int i = 0; i < capacity; i++)
            freeStack[i] = capacity - 1 - i;

        return new PoolAllocator<T>
        {
            Slots = slots,
            FreeStack = freeStack,
            InUse = inUse,
            Capacity = capacity,
            FreeCount = capacity,
            ActiveCount = 0,
            _ownsMemory = 0,
        };
    }

    public static PoolAllocator<T> CreateStandalone(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "PoolAllocator capacity must be positive.");

        ulong slotsRaw = (ulong)sizeof(T) * (ulong)capacity;
        ulong stackRaw = (ulong)sizeof(int) * (ulong)capacity;
        ulong inUseRaw = (ulong)capacity;
        if (slotsRaw > nuint.MaxValue - 63 || stackRaw > nuint.MaxValue - 63 || inUseRaw > nuint.MaxValue - 63)
            throw new OutOfMemoryException("PoolAllocator size overflow.");

        nuint slotsBytes = LinearArena.AlignUp((nuint)slotsRaw, 64);
        nuint stackBytes = LinearArena.AlignUp((nuint)stackRaw, 64);
        nuint inUseBytes = LinearArena.AlignUp((nuint)inUseRaw, 64);
        if (slotsBytes > nuint.MaxValue - stackBytes || slotsBytes + stackBytes > nuint.MaxValue - inUseBytes)
            throw new OutOfMemoryException("PoolAllocator total size overflow.");

        byte* block = (byte*)NativeMemoryTracker.AlignedAllocZeroed(slotsBytes + stackBytes + inUseBytes, 64);
        T* slots = (T*)block;
        int* freeStack = (int*)(block + slotsBytes);
        byte* inUse = block + slotsBytes + stackBytes;
        for (int i = 0; i < capacity; i++)
            freeStack[i] = capacity - 1 - i;

        return new PoolAllocator<T>
        {
            Slots = slots,
            FreeStack = freeStack,
            InUse = inUse,
            Capacity = capacity,
            FreeCount = capacity,
            ActiveCount = 0,
            _ownsMemory = 1,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RentIndex()
    {
        if (FreeCount <= 0)
            throw new OutOfMemoryException($"PoolAllocator<{typeof(T).Name}> exhausted ({Capacity} slots).");
        int idx = FreeStack[--FreeCount];
        InUse[idx] = 1;
        ActiveCount++;
        Slots[idx] = default;
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRentIndex(out int index)
    {
        if (FreeCount <= 0)
        {
            index = -1;
            return false;
        }
        index = FreeStack[--FreeCount];
        InUse[index] = 1;
        ActiveCount++;
        Slots[index] = default;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReturnIndex(int index)
    {
        if ((uint)index >= (uint)Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (InUse == null || InUse[index] == 0 || FreeCount >= Capacity)
            throw new InvalidOperationException($"PoolAllocator<{typeof(T).Name}> double-free or unrented slot {index}.");
        InUse[index] = 0;
        FreeStack[FreeCount++] = index;
        ActiveCount--;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T* GetPtr(int index) => Slots + index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref T GetRef(int index) => ref Slots[index];

    public void Reset()
    {
        for (int i = 0; i < Capacity; i++)
        {
            FreeStack[i] = Capacity - 1 - i;
            if (InUse != null) InUse[i] = 0;
        }
        FreeCount = Capacity;
        ActiveCount = 0;
    }

    public void Dispose()
    {
        if (_ownsMemory != 0 && Slots != null)
        {
            nuint slotsBytes = LinearArena.AlignUp((nuint)sizeof(T) * (nuint)Capacity, 64);
            nuint stackBytes = LinearArena.AlignUp((nuint)sizeof(int) * (nuint)Capacity, 64);
            nuint inUseBytes = LinearArena.AlignUp((nuint)Capacity, 64);
            NativeMemoryTracker.AlignedFree(Slots, slotsBytes + stackBytes + inUseBytes);
        }
        Slots = null;
        FreeStack = null;
        InUse = null;
        Capacity = 0;
        FreeCount = 0;
        ActiveCount = 0;
        _ownsMemory = 0;
    }
}

/// <summary>
/// Variable-size native free-list allocator with address-ordered coalescing.
/// Ideal for dynamic allocations that outlive a single frame without touching the GC heap.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FreeListAllocator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockHeader
    {
        public nuint Size;        // Total block size including header
        public BlockHeader* Next; // Next free block (null if allocated or tail)
        public uint Magic;        // 0xB10CF8EE when free, 0xB10CA110 when allocated
        public uint Padding;
    }

    private const uint MagicFree = 0xB10CF8EE;
    private const uint MagicUsed = 0xB10CA110;

    public byte* BasePtr;
    public nuint Capacity;
    public nuint UsedBytes;
    public nuint PeakUsedBytes;
    private BlockHeader* _freeHead;

    public readonly bool IsValid => BasePtr != null;

    public static FreeListAllocator Create(nuint capacityBytes)
    {
        if (capacityBytes > nuint.MaxValue - 63)
            throw new OutOfMemoryException("FreeListAllocator capacity overflow.");
        nuint cap = LinearArena.AlignUp(capacityBytes < 4096 ? 4096u : capacityBytes, 64);
        byte* ptr = (byte*)NativeMemoryTracker.AlignedAllocZeroed(cap, 64);
        BlockHeader* head = (BlockHeader*)ptr;
        head->Size = cap;
        head->Next = null;
        head->Magic = MagicFree;

        return new FreeListAllocator
        {
            BasePtr = ptr,
            Capacity = cap,
            UsedBytes = 0,
            PeakUsedBytes = 0,
            _freeHead = head,
        };
    }

    public void* Alloc(nuint byteCount, bool zeroed = true)
    {
        if (BasePtr == null)
            throw new InvalidOperationException("FreeListAllocator is not initialized.");

        nuint headerSize = LinearArena.AlignUp((nuint)sizeof(BlockHeader), 16);
        nuint reqPayload = byteCount == 0 ? 16u : byteCount;
        if (reqPayload > nuint.MaxValue - 15)
            throw new OutOfMemoryException($"FreeListAllocator out of memory: requested {byteCount} bytes, used {UsedBytes}/{Capacity}.");
        nuint alignedPayload = LinearArena.AlignUp(reqPayload, 16);
        if (alignedPayload > Capacity || headerSize > Capacity - alignedPayload)
            throw new OutOfMemoryException($"FreeListAllocator out of memory: requested {byteCount} bytes, used {UsedBytes}/{Capacity}.");
        nuint totalNeeded = headerSize + alignedPayload;
        nuint minSplitSize = headerSize + 32;

        BlockHeader* prev = null;
        BlockHeader* curr = _freeHead;

        while (curr != null)
        {
            if (curr->Size >= totalNeeded)
            {
                nuint remaining = curr->Size - totalNeeded;
                if (remaining >= minSplitSize)
                {
                    BlockHeader* split = (BlockHeader*)((byte*)curr + totalNeeded);
                    split->Size = remaining;
                    split->Next = curr->Next;
                    split->Magic = MagicFree;

                    curr->Size = totalNeeded;
                    if (prev == null) _freeHead = split;
                    else prev->Next = split;
                }
                else
                {
                    if (prev == null) _freeHead = curr->Next;
                    else prev->Next = curr->Next;
                }

                curr->Next = null;
                curr->Magic = MagicUsed;

                UsedBytes += curr->Size;
                if (UsedBytes > PeakUsedBytes) PeakUsedBytes = UsedBytes;

                byte* payload = (byte*)curr + headerSize;
                if (zeroed)
                    NativeMemory.Clear(payload, curr->Size - headerSize);
                return payload;
            }

            prev = curr;
            curr = curr->Next;
        }

        throw new OutOfMemoryException($"FreeListAllocator out of memory: requested {byteCount} bytes, used {UsedBytes}/{Capacity}.");
    }

    public T* Alloc<T>(int count = 1) where T : unmanaged
    {
        if (count <= 0) return null;
        return (T*)Alloc((nuint)sizeof(T) * (nuint)count, zeroed: true);
    }

    public void Free(void* ptr)
    {
        if (ptr == null || BasePtr == null) return;

        nuint headerSize = LinearArena.AlignUp((nuint)sizeof(BlockHeader), 16);
        byte* bytePtr = (byte*)ptr;
        if (bytePtr < BasePtr + headerSize || bytePtr >= BasePtr + Capacity)
            throw new ArgumentException("Pointer does not belong to this FreeListAllocator.", nameof(ptr));
        if (((nuint)(bytePtr - BasePtr) & 15u) != 0)
            throw new ArgumentException("Misaligned pointer passed to FreeListAllocator.Free.", nameof(ptr));

        BlockHeader* block = (BlockHeader*)(bytePtr - headerSize);
        if (block->Magic != MagicUsed || block->Size < headerSize + 16 || (byte*)block + block->Size > BasePtr + Capacity)
            throw new InvalidOperationException("Double-free or corrupted FreeListAllocator block.");

        block->Magic = MagicFree;
        UsedBytes -= block->Size;

        // Address-ordered insertion + coalescing
        BlockHeader* prev = null;
        BlockHeader* curr = _freeHead;
        while (curr != null && curr < block)
        {
            prev = curr;
            curr = curr->Next;
        }

        block->Next = curr;
        if (prev == null) _freeHead = block;
        else prev->Next = block;

        // Coalesce with next
        if (curr != null && (byte*)block + block->Size == (byte*)curr)
        {
            block->Size += curr->Size;
            block->Next = curr->Next;
            curr->Magic = 0;
            curr->Next = null;
        }

        // Coalesce with prev
        if (prev != null && (byte*)prev + prev->Size == (byte*)block)
        {
            prev->Size += block->Size;
            prev->Next = block->Next;
            block->Magic = 0;
            block->Next = null;
        }
    }

    public void Reset()
    {
        if (BasePtr == null) return;
        BlockHeader* head = (BlockHeader*)BasePtr;
        head->Size = Capacity;
        head->Next = null;
        head->Magic = MagicFree;
        _freeHead = head;
        UsedBytes = 0;
    }

    public void Dispose()
    {
        if (BasePtr != null)
        {
            NativeMemoryTracker.AlignedFree(BasePtr, Capacity);
            BasePtr = null;
            Capacity = 0;
            UsedBytes = 0;
            _freeHead = null;
        }
    }
}
