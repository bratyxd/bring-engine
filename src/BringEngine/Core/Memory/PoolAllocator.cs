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
    public int Capacity;
    public int FreeCount;
    public int ActiveCount;
    private byte _ownsMemory;

    public readonly bool IsValid => Slots != null;

    public static PoolAllocator<T> Create(ref LinearArena arena, int capacity)
    {
        T* slots = arena.Alloc<T>(capacity, 64);
        int* freeStack = arena.AllocUninitialized<int>(capacity, 16);
        for (int i = 0; i < capacity; i++)
            freeStack[i] = capacity - 1 - i;

        return new PoolAllocator<T>
        {
            Slots = slots,
            FreeStack = freeStack,
            Capacity = capacity,
            FreeCount = capacity,
            ActiveCount = 0,
            _ownsMemory = 0,
        };
    }

    public static PoolAllocator<T> CreateStandalone(int capacity)
    {
        nuint slotsBytes = LinearArena.AlignUp((nuint)sizeof(T) * (nuint)capacity, 64);
        nuint stackBytes = LinearArena.AlignUp((nuint)sizeof(int) * (nuint)capacity, 64);
        byte* block = (byte*)NativeMemoryTracker.AlignedAllocZeroed(slotsBytes + stackBytes, 64);
        T* slots = (T*)block;
        int* freeStack = (int*)(block + slotsBytes);
        for (int i = 0; i < capacity; i++)
            freeStack[i] = capacity - 1 - i;

        return new PoolAllocator<T>
        {
            Slots = slots,
            FreeStack = freeStack,
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
        ActiveCount++;
        Slots[index] = default;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReturnIndex(int index)
    {
        if ((uint)index >= (uint)Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));
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
            FreeStack[i] = Capacity - 1 - i;
        FreeCount = Capacity;
        ActiveCount = 0;
    }

    public void Dispose()
    {
        if (_ownsMemory != 0 && Slots != null)
        {
            nuint slotsBytes = LinearArena.AlignUp((nuint)sizeof(T) * (nuint)Capacity, 64);
            nuint stackBytes = LinearArena.AlignUp((nuint)sizeof(int) * (nuint)Capacity, 64);
            NativeMemoryTracker.AlignedFree(Slots, slotsBytes + stackBytes);
        }
        Slots = null;
        FreeStack = null;
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
        nuint totalNeeded = headerSize + LinearArena.AlignUp(byteCount == 0 ? 16u : byteCount, 16);
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
        BlockHeader* block = (BlockHeader*)((byte*)ptr - headerSize);
        if (block->Magic != MagicUsed)
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
        }

        // Coalesce with prev
        if (prev != null && (byte*)prev + prev->Size == (byte*)block)
        {
            prev->Size += block->Size;
            prev->Next = block->Next;
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
