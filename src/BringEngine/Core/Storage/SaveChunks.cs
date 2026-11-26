using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Memory;
using BringEngine.Networking;

namespace BringEngine.Core.Storage;

/// <summary>
/// Non-owning view over a single versioned binary chunk inside a SavePayloadKind.Chunked save payload.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SaveChunkView
{
    public StringId Id;
    public ushort Version;
    public ushort Flags;
    public byte* DataPtr;
    public int Length;

    public readonly bool IsValid => Id.IsValid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> AsBytes()
        => DataPtr == null || Length <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(DataPtr, Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<T> AsSpan<T>() where T : unmanaged
    {
        if (DataPtr == null || Length <= 0) return ReadOnlySpan<T>.Empty;
        int elemSize = sizeof(T);
        if (elemSize <= 0 || (Length % elemSize) != 0) return ReadOnlySpan<T>.Empty;
        return new ReadOnlySpan<T>(DataPtr, Length / elemSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly NetReader CreateNetReader() => NetReader.Create(DataPtr, Length);
}

/// <summary>
/// Zero-GC unmanaged writer for tagged, individually versioned binary save chunks (ideal for large worlds,
/// tilemaps, voxels, and SoA entity dumps where JSON is too large/slow while keeping patch-safe schema evolution).
/// Each chunk has a 16-byte little-endian header: [ulong ChunkId][ushort Version][ushort Flags][int Length].
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SaveChunkWriter
{
    public const int ChunkHeaderSize = 16;

    public NativeList<byte> Buffer;
    public byte Overflow;

    public readonly int BytesWritten => Buffer.Count;
    public readonly bool HasOverflow => Overflow != 0;

    /// <summary>Creates a growable native chunk writer (must call Dispose() to release native memory).</summary>
    public static SaveChunkWriter CreateGrowable(int initialCapacity = 4096) => new SaveChunkWriter
    {
        Buffer = NativeList<byte>.CreateGrowable(initialCapacity < 64 ? 64 : initialCapacity),
        Overflow = 0,
    };

    /// <summary>Creates a fixed-capacity arena-backed chunk writer (freed automatically with the arena).</summary>
    public static SaveChunkWriter CreateInArena(ref LinearArena arena, int capacity, nuint alignment = 16) => new SaveChunkWriter
    {
        Buffer = NativeList<byte>.CreateInArena(ref arena, capacity, alignment),
        Overflow = 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> WrittenSpan()
        => Buffer.Data == null || Buffer.Count <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(Buffer.Data, Buffer.Count);

    public void Reset()
    {
        Buffer.Clear();
        Overflow = 0;
    }

    public bool WriteChunk(StringId id, ushort chunkVersion, ReadOnlySpan<byte> payload, ushort flags = 0)
    {
        if (Overflow != 0 || !id.IsValid || payload.Length < 0)
        {
            Overflow = 1;
            return false;
        }

        int needed = Buffer.Count + ChunkHeaderSize + payload.Length;
        if (needed > Buffer.Capacity)
        {
            try
            {
                Buffer.EnsureCapacity(needed);
            }
            catch
            {
                Overflow = 1;
                return false;
            }
        }

        byte* dst = Buffer.Data + Buffer.Count;
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(dst + 0, 8), id.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(dst + 8, 2), chunkVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(dst + 10, 2), flags);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(dst + 12, 4), payload.Length);
        if (!payload.IsEmpty)
            payload.CopyTo(new Span<byte>(dst + ChunkHeaderSize, payload.Length));

        Buffer.Count = needed;
        return true;
    }

    /// <summary>Writes a contiguous unmanaged SoA array directly as a versioned binary chunk (0 GC).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool WriteChunk<T>(StringId id, ushort chunkVersion, ReadOnlySpan<T> items, ushort flags = 0) where T : unmanaged
        => WriteChunk(id, chunkVersion, MemoryMarshal.AsBytes(items), flags);

    public void Dispose()
    {
        Buffer.Dispose();
        Overflow = 0;
    }
}

/// <summary>
/// Zero-GC bounds-checked reader for SavePayloadKind.Chunked binary payloads.
/// Safely skips unknown chunks and flags Malformed = 1 on truncated/corrupt input without throwing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SaveChunkReader
{
    public byte* Data;
    public int Length;
    public int Position;
    public byte Malformed;

    public readonly bool IsMalformed => Malformed != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SaveChunkReader Create(byte* data, int length) => new SaveChunkReader
    {
        Data = data,
        Length = length > 0 ? length : 0,
        Position = 0,
        Malformed = 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Position = 0;
        Malformed = 0;
    }

    public bool Next(out SaveChunkView chunk)
    {
        chunk = default;
        if (Malformed != 0 || Data == null || Position >= Length)
            return false;

        if (Position + SaveChunkWriter.ChunkHeaderSize > Length)
        {
            Malformed = 1;
            return false;
        }

        byte* hdr = Data + Position;
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(hdr + 0, 8));
        ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(hdr + 8, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(hdr + 10, 2));
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(hdr + 12, 4));

        int dataStart = Position + SaveChunkWriter.ChunkHeaderSize;
        if (payloadLen < 0 || dataStart + payloadLen > Length)
        {
            Malformed = 1;
            return false;
        }

        chunk = new SaveChunkView
        {
            Id = new StringId(id),
            Version = ver,
            Flags = flags,
            DataPtr = payloadLen > 0 ? Data + dataStart : null,
            Length = payloadLen,
        };
        Position = dataStart + payloadLen;
        return true;
    }

    /// <summary>
    /// Scans the payload from the beginning for a chunk matching the given StringId (0 GC).
    /// Leaves the reader's own cursor untouched so callers can look up chunks in any order.
    /// </summary>
    public readonly bool TryFind(StringId id, out SaveChunkView chunk)
    {
        var scan = Create(Data, Length);
        while (scan.Next(out SaveChunkView curr))
        {
            if (curr.Id == id)
            {
                chunk = curr;
                return true;
            }
        }
        chunk = default;
        return false;
    }
}
