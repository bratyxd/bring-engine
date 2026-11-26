using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Networking;

/// <summary>
/// Zero-GC unmanaged little-endian binary packet writer over native or stack memory.
/// Safe against buffer exhaustion: sets Overflow = 1 instead of throwing or writing out of bounds.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NetWriter
{
    public byte* Data;
    public int Capacity;
    public int Position;
    public byte Overflow;

    public readonly int BytesWritten => Position;
    public readonly int Remaining => Capacity - Position;
    public readonly bool HasOverflow => Overflow != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NetWriter Create(byte* buffer, int capacity) => new NetWriter
    {
        Data = buffer,
        Capacity = capacity > 0 ? capacity : 0,
        Position = 0,
        Overflow = 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NetWriter CreateInArena(ref LinearArena arena, int capacity, nuint alignment = 16)
    {
        byte* ptr = capacity > 0 ? arena.AllocUninitialized<byte>(capacity, alignment) : null;
        return Create(ptr, capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Position = 0;
        Overflow = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> WrittenSpan()
        => Data == null || Position <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(Data, Position);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnsureSpace(int bytes)
    {
        if (Overflow != 0 || Data == null || bytes < 0 || Position < 0 || Position > Capacity || bytes > Capacity - Position)
        {
            Overflow = 1;
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte v)
    {
        if (!EnsureSpace(1)) return;
        Data[Position++] = v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16(short v)
    {
        if (!EnsureSpace(2)) return;
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(Data + Position, 2), v);
        Position += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(ushort v)
    {
        if (!EnsureSpace(2)) return;
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(Data + Position, 2), v);
        Position += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int v)
    {
        if (!EnsureSpace(4)) return;
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(Data + Position, 4), v);
        Position += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(uint v)
    {
        if (!EnsureSpace(4)) return;
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(Data + Position, 4), v);
        Position += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64(long v)
    {
        if (!EnsureSpace(8)) return;
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(Data + Position, 8), v);
        Position += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64(ulong v)
    {
        if (!EnsureSpace(8)) return;
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(Data + Position, 8), v);
        Position += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat(float v) => WriteInt32(BitConverter.SingleToInt32Bits(v));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDouble(double v) => WriteInt64(BitConverter.DoubleToInt64Bits(v));

    /// <summary>
    /// Unsigned 7-bit LEB128 varint (1..5 bytes). Compact for tick deltas, entity counts, and lengths.
    /// </summary>
    public void WriteVarUInt32(uint value)
    {
        while (value >= 0x80u)
        {
            WriteByte((byte)((value & 0x7Fu) | 0x80u));
            if (Overflow != 0) return;
            value >>= 7;
        }
        WriteByte((byte)value);
    }

    /// <summary>
    /// Signed ZigZag + LEB128 varint (compact for small positive and negative deltas).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVarInt32(int value)
    {
        uint zigzag = (uint)((value << 1) ^ (value >> 31));
        WriteVarUInt32(zigzag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFix32(Fix32 v) => WriteInt32(v.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFix64(Fix64 v) => WriteInt64(v.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixVec2(in FixVec2 v)
    {
        WriteInt32(v.X.Raw);
        WriteInt32(v.Y.Raw);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixVec3(in FixVec3 v)
    {
        WriteInt32(v.X.Raw);
        WriteInt32(v.Y.Raw);
        WriteInt32(v.Z.Raw);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixQuat(in FixQuat q)
    {
        WriteInt32(q.X.Raw);
        WriteInt32(q.Y.Raw);
        WriteInt32(q.Z.Raw);
        WriteInt32(q.W.Raw);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteEntityId(EntityId id) => WriteUInt32(id.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteStringId(StringId id) => WriteUInt64(id.Value);

    public void WriteBytesRaw(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        if (!EnsureSpace(bytes.Length)) return;
        bytes.CopyTo(new Span<byte>(Data + Position, bytes.Length));
        Position += bytes.Length;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteVarUInt32((uint)bytes.Length);
        if (Overflow != 0) return;
        WriteBytesRaw(bytes);
    }

    public void WriteFixedString64(in FixedString64 s)
    {
        int len = s.Length < 0 ? 0 : (s.Length > FixedString64.MaxBytes ? FixedString64.MaxBytes : s.Length);
        WriteByte((byte)len);
        if (Overflow != 0 || len == 0) return;
        fixed (byte* src = s.Data)
            WriteBytesRaw(new ReadOnlySpan<byte>(src, len));
    }

    public void WriteFixedString256(in FixedString256 s)
    {
        int len = s.Length < 0 ? 0 : (s.Length > FixedString256.MaxBytes ? FixedString256.MaxBytes : s.Length);
        WriteByte((byte)len);
        if (Overflow != 0 || len == 0) return;
        fixed (byte* src = s.Data)
            WriteBytesRaw(new ReadOnlySpan<byte>(src, len));
    }
}

/// <summary>
/// Zero-GC unmanaged little-endian binary packet reader over native or stack memory.
/// Safe against malformed/truncated packets: sets Overflow = 1 instead of throwing or reading out of bounds.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NetReader
{
    public byte* Data;
    public int Length;
    public int Position;
    public byte Overflow;

    public readonly int Remaining => Length - Position;
    public readonly bool HasOverflow => Overflow != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NetReader Create(byte* buffer, int length) => new NetReader
    {
        Data = buffer,
        Length = length > 0 ? length : 0,
        Position = 0,
        Overflow = 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnsureAvailable(int bytes)
    {
        if (Overflow != 0 || Data == null || bytes < 0 || Position < 0 || Position > Length || bytes > Length - Position)
        {
            Overflow = 1;
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (!EnsureAvailable(1)) return 0;
        return Data[Position++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadByte() != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16()
    {
        if (!EnsureAvailable(2)) return 0;
        short v = BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(Data + Position, 2));
        Position += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        if (!EnsureAvailable(2)) return 0;
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(Data + Position, 2));
        Position += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        if (!EnsureAvailable(4)) return 0;
        int v = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(Data + Position, 4));
        Position += 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32()
    {
        if (!EnsureAvailable(4)) return 0;
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(Data + Position, 4));
        Position += 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64()
    {
        if (!EnsureAvailable(8)) return 0;
        long v = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(Data + Position, 8));
        Position += 8;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64()
    {
        if (!EnsureAvailable(8)) return 0;
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(Data + Position, 8));
        Position += 8;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    public uint ReadVarUInt32()
    {
        uint result = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = ReadByte();
            if (Overflow != 0) return 0;
            if (i == 4 && b > 0x0Fu)
            {
                Overflow = 1;
                return 0;
            }
            result |= (uint)(b & 0x7Fu) << shift;
            if ((b & 0x80u) == 0)
                return result;
            shift += 7;
        }
        Overflow = 1;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadVarInt32()
    {
        uint z = ReadVarUInt32();
        return (int)((z >> 1) ^ (uint)-(int)(z & 1u));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Fix32 ReadFix32() => new Fix32(ReadInt32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Fix64 ReadFix64() => new Fix64(ReadInt64());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FixVec2 ReadFixVec2() => new FixVec2(ReadFix32(), ReadFix32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FixVec3 ReadFixVec3() => new FixVec3(ReadFix32(), ReadFix32(), ReadFix32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FixQuat ReadFixQuat() => new FixQuat(ReadFix32(), ReadFix32(), ReadFix32(), ReadFix32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityId ReadEntityId() => new EntityId(ReadUInt32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringId ReadStringId() => new StringId(ReadUInt64());

    public ReadOnlySpan<byte> ReadBytesRaw(int count)
    {
        if (count == 0) return ReadOnlySpan<byte>.Empty;
        if (!EnsureAvailable(count)) return ReadOnlySpan<byte>.Empty;
        var span = new ReadOnlySpan<byte>(Data + Position, count);
        Position += count;
        return span;
    }

    public ReadOnlySpan<byte> ReadBytes(int maxAllowed = int.MaxValue)
    {
        if (maxAllowed < 0)
        {
            Overflow = 1;
            return ReadOnlySpan<byte>.Empty;
        }
        uint len = ReadVarUInt32();
        if (Overflow != 0 || len > (uint)maxAllowed || len > (uint)Remaining)
        {
            Overflow = 1;
            return ReadOnlySpan<byte>.Empty;
        }
        return ReadBytesRaw((int)len);
    }

    public FixedString64 ReadFixedString64()
    {
        byte len = ReadByte();
        if (Overflow != 0 || len > FixedString64.MaxBytes)
        {
            Overflow = 1;
            return default;
        }
        ReadOnlySpan<byte> raw = ReadBytesRaw(len);
        return Overflow != 0 ? default : FixedString64.FromUtf8(raw);
    }

    public FixedString256 ReadFixedString256()
    {
        byte len = ReadByte();
        if (Overflow != 0 || len > FixedString256.MaxBytes)
        {
            Overflow = 1;
            return default;
        }
        ReadOnlySpan<byte> raw = ReadBytesRaw(len);
        return Overflow != 0 ? default : FixedString256.FromUtf8(raw);
    }
}

/// <summary>
/// Deterministic IEEE 802.3 CRC32 helper for wire packet integrity and rollback state checksums.
/// </summary>
public static class NetHash
{
    private static readonly uint[] s_crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1u) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data, uint seed = 0u)
    {
        uint crc = ~seed;
        uint[] table = s_crc32Table;
        for (int i = 0; i < data.Length; i++)
            crc = table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
        return ~crc;
    }
}
