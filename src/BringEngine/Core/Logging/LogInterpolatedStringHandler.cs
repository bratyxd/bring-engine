using System;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Core.Logging;

/// <summary>
/// Stack-allocated zero-GC UTF-8 formatter backing all Bring Engine interpolated log handlers.
/// Writes directly into a fixed 496-byte inline buffer with no managed string allocations.
/// </summary>
public unsafe ref struct LogUtf8Builder
{
    private byte* _dst;
    private int _cap;
    public int Length;
    public byte Truncated;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LogUtf8Builder(byte* dst, int capacity)
    {
        _dst = dst;
        _cap = capacity;
        Length = 0;
        Truncated = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> WrittenSpan()
        => _dst == null || Length <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_dst, Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendByte(byte b)
    {
        if (Length < _cap)
            _dst[Length++] = b;
        else
            Truncated = 1;
    }

    public void AppendUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return;
        int rem = _cap - Length;
        if (rem <= 0)
        {
            Truncated = 1;
            return;
        }
        int copy = utf8.Length <= rem ? utf8.Length : rem;
        utf8.Slice(0, copy).CopyTo(new Span<byte>(_dst + Length, copy));
        Length += copy;
        if (copy < utf8.Length) Truncated = 1;
    }

    public void AppendChars(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;
        int rem = _cap - Length;
        if (rem <= 0)
        {
            Truncated = 1;
            return;
        }

        // Fast ASCII path (almost all log literals)
        int i = 0;
        byte* dst = _dst + Length;
        int maxAscii = chars.Length < rem ? chars.Length : rem;
        while (i < maxAscii)
        {
            char c = chars[i];
            if (c > 0x7F) break;
            dst[i] = (byte)c;
            i++;
        }
        Length += i;
        if (i == chars.Length) return;
        if (Length >= _cap)
        {
            Truncated = 1;
            return;
        }

        // General UTF-8 path for non-ASCII characters
        ReadOnlySpan<char> tail = chars.Slice(i);
        Span<byte> dstSpan = new Span<byte>(_dst + Length, _cap - Length);
        if (Encoding.UTF8.TryGetBytes(tail, dstSpan, out int written))
        {
            Length += written;
        }
        else
        {
            // Fit as many whole chars as possible without throwing
            int fit = tail.Length;
            while (fit > 0 && Encoding.UTF8.GetByteCount(tail.Slice(0, fit)) > dstSpan.Length)
                fit--;
            if (fit > 0)
                Length += Encoding.UTF8.GetBytes(tail.Slice(0, fit), dstSpan);
            Truncated = 1;
        }
    }

    public void AppendBool(bool v)
        => AppendUtf8(v ? "true"u8 : "false"u8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendIUtf8<T>(T value, ReadOnlySpan<char> format = default) where T : IUtf8SpanFormattable
    {
        int rem = _cap - Length;
        if (rem <= 0)
        {
            Truncated = 1;
            return;
        }
        if (value.TryFormat(new Span<byte>(_dst + Length, rem), out int written, format, CultureInfo.InvariantCulture))
            Length += written;
        else
            Truncated = 1;
    }

    private void AppendFloat(float value, ReadOnlySpan<char> format = default)
        => AppendIUtf8(value, format);

    private void AppendFix32(Fix32 v, ReadOnlySpan<char> format = default)
        => AppendIUtf8(v.ToFloat(), format.IsEmpty ? "F3" : format);

    private void AppendFix64(Fix64 v, ReadOnlySpan<char> format = default)
        => AppendIUtf8(v.ToDouble(), format.IsEmpty ? "F4" : format);

    private void AppendVec2(float x, float y)
    {
        AppendByte((byte)'(');
        AppendFloat(x, "F2");
        AppendUtf8(", "u8);
        AppendFloat(y, "F2");
        AppendByte((byte)')');
    }

    private void AppendVec3(float x, float y, float z)
    {
        AppendByte((byte)'(');
        AppendFloat(x, "F2");
        AppendUtf8(", "u8);
        AppendFloat(y, "F2");
        AppendUtf8(", "u8);
        AppendFloat(z, "F2");
        AppendByte((byte)')');
    }

    private void AppendHandle(ReadOnlySpan<byte> prefix, uint raw)
    {
        if (raw == 0)
        {
            AppendUtf8(prefix);
            AppendUtf8("(Invalid)"u8);
            return;
        }
        AppendUtf8(prefix);
        AppendByte((byte)'(');
        AppendIUtf8(HandlePacking.UnpackIndex(raw));
        AppendUtf8(":g"u8);
        AppendIUtf8(HandlePacking.UnpackGeneration(raw));
        AppendByte((byte)')');
    }

    /// <summary>
    /// Zero-GC generic formatter: CoreCLR JIT constant-folds typeof(T) checks into direct calls
    /// with zero boxing for all primitives, Bring Engine fixed-math/vectors/strings/handles, and common BCL structs.
    /// </summary>
    public void AppendFormatted<T>(T value, ReadOnlySpan<char> format = default)
    {
        if (typeof(T) == typeof(int)) { AppendIUtf8(Unsafe.As<T, int>(ref value), format); return; }
        if (typeof(T) == typeof(uint)) { AppendIUtf8(Unsafe.As<T, uint>(ref value), format); return; }
        if (typeof(T) == typeof(long)) { AppendIUtf8(Unsafe.As<T, long>(ref value), format); return; }
        if (typeof(T) == typeof(ulong)) { AppendIUtf8(Unsafe.As<T, ulong>(ref value), format); return; }
        if (typeof(T) == typeof(float)) { AppendIUtf8(Unsafe.As<T, float>(ref value), format); return; }
        if (typeof(T) == typeof(double)) { AppendIUtf8(Unsafe.As<T, double>(ref value), format); return; }
        if (typeof(T) == typeof(short)) { AppendIUtf8(Unsafe.As<T, short>(ref value), format); return; }
        if (typeof(T) == typeof(ushort)) { AppendIUtf8(Unsafe.As<T, ushort>(ref value), format); return; }
        if (typeof(T) == typeof(byte)) { AppendIUtf8(Unsafe.As<T, byte>(ref value), format); return; }
        if (typeof(T) == typeof(sbyte)) { AppendIUtf8(Unsafe.As<T, sbyte>(ref value), format); return; }
        if (typeof(T) == typeof(nint)) { AppendIUtf8(Unsafe.As<T, nint>(ref value), format); return; }
        if (typeof(T) == typeof(nuint)) { AppendIUtf8(Unsafe.As<T, nuint>(ref value), format); return; }
        if (typeof(T) == typeof(bool)) { AppendBool(Unsafe.As<T, bool>(ref value)); return; }
        if (typeof(T) == typeof(char))
        {
            char c = Unsafe.As<T, char>(ref value);
            AppendChars(new ReadOnlySpan<char>(&c, 1));
            return;
        }
        if (typeof(T) == typeof(Guid)) { AppendIUtf8(Unsafe.As<T, Guid>(ref value), format); return; }
        if (typeof(T) == typeof(DateTime)) { AppendIUtf8(Unsafe.As<T, DateTime>(ref value), format); return; }
        if (typeof(T) == typeof(TimeSpan)) { AppendIUtf8(Unsafe.As<T, TimeSpan>(ref value), format); return; }

        // Bring Engine unmanaged types (zero GC)
        if (typeof(T) == typeof(Fix32)) { AppendFix32(Unsafe.As<T, Fix32>(ref value), format); return; }
        if (typeof(T) == typeof(Fix64)) { AppendFix64(Unsafe.As<T, Fix64>(ref value), format); return; }
        if (typeof(T) == typeof(FixVec2))
        {
            FixVec2 v = Unsafe.As<T, FixVec2>(ref value);
            AppendVec2(v.X.ToFloat(), v.Y.ToFloat());
            return;
        }
        if (typeof(T) == typeof(FixVec3))
        {
            FixVec3 v = Unsafe.As<T, FixVec3>(ref value);
            AppendVec3(v.X.ToFloat(), v.Y.ToFloat(), v.Z.ToFloat());
            return;
        }
        if (typeof(T) == typeof(Vec2))
        {
            Vec2 v = Unsafe.As<T, Vec2>(ref value);
            AppendVec2(v.X, v.Y);
            return;
        }
        if (typeof(T) == typeof(Vec3))
        {
            Vec3 v = Unsafe.As<T, Vec3>(ref value);
            AppendVec3(v.X, v.Y, v.Z);
            return;
        }
        if (typeof(T) == typeof(FixedString64))
        {
            FixedString64 s = Unsafe.As<T, FixedString64>(ref value);
            if (s.Length > 0) AppendUtf8(new ReadOnlySpan<byte>(s.Data, s.Length));
            return;
        }
        if (typeof(T) == typeof(FixedString256))
        {
            FixedString256 s = Unsafe.As<T, FixedString256>(ref value);
            if (s.Length > 0) AppendUtf8(new ReadOnlySpan<byte>(s.Data, s.Length));
            return;
        }
        if (typeof(T) == typeof(StringId))
        {
            AppendUtf8("0x"u8);
            AppendIUtf8(Unsafe.As<T, StringId>(ref value).Value, "X16");
            return;
        }
        if (typeof(T) == typeof(EntityId)) { AppendHandle("Entity"u8, Unsafe.As<T, EntityId>(ref value).Raw); return; }
        if (typeof(T) == typeof(SceneId)) { AppendHandle("Scene"u8, Unsafe.As<T, SceneId>(ref value).Raw); return; }
        if (typeof(T) == typeof(BundleId)) { AppendHandle("Bundle"u8, Unsafe.As<T, BundleId>(ref value).Raw); return; }
        if (typeof(T) == typeof(JobHandle)) { AppendHandle("Job"u8, Unsafe.As<T, JobHandle>(ref value).Raw); return; }
        if (typeof(T) == typeof(HttpRequestHandle)) { AppendHandle("HttpReq"u8, Unsafe.As<T, HttpRequestHandle>(ref value).Raw); return; }
        if (typeof(T) == typeof(WebSocketHandle)) { AppendHandle("WebSocket"u8, Unsafe.As<T, WebSocketHandle>(ref value).Raw); return; }
        if (typeof(T) == typeof(LogFileHandle)) { AppendHandle("LogFile"u8, Unsafe.As<T, LogFileHandle>(ref value).Raw); return; }
        if (typeof(T) == typeof(LogLevel))
        {
            AppendUtf8(LogSystem.GetLevelTagUtf8(Unsafe.As<T, LogLevel>(ref value)));
            return;
        }
        if (typeof(T) == typeof(string))
        {
            string? s = Unsafe.As<T, string?>(ref value);
            if (s != null) AppendChars(s.AsSpan());
            return;
        }

        // Cold-path fallback for arbitrary user types/enums
        string? fallback = value?.ToString();
        if (fallback != null) AppendChars(fallback.AsSpan());
    }
}

[InlineArray(LogEntry.MaxMessageBytes)]
internal struct LogStackBuffer
{
    private byte _element0;
}

/// <summary>
/// Compiler-recognized zero-GC interpolated string handler for LogSystem.Write(state, level, $"...").
/// When level &lt; state->MinLevel, sets shouldAppend = false so NO arguments are evaluated and 0 bytes are formatted.
/// </summary>
[InterpolatedStringHandler]
public unsafe ref struct LogInterpolatedStringHandler
{
    private LogStackBuffer _buf;
    internal int Length;
    internal byte Truncated;
    internal readonly bool Enabled;

    public LogInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, LogLevel level, out bool shouldAppend)
    {
        _ = literalLength;
        _ = formattedCount;
        _buf = default;
        Length = 0;
        Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, level);
        shouldAppend = Enabled;
    }

    public LogInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, LogFileHandle file, LogLevel level, out bool shouldAppend)
    {
        _ = literalLength;
        _ = formattedCount;
        _ = file;
        _buf = default;
        Length = 0;
        Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, level);
        shouldAppend = Enabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte* DataPtr()
        => (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref Unsafe.AsRef(in _buf)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogUtf8Builder CreateBuilder()
    {
        byte* ptr = (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref _buf));
        return new LogUtf8Builder(ptr, LogEntry.MaxMessageBytes) { Length = Length, Truncated = Truncated };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Commit(in LogUtf8Builder b)
    {
        Length = b.Length;
        Truncated = b.Truncated;
    }

    public void AppendLiteral(string s) { var b = CreateBuilder(); b.AppendChars(s.AsSpan()); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<char> v) { var b = CreateBuilder(); b.AppendChars(v); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<byte> v) { var b = CreateBuilder(); b.AppendUtf8(v); Commit(in b); }
    public void AppendFormatted(string? v) { if (v == null) return; var b = CreateBuilder(); b.AppendChars(v.AsSpan()); Commit(in b); }
    public void AppendFormatted<T>(T v) { var b = CreateBuilder(); b.AppendFormatted(v); Commit(in b); }
    public void AppendFormatted<T>(T v, string? format) { var b = CreateBuilder(); b.AppendFormatted(v, format.AsSpan()); Commit(in b); }
}

/// <summary>Zero-GC level-filtered handler for LogSystem.Trace (skips all argument evaluation if Trace is disabled).</summary>
[InterpolatedStringHandler]
public unsafe ref struct LogTraceInterpolatedStringHandler
{
    private LogStackBuffer _buf;
    internal int Length;
    internal byte Truncated;
    internal readonly bool Enabled;

    public LogTraceInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, out bool shouldAppend)
    {
        _ = literalLength; _ = formattedCount; _buf = default; Length = 0; Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, LogLevel.Trace); shouldAppend = Enabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte* DataPtr() => (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref Unsafe.AsRef(in _buf)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogUtf8Builder CreateBuilder() => new LogUtf8Builder((byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref _buf)), LogEntry.MaxMessageBytes) { Length = Length, Truncated = Truncated };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Commit(in LogUtf8Builder b) { Length = b.Length; Truncated = b.Truncated; }

    public void AppendLiteral(string s) { var b = CreateBuilder(); b.AppendChars(s.AsSpan()); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<char> v) { var b = CreateBuilder(); b.AppendChars(v); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<byte> v) { var b = CreateBuilder(); b.AppendUtf8(v); Commit(in b); }
    public void AppendFormatted(string? v) { if (v == null) return; var b = CreateBuilder(); b.AppendChars(v.AsSpan()); Commit(in b); }
    public void AppendFormatted<T>(T v) { var b = CreateBuilder(); b.AppendFormatted(v); Commit(in b); }
    public void AppendFormatted<T>(T v, string? format) { var b = CreateBuilder(); b.AppendFormatted(v, format.AsSpan()); Commit(in b); }
}

/// <summary>Zero-GC level-filtered handler for LogSystem.Debug (skips all argument evaluation if Debug is disabled).</summary>
[InterpolatedStringHandler]
public unsafe ref struct LogDebugInterpolatedStringHandler
{
    private LogStackBuffer _buf;
    internal int Length;
    internal byte Truncated;
    internal readonly bool Enabled;

    public LogDebugInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, out bool shouldAppend)
    {
        _ = literalLength; _ = formattedCount; _buf = default; Length = 0; Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, LogLevel.Debug); shouldAppend = Enabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte* DataPtr() => (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref Unsafe.AsRef(in _buf)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogUtf8Builder CreateBuilder() => new LogUtf8Builder((byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref _buf)), LogEntry.MaxMessageBytes) { Length = Length, Truncated = Truncated };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Commit(in LogUtf8Builder b) { Length = b.Length; Truncated = b.Truncated; }

    public void AppendLiteral(string s) { var b = CreateBuilder(); b.AppendChars(s.AsSpan()); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<char> v) { var b = CreateBuilder(); b.AppendChars(v); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<byte> v) { var b = CreateBuilder(); b.AppendUtf8(v); Commit(in b); }
    public void AppendFormatted(string? v) { if (v == null) return; var b = CreateBuilder(); b.AppendChars(v.AsSpan()); Commit(in b); }
    public void AppendFormatted<T>(T v) { var b = CreateBuilder(); b.AppendFormatted(v); Commit(in b); }
    public void AppendFormatted<T>(T v, string? format) { var b = CreateBuilder(); b.AppendFormatted(v, format.AsSpan()); Commit(in b); }
}

/// <summary>Zero-GC level-filtered handler for LogSystem.Info.</summary>
[InterpolatedStringHandler]
public unsafe ref struct LogInfoInterpolatedStringHandler
{
    private LogStackBuffer _buf;
    internal int Length;
    internal byte Truncated;
    internal readonly bool Enabled;

    public LogInfoInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, out bool shouldAppend)
    {
        _ = literalLength; _ = formattedCount; _buf = default; Length = 0; Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, LogLevel.Info); shouldAppend = Enabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte* DataPtr() => (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref Unsafe.AsRef(in _buf)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogUtf8Builder CreateBuilder() => new LogUtf8Builder((byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref _buf)), LogEntry.MaxMessageBytes) { Length = Length, Truncated = Truncated };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Commit(in LogUtf8Builder b) { Length = b.Length; Truncated = b.Truncated; }

    public void AppendLiteral(string s) { var b = CreateBuilder(); b.AppendChars(s.AsSpan()); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<char> v) { var b = CreateBuilder(); b.AppendChars(v); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<byte> v) { var b = CreateBuilder(); b.AppendUtf8(v); Commit(in b); }
    public void AppendFormatted(string? v) { if (v == null) return; var b = CreateBuilder(); b.AppendChars(v.AsSpan()); Commit(in b); }
    public void AppendFormatted<T>(T v) { var b = CreateBuilder(); b.AppendFormatted(v); Commit(in b); }
    public void AppendFormatted<T>(T v, string? format) { var b = CreateBuilder(); b.AppendFormatted(v, format.AsSpan()); Commit(in b); }
}

/// <summary>Zero-GC level-filtered handler for LogSystem.Warn.</summary>
[InterpolatedStringHandler]
public unsafe ref struct LogWarnInterpolatedStringHandler
{
    private LogStackBuffer _buf;
    internal int Length;
    internal byte Truncated;
    internal readonly bool Enabled;

    public LogWarnInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, out bool shouldAppend)
    {
        _ = literalLength; _ = formattedCount; _buf = default; Length = 0; Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, LogLevel.Warn); shouldAppend = Enabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte* DataPtr() => (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref Unsafe.AsRef(in _buf)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogUtf8Builder CreateBuilder() => new LogUtf8Builder((byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref _buf)), LogEntry.MaxMessageBytes) { Length = Length, Truncated = Truncated };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Commit(in LogUtf8Builder b) { Length = b.Length; Truncated = b.Truncated; }

    public void AppendLiteral(string s) { var b = CreateBuilder(); b.AppendChars(s.AsSpan()); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<char> v) { var b = CreateBuilder(); b.AppendChars(v); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<byte> v) { var b = CreateBuilder(); b.AppendUtf8(v); Commit(in b); }
    public void AppendFormatted(string? v) { if (v == null) return; var b = CreateBuilder(); b.AppendChars(v.AsSpan()); Commit(in b); }
    public void AppendFormatted<T>(T v) { var b = CreateBuilder(); b.AppendFormatted(v); Commit(in b); }
    public void AppendFormatted<T>(T v, string? format) { var b = CreateBuilder(); b.AppendFormatted(v, format.AsSpan()); Commit(in b); }
}

/// <summary>Zero-GC level-filtered handler for LogSystem.Error.</summary>
[InterpolatedStringHandler]
public unsafe ref struct LogErrorInterpolatedStringHandler
{
    private LogStackBuffer _buf;
    internal int Length;
    internal byte Truncated;
    internal readonly bool Enabled;

    public LogErrorInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, out bool shouldAppend)
    {
        _ = literalLength; _ = formattedCount; _buf = default; Length = 0; Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, LogLevel.Error); shouldAppend = Enabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte* DataPtr() => (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref Unsafe.AsRef(in _buf)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogUtf8Builder CreateBuilder() => new LogUtf8Builder((byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref _buf)), LogEntry.MaxMessageBytes) { Length = Length, Truncated = Truncated };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Commit(in LogUtf8Builder b) { Length = b.Length; Truncated = b.Truncated; }

    public void AppendLiteral(string s) { var b = CreateBuilder(); b.AppendChars(s.AsSpan()); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<char> v) { var b = CreateBuilder(); b.AppendChars(v); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<byte> v) { var b = CreateBuilder(); b.AppendUtf8(v); Commit(in b); }
    public void AppendFormatted(string? v) { if (v == null) return; var b = CreateBuilder(); b.AppendChars(v.AsSpan()); Commit(in b); }
    public void AppendFormatted<T>(T v) { var b = CreateBuilder(); b.AppendFormatted(v); Commit(in b); }
    public void AppendFormatted<T>(T v, string? format) { var b = CreateBuilder(); b.AppendFormatted(v, format.AsSpan()); Commit(in b); }
}

/// <summary>Zero-GC level-filtered handler for LogSystem.Fatal.</summary>
[InterpolatedStringHandler]
public unsafe ref struct LogFatalInterpolatedStringHandler
{
    private LogStackBuffer _buf;
    internal int Length;
    internal byte Truncated;
    internal readonly bool Enabled;

    public LogFatalInterpolatedStringHandler(int literalLength, int formattedCount, LogState* state, out bool shouldAppend)
    {
        _ = literalLength; _ = formattedCount; _buf = default; Length = 0; Truncated = 0;
        Enabled = LogSystem.IsEnabled(state, LogLevel.Fatal); shouldAppend = Enabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte* DataPtr() => (byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref Unsafe.AsRef(in _buf)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LogUtf8Builder CreateBuilder() => new LogUtf8Builder((byte*)Unsafe.AsPointer(ref Unsafe.As<LogStackBuffer, byte>(ref _buf)), LogEntry.MaxMessageBytes) { Length = Length, Truncated = Truncated };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Commit(in LogUtf8Builder b) { Length = b.Length; Truncated = b.Truncated; }

    public void AppendLiteral(string s) { var b = CreateBuilder(); b.AppendChars(s.AsSpan()); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<char> v) { var b = CreateBuilder(); b.AppendChars(v); Commit(in b); }
    public void AppendFormatted(ReadOnlySpan<byte> v) { var b = CreateBuilder(); b.AppendUtf8(v); Commit(in b); }
    public void AppendFormatted(string? v) { if (v == null) return; var b = CreateBuilder(); b.AppendChars(v.AsSpan()); Commit(in b); }
    public void AppendFormatted<T>(T v) { var b = CreateBuilder(); b.AppendFormatted(v); Commit(in b); }
    public void AppendFormatted<T>(T v, string? format) { var b = CreateBuilder(); b.AppendFormatted(v, format.AsSpan()); Commit(in b); }
}
