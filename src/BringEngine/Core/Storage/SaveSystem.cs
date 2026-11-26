using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BringEngine.Assets.Compression;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Networking;

namespace BringEngine.Core.Storage;

public enum SavePayloadKind : byte
{
    /// <summary>Plain file bytes with NO BSAV envelope (e.g. human-editable settings.json). Still atomic .tmp + .bak.</summary>
    Raw = 0,
    /// <summary>UTF-8 JSON payload wrapped inside a compressed, CRC32-verified BSAV container.</summary>
    JsonUtf8 = 1,
    /// <summary>Custom binary payload (e.g. NetWriter or game-chosen serializer) inside a BSAV container.</summary>
    Binary = 2,
    /// <summary>Tagged, per-chunk versioned binary payload (SaveChunkWriter/SaveChunkReader) inside a BSAV container.</summary>
    Chunked = 3,
}

public enum SaveOpKind : byte
{
    Save = 0,
    Load = 1,
    Delete = 2,
}

public enum SaveStatus : byte
{
    Ok = 0,
    NotFound = 1,
    Corrupted = 2,
    IoError = 3,
    Cancelled = 4,
}

/// <summary>
/// 32-byte little-endian binary envelope header for Bring Engine save files ("BSAV").
/// Verifies format, game schema version, compression mode, uncompressed/compressed sizes, and CRC32.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BsavHeader
{
    public const uint MagicValue = 0x56415342u; // "BSAV"
    public const ushort CurrentFormatVersion = 1;

    public uint Magic;
    public ushort FormatVersion;
    public byte PayloadKind;
    public byte Compression;
    public uint GameSchemaVersion;
    public int UncompressedBytes;
    public int CompressedBytes;
    public uint PayloadCrc32;
    public long SavedUtcTicks;
}

/// <summary>
/// Unmanaged result returned by SaveSystem.PollCompleted or SaveSync/LoadSync.
/// Caller MUST call SaveSystem.ReleaseResult(ref result) when DataPtr is non-null to ensure 0 native leaks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SaveResult
{
    public SaveOpHandle Handle;
    public ulong UserTag;
    public SaveOpKind OpKind;
    public SaveStatus Status;
    public SavePayloadKind PayloadKind;
    public PackCompressionMode Compression;
    public byte LoadedFromBackup;
    public uint GameSchemaVersion;
    public long SavedUtcTicks;
    public byte* DataPtr;
    public int DataLength;
    public nuint DataAllocSize;
    public FixedString64 SlotName;
    public FixedString256 Error;

    public readonly bool IsSuccess => Status == SaveStatus.Ok;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> AsDataSpan()
        => DataPtr == null || DataLength <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(DataPtr, DataLength);

    public readonly string DataAsUtf8String()
        => DataPtr == null || DataLength <= 0 ? string.Empty : Encoding.UTF8.GetString(DataPtr, DataLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly SaveChunkReader CreateChunkReader() => SaveChunkReader.Create(DataPtr, DataLength);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SaveOpSlotMeta
{
    public uint Generation;
    public int State; // 0 = Free, 1 = Pending, 2 = QueuedForPoll
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SaveCommand
{
    public SaveOpHandle Handle;
    public int SlotIndex;
    public ulong UserTag;
    public SaveOpKind OpKind;
    public SavePayloadKind PayloadKind;
    public PackCompressionMode Compression;
    public byte CreateOrDeleteBackup;
    public uint GameSchemaVersion;
    public FixedString64 SlotName;
    public byte* PayloadPtr;
    public int PayloadLength;
    public nuint PayloadAllocSize;
}

/// <summary>
/// Unmanaged save system state allocated in stable pinned native memory (e.g., EngineAllocators.Persistent).
/// Executes saves/loads strictly in FIFO order on a dedicated background I/O worker thread so autosaves never
/// hitch frames and back-to-back writes to the same slot can never race.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SaveSystemState
{
    public const int DefaultMaxPendingOps = 64;
    public const int DefaultMaxSaveBytes = 64 * 1024 * 1024;

    internal SaveOpSlotMeta* Slots;
    internal SaveCommand* CmdQueue;
    internal int CmdMask;
    internal int CmdHead;
    internal int CmdTail;
    internal CachePaddedInt CmdLock;

    internal SaveResult* DoneQueue;
    internal int DoneMask;
    internal int DoneHead;
    internal int DoneTail;
    internal CachePaddedInt DoneLock;

    internal CachePaddedInt SlotLock;

    public int MaxPendingOps;
    public int MaxSaveBytes;
    public int PoolSlot;
    public byte Active;
    internal int ShutdownFlag;

    public CachePaddedInt PendingCount;
    public CachePaddedLong TotalSavesCompleted;
    public CachePaddedLong TotalLoadsCompleted;
    public CachePaddedLong TotalBackupRecoveries;
}

/// <summary>
/// Zero-OOP, zero-GC hot-path local save system for Bring Engine (0 external NuGet dependencies).
/// - Uses PlatformPaths.ResolveSavesDirectory(appName) sharing the same OS app-data parent as LogSystem.
/// - Crash-safe atomic writes (.tmp + fsync + .bak rotation + atomic replace) for every save mode.
/// - Supports 3 payload lanes:
///   1) Raw files (SaveRawAsync/LoadRawAsync) for human-editable settings.json.
///   2) BSAV + UTF-8 JSON (SaveAsync with SavePayloadKind.JsonUtf8) for patch-safe profiles/progression.
///   3) BSAV + Versioned Binary Chunks (SaveChunkWriter/SaveChunkReader) for large multi-MB worlds &amp; SoA dumps.
/// - Automatic CRC32 validation and seamless .bak fallback on corruption.
/// </summary>
public static unsafe class SaveSystem
{
    private const int MaxInstances = 8;

    private struct ManagedSavePool
    {
        public string? SavesDirectory;
        public AutoResetEvent? WakeEvent;
        public Thread? WorkerThread;
        public bool InUse;
    }

    private static readonly ManagedSavePool[] s_pools = new ManagedSavePool[MaxInstances];
    private static readonly object s_poolLock = new();

    public static void Start(
        SaveSystemState* state,
        ref LinearArena persistentArena,
        ReadOnlySpan<char> appName = default,
        int maxPendingOps = SaveSystemState.DefaultMaxPendingOps,
        int maxSaveBytes = SaveSystemState.DefaultMaxSaveBytes,
        ReadOnlySpan<char> directoryOverride = default)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (maxPendingOps < 4) maxPendingOps = 4;
        if (maxSaveBytes < 4096) maxSaveBytes = 4096;

        int qCap = 8;
        while (qCap < maxPendingOps * 2) qCap <<= 1;

        string savesDir = directoryOverride.IsEmpty
            ? PlatformPaths.ResolveSavesDirectory(appName)
            : new string(directoryOverride);
        Directory.CreateDirectory(savesDir);

        int poolSlot = -1;
        lock (s_poolLock)
        {
            for (int i = 0; i < MaxInstances; i++)
            {
                if (!s_pools[i].InUse)
                {
                    s_pools[i].InUse = true;
                    s_pools[i].SavesDirectory = savesDir;
                    s_pools[i].WakeEvent = new AutoResetEvent(false);
                    poolSlot = i;
                    break;
                }
            }
        }
        if (poolSlot < 0)
            throw new InvalidOperationException("Maximum concurrent SaveSystemState instances exceeded.");

        *state = default;
        state->Slots = persistentArena.Alloc<SaveOpSlotMeta>(maxPendingOps, 64);
        for (int i = 0; i < maxPendingOps; i++)
        {
            state->Slots[i].Generation = 1;
            state->Slots[i].State = 0;
        }
        state->CmdQueue = persistentArena.Alloc<SaveCommand>(qCap, 64);
        state->CmdMask = qCap - 1;
        state->DoneQueue = persistentArena.Alloc<SaveResult>(qCap, 64);
        state->DoneMask = qCap - 1;
        state->MaxPendingOps = maxPendingOps;
        state->MaxSaveBytes = maxSaveBytes;
        state->PoolSlot = poolSlot;
        state->Active = 1;

        nint rawPtr = (nint)state;
        var thread = new Thread(WorkerEntry)
        {
            Name = "BringEngine.SaveWorker",
            IsBackground = true,
        };
        s_pools[poolSlot].WorkerThread = thread;
        thread.Start(rawPtr);
    }

    /// <summary>
    /// Finishes all currently queued save/load operations cleanly, joins the background I/O thread,
    /// and frees any unpolled native result buffers so NativeMemoryTracker.LiveBytes == 0.
    /// </summary>
    public static void Stop(SaveSystemState* state)
    {
        if (state == null || state->Active == 0) return;
        int poolSlot = state->PoolSlot;

        state->Active = 0;
        Volatile.Write(ref state->ShutdownFlag, 1);
        Thread.MemoryBarrier();

        try { s_pools[poolSlot].WakeEvent?.Set(); } catch { }
        s_pools[poolSlot].WorkerThread?.Join();

        while (TryDequeueDoneRaw(state, out SaveResult leftover))
            ReleaseResult(ref leftover);

        s_pools[poolSlot].WakeEvent?.Dispose();
        lock (s_poolLock)
        {
            s_pools[poolSlot] = default;
        }
    }

    // =========================================================================
    // ZONE 1 ASYNC HANDLE + ZERO-GC POLLING API
    // =========================================================================

    /// <summary>
    /// Enqueues a crash-safe asynchronous BSAV write (compression + CRC32 + atomic .tmp/.bak rename off-thread).
    /// Copies payload into native memory and returns immediately (0 GC bytes on the Logic Thread).
    /// </summary>
    public static SaveOpHandle SaveAsync(
        SaveSystemState* state,
        ReadOnlySpan<char> slotName,
        ReadOnlySpan<byte> payload,
        uint schemaVersion = 1,
        SavePayloadKind kind = SavePayloadKind.JsonUtf8,
        PackCompressionMode compression = PackCompressionMode.Lz4,
        bool createBackup = true,
        ulong userTag = 0)
    {
        if (kind == SavePayloadKind.Raw)
            return SaveRawAsync(state, slotName, payload, createBackup, userTag);
        return EnqueueSaveOp(state, SaveOpKind.Save, slotName, payload, schemaVersion, kind, compression, createBackup, userTag);
    }

    /// <summary>
    /// Enqueues a crash-safe asynchronous raw file write (no BSAV header — ideal for human-editable settings.json).
    /// Still uses atomic .tmp + fsync + .bak rotation. 0 GC bytes on the Logic Thread.
    /// </summary>
    public static SaveOpHandle SaveRawAsync(
        SaveSystemState* state,
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<byte> rawBytes,
        bool createBackup = true,
        ulong userTag = 0)
        => EnqueueSaveOp(state, SaveOpKind.Save, fileName, rawBytes, 0, SavePayloadKind.Raw, PackCompressionMode.None, createBackup, userTag);

    /// <summary>
    /// Enqueues an asynchronous BSAV read with CRC32 validation and automatic .bak corruption fallback (0 GC on Logic Thread).
    /// </summary>
    public static SaveOpHandle LoadAsync(
        SaveSystemState* state,
        ReadOnlySpan<char> slotName,
        ulong userTag = 0)
        => EnqueueSaveOp(state, SaveOpKind.Load, slotName, default, 0, SavePayloadKind.Binary, PackCompressionMode.None, true, userTag);

    /// <summary>
    /// Enqueues an asynchronous raw file read (e.g. settings.json) with automatic .bak fallback (0 GC on Logic Thread).
    /// </summary>
    public static SaveOpHandle LoadRawAsync(
        SaveSystemState* state,
        ReadOnlySpan<char> fileName,
        ulong userTag = 0)
        => EnqueueSaveOp(state, SaveOpKind.Load, fileName, default, 0, SavePayloadKind.Raw, PackCompressionMode.None, true, userTag);

    public static SaveOpHandle DeleteAsync(
        SaveSystemState* state,
        ReadOnlySpan<char> slotName,
        bool deleteBackup = true,
        ulong userTag = 0)
        => EnqueueSaveOp(state, SaveOpKind.Delete, slotName, default, 0, SavePayloadKind.Raw, PackCompressionMode.None, deleteBackup, userTag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPending(SaveSystemState* state, SaveOpHandle handle)
    {
        if (state == null || !handle.IsValid) return false;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxPendingOps) return false;
        if (Volatile.Read(ref state->Slots[idx].Generation) != handle.Generation) return false;
        return Volatile.Read(ref state->Slots[idx].State) == 1;
    }

    /// <summary>
    /// Pops the next completed save/load result from the native mailbox (0 GC bytes).
    /// Caller MUST call ReleaseResult(ref result) when done.
    /// </summary>
    public static bool PollCompleted(SaveSystemState* state, out SaveResult result)
    {
        if (state == null)
        {
            result = default;
            return false;
        }
        if (!TryDequeueDoneRaw(state, out result))
            return false;

        uint idx = result.Handle.Index;
        if (idx < (uint)state->MaxPendingOps)
        {
            AcquireSpinLock(ref state->SlotLock.Value);
            uint nextGen = (state->Slots[idx].Generation + 1) & HandlePacking.GenerationMask;
            if (nextGen == 0) nextGen = 1;
            Volatile.Write(ref state->Slots[idx].Generation, nextGen);
            Volatile.Write(ref state->Slots[idx].State, 0);
            ReleaseSpinLock(ref state->SlotLock.Value);
        }
        return true;
    }

    /// <summary>Frees the native DataPtr owned by a SaveResult (0 GC, idempotent).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReleaseResult(ref SaveResult result)
    {
        if (result.DataPtr != null && result.DataAllocSize > 0)
            NativeMemoryTracker.AlignedFree(result.DataPtr, result.DataAllocSize);
        result.DataPtr = null;
        result.DataLength = 0;
        result.DataAllocSize = 0;
    }

    // =========================================================================
    // SYNCHRONOUS HELPERS (BOOT SETTINGS, SHUTDOWN SAVES, SLOT QUERIES)
    // =========================================================================

    public static SaveResult SaveSync(
        SaveSystemState* state,
        ReadOnlySpan<char> slotName,
        ReadOnlySpan<byte> payload,
        uint schemaVersion = 1,
        SavePayloadKind kind = SavePayloadKind.JsonUtf8,
        PackCompressionMode compression = PackCompressionMode.Lz4,
        bool createBackup = true)
    {
        if (state == null || state->Active == 0)
            return MakeErrorResult(SaveOpKind.Save, slotName, SaveStatus.IoError, "SaveSystem is not active.");
        string savesDir = s_pools[state->PoolSlot].SavesDirectory!;
        string safeName = PlatformPaths.SanitizeFileName(slotName, "save.bsav");
        return ExecuteSaveFile(state, savesDir, safeName, payload, schemaVersion, kind, compression, createBackup);
    }

    public static SaveResult SaveRawSync(
        SaveSystemState* state,
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<byte> rawBytes,
        bool createBackup = true)
        => SaveSync(state, fileName, rawBytes, 0, SavePayloadKind.Raw, PackCompressionMode.None, createBackup);

    public static SaveResult LoadSync(SaveSystemState* state, ReadOnlySpan<char> slotName)
    {
        if (state == null || state->Active == 0)
            return MakeErrorResult(SaveOpKind.Load, slotName, SaveStatus.IoError, "SaveSystem is not active.");
        string savesDir = s_pools[state->PoolSlot].SavesDirectory!;
        string safeName = PlatformPaths.SanitizeFileName(slotName, "save.bsav");
        return ExecuteLoadFile(state, savesDir, safeName, rawMode: false);
    }

    public static SaveResult LoadRawSync(SaveSystemState* state, ReadOnlySpan<char> fileName)
    {
        if (state == null || state->Active == 0)
            return MakeErrorResult(SaveOpKind.Load, fileName, SaveStatus.IoError, "SaveSystem is not active.");
        string savesDir = s_pools[state->PoolSlot].SavesDirectory!;
        string safeName = PlatformPaths.SanitizeFileName(fileName, "settings.json");
        return ExecuteLoadFile(state, savesDir, safeName, rawMode: true);
    }

    public static bool Exists(SaveSystemState* state, ReadOnlySpan<char> slotName)
    {
        if (state == null || state->Active == 0 || slotName.IsEmpty) return false;
        string path = GetSlotPath(state, slotName);
        return File.Exists(path) || File.Exists(path + ".bak");
    }

    public static bool Delete(SaveSystemState* state, ReadOnlySpan<char> slotName, bool deleteBackup = true)
    {
        if (state == null || state->Active == 0 || slotName.IsEmpty) return false;
        string path = GetSlotPath(state, slotName);
        bool removed = false;
        if (File.Exists(path)) { File.Delete(path); removed = true; }
        if (deleteBackup && File.Exists(path + ".bak")) { File.Delete(path + ".bak"); removed = true; }
        if (File.Exists(path + ".tmp")) { try { File.Delete(path + ".tmp"); } catch { } }
        return removed;
    }

    public static string GetSavesDirectory(SaveSystemState* state)
        => state != null && state->Active != 0 ? (s_pools[state->PoolSlot].SavesDirectory ?? string.Empty) : string.Empty;

    public static string GetSlotPath(SaveSystemState* state, ReadOnlySpan<char> slotName)
    {
        string dir = GetSavesDirectory(state);
        if (string.IsNullOrEmpty(dir)) return string.Empty;
        return Path.Combine(dir, PlatformPaths.SanitizeFileName(slotName, "save.bsav"));
    }

    /// <summary>
    /// Lists save file names in the saves directory, excluding internal .tmp and .bak files (cold UI helper).
    /// </summary>
    public static string[] ListSlots(SaveSystemState* state, string searchPattern = "*")
    {
        string dir = GetSavesDirectory(state);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<string>();

        string[] paths = Directory.GetFiles(dir, searchPattern, SearchOption.TopDirectoryOnly);
        var list = new System.Collections.Generic.List<string>(paths.Length);
        for (int i = 0; i < paths.Length; i++)
        {
            string name = Path.GetFileName(paths[i]);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(name);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list.ToArray();
    }

    // =========================================================================
    // INTERNAL FIFO QUEUE & BACKGROUND I/O WORKER
    // =========================================================================

    private static SaveOpHandle EnqueueSaveOp(
        SaveSystemState* state,
        SaveOpKind opKind,
        ReadOnlySpan<char> slotName,
        ReadOnlySpan<byte> payload,
        uint schemaVersion,
        SavePayloadKind kind,
        PackCompressionMode compression,
        bool backupFlag,
        ulong userTag)
    {
        if (state == null || state->Active == 0 || slotName.IsEmpty)
            return SaveOpHandle.Invalid;
        if (payload.Length > state->MaxSaveBytes)
            return SaveOpHandle.Invalid;

        int slotIdx = -1;
        uint gen = 0;
        AcquireSpinLock(ref state->SlotLock.Value);
        for (int i = 0; i < state->MaxPendingOps; i++)
        {
            if (state->Slots[i].State == 0)
            {
                state->Slots[i].State = 1;
                gen = state->Slots[i].Generation;
                slotIdx = i;
                break;
            }
        }
        ReleaseSpinLock(ref state->SlotLock.Value);

        if (slotIdx < 0)
            return SaveOpHandle.Invalid;

        byte* payloadPtr = null;
        nuint payloadAlloc = 0;
        if (opKind == SaveOpKind.Save && payload.Length > 0)
        {
            payloadAlloc = (nuint)payload.Length;
            payloadPtr = (byte*)NativeMemoryTracker.AlignedAlloc(payloadAlloc, 16);
            payload.CopyTo(new Span<byte>(payloadPtr, payload.Length));
        }

        SaveOpHandle handle = SaveOpHandle.Create((uint)slotIdx, gen);
        var cmd = new SaveCommand
        {
            Handle = handle,
            SlotIndex = slotIdx,
            UserTag = userTag,
            OpKind = opKind,
            PayloadKind = kind,
            Compression = compression,
            CreateOrDeleteBackup = backupFlag ? (byte)1 : (byte)0,
            GameSchemaVersion = schemaVersion,
            SlotName = FixedString64.From(slotName),
            PayloadPtr = payloadPtr,
            PayloadLength = payload.Length,
            PayloadAllocSize = payloadAlloc,
        };

        AcquireSpinLock(ref state->CmdLock.Value);
        int tail = state->CmdTail;
        state->CmdQueue[tail & state->CmdMask] = cmd;
        state->CmdTail = tail + 1;
        ReleaseSpinLock(ref state->CmdLock.Value);

        Interlocked.Increment(ref state->PendingCount.Value);
        try { s_pools[state->PoolSlot].WakeEvent?.Set(); } catch { }
        return handle;
    }

    private static void WorkerEntry(object? arg)
    {
        SaveSystemState* state = (SaveSystemState*)(nint)arg!;
        int poolSlot = state->PoolSlot;
        AutoResetEvent wakeEvt = s_pools[poolSlot].WakeEvent!;
        string savesDir = s_pools[poolSlot].SavesDirectory!;

        while (true)
        {
            if (TryDequeueCmd(state, out SaveCommand cmd))
            {
                SaveResult res;
                try
                {
                    string safeName = PlatformPaths.SanitizeFileName(cmd.SlotName.ToString().AsSpan(), "save.bsav");
                    if (cmd.OpKind == SaveOpKind.Save)
                    {
                        ReadOnlySpan<byte> payload = cmd.PayloadPtr != null && cmd.PayloadLength > 0
                            ? new ReadOnlySpan<byte>(cmd.PayloadPtr, cmd.PayloadLength)
                            : ReadOnlySpan<byte>.Empty;
                        res = ExecuteSaveFile(state, savesDir, safeName, payload, cmd.GameSchemaVersion, cmd.PayloadKind, cmd.Compression, cmd.CreateOrDeleteBackup != 0);
                    }
                    else if (cmd.OpKind == SaveOpKind.Load)
                    {
                        res = ExecuteLoadFile(state, savesDir, safeName, rawMode: cmd.PayloadKind == SavePayloadKind.Raw);
                    }
                    else
                    {
                        string path = Path.Combine(savesDir, safeName);
                        if (File.Exists(path)) File.Delete(path);
                        if (cmd.CreateOrDeleteBackup != 0 && File.Exists(path + ".bak")) File.Delete(path + ".bak");
                        if (File.Exists(path + ".tmp")) { try { File.Delete(path + ".tmp"); } catch { } }
                        res = new SaveResult
                        {
                            OpKind = SaveOpKind.Delete,
                            Status = SaveStatus.Ok,
                            SlotName = FixedString64.From(safeName),
                        };
                    }
                }
                catch (Exception ex)
                {
                    res = new SaveResult
                    {
                        OpKind = cmd.OpKind,
                        Status = SaveStatus.IoError,
                        SlotName = cmd.SlotName,
                        Error = FixedString256.From(ex.Message),
                    };
                }
                finally
                {
                    if (cmd.PayloadPtr != null && cmd.PayloadAllocSize > 0)
                        NativeMemoryTracker.AlignedFree(cmd.PayloadPtr, cmd.PayloadAllocSize);
                }

                res.Handle = cmd.Handle;
                res.UserTag = cmd.UserTag;
                Interlocked.Decrement(ref state->PendingCount.Value);
                EnqueueDone(state, cmd.SlotIndex, in res);
                continue;
            }

            if (Volatile.Read(ref state->ShutdownFlag) != 0)
                break;

            wakeEvt.WaitOne(25);
        }
    }

    private static SaveResult ExecuteSaveFile(
        SaveSystemState* state,
        string savesDir,
        string safeName,
        ReadOnlySpan<byte> payload,
        uint schemaVersion,
        SavePayloadKind kind,
        PackCompressionMode compression,
        bool createBackup)
    {
        var result = new SaveResult
        {
            OpKind = SaveOpKind.Save,
            Status = SaveStatus.IoError,
            PayloadKind = kind,
            Compression = kind == SavePayloadKind.Raw ? PackCompressionMode.None : compression,
            GameSchemaVersion = schemaVersion,
            SavedUtcTicks = DateTime.UtcNow.Ticks,
            SlotName = FixedString64.From(safeName),
        };

        try
        {
            Directory.CreateDirectory(savesDir);
            string targetPath = Path.Combine(savesDir, safeName);
            string tmpPath = targetPath + ".tmp";
            string bakPath = targetPath + ".bak";

            if (kind == SavePayloadKind.Raw)
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 16384))
                {
                    if (!payload.IsEmpty) fs.Write(payload);
                    fs.Flush(flushToDisk: true);
                }
            }
            else
            {
                uint crc = NetHash.Crc32(payload);
                byte[] compressed = payload.IsEmpty || compression == PackCompressionMode.None
                    ? (payload.IsEmpty ? Array.Empty<byte>() : payload.ToArray())
                    : PackCompressionSystem.Compress(payload, compression);

                var hdr = new BsavHeader
                {
                    Magic = BsavHeader.MagicValue,
                    FormatVersion = BsavHeader.CurrentFormatVersion,
                    PayloadKind = (byte)kind,
                    Compression = (byte)compression,
                    GameSchemaVersion = schemaVersion,
                    UncompressedBytes = payload.Length,
                    CompressedBytes = compressed.Length,
                    PayloadCrc32 = crc,
                    SavedUtcTicks = result.SavedUtcTicks,
                };

                byte* hdrBytes = stackalloc byte[sizeof(BsavHeader)];
                *(BsavHeader*)hdrBytes = hdr;

                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 16384))
                {
                    fs.Write(new ReadOnlySpan<byte>(hdrBytes, sizeof(BsavHeader)));
                    if (compressed.Length > 0) fs.Write(compressed, 0, compressed.Length);
                    fs.Flush(flushToDisk: true);
                }
            }

            // Preserve previous good file as .bak BEFORE replacing targetPath so targetPath is always valid
            if (createBackup && File.Exists(targetPath))
                File.Copy(targetPath, bakPath, overwrite: true);

            // Atomic same-directory rename replacing targetPath
            File.Move(tmpPath, targetPath, overwrite: true);

            result.Status = SaveStatus.Ok;
            result.DataLength = payload.Length;
            Interlocked.Increment(ref state->TotalSavesCompleted.Value);
        }
        catch (Exception ex)
        {
            result.Status = SaveStatus.IoError;
            result.Error = FixedString256.From(ex.Message);
        }

        return result;
    }

    private static SaveResult ExecuteLoadFile(SaveSystemState* state, string savesDir, string safeName, bool rawMode)
    {
        string targetPath = Path.Combine(savesDir, safeName);
        string bakPath = targetPath + ".bak";

        bool hasPrimary = File.Exists(targetPath);
        bool hasBackup = File.Exists(bakPath);

        if (!hasPrimary && !hasBackup)
        {
            return new SaveResult
            {
                OpKind = SaveOpKind.Load,
                Status = SaveStatus.NotFound,
                PayloadKind = rawMode ? SavePayloadKind.Raw : SavePayloadKind.Binary,
                SlotName = FixedString64.From(safeName),
                Error = FixedString256.From("Save file not found."),
            };
        }

        SaveResult primary = hasPrimary
            ? TryReadSingleFile(targetPath, safeName, rawMode, state->MaxSaveBytes)
            : new SaveResult { OpKind = SaveOpKind.Load, Status = SaveStatus.NotFound, SlotName = FixedString64.From(safeName) };

        if (primary.Status == SaveStatus.Ok)
        {
            Interlocked.Increment(ref state->TotalLoadsCompleted.Value);
            return primary;
        }

        // Primary missing or corrupted -> automatically recover from .bak if present
        if (hasBackup)
        {
            SaveResult backup = TryReadSingleFile(bakPath, safeName, rawMode, state->MaxSaveBytes);
            if (backup.Status == SaveStatus.Ok)
            {
                backup.LoadedFromBackup = 1;
                Interlocked.Increment(ref state->TotalLoadsCompleted.Value);
                Interlocked.Increment(ref state->TotalBackupRecoveries.Value);
                return backup;
            }
            ReleaseResult(ref backup);
        }

        return primary;
    }

    private static SaveResult TryReadSingleFile(string path, string safeName, bool rawMode, int maxSaveBytes)
    {
        var result = new SaveResult
        {
            OpKind = SaveOpKind.Load,
            Status = SaveStatus.IoError,
            PayloadKind = rawMode ? SavePayloadKind.Raw : SavePayloadKind.Binary,
            SlotName = FixedString64.From(safeName),
        };

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384);
            long fileLen = fs.Length;

            if (rawMode)
            {
                if (fileLen > maxSaveBytes)
                {
                    result.Status = SaveStatus.Corrupted;
                    result.Error = FixedString256.From("Raw save file exceeded MaxSaveBytes.");
                    return result;
                }

                int len = (int)fileLen;
                byte* ptr = null;
                nuint allocSize = 0;
                if (len > 0)
                {
                    allocSize = (nuint)len;
                    ptr = (byte*)NativeMemoryTracker.AlignedAlloc(allocSize, 16);
                    int got = ReadExact(fs, new Span<byte>(ptr, len));
                    if (got != len)
                    {
                        NativeMemoryTracker.AlignedFree(ptr, allocSize);
                        result.Status = SaveStatus.Corrupted;
                        result.Error = FixedString256.From("Unexpected EOF reading raw save file.");
                        return result;
                    }
                }

                result.Status = SaveStatus.Ok;
                result.PayloadKind = SavePayloadKind.Raw;
                result.Compression = PackCompressionMode.None;
                result.DataPtr = ptr;
                result.DataLength = len;
                result.DataAllocSize = allocSize;
                return result;
            }

            // BSAV container validation
            int hdrSize = sizeof(BsavHeader);
            if (fileLen < hdrSize)
            {
                result.Status = SaveStatus.Corrupted;
                result.Error = FixedString256.From("Save file shorter than BSAV header.");
                return result;
            }

            byte* hdrBuf = stackalloc byte[hdrSize];
            if (ReadExact(fs, new Span<byte>(hdrBuf, hdrSize)) != hdrSize)
            {
                result.Status = SaveStatus.Corrupted;
                result.Error = FixedString256.From("Failed reading BSAV header.");
                return result;
            }

            BsavHeader hdr = *(BsavHeader*)hdrBuf;
            if (hdr.Magic != BsavHeader.MagicValue || hdr.FormatVersion != BsavHeader.CurrentFormatVersion)
            {
                result.Status = SaveStatus.Corrupted;
                result.Error = FixedString256.From("Invalid BSAV magic or unsupported format version.");
                return result;
            }

            if (hdr.UncompressedBytes < 0 || hdr.UncompressedBytes > maxSaveBytes ||
                hdr.CompressedBytes < 0 || fileLen - hdrSize != hdr.CompressedBytes)
            {
                result.Status = SaveStatus.Corrupted;
                result.Error = FixedString256.From("BSAV payload size mismatch or exceeds MaxSaveBytes.");
                return result;
            }

            PackCompressionMode compMode = (PackCompressionMode)hdr.Compression;
            if (compMode != PackCompressionMode.None && compMode != PackCompressionMode.Lz4 && compMode != PackCompressionMode.Brotli)
            {
                result.Status = SaveStatus.Corrupted;
                result.Error = FixedString256.From("Unknown BSAV compression mode.");
                return result;
            }

            byte* outPtr = null;
            nuint outAlloc = 0;

            if (hdr.UncompressedBytes > 0)
            {
                outAlloc = (nuint)hdr.UncompressedBytes;
                outPtr = (byte*)NativeMemoryTracker.AlignedAlloc(outAlloc, 16);
                try
                {
                    if (compMode == PackCompressionMode.None)
                    {
                        if (hdr.CompressedBytes != hdr.UncompressedBytes ||
                            ReadExact(fs, new Span<byte>(outPtr, hdr.UncompressedBytes)) != hdr.UncompressedBytes)
                        {
                            NativeMemoryTracker.AlignedFree(outPtr, outAlloc);
                            result.Status = SaveStatus.Corrupted;
                            result.Error = FixedString256.From("Truncated uncompressed BSAV payload.");
                            return result;
                        }
                    }
                    else
                    {
                        nuint compAlloc = (nuint)hdr.CompressedBytes;
                        byte* compPtr = compAlloc > 0 ? (byte*)NativeMemoryTracker.AlignedAlloc(compAlloc, 16) : null;
                        try
                        {
                            if (compPtr == null || ReadExact(fs, new Span<byte>(compPtr, hdr.CompressedBytes)) != hdr.CompressedBytes)
                            {
                                NativeMemoryTracker.AlignedFree(outPtr, outAlloc);
                                result.Status = SaveStatus.Corrupted;
                                result.Error = FixedString256.From("Truncated compressed BSAV payload.");
                                return result;
                            }

                            int decoded = PackCompressionSystem.Decompress(
                                new ReadOnlySpan<byte>(compPtr, hdr.CompressedBytes),
                                compMode,
                                new Span<byte>(outPtr, hdr.UncompressedBytes));
                            if (decoded != hdr.UncompressedBytes)
                            {
                                NativeMemoryTracker.AlignedFree(outPtr, outAlloc);
                                result.Status = SaveStatus.Corrupted;
                                result.Error = FixedString256.From("BSAV decompressed byte count mismatch.");
                                return result;
                            }
                        }
                        finally
                        {
                            if (compPtr != null && compAlloc > 0)
                                NativeMemoryTracker.AlignedFree(compPtr, compAlloc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    NativeMemoryTracker.AlignedFree(outPtr, outAlloc);
                    result.Status = SaveStatus.Corrupted;
                    result.Error = FixedString256.From(ex.Message);
                    return result;
                }
            }

            // Verify CRC32 over exact uncompressed payload
            ReadOnlySpan<byte> decodedSpan = outPtr != null && hdr.UncompressedBytes > 0
                ? new ReadOnlySpan<byte>(outPtr, hdr.UncompressedBytes)
                : ReadOnlySpan<byte>.Empty;
            uint actualCrc = NetHash.Crc32(decodedSpan);
            if (actualCrc != hdr.PayloadCrc32)
            {
                if (outPtr != null && outAlloc > 0)
                    NativeMemoryTracker.AlignedFree(outPtr, outAlloc);
                result.Status = SaveStatus.Corrupted;
                result.Error = FixedString256.From("BSAV CRC32 checksum mismatch.");
                return result;
            }

            result.Status = SaveStatus.Ok;
            result.PayloadKind = (SavePayloadKind)hdr.PayloadKind;
            result.Compression = compMode;
            result.GameSchemaVersion = hdr.GameSchemaVersion;
            result.SavedUtcTicks = hdr.SavedUtcTicks;
            result.DataPtr = outPtr;
            result.DataLength = hdr.UncompressedBytes;
            result.DataAllocSize = outAlloc;
            return result;
        }
        catch (Exception ex)
        {
            result.Status = SaveStatus.IoError;
            result.Error = FixedString256.From(ex.Message);
            return result;
        }
    }

    private static int ReadExact(FileStream fs, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = fs.Read(dst.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static SaveResult MakeErrorResult(SaveOpKind opKind, ReadOnlySpan<char> slotName, SaveStatus status, string msg) => new SaveResult
    {
        OpKind = opKind,
        Status = status,
        SlotName = FixedString64.From(slotName),
        Error = FixedString256.From(msg),
    };

    private static bool TryDequeueCmd(SaveSystemState* state, out SaveCommand cmd)
    {
        AcquireSpinLock(ref state->CmdLock.Value);
        int head = state->CmdHead;
        if (head == state->CmdTail)
        {
            ReleaseSpinLock(ref state->CmdLock.Value);
            cmd = default;
            return false;
        }
        cmd = state->CmdQueue[head & state->CmdMask];
        state->CmdHead = head + 1;
        ReleaseSpinLock(ref state->CmdLock.Value);
        return true;
    }

    private static void EnqueueDone(SaveSystemState* state, int slotIdx, in SaveResult res)
    {
        Volatile.Write(ref state->Slots[slotIdx].State, 2);

        AcquireSpinLock(ref state->DoneLock.Value);
        int tail = state->DoneTail;
        state->DoneQueue[tail & state->DoneMask] = res;
        state->DoneTail = tail + 1;
        ReleaseSpinLock(ref state->DoneLock.Value);
    }

    private static bool TryDequeueDoneRaw(SaveSystemState* state, out SaveResult res)
    {
        AcquireSpinLock(ref state->DoneLock.Value);
        int head = state->DoneHead;
        if (head == state->DoneTail)
        {
            ReleaseSpinLock(ref state->DoneLock.Value);
            res = default;
            return false;
        }
        res = state->DoneQueue[head & state->DoneMask];
        state->DoneHead = head + 1;
        ReleaseSpinLock(ref state->DoneLock.Value);
        return true;
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
