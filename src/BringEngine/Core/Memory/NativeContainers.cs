using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace BringEngine.Core.Memory;

/// <summary>
/// Deterministic 64-bit FNV-1a string identifier for zero-allocation hot-path lookups.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct StringId : IEquatable<StringId>
{
    public readonly ulong Value;
    public static readonly StringId Empty = default;

    public bool IsValid => Value != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringId(ulong value) => Value = value;

    public static unsafe StringId Hash(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return Empty;
        ulong h = 14695981039346656037UL;
        int i = 0;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c > 0x7F) break;
            h ^= (byte)c;
            h *= 1099511628211UL;
        }
        if (i < text.Length)
        {
            byte* stackBuf = stackalloc byte[256];
            Span<byte> dst = new Span<byte>(stackBuf, 256);
            ReadOnlySpan<char> remaining = text.Slice(i);
            while (!remaining.IsEmpty)
            {
                bool isFinal = remaining.Length <= 85;
                Utf8.FromUtf16(
                    remaining, dst, out int charsRead, out int bytesWritten,
                    replaceInvalidSequences: true, isFinalBlock: isFinal);
                for (int b = 0; b < bytesWritten; b++)
                {
                    h ^= stackBuf[b];
                    h *= 1099511628211UL;
                }
                if (charsRead <= 0) break;
                remaining = remaining.Slice(charsRead);
            }
        }
        return new StringId(h == 0 ? 1UL : h);
    }

    public static StringId HashUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return Empty;
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < utf8.Length; i++)
        {
            h ^= utf8[i];
            h *= 1099511628211UL;
        }
        return new StringId(h == 0 ? 1UL : h);
    }

    public static implicit operator StringId(string s) => Hash(s.AsSpan());
    public bool Equals(StringId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is StringId other && Value == other.Value;
    public override int GetHashCode() => (int)(Value ^ (Value >> 32));
    public static bool operator ==(StringId a, StringId b) => a.Value == b.Value;
    public static bool operator !=(StringId a, StringId b) => a.Value != b.Value;
}

/// <summary>
/// Inline 64-byte UTF-8 unmanaged string (zero GC allocations).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FixedString64 : IEquatable<FixedString64>
{
    public const int MaxBytes = 60;
    public int Length;
    public fixed byte Data[MaxBytes];

    public static FixedString64 From(ReadOnlySpan<char> text)
    {
        FixedString64 s = default;
        if (text.IsEmpty) return s;
        Span<byte> dst = new Span<byte>(s.Data, MaxBytes);
        OperationStatus status = Utf8.FromUtf16(
            text, dst, out int charsRead, out int bytesWritten,
            replaceInvalidSequences: true, isFinalBlock: false);
        if (status == OperationStatus.NeedMoreData && bytesWritten < MaxBytes)
        {
            Utf8.FromUtf16(
                text.Slice(charsRead), dst.Slice(bytesWritten), out _, out int extra,
                replaceInvalidSequences: true, isFinalBlock: true);
            bytesWritten += extra;
        }
        s.Length = bytesWritten;
        return s;
    }

    public static FixedString64 FromUtf8(ReadOnlySpan<byte> utf8)
    {
        FixedString64 s = default;
        int len = utf8.Length;
        if (len > MaxBytes)
        {
            len = MaxBytes;
            int minLen = len > 3 ? len - 3 : 0;
            while (len > minLen && (utf8[len] & 0xC0) == 0x80)
                len--;
        }
        s.Length = len;
        if (len > 0)
            utf8.Slice(0, len).CopyTo(new Span<byte>(s.Data, len));
        return s;
    }

    public readonly StringId ToStringId()
    {
        fixed (byte* ptr = Data)
            return StringId.HashUtf8(new ReadOnlySpan<byte>(ptr, Length));
    }

    public readonly override string ToString()
    {
        if (Length <= 0) return string.Empty;
        fixed (byte* ptr = Data)
            return Encoding.UTF8.GetString(ptr, Length);
    }

    public readonly bool Equals(FixedString64 other)
    {
        if (Length != other.Length) return false;
        fixed (byte* a = Data)
        {
            byte* b = other.Data;
            for (int i = 0; i < Length; i++)
                if (a[i] != b[i]) return false;
        }
        return true;
    }
}

/// <summary>
/// Inline 256-byte UTF-8 unmanaged string (suitable for file paths, UI text, shader names).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FixedString256
{
    public const int MaxBytes = 252;
    public int Length;
    public fixed byte Data[MaxBytes];

    public static FixedString256 From(ReadOnlySpan<char> text)
    {
        FixedString256 s = default;
        if (text.IsEmpty) return s;
        Span<byte> dst = new Span<byte>(s.Data, MaxBytes);
        OperationStatus status = Utf8.FromUtf16(
            text, dst, out int charsRead, out int bytesWritten,
            replaceInvalidSequences: true, isFinalBlock: false);
        if (status == OperationStatus.NeedMoreData && bytesWritten < MaxBytes)
        {
            Utf8.FromUtf16(
                text.Slice(charsRead), dst.Slice(bytesWritten), out _, out int extra,
                replaceInvalidSequences: true, isFinalBlock: true);
            bytesWritten += extra;
        }
        s.Length = bytesWritten;
        return s;
    }

    public static FixedString256 FromUtf8(ReadOnlySpan<byte> utf8)
    {
        FixedString256 s = default;
        int len = utf8.Length;
        if (len > MaxBytes)
        {
            len = MaxBytes;
            int minLen = len > 3 ? len - 3 : 0;
            while (len > minLen && (utf8[len] & 0xC0) == 0x80)
                len--;
        }
        s.Length = len;
        if (len > 0)
            utf8.Slice(0, len).CopyTo(new Span<byte>(s.Data, len));
        return s;
    }

    public readonly ReadOnlySpan<byte> AsUtf8Span()
    {
        fixed (byte* ptr = Data)
            return new ReadOnlySpan<byte>(ptr, Length);
    }

    public readonly StringId ToStringId()
    {
        fixed (byte* ptr = Data)
            return StringId.HashUtf8(new ReadOnlySpan<byte>(ptr, Length));
    }

    public readonly override string ToString()
    {
        if (Length <= 0) return string.Empty;
        fixed (byte* ptr = Data)
            return Encoding.UTF8.GetString(ptr, Length);
    }
}

/// <summary>
/// Non-owning unmanaged view over contiguous native memory.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSlice<T> where T : unmanaged
{
    public T* Ptr;
    public int Length;

    public readonly bool IsEmpty => Ptr == null || Length <= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeSlice(T* ptr, int length)
    {
        Ptr = ptr;
        Length = length;
    }

    public readonly ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
#if DEBUG
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
#endif
            return ref Ptr[index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Span<T> AsSpan() => Ptr == null || Length <= 0 ? Span<T>.Empty : new Span<T>(Ptr, Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<T> AsReadOnlySpan() => Ptr == null || Length <= 0 ? ReadOnlySpan<T>.Empty : new ReadOnlySpan<T>(Ptr, Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly NativeSlice<T> Slice(int start, int length) => new NativeSlice<T>(Ptr + start, length);
}

/// <summary>
/// Contiguous unmanaged list backed by a LinearArena (fixed capacity) or standalone aligned memory (growable).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeList<T> where T : unmanaged
{
    public T* Data;
    public int Count;
    public int Capacity;
    private byte _ownsMemory;

    public readonly bool IsValid => Data != null;

    public static NativeList<T> CreateInArena(ref LinearArena arena, int capacity, nuint alignment = 16)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        T* data = capacity > 0 ? arena.AllocUninitialized<T>(capacity, alignment) : null;
        return new NativeList<T>
        {
            Data = data,
            Count = 0,
            Capacity = capacity,
            _ownsMemory = 0,
        };
    }

    public static NativeList<T> CreateGrowable(int initialCapacity = 16)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        int cap = initialCapacity < 4 ? 4 : initialCapacity;
        ulong bytes = (ulong)sizeof(T) * (ulong)cap;
        if (bytes > int.MaxValue)
            throw new OutOfMemoryException($"NativeList<{typeof(T).Name}> initial capacity {cap} exceeds maximum.");
        T* data = (T*)NativeMemoryTracker.AlignedAllocZeroed((nuint)bytes, 64);
        return new NativeList<T>
        {
            Data = data,
            Count = 0,
            Capacity = cap,
            _ownsMemory = 1,
        };
    }

    public readonly ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
#if DEBUG
            if ((uint)index >= (uint)Count)
                throw new IndexOutOfRangeException();
#endif
            return ref Data[index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(in T item)
    {
        if (Count >= Capacity)
        {
            if (_ownsMemory == 0)
                throw new InvalidOperationException($"Arena-backed NativeList<{typeof(T).Name}> exceeded fixed capacity {Capacity}.");
            EnsureCapacity(Count + 1);
        }
        Data[Count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T AddRef()
    {
        if (Count >= Capacity)
        {
            if (_ownsMemory == 0)
                throw new InvalidOperationException($"Arena-backed NativeList<{typeof(T).Name}> exceeded fixed capacity {Capacity}.");
            EnsureCapacity(Count + 1);
        }
        ref T slot = ref Data[Count++];
        slot = default;
        return ref slot;
    }

    public void EnsureCapacity(int minCapacity)
    {
        if (minCapacity <= Capacity) return;
        if (_ownsMemory == 0)
            throw new InvalidOperationException("Cannot grow an arena-backed NativeList.");
        int elemSize = sizeof(T);
        int maxElements = elemSize > 0 ? int.MaxValue / elemSize : int.MaxValue;
        if (minCapacity > maxElements)
            throw new OutOfMemoryException($"NativeList<{typeof(T).Name}> requested capacity {minCapacity} exceeds max elements ({maxElements}).");

        long newCap = Capacity <= 0 ? 8L : (long)Capacity;
        while (newCap < minCapacity)
        {
            newCap *= 2L;
            if (newCap > maxElements)
            {
                newCap = minCapacity;
                break;
            }
        }
        Grow((int)newCap);
    }

    private void Grow(int newCapacity)
    {
        nuint newBytes = (nuint)sizeof(T) * (nuint)newCapacity;
        T* newData = (T*)NativeMemoryTracker.AlignedAllocZeroed(newBytes, 64);
        if (Data != null)
        {
            if (Count > 0)
                Buffer.MemoryCopy(Data, newData, newBytes, (nuint)sizeof(T) * (nuint)Count);
            NativeMemoryTracker.AlignedFree(Data, (nuint)sizeof(T) * (nuint)Capacity);
        }
        Data = newData;
        Capacity = newCapacity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveAtSwapBack(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        int last = --Count;
        if (index != last)
            Data[index] = Data[last];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => Count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Span<T> AsSpan() => Data == null || Count <= 0 ? Span<T>.Empty : new Span<T>(Data, Count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly NativeSlice<T> AsSlice() => new NativeSlice<T>(Data, Count);

    public void Dispose()
    {
        if (_ownsMemory != 0 && Data != null)
            NativeMemoryTracker.AlignedFree(Data, (nuint)sizeof(T) * (nuint)Capacity);
        Data = null;
        Count = 0;
        Capacity = 0;
        _ownsMemory = 0;
    }
}

/// <summary>
/// Cache-friendly ECS component storage:
/// O(1) lookup, add, and swap-back removal while keeping dense values tightly packed for linear iteration.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSparseSet<T> where T : unmanaged
{
    public int* Sparse;       // Indexed by entityIndex -> denseIndex (-1 if absent)
    public uint* DenseIds;    // Parallel to DenseValues -> entityIndex
    public T* DenseValues;    // Tightly packed contiguous values for cache hits
    public int MaxEntityId;
    public int DenseCapacity;
    public int Count;

    public static NativeSparseSet<T> Create(ref LinearArena arena, int maxEntityId, int denseCapacity)
    {
        if (maxEntityId <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntityId), "maxEntityId must be positive.");
        if (denseCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(denseCapacity), "denseCapacity must be positive.");

        int* sparse = arena.AllocUninitialized<int>(maxEntityId, 64);
        for (int i = 0; i < maxEntityId; i++) sparse[i] = -1;

        uint* denseIds = arena.AllocUninitialized<uint>(denseCapacity, 64);
        T* denseValues = arena.Alloc<T>(denseCapacity, 64);

        return new NativeSparseSet<T>
        {
            Sparse = sparse,
            DenseIds = denseIds,
            DenseValues = denseValues,
            MaxEntityId = maxEntityId,
            DenseCapacity = denseCapacity,
            Count = 0,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Has(uint entityIndex)
    {
        if (entityIndex >= (uint)MaxEntityId) return false;
        int d = Sparse[entityIndex];
        return d >= 0 && d < Count && DenseIds[d] == entityIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetDenseIndex(uint entityIndex)
    {
        if (entityIndex >= (uint)MaxEntityId) return -1;
        return Sparse[entityIndex];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T AddOrReplace(uint entityIndex, in T value)
    {
        if (entityIndex >= (uint)MaxEntityId)
            throw new ArgumentOutOfRangeException(nameof(entityIndex));
        int existing = Sparse[entityIndex];
        if (existing >= 0 && existing < Count && DenseIds[existing] == entityIndex)
        {
            DenseValues[existing] = value;
            return ref DenseValues[existing];
        }

        if (Count >= DenseCapacity)
            throw new OutOfMemoryException($"NativeSparseSet<{typeof(T).Name}> dense capacity {DenseCapacity} exceeded.");

        int denseIdx = Count++;
        Sparse[entityIndex] = denseIdx;
        DenseIds[denseIdx] = entityIndex;
        DenseValues[denseIdx] = value;
        return ref DenseValues[denseIdx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref T GetRef(uint entityIndex)
    {
        if (entityIndex >= (uint)MaxEntityId)
            throw new ArgumentOutOfRangeException(nameof(entityIndex));
        int d = Sparse[entityIndex];
        if (d < 0 || d >= Count || DenseIds[d] != entityIndex)
            throw new InvalidOperationException($"Entity {entityIndex} does not have component {typeof(T).Name}.");
        return ref DenseValues[d];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T* TryGetPtr(uint entityIndex)
    {
        if (entityIndex >= (uint)MaxEntityId) return null;
        int d = Sparse[entityIndex];
        return d >= 0 ? DenseValues + d : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(uint entityIndex)
    {
        if (entityIndex >= (uint)MaxEntityId) return false;
        int d = Sparse[entityIndex];
        if (d < 0) return false;

        int last = --Count;
        if (d != last)
        {
            uint movedId = DenseIds[last];
            DenseIds[d] = movedId;
            DenseValues[d] = DenseValues[last];
            Sparse[movedId] = d;
        }

        Sparse[entityIndex] = -1;
        return true;
    }

    public void Clear()
    {
        for (int i = 0; i < Count; i++)
            Sparse[DenseIds[i]] = -1;
        Count = 0;
    }
}

/// <summary>
/// Open-addressing linear-probing unmanaged hash map keyed by ulong (StringId / Handle / Hash).
/// Zero GC allocations, arena-backed.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeHashMap<TValue> where TValue : unmanaged
{
    public ulong* Keys;
    public TValue* Values;
    public byte* States; // 0 = empty, 1 = occupied, 2 = tombstone
    public int Capacity; // Power of 2
    public int Mask;
    public int Count;

    public static NativeHashMap<TValue> Create(ref LinearArena arena, int minCapacity)
    {
        if (minCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(minCapacity));
        long target = (long)minCapacity * 2L;
        if (target > 0x4000_0000L)
            throw new OutOfMemoryException($"NativeHashMap<{typeof(TValue).Name}> capacity {minCapacity} exceeds maximum.");
        int cap = 16;
        while (cap < target) cap <<= 1;

        ulong* keys = arena.Alloc<ulong>(cap, 64);
        TValue* values = arena.Alloc<TValue>(cap, 64);
        byte* states = arena.Alloc<byte>(cap, 64);

        return new NativeHashMap<TValue>
        {
            Keys = keys,
            Values = values,
            States = states,
            Capacity = cap,
            Mask = cap - 1,
            Count = 0,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashSlot(ulong key, int mask)
    {
        key ^= key >> 33;
        key *= 0xff51afd7ed558ccdUL;
        key ^= key >> 33;
        key *= 0xc4ceb9fe1a85ec53UL;
        key ^= key >> 33;
        return (int)(key & (ulong)mask);
    }

    public bool TryGet(ulong key, out TValue value)
    {
        int slot = HashSlot(key, Mask);
        for (int probe = 0; probe < Capacity; probe++)
        {
            byte st = States[slot];
            if (st == 0)
            {
                value = default;
                return false;
            }
            if (st == 1 && Keys[slot] == key)
            {
                value = Values[slot];
                return true;
            }
            slot = (slot + 1) & Mask;
        }
        value = default;
        return false;
    }

    public TValue* TryGetPtr(ulong key)
    {
        int slot = HashSlot(key, Mask);
        for (int probe = 0; probe < Capacity; probe++)
        {
            byte st = States[slot];
            if (st == 0) return null;
            if (st == 1 && Keys[slot] == key) return Values + slot;
            slot = (slot + 1) & Mask;
        }
        return null;
    }

    public void Set(ulong key, in TValue value)
    {
        int slot = HashSlot(key, Mask);
        int firstTombstone = -1;

        for (int probe = 0; probe < Capacity; probe++)
        {
            byte st = States[slot];
            if (st == 0)
            {
                int target = firstTombstone >= 0 ? firstTombstone : slot;
                Keys[target] = key;
                Values[target] = value;
                States[target] = 1;
                Count++;
                return;
            }
            if (st == 1 && Keys[slot] == key)
            {
                Values[slot] = value;
                return;
            }
            if (st == 2 && firstTombstone < 0)
                firstTombstone = slot;

            slot = (slot + 1) & Mask;
        }

        if (firstTombstone >= 0)
        {
            Keys[firstTombstone] = key;
            Values[firstTombstone] = value;
            States[firstTombstone] = 1;
            Count++;
            return;
        }

        throw new OutOfMemoryException($"NativeHashMap<{typeof(TValue).Name}> capacity {Capacity} exhausted.");
    }

    public bool Remove(ulong key)
    {
        int slot = HashSlot(key, Mask);
        for (int probe = 0; probe < Capacity; probe++)
        {
            byte st = States[slot];
            if (st == 0) return false;
            if (st == 1 && Keys[slot] == key)
            {
                Count--;
                int next = (slot + 1) & Mask;
                if (States[next] == 0)
                {
                    States[slot] = 0;
                    // Also clear any contiguous preceding tombstones that now terminate at empty slot
                    int cur = (slot - 1) & Mask;
                    for (int back = 0; back < Capacity && States[cur] == 2; back++)
                    {
                        States[cur] = 0;
                        cur = (cur - 1) & Mask;
                    }
                }
                else
                {
                    States[slot] = 2;
                }
                return true;
            }
            slot = (slot + 1) & Mask;
        }
        return false;
    }

    public void Clear()
    {
        NativeMemory.Clear(States, (nuint)Capacity);
        Count = 0;
    }
}
