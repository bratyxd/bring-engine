using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Core.Storage;
using BringEngine.Core.Threading;

namespace BringEngine.Core.Logging;

public enum LogLevel : byte
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
    None = 6,
}

public enum LogOverflowPolicy : byte
{
    /// <summary>Yield briefly for the background writer to drain when the ring buffer is full (guarantees zero lost lines).</summary>
    BlockUntilSpace = 0,
    /// <summary>Drop the incoming line immediately when the ring buffer is full (never stalls caller even under extreme bursts).</summary>
    DropNewest = 1,
}

internal enum LogCommandKind : byte
{
    WriteLine = 0,
    CloseFile = 1,
}

/// <summary>
/// 512-byte cache-aligned fixed-size native log ring entry (0 GC allocations).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct LogEntry
{
    public const int MaxMessageBytes = 492;

    public long UtcTicks;
    public uint FileRaw;
    public LogLevel Level;
    public byte CommandKind; // (byte)LogCommandKind
    public byte Flags;       // bit 0 = Truncated, bit 1 = AutoFlush
    public byte Pad;
    public ushort MessageLength;
    public ushort Pad2;
    public fixed byte Message[MaxMessageBytes];
}

[StructLayout(LayoutKind.Sequential)]
internal struct LogFileSlotMeta
{
    public uint Generation;
    public int State; // 0 = Free, 1 = Open, 2 = Closing
    public int MaxLines;
    public FixedString64 FileName;
}

/// <summary>
/// Unmanaged logger state allocated in stable pinned native memory (e.g., EngineAllocators.Persistent).
/// Hot-path log calls format UTF-8 directly into a native MPSC ring buffer with 0 GC allocations and 0 disk I/O;
/// a dedicated background writer thread handles pretty timestamps, disk appends, and O(1)-amortized FIFO line trimming.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct LogState
{
    public const int DefaultMaxLines = 10_000;
    public const int DefaultMaxFiles = 16;
    public const int DefaultQueueCapacity = 1024;
    public const string DefaultFileName = "engine.log";

    internal LogEntry* Ring;
    internal int RingMask;
    internal long RingHead;
    internal long RingTail;
    internal CachePaddedInt QueueLock;

    internal LogFileSlotMeta* Files;
    internal CachePaddedInt FileSlotLock;
    public int MaxFiles;

    public int PoolSlot;
    public int MinLevelInt;
    public LogOverflowPolicy OverflowPolicy;
    public byte AutoFlushOnError;
    public byte MirrorToConsole;
    public byte Active;
    internal int ShutdownFlag;
    internal int WriterAlive;

    internal CachePaddedLong FlushRequestSeq;
    internal CachePaddedLong FlushAckSeq;

    public CachePaddedLong TotalLogged;
    public CachePaddedLong TotalWritten;
    public CachePaddedLong TotalDropped;
    public CachePaddedLong TotalCompactions;
}

/// <summary>
/// Zero-OOP, zero-GC hot-path core logging system for Bring Engine.
/// - Dynamically resolves OS-standard writable log directories (Windows %LOCALAPPDATA%, macOS ~/Library/Logs, Linux/SteamOS $XDG_STATE_HOME).
/// - Logs to a default file ("engine.log") unless a specific LogFileHandle is passed.
/// - Enforces a per-file rolling line cap (default 10,000 lines for both default and extra files),
///   evicting oldest lines from line 1 via amortized O(1)-offset compaction (and exact trim on Flush/CloseFile/Stop).
/// </summary>
public static unsafe class LogSystem
{
    private const int MaxInstances = 8;

    private struct ManagedFileWriter
    {
        public string? FullPath;
        public FileStream? Stream;
        public int MaxLines;
        public int CompactionSlack;
        public long* LineOffsets;   // Native ring of byte start offsets for surviving lines
        public int OffsetMask;
        public nuint OffsetAllocBytes;
        public long Head;
        public long Tail;
        public bool IsOpen;

        public readonly long LineCount => Tail - Head;
    }

    private struct ManagedLogPool
    {
        public string? BaseDirectory;
        public ManagedFileWriter[]? Files;
        public AutoResetEvent? WakeEvent;
        public ManualResetEventSlim? FlushEvent;
        public Thread? WriterThread;
        public object? FileOpenLock;
        public bool InUse;
    }

    private static readonly ManagedLogPool[] s_pools = new ManagedLogPool[MaxInstances];
    private static readonly object s_poolLock = new();

    /// <summary>
    /// Resolves the OS-standard writable log directory under the unified per-game app-data parent (headless-safe, zero SDL3 dependency):
    /// - Windows: %LOCALAPPDATA%\&lt;AppName&gt;\Logs
    /// - macOS:   ~/Library/Application Support/&lt;AppName&gt;/Logs
    /// - Linux:   $XDG_DATA_HOME/&lt;AppName&gt;/logs (fallback ~/.local/share/&lt;AppName&gt;/logs)
    /// </summary>
    public static string ResolvePlatformLogDirectory(ReadOnlySpan<char> appName)
        => PlatformPaths.ResolveLogsDirectory(appName);

    /// <summary>
    /// Initializes LogState inside stable pinned native memory (e.g., EngineAllocators.Persistent),
    /// creates the platform log directory + default log file, and starts the background writer thread.
    /// </summary>
    public static void Start(
        LogState* state,
        ref LinearArena persistentArena,
        ReadOnlySpan<char> appName = default,
        ReadOnlySpan<char> defaultFileName = default,
        int defaultMaxLines = LogState.DefaultMaxLines,
        LogLevel minLevel = LogLevel.Info,
        bool mirrorToConsole = false,
        bool autoFlushOnError = true,
        LogOverflowPolicy overflowPolicy = LogOverflowPolicy.BlockUntilSpace,
        int maxFiles = LogState.DefaultMaxFiles,
        int queueCapacity = LogState.DefaultQueueCapacity,
        ReadOnlySpan<char> directoryOverride = default)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (defaultMaxLines < 1) defaultMaxLines = LogState.DefaultMaxLines;
        if (maxFiles < 1) maxFiles = 1;

        int ringCap = 64;
        while (ringCap < queueCapacity) ringCap <<= 1;

        string baseDir = directoryOverride.IsEmpty
            ? ResolvePlatformLogDirectory(appName)
            : new string(directoryOverride);
        Directory.CreateDirectory(baseDir);

        string defName = defaultFileName.IsEmpty
            ? LogState.DefaultFileName
            : SanitizeFileName(defaultFileName, LogState.DefaultFileName);

        int poolSlot = -1;
        lock (s_poolLock)
        {
            for (int i = 0; i < MaxInstances; i++)
            {
                if (!s_pools[i].InUse)
                {
                    s_pools[i].InUse = true;
                    s_pools[i].BaseDirectory = baseDir;
                    s_pools[i].Files = new ManagedFileWriter[maxFiles];
                    s_pools[i].WakeEvent = new AutoResetEvent(false);
                    s_pools[i].FlushEvent = new ManualResetEventSlim(false);
                    s_pools[i].FileOpenLock = new object();
                    poolSlot = i;
                    break;
                }
            }
        }
        if (poolSlot < 0)
            throw new InvalidOperationException("Maximum concurrent LogState instances exceeded.");

        *state = default;
        state->Ring = persistentArena.Alloc<LogEntry>(ringCap, 64);
        state->RingMask = ringCap - 1;
        state->Files = persistentArena.Alloc<LogFileSlotMeta>(maxFiles, 64);
        for (int i = 0; i < maxFiles; i++)
        {
            state->Files[i].Generation = 1;
            state->Files[i].State = 0;
        }
        state->MaxFiles = maxFiles;
        state->PoolSlot = poolSlot;
        state->MinLevelInt = (int)minLevel;
        state->OverflowPolicy = overflowPolicy;
        state->AutoFlushOnError = autoFlushOnError ? (byte)1 : (byte)0;
        state->MirrorToConsole = mirrorToConsole ? (byte)1 : (byte)0;
        state->Active = 1;
        state->WriterAlive = 1;

        // Open default file at slot 0 before launching background thread
        try
        {
            OpenFileSlotInternal(state, slotIdx: 0, defName, defaultMaxLines);
        }
        catch
        {
            CleanupPoolSlotOnStartFailure(poolSlot);
            state->Active = 0;
            state->WriterAlive = 0;
            throw;
        }

        nint rawPtr = (nint)state;
        var thread = new Thread(WriterThreadEntry)
        {
            Name = "BringEngine.LogWriter",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        s_pools[poolSlot].WriterThread = thread;
        thread.Start(rawPtr);
    }

    /// <summary>
    /// Opens or creates an additional named log file inside the active log directory.
    /// Defaults to 10,000 lines unless maxLines is specified.
    /// </summary>
    public static LogFileHandle OpenFile(
        LogState* state,
        ReadOnlySpan<char> fileName,
        int maxLines = LogState.DefaultMaxLines)
    {
        if (state == null || state->Active == 0 || fileName.IsEmpty)
            return LogFileHandle.Invalid;
        if (maxLines < 1) maxLines = LogState.DefaultMaxLines;

        string safeName = SanitizeFileName(fileName, "custom.log");
        int poolSlot = state->PoolSlot;

        int slotIdx = -1;
        uint gen = 0;
        AcquireSpinLock(ref state->FileSlotLock.Value);
        for (int i = 1; i < state->MaxFiles; i++)
        {
            if (Volatile.Read(ref state->Files[i].State) == 0)
            {
                uint nextGen = (state->Files[i].Generation + 1) & HandlePacking.GenerationMask;
                if (nextGen == 0) nextGen = 1;
                state->Files[i].Generation = nextGen;
                state->Files[i].State = 1;
                gen = nextGen;
                slotIdx = i;
                break;
            }
        }
        ReleaseSpinLock(ref state->FileSlotLock.Value);

        if (slotIdx < 0)
            return LogFileHandle.Invalid;

        try
        {
            lock (s_pools[poolSlot].FileOpenLock!)
            {
                OpenFileSlotInternal(state, slotIdx, safeName, maxLines);
            }
        }
        catch
        {
            Volatile.Write(ref state->Files[slotIdx].State, 0);
            throw;
        }

        return LogFileHandle.Create((uint)slotIdx, gen);
    }

    /// <summary>
    /// Flushes and closes a secondary log file opened via OpenFile (cannot close the default file; use Stop for shutdown).
    /// </summary>
    public static void CloseFile(LogState* state, LogFileHandle handle)
    {
        if (state == null || state->Active == 0 || !handle.IsValid) return;
        uint idx = handle.Index;
        if (idx == 0 || idx >= (uint)state->MaxFiles) return;
        if (Volatile.Read(ref state->Files[idx].Generation) != handle.Generation) return;
        if (Volatile.Read(ref state->Files[idx].State) != 1) return;

        Volatile.Write(ref state->Files[idx].State, 2);

        LogEntry cmd = default;
        cmd.FileRaw = handle.Raw;
        cmd.CommandKind = (byte)LogCommandKind.CloseFile;
        EnqueueEntry(state, in cmd, forceBlock: true);
        Flush(state);
    }

    /// <summary>
    /// Blocks until the background writer drains all currently queued lines and compacts every file to &lt;= MaxLines.
    /// </summary>
    public static void Flush(LogState* state)
    {
        if (state == null || state->Active == 0) return;
        if (Volatile.Read(ref state->WriterAlive) == 0) return;

        int poolSlot = state->PoolSlot;
        s_pools[poolSlot].FlushEvent?.Reset();
        long req = Interlocked.Increment(ref state->FlushRequestSeq.Value);
        try { s_pools[poolSlot].WakeEvent?.Set(); } catch { }

        while (Volatile.Read(ref state->FlushAckSeq.Value) < req && Volatile.Read(ref state->WriterAlive) != 0)
        {
            s_pools[poolSlot].FlushEvent?.Wait(10);
        }
    }

    /// <summary>
    /// Drains all queued log entries, enforces exact MaxLines caps on every file, closes file handles,
    /// stops the background writer thread, and frees all native offset buffers (0 native leaks).
    /// </summary>
    public static void Stop(LogState* state)
    {
        if (state == null || state->Active == 0) return;
        int poolSlot = state->PoolSlot;

        Flush(state);

        Volatile.Write(ref state->ShutdownFlag, 1);
        state->Active = 0;
        Thread.MemoryBarrier();

        try { s_pools[poolSlot].WakeEvent?.Set(); } catch { }
        s_pools[poolSlot].WriterThread?.Join();

        ManagedFileWriter[]? files = s_pools[poolSlot].Files;
        if (files != null)
        {
            for (int i = 0; i < files.Length; i++)
                CloseManagedFile(ref files[i]);
        }

        s_pools[poolSlot].WakeEvent?.Dispose();
        s_pools[poolSlot].FlushEvent?.Dispose();
        lock (s_poolLock)
        {
            s_pools[poolSlot] = default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetMinLevel(LogState* state, LogLevel minLevel)
    {
        if (state != null)
            Volatile.Write(ref state->MinLevelInt, (int)minLevel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LogLevel GetMinLevel(LogState* state)
        => state != null ? (LogLevel)Volatile.Read(ref state->MinLevelInt) : LogLevel.None;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogState* state, LogLevel level)
        => state != null && state->Active != 0 && (int)level >= Volatile.Read(ref state->MinLevelInt) && level != LogLevel.None;

    public static string GetLogDirectory(LogState* state)
        => state != null && state->Active != 0 ? (s_pools[state->PoolSlot].BaseDirectory ?? string.Empty) : string.Empty;

    public static string GetFilePath(LogState* state, LogFileHandle handle = default)
    {
        if (state == null || state->Active == 0) return string.Empty;
        uint idx = handle.IsValid ? handle.Index : 0u;
        if (idx >= (uint)state->MaxFiles) return string.Empty;
        if (handle.IsValid && Volatile.Read(ref state->Files[idx].Generation) != handle.Generation)
            return string.Empty;
        return s_pools[state->PoolSlot].Files?[idx].FullPath ?? string.Empty;
    }

    // =========================================================================
    // HOT-PATH ZERO-GC LOGGING API (DEFAULT FILE + SPECIFIC FILE OVERLOADS)
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(
        LogState* state,
        LogLevel level,
        [InterpolatedStringHandlerArgument("state", "level")] ref LogInterpolatedStringHandler message)
    {
        if (!message.Enabled) return;
        EnqueueUtf8(state, LogFileHandle.Default, level, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(
        LogState* state,
        LogFileHandle file,
        LogLevel level,
        [InterpolatedStringHandlerArgument("state", "file", "level")] ref LogInterpolatedStringHandler message)
    {
        if (!message.Enabled) return;
        EnqueueUtf8(state, file, level, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated);
    }

    public static void WriteUtf8(LogState* state, LogLevel level, ReadOnlySpan<byte> utf8Message, LogFileHandle file = default)
    {
        if (!IsEnabled(state, level)) return;
        EnqueueUtf8(state, file, level, utf8Message, truncated: 0);
    }

    public static void WriteChars(LogState* state, LogLevel level, ReadOnlySpan<char> message, LogFileHandle file = default)
    {
        if (!IsEnabled(state, level)) return;
        byte* stackBuf = stackalloc byte[LogEntry.MaxMessageBytes];
        var b = new LogUtf8Builder(stackBuf, LogEntry.MaxMessageBytes);
        b.AppendChars(message);
        EnqueueUtf8(state, file, level, b.WrittenSpan(), b.Truncated);
    }

    // Level-specific zero-GC interpolated shorthands (default file)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(LogState* state, [InterpolatedStringHandlerArgument("state")] ref LogTraceInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, LogFileHandle.Default, LogLevel.Trace, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(LogState* state, [InterpolatedStringHandlerArgument("state")] ref LogDebugInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, LogFileHandle.Default, LogLevel.Debug, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(LogState* state, [InterpolatedStringHandlerArgument("state")] ref LogInfoInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, LogFileHandle.Default, LogLevel.Info, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warn(LogState* state, [InterpolatedStringHandlerArgument("state")] ref LogWarnInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, LogFileHandle.Default, LogLevel.Warn, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(LogState* state, [InterpolatedStringHandlerArgument("state")] ref LogErrorInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, LogFileHandle.Default, LogLevel.Error, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fatal(LogState* state, [InterpolatedStringHandlerArgument("state")] ref LogFatalInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, LogFileHandle.Default, LogLevel.Fatal, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    // Level-specific zero-GC interpolated shorthands (specific LogFileHandle)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(LogState* state, LogFileHandle file, [InterpolatedStringHandlerArgument("state")] ref LogTraceInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, file, LogLevel.Trace, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(LogState* state, LogFileHandle file, [InterpolatedStringHandlerArgument("state")] ref LogDebugInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, file, LogLevel.Debug, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(LogState* state, LogFileHandle file, [InterpolatedStringHandlerArgument("state")] ref LogInfoInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, file, LogLevel.Info, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warn(LogState* state, LogFileHandle file, [InterpolatedStringHandlerArgument("state")] ref LogWarnInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, file, LogLevel.Warn, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(LogState* state, LogFileHandle file, [InterpolatedStringHandlerArgument("state")] ref LogErrorInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, file, LogLevel.Error, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fatal(LogState* state, LogFileHandle file, [InterpolatedStringHandlerArgument("state")] ref LogFatalInterpolatedStringHandler message)
    { if (message.Enabled) EnqueueUtf8(state, file, LogLevel.Fatal, new ReadOnlySpan<byte>(message.DataPtr(), message.Length), message.Truncated); }

    // Level-specific plain ReadOnlySpan<char> / UTF-8 shorthands (default file + specific file, 0 GC)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(LogState* state, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Trace, message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(LogState* state, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Debug, message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(LogState* state, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Info, message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warn(LogState* state, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Warn, message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(LogState* state, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Error, message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fatal(LogState* state, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Fatal, message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(LogState* state, LogFileHandle file, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Trace, message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(LogState* state, LogFileHandle file, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Debug, message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(LogState* state, LogFileHandle file, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Info, message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warn(LogState* state, LogFileHandle file, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Warn, message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(LogState* state, LogFileHandle file, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Error, message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fatal(LogState* state, LogFileHandle file, ReadOnlySpan<char> message) => WriteChars(state, LogLevel.Fatal, message, file);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(LogState* state, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Trace, utf8Message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(LogState* state, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Debug, utf8Message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(LogState* state, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Info, utf8Message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warn(LogState* state, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Warn, utf8Message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(LogState* state, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Error, utf8Message);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fatal(LogState* state, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Fatal, utf8Message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(LogState* state, LogFileHandle file, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Trace, utf8Message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(LogState* state, LogFileHandle file, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Debug, utf8Message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(LogState* state, LogFileHandle file, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Info, utf8Message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warn(LogState* state, LogFileHandle file, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Warn, utf8Message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(LogState* state, LogFileHandle file, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Error, utf8Message, file);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fatal(LogState* state, LogFileHandle file, ReadOnlySpan<byte> utf8Message) => WriteUtf8(state, LogLevel.Fatal, utf8Message, file);

    internal static ReadOnlySpan<byte> GetLevelTagUtf8(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE"u8,
        LogLevel.Debug => "DEBUG"u8,
        LogLevel.Info => "INFO "u8,
        LogLevel.Warn => "WARN "u8,
        LogLevel.Error => "ERROR"u8,
        LogLevel.Fatal => "FATAL"u8,
        _ => "INFO "u8,
    };

    // =========================================================================
    // INTERNAL NATIVE MPSC RING BUFFER & BACKGROUND WRITER THREAD
    // =========================================================================

    private static void EnqueueUtf8(LogState* state, LogFileHandle file, LogLevel level, ReadOnlySpan<byte> utf8, byte truncated)
    {
        LogEntry entry = default;
        entry.UtcTicks = DateTime.UtcNow.Ticks;
        entry.FileRaw = file.Raw;
        entry.Level = level;
        entry.CommandKind = (byte)LogCommandKind.WriteLine;
        byte flags = truncated != 0 ? (byte)1 : (byte)0;
        if (state->AutoFlushOnError != 0 && level >= LogLevel.Error)
            flags |= 2;
        entry.Flags = flags;

        int len = utf8.Length;
        if (len > LogEntry.MaxMessageBytes)
        {
            len = LogEntry.MaxMessageBytes;
            entry.Flags |= 1;
        }
        entry.MessageLength = (ushort)len;
        if (len > 0)
            utf8.Slice(0, len).CopyTo(new Span<byte>(entry.Message, len));

        EnqueueEntry(state, in entry, forceBlock: false);
    }

    private static void EnqueueEntry(LogState* state, in LogEntry entry, bool forceBlock)
    {
        bool shouldBlock = forceBlock || state->OverflowPolicy == LogOverflowPolicy.BlockUntilSpace;
        int poolSlot = state->PoolSlot;

        while (true)
        {
            AcquireSpinLock(ref state->QueueLock.Value);
            long head = Volatile.Read(ref state->RingHead);
            long tail = state->RingTail;
            if ((tail - head) <= state->RingMask)
            {
                state->Ring[tail & state->RingMask] = entry;
                Volatile.Write(ref state->RingTail, tail + 1);
                ReleaseSpinLock(ref state->QueueLock.Value);

                if (entry.CommandKind == (byte)LogCommandKind.WriteLine)
                    Interlocked.Increment(ref state->TotalLogged.Value);

                // Wake background writer (0 GC bytes)
                try { s_pools[poolSlot].WakeEvent?.Set(); } catch { }
                return;
            }
            ReleaseSpinLock(ref state->QueueLock.Value);

            if (!shouldBlock || Volatile.Read(ref state->ShutdownFlag) != 0 || Volatile.Read(ref state->WriterAlive) == 0)
            {
                Interlocked.Increment(ref state->TotalDropped.Value);
                return;
            }

            try { s_pools[poolSlot].WakeEvent?.Set(); } catch { }
            Thread.Yield();
        }
    }

    private static void WriterThreadEntry(object? arg)
    {
        LogState* state = (LogState*)(nint)arg!;
        int poolSlot = state->PoolSlot;
        AutoResetEvent wakeEvt = s_pools[poolSlot].WakeEvent!;
        ManualResetEventSlim flushEvt = s_pools[poolSlot].FlushEvent!;

        byte[] lineBuf = new byte[1024];
        byte[] copyBuf = new byte[65536];
        Stream? consoleOut = state->MirrorToConsole != 0 ? Console.OpenStandardOutput() : null;

        try
        {
            while (true)
            {
                bool didWork = DrainRingBatch(state, poolSlot, lineBuf, copyBuf, consoleOut);

                long reqSeq = Volatile.Read(ref state->FlushRequestSeq.Value);
                long ackSeq = Volatile.Read(ref state->FlushAckSeq.Value);
                if (reqSeq > ackSeq)
                {
                    // Drain anything enqueued right before the flush barrier
                    DrainRingBatch(state, poolSlot, lineBuf, copyBuf, consoleOut);
                    FlushAndCompactAllFiles(state, poolSlot, copyBuf);
                    Volatile.Write(ref state->FlushAckSeq.Value, reqSeq);
                    flushEvt.Set();
                    continue;
                }

                if (Volatile.Read(ref state->ShutdownFlag) != 0)
                {
                    DrainRingBatch(state, poolSlot, lineBuf, copyBuf, consoleOut);
                    FlushAndCompactAllFiles(state, poolSlot, copyBuf);
                    break;
                }

                if (!didWork)
                    wakeEvt.WaitOne(25);
            }
        }
        finally
        {
            Volatile.Write(ref state->WriterAlive, 0);
            flushEvt.Set();
        }
    }

    private static bool DrainRingBatch(LogState* state, int poolSlot, byte[] lineBuf, byte[] copyBuf, Stream? consoleOut)
    {
        bool any = false;
        ManagedFileWriter[]? files = s_pools[poolSlot].Files;
        object openLock = s_pools[poolSlot].FileOpenLock!;

        while (true)
        {
            long head = Volatile.Read(ref state->RingHead);
            long tail = Volatile.Read(ref state->RingTail);
            if (head >= tail) break;

            LogEntry entry = state->Ring[head & state->RingMask];
            Volatile.Write(ref state->RingHead, head + 1);
            any = true;

            if (files == null) continue;

            if (entry.CommandKind == (byte)LogCommandKind.CloseFile)
            {
                var h = new LogFileHandle(entry.FileRaw);
                uint idx = h.Index;
                if (idx > 0 && idx < (uint)files.Length)
                {
                    lock (openLock)
                    {
                        if (files[idx].IsOpen)
                        {
                            CompactIfNeeded(state, ref files[idx], copyBuf, forceExact: true);
                            files[idx].Stream?.Flush(flushToDisk: false);
                            CloseManagedFile(ref files[idx]);
                        }
                        Volatile.Write(ref state->Files[idx].State, 0);
                    }
                }
                continue;
            }

            int targetIdx = 0;
            if (entry.FileRaw != 0)
            {
                var h = new LogFileHandle(entry.FileRaw);
                uint idx = h.Index;
                if (idx < (uint)state->MaxFiles &&
                    Volatile.Read(ref state->Files[idx].Generation) == h.Generation &&
                    Volatile.Read(ref state->Files[idx].State) != 0)
                {
                    targetIdx = (int)idx;
                }
                else
                {
                    continue; // Stale or closed handle -> silently drop
                }
            }

            int formattedLen = FormatLogLineUtf8(in entry, lineBuf);
            lock (openLock)
            {
                ref ManagedFileWriter fw = ref files[targetIdx];
                if (!fw.IsOpen || fw.Stream == null) continue;

                long startOffset = fw.Stream.Position;
                fw.LineOffsets[fw.Tail & fw.OffsetMask] = startOffset;
                fw.Tail++;

                fw.Stream.Write(lineBuf, 0, formattedLen);
                Interlocked.Increment(ref state->TotalWritten.Value);

                bool forceExact = (entry.Flags & 2) != 0;
                if (fw.LineCount >= fw.MaxLines + fw.CompactionSlack || (forceExact && fw.LineCount > fw.MaxLines))
                    CompactIfNeeded(state, ref fw, copyBuf, forceExact: true);

                if (forceExact)
                    fw.Stream?.Flush(flushToDisk: false);
            }

            if (consoleOut != null)
            {
                try { consoleOut.Write(lineBuf, 0, formattedLen); } catch { }
            }
        }

        return any;
    }

    private static void FlushAndCompactAllFiles(LogState* state, int poolSlot, byte[] copyBuf)
    {
        ManagedFileWriter[]? files = s_pools[poolSlot].Files;
        object? openLock = s_pools[poolSlot].FileOpenLock;
        if (files == null || openLock == null) return;

        lock (openLock)
        {
            for (int i = 0; i < files.Length; i++)
            {
                if (!files[i].IsOpen || files[i].Stream == null) continue;
                CompactIfNeeded(state, ref files[i], copyBuf, forceExact: true);
                files[i].Stream!.Flush(flushToDisk: false);
            }
        }
    }

    /// <summary>
    /// Formats "[YYYY-MM-DD HH:MM:SS.fff] [LEVEL] &lt;msg&gt;\n" directly into UTF-8 bytes with 0 GC allocations.
    /// Sanitizes embedded CR/LF inside messages so 1 log entry = strictly 1 file line.
    /// </summary>
    private static int FormatLogLineUtf8(in LogEntry entry, byte[] dst)
    {
        DateTime local = new DateTime(entry.UtcTicks, DateTimeKind.Utc).ToLocalTime();
        int p = 0;

        dst[p++] = (byte)'[';
        Write4Digits(dst, ref p, local.Year);
        dst[p++] = (byte)'-';
        Write2Digits(dst, ref p, local.Month);
        dst[p++] = (byte)'-';
        Write2Digits(dst, ref p, local.Day);
        dst[p++] = (byte)' ';
        Write2Digits(dst, ref p, local.Hour);
        dst[p++] = (byte)':';
        Write2Digits(dst, ref p, local.Minute);
        dst[p++] = (byte)':';
        Write2Digits(dst, ref p, local.Second);
        dst[p++] = (byte)'.';
        Write3Digits(dst, ref p, local.Millisecond);
        dst[p++] = (byte)']';
        dst[p++] = (byte)' ';
        dst[p++] = (byte)'[';

        ReadOnlySpan<byte> tag = GetLevelTagUtf8(entry.Level);
        for (int i = 0; i < tag.Length; i++) dst[p++] = tag[i];

        dst[p++] = (byte)']';
        dst[p++] = (byte)' ';

        int msgLen = entry.MessageLength;
        fixed (byte* src = entry.Message)
        {
            for (int i = 0; i < msgLen; i++)
            {
                byte b = src[i];
                dst[p++] = (b == (byte)'\n' || b == (byte)'\r') ? (byte)' ' : b;
            }
        }

        if ((entry.Flags & 1) != 0)
        {
            dst[p++] = (byte)'.';
            dst[p++] = (byte)'.';
            dst[p++] = (byte)'.';
        }

        dst[p++] = (byte)'\n';
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write2Digits(byte[] dst, ref int p, int v)
    {
        dst[p++] = (byte)('0' + (v / 10) % 10);
        dst[p++] = (byte)('0' + (v % 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write3Digits(byte[] dst, ref int p, int v)
    {
        dst[p++] = (byte)('0' + (v / 100) % 10);
        dst[p++] = (byte)('0' + (v / 10) % 10);
        dst[p++] = (byte)('0' + (v % 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write4Digits(byte[] dst, ref int p, int v)
    {
        dst[p++] = (byte)('0' + (v / 1000) % 10);
        dst[p++] = (byte)('0' + (v / 100) % 10);
        dst[p++] = (byte)('0' + (v / 10) % 10);
        dst[p++] = (byte)('0' + (v % 10));
    }

    // =========================================================================
    // FILE OPENING & O(1)-OFFSET FIFO COMPACTION
    // =========================================================================

    private static void OpenFileSlotInternal(LogState* state, int slotIdx, string fileName, int maxLines)
    {
        int poolSlot = state->PoolSlot;
        string baseDir = s_pools[poolSlot].BaseDirectory!;
        string fullPath = Path.Combine(baseDir, fileName);

        int slack = maxLines / 10;
        if (slack < 1) slack = 1;
        if (slack > 1024) slack = 1024;

        int ringCap = 16;
        int minCap = maxLines + slack + 2;
        while (ringCap < minCap) ringCap <<= 1;

        nuint allocBytes = (nuint)sizeof(long) * (nuint)ringCap;
        long* offsets = (long*)NativeMemoryTracker.AlignedAllocZeroed(allocBytes, 64);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 16384);
            var fw = new ManagedFileWriter
            {
                FullPath = fullPath,
                Stream = stream,
                MaxLines = maxLines,
                CompactionSlack = slack,
                LineOffsets = offsets,
                OffsetMask = ringCap - 1,
                OffsetAllocBytes = allocBytes,
                Head = 0,
                Tail = 0,
                IsOpen = true,
            };

            if (stream.Length > 0)
            {
                byte[] scanBuf = new byte[65536];
                ScanExistingLines(ref fw, scanBuf);
                if (fw.LineCount > fw.MaxLines)
                    CompactIfNeeded(state, ref fw, scanBuf, forceExact: true);
            }

            s_pools[poolSlot].Files![slotIdx] = fw;
            state->Files[slotIdx].State = 1;
            state->Files[slotIdx].MaxLines = maxLines;
            state->Files[slotIdx].FileName = FixedString64.From(fileName);
        }
        catch
        {
            stream?.Dispose();
            NativeMemoryTracker.AlignedFree(offsets, allocBytes);
            throw;
        }
    }

    private static void ScanExistingLines(ref ManagedFileWriter fw, byte[] buf)
    {
        FileStream stream = fw.Stream!;
        long fileLen = stream.Length;
        if (fileLen <= 0) return;

        stream.Position = 0;
        long pos = 0;
        bool atLineStart = true;
        byte lastByte = 0;

        while (pos < fileLen)
        {
            int n = stream.Read(buf, 0, buf.Length);
            if (n <= 0) break;

            for (int i = 0; i < n; i++)
            {
                if (atLineStart)
                {
                    if ((fw.Tail - fw.Head) > fw.OffsetMask)
                        fw.Head++;
                    fw.LineOffsets[fw.Tail & fw.OffsetMask] = pos + i;
                    fw.Tail++;
                    atLineStart = false;
                }

                lastByte = buf[i];
                if (lastByte == (byte)'\n')
                    atLineStart = true;
            }
            pos += n;
        }

        // Ensure trailing newline if an unclean shutdown left a partial last line
        stream.Position = fileLen;
        if (lastByte != (byte)'\n')
            stream.WriteByte((byte)'\n');
    }

    private static void CompactIfNeeded(LogState* state, ref ManagedFileWriter fw, byte[] copyBuf, bool forceExact)
    {
        long count = fw.LineCount;
        if (count <= fw.MaxLines) return;
        if (!forceExact && count < fw.MaxLines + fw.CompactionSlack) return;

        FileStream stream = fw.Stream!;
        long dropCount = count - fw.MaxLines;
        long cutOffset = fw.LineOffsets[(fw.Head + dropCount) & fw.OffsetMask];
        long fileEnd = stream.Length;

        if (cutOffset <= 0 || cutOffset >= fileEnd)
        {
            stream.SetLength(0);
            stream.Position = 0;
            fw.Head = 0;
            fw.Tail = 0;
            return;
        }

        long keepBytes = fileEnd - cutOffset;
        long readPos = cutOffset;
        long writePos = 0;
        long remaining = keepBytes;

        while (remaining > 0)
        {
            int chunk = remaining < copyBuf.Length ? (int)remaining : copyBuf.Length;
            stream.Position = readPos;
            int got = 0;
            while (got < chunk)
            {
                int n = stream.Read(copyBuf, got, chunk - got);
                if (n <= 0) break;
                got += n;
            }
            if (got <= 0) break;

            stream.Position = writePos;
            stream.Write(copyBuf, 0, got);
            readPos += got;
            writePos += got;
            remaining -= got;
        }

        stream.SetLength(writePos);
        stream.Position = writePos;

        fw.Head += dropCount;
        for (long i = fw.Head; i < fw.Tail; i++)
            fw.LineOffsets[i & fw.OffsetMask] -= cutOffset;

        Interlocked.Increment(ref state->TotalCompactions.Value);
    }

    private static void CloseManagedFile(ref ManagedFileWriter fw)
    {
        fw.Stream?.Dispose();
        if (fw.LineOffsets != null && fw.OffsetAllocBytes > 0)
            NativeMemoryTracker.AlignedFree(fw.LineOffsets, fw.OffsetAllocBytes);
        fw = default;
    }

    private static void CleanupPoolSlotOnStartFailure(int poolSlot)
    {
        ManagedFileWriter[]? files = s_pools[poolSlot].Files;
        if (files != null)
        {
            for (int i = 0; i < files.Length; i++)
                CloseManagedFile(ref files[i]);
        }
        s_pools[poolSlot].WakeEvent?.Dispose();
        s_pools[poolSlot].FlushEvent?.Dispose();
        lock (s_poolLock)
        {
            s_pools[poolSlot] = default;
        }
    }

    private static string SanitizeSegment(ReadOnlySpan<char> input, string fallback)
    {
        if (input.IsEmpty) return fallback;
        int cap = input.Length < 128 ? input.Length : 128;
        Span<char> buf = stackalloc char[cap];
        int len = 0;
        for (int i = 0; i < input.Length && len < buf.Length; i++)
        {
            char c = input[i];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' ')
                buf[len++] = c;
        }
        string result = new string(buf.Slice(0, len)).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static string SanitizeFileName(ReadOnlySpan<char> input, string fallback)
    {
        string name = Path.GetFileName(new string(input));
        string safe = SanitizeSegment(name.AsSpan(), fallback);
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireSpinLock(ref int lockWord)
    {
        if (Interlocked.Exchange(ref lockWord, 1) == 0) return;
        int spins = 0;
        while (Interlocked.Exchange(ref lockWord, 1) != 0)
        {
            if (++spins < 32) Thread.SpinWait(16);
            else Thread.Yield();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseSpinLock(ref int lockWord) => Volatile.Write(ref lockWord, 0);
}
