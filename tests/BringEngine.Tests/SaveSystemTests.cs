using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using BringEngine.Assets.Compression;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Storage;
using BringEngine.Networking;

namespace BringEngine.Tests;

public static unsafe class SaveSystemTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== Core Storage: PlatformPaths, Atomic BSAV/Raw Saves, Chunks, CRC32 Backup Recovery & Zero-GC Async ===");
        Assert.Run("SaveSystem_UnifiedPlatformPathsWithLogs", &TestUnifiedPlatformPaths);
        Assert.Run("SaveSystem_RawJsonAndBsavJsonLz4BrotliSchemaVersions", &TestRawAndBsavJson);
        Assert.Run("SaveSystem_VersionedBinaryChunksBigSoAAndForwardCompat", &TestChunkedBigSoA);
        Assert.Run("SaveSystem_CorruptionCrc32AndAutomaticBackupRecovery", &TestCorruptionAndBackupRecovery);
        Assert.Run("SaveSystem_Zone1AsyncFifoOrderZeroGcAndZeroLeaks", &TestAsyncFifoZeroGcAndLeaks);
        Assert.Run("SaveSystem_RawAsyncLongSlotNamesPathSanitizationAndLogging", &TestRawAsyncAndPathSanitization);
        Assert.Run("SaveSystem_AdversarialBsavLz4BrotliAndChunkBounds", &TestAdversarialBsavLz4BrotliAndChunkBounds);
        Assert.Run("SaveSystem_MaxPendingOpsAndMaxSaveBytesLimits", &TestMaxPendingOpsAndMaxSaveBytesLimits);
    }

    private static string CreateTempSaveDir(string suffix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"bring_save_test_{suffix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string[] ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var list = new System.Collections.Generic.List<string>();
        while (sr.ReadLine() is string line)
            list.Add(line);
        return list.ToArray();
    }

    private static void TestUnifiedPlatformPaths()
    {
        string appDir = PlatformPaths.ResolveAppDataDirectory("BringSaveTest");
        string logsDir = PlatformPaths.ResolveLogsDirectory("BringSaveTest");
        string savesDir = PlatformPaths.ResolveSavesDirectory("BringSaveTest");

        Assert.Equal(logsDir, LogSystem.ResolvePlatformLogDirectory("BringSaveTest"), "LogSystem uses shared PlatformPaths");
        Assert.Equal(appDir, Path.GetDirectoryName(logsDir) ?? string.Empty, "Logs shares unified app-data parent");
        Assert.Equal(appDir, Path.GetDirectoryName(savesDir) ?? string.Empty, "Saves shares unified app-data parent");

        if (OperatingSystem.IsWindows())
            Assert.True(savesDir.EndsWith(Path.Combine("BringSaveTest", "Saves"), StringComparison.OrdinalIgnoreCase), "Windows Saves path");
        else if (OperatingSystem.IsMacOS())
            Assert.True(savesDir.Contains(Path.Combine("Library", "Application Support", "BringSaveTest", "Saves"), StringComparison.Ordinal), "macOS Saves path");
        else
            Assert.True(savesDir.EndsWith(Path.Combine("BringSaveTest", "saves"), StringComparison.Ordinal), "Linux Saves path");
    }

    private static void TestRawAndBsavJson()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempSaveDir("json");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        SaveSystemState* saves = alloc.Persistent.Alloc<SaveSystemState>(1, 64);

        try
        {
            SaveSystem.Start(saves, ref alloc.Persistent, appName: "JsonSaveTest", directoryOverride: tempDir);

            // 1. Mode A: Raw JSON settings (human-readable on disk, no BSAV header)
            ReadOnlySpan<byte> settingsJson = "{\"vsync\":true,\"fpsCap\":60,\"volume\":0.85}"u8;
            SaveResult rSaveRaw = SaveSystem.SaveRawSync(saves, "settings.json", settingsJson);
            Assert.True(rSaveRaw.IsSuccess, $"SaveRawSync ok ({rSaveRaw.Error})");

            string rawTextOnDisk = File.ReadAllText(SaveSystem.GetSlotPath(saves, "settings.json"));
            Assert.Equal("{\"vsync\":true,\"fpsCap\":60,\"volume\":0.85}", rawTextOnDisk, "Raw settings.json is plain editable JSON on disk");

            SaveResult rLoadRaw = SaveSystem.LoadRawSync(saves, "settings.json");
            try
            {
                Assert.True(rLoadRaw.IsSuccess, "LoadRawSync ok");
                Assert.Equal((byte)SavePayloadKind.Raw, (byte)rLoadRaw.PayloadKind, "Raw kind");
                Assert.Equal("{\"vsync\":true,\"fpsCap\":60,\"volume\":0.85}", rLoadRaw.DataAsUtf8String(), "Raw JSON round-trip");
            }
            finally
            {
                SaveSystem.ReleaseResult(ref rLoadRaw);
            }

            // 2. Mode B: BSAV + JSON with LZ4 and Brotli + GameSchemaVersion
            var sb = new StringBuilder();
            sb.Append("{\"player\":\"Hero\",\"items\":[");
            for (int i = 0; i < 200; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"potion_hp\",\"qty\":5,\"tier\":2}");
            }
            sb.Append("]}");
            byte[] bigJson = Encoding.UTF8.GetBytes(sb.ToString());

            SaveResult rLz4 = SaveSystem.SaveSync(saves, "slot_lz4.bsav", bigJson, schemaVersion: 7, SavePayloadKind.JsonUtf8, PackCompressionMode.Lz4);
            SaveResult rBr = SaveSystem.SaveSync(saves, "slot_br.bsav", bigJson, schemaVersion: 9, SavePayloadKind.JsonUtf8, PackCompressionMode.Brotli);
            Assert.True(rLz4.IsSuccess && rBr.IsSuccess, "LZ4 & Brotli BSAV saves succeeded");

            long lz4Size = new FileInfo(SaveSystem.GetSlotPath(saves, "slot_lz4.bsav")).Length;
            long brSize = new FileInfo(SaveSystem.GetSlotPath(saves, "slot_br.bsav")).Length;
            Assert.True(lz4Size < bigJson.Length / 2, $"LZ4 compressed JSON significantly ({lz4Size} vs {bigJson.Length})");
            Assert.True(brSize < bigJson.Length / 4, $"Brotli compressed JSON heavily ({brSize} vs {bigJson.Length})");

            SaveResult loadLz4 = SaveSystem.LoadSync(saves, "slot_lz4.bsav");
            try
            {
                Assert.True(loadLz4.IsSuccess, "Load LZ4 BSAV ok");
                Assert.Equal(7u, loadLz4.GameSchemaVersion, "LZ4 schema version preserved");
                Assert.Equal((byte)SavePayloadKind.JsonUtf8, (byte)loadLz4.PayloadKind, "JsonUtf8 kind preserved");
                Assert.Equal((byte)0, loadLz4.LoadedFromBackup, "Loaded from primary");
                Assert.True(loadLz4.AsDataSpan().SequenceEqual(bigJson), "LZ4 payload exact match");
            }
            finally
            {
                SaveSystem.ReleaseResult(ref loadLz4);
            }

            SaveResult loadBr = SaveSystem.LoadSync(saves, "slot_br.bsav");
            try
            {
                Assert.True(loadBr.IsSuccess, "Load Brotli BSAV ok");
                Assert.Equal(9u, loadBr.GameSchemaVersion, "Brotli schema version preserved");
                Assert.True(loadBr.AsDataSpan().SequenceEqual(bigJson), "Brotli payload exact match");
            }
            finally
            {
                SaveSystem.ReleaseResult(ref loadBr);
            }

            // 3. Exists, ListSlots (skips .bak/.tmp), Delete
            Assert.True(SaveSystem.Exists(saves, "slot_lz4.bsav"), "Exists true");
            string[] slots = SaveSystem.ListSlots(saves, "*.bsav");
            Assert.Equal(2, slots.Length, "ListSlots returns 2 .bsav files (excluding .bak/.tmp)");
            Assert.True(SaveSystem.Delete(saves, "slot_lz4.bsav"), "Delete succeeded");
            Assert.True(!SaveSystem.Exists(saves, "slot_lz4.bsav"), "Deleted slot no longer exists");

            SaveResult missing = SaveSystem.LoadSync(saves, "nonexistent.bsav");
            Assert.Equal((byte)SaveStatus.NotFound, (byte)missing.Status, "Missing slot returns NotFound");
        }
        finally
        {
            SaveSystem.Stop(saves);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after raw/BSAV JSON test");
    }

    private static void TestChunkedBigSoA()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempSaveDir("chunks");
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        SaveSystemState* saves = alloc.Persistent.Alloc<SaveSystemState>(1, 64);

        try
        {
            SaveSystem.Start(saves, ref alloc.Persistent, appName: "ChunkSaveTest", directoryOverride: tempDir);

            // Build 3 independently versioned chunks: meta + 65,536 tiles (128 KB) + 512 FixVec3 entities
            const int tileCount = 65536;
            const int entCount = 512;
            ushort* tiles = alloc.Persistent.AllocUninitialized<ushort>(tileCount, 64);
            for (int i = 0; i < tileCount; i++)
                tiles[i] = (ushort)((i / 64) % 16); // realistic spatial repetition

            FixVec3* positions = alloc.Persistent.AllocUninitialized<FixVec3>(entCount, 64);
            for (int i = 0; i < entCount; i++)
                positions[i] = new FixVec3(Fix32.FromInt(i), Fix32.FromRatio(i, 4), Fix32.FromInt(-i));

            byte* metaBuf = stackalloc byte[128];
            var metaWriter = NetWriter.Create(metaBuf, 128);
            metaWriter.WriteUInt64(0xDEADBEEF_12345678UL); // world seed
            FixedString64 worldName = FixedString64.From("BringWorldAlpha");
            metaWriter.WriteFixedString64(in worldName);

            var chunkWriter = SaveChunkWriter.CreateGrowable(16384);
            try
            {
                Assert.True(chunkWriter.WriteChunk(StringId.Hash("meta"), chunkVersion: 1, metaWriter.WrittenSpan()), "Write meta chunk");
                Assert.True(chunkWriter.WriteChunk(StringId.Hash("tiles"), chunkVersion: 2, new ReadOnlySpan<ushort>(tiles, tileCount)), "Write tiles SoA chunk");
                Assert.True(chunkWriter.WriteChunk(StringId.Hash("entities"), chunkVersion: 1, new ReadOnlySpan<FixVec3>(positions, entCount)), "Write entities FixVec3 chunk");
                Assert.True(!chunkWriter.HasOverflow, "ChunkWriter did not overflow");

                SaveResult saveRes = SaveSystem.SaveSync(
                    saves, "world_0.bsav", chunkWriter.WrittenSpan(),
                    schemaVersion: 4, SavePayloadKind.Chunked, PackCompressionMode.Lz4);
                Assert.True(saveRes.IsSuccess, $"Chunked world save ok ({saveRes.Error})");
            }
            finally
            {
                chunkWriter.Dispose();
            }

            long onDiskBytes = new FileInfo(SaveSystem.GetSlotPath(saves, "world_0.bsav")).Length;
            Assert.True(onDiskBytes < tileCount * 2 / 4, $"LZ4 heavily compressed chunked world ({onDiskBytes} bytes)");

            SaveResult loadRes = SaveSystem.LoadSync(saves, "world_0.bsav");
            try
            {
                Assert.True(loadRes.IsSuccess, "Load chunked world ok");
                Assert.Equal((byte)SavePayloadKind.Chunked, (byte)loadRes.PayloadKind, "Chunked payload kind");
                Assert.Equal(4u, loadRes.GameSchemaVersion, "World schema version");

                SaveChunkReader reader = loadRes.CreateChunkReader();

                // Patch-safety: missing optional future chunk returns false cleanly
                Assert.True(!reader.TryFind(StringId.Hash("weather_v2"), out _), "Missing optional chunk returns false");

                // Lookup chunks in arbitrary order
                Assert.True(reader.TryFind(StringId.Hash("entities"), out SaveChunkView entChunk), "Found entities chunk");
                Assert.Equal((ushort)1, entChunk.Version, "entities chunk v1");
                ReadOnlySpan<FixVec3> loadedPos = entChunk.AsSpan<FixVec3>();
                Assert.Equal(entCount, loadedPos.Length, "512 FixVec3 loaded");
                for (int i = 0; i < entCount; i++)
                    Assert.True(loadedPos[i].Equals(positions[i]), $"FixVec3[{i}] bit-exact");

                Assert.True(reader.TryFind(StringId.Hash("tiles"), out SaveChunkView tileChunk), "Found tiles chunk");
                Assert.Equal((ushort)2, tileChunk.Version, "tiles chunk v2");
                ReadOnlySpan<ushort> loadedTiles = tileChunk.AsSpan<ushort>();
                Assert.Equal(tileCount, loadedTiles.Length, "65536 tiles loaded");
                Assert.Equal(tiles[12345], loadedTiles[12345], "Tile sample exact");

                Assert.True(reader.TryFind(StringId.Hash("meta"), out SaveChunkView metaChunk), "Found meta chunk");
                NetReader mr = metaChunk.CreateNetReader();
                Assert.Equal(0xDEADBEEF_12345678UL, mr.ReadUInt64(), "World seed exact");
                Assert.True(mr.ReadFixedString64().Equals(worldName), "World name exact");
            }
            finally
            {
                SaveSystem.ReleaseResult(ref loadRes);
            }
        }
        finally
        {
            SaveSystem.Stop(saves);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after chunked SoA save test");
    }

    private static void TestCorruptionAndBackupRecovery()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempSaveDir("corrupt");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        SaveSystemState* saves = alloc.Persistent.Alloc<SaveSystemState>(1, 64);

        try
        {
            SaveSystem.Start(saves, ref alloc.Persistent, appName: "CorruptTest", directoryOverride: tempDir);

            // Save v1, then save v2 -> v1 rotates into slot_1.bsav.bak
            Assert.True(SaveSystem.SaveSync(saves, "slot_1.bsav", "{\"step\":1}"u8, schemaVersion: 1, SavePayloadKind.JsonUtf8, PackCompressionMode.None).IsSuccess, "Save v1");
            Assert.True(SaveSystem.SaveSync(saves, "slot_1.bsav", "{\"step\":2}"u8, schemaVersion: 2, SavePayloadKind.JsonUtf8, PackCompressionMode.None).IsSuccess, "Save v2");

            string primaryPath = SaveSystem.GetSlotPath(saves, "slot_1.bsav");
            string bakPath = primaryPath + ".bak";
            Assert.True(File.Exists(bakPath), "Backup file created on second save");

            // 1. Flip a byte in primary payload -> CRC32 mismatch -> auto-recovers v1 from .bak!
            byte[] corrupt = File.ReadAllBytes(primaryPath);
            corrupt[^1] ^= 0xFF;
            File.WriteAllBytes(primaryPath, corrupt);

            SaveResult rec1 = SaveSystem.LoadSync(saves, "slot_1.bsav");
            try
            {
                Assert.True(rec1.IsSuccess, "CRC32 corruption recovered from .bak");
                Assert.Equal((byte)1, rec1.LoadedFromBackup, "LoadedFromBackup == 1");
                Assert.Equal(1u, rec1.GameSchemaVersion, "Recovered v1 schema version");
                Assert.Equal("{\"step\":1}", rec1.DataAsUtf8String(), "Recovered v1 payload");
            }
            finally
            {
                SaveSystem.ReleaseResult(ref rec1);
            }

            // 2. Truncate primary to 10 bytes (shorter than 32-byte BSAV header) -> recovers v1 from .bak!
            File.WriteAllBytes(primaryPath, new byte[10]);
            SaveResult rec2 = SaveSystem.LoadSync(saves, "slot_1.bsav");
            try
            {
                Assert.True(rec2.IsSuccess && rec2.LoadedFromBackup == 1, "Truncated primary recovered from .bak");
                Assert.Equal("{\"step\":1}", rec2.DataAsUtf8String(), "Truncated recovery exact");
            }
            finally
            {
                SaveSystem.ReleaseResult(ref rec2);
            }

            // 3. Corrupt BOTH primary and backup -> returns Corrupted cleanly with 0 leaks
            File.WriteAllBytes(bakPath, new byte[5]);
            SaveResult fail = SaveSystem.LoadSync(saves, "slot_1.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)fail.Status, "Both corrupted returns Corrupted");
            Assert.True(fail.DataPtr == null, "No leaked buffer on Corrupted");
            Assert.Equal(2L, saves->TotalBackupRecoveries.Value, "Backup recovery telemetry counted");
        }
        finally
        {
            SaveSystem.Stop(saves);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after corruption recovery test");
    }

    private static void TestAsyncFifoZeroGcAndLeaks()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempSaveDir("async");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        SaveSystemState* saves = alloc.Persistent.Alloc<SaveSystemState>(1, 64);

        try
        {
            SaveSystem.Start(saves, ref alloc.Persistent, appName: "AsyncSaveTest", directoryOverride: tempDir);

            // 1. Strict FIFO ordering & dedicated background SaveWorker thread verification
            SaveOpHandle h1 = SaveSystem.SaveAsync(saves, "auto.bsav", "{\"gen\":1}"u8, schemaVersion: 1, userTag: 11UL);
            SaveOpHandle h2 = SaveSystem.SaveAsync(saves, "auto.bsav", "{\"gen\":2}"u8, schemaVersion: 2, userTag: 22UL);
            SaveOpHandle h3 = SaveSystem.LoadAsync(saves, "auto.bsav", userTag: 33UL);
            Assert.True(h1.IsValid && h2.IsValid && h3.IsValid, "Async handles valid");

            SaveResult r1 = WaitSaveResult(saves, h1);
            SaveResult r2 = WaitSaveResult(saves, h2);
            SaveResult r3 = WaitSaveResult(saves, h3);
            int workerTid = Volatile.Read(ref saves->WorkerThreadId);
            Assert.True(workerTid != 0 && workerTid != Environment.CurrentManagedThreadId,
                $"SaveWorker runs on dedicated separate thread (worker={workerTid}, caller={Environment.CurrentManagedThreadId})");
            try
            {
                Assert.True(r1.IsSuccess && r2.IsSuccess && r3.IsSuccess, "All 3 FIFO ops succeeded");
                Assert.Equal(33UL, r3.UserTag, "UserTag preserved");
                Assert.Equal(2u, r3.GameSchemaVersion, "FIFO load saw second save");
                Assert.Equal("{\"gen\":2}", r3.DataAsUtf8String(), "FIFO load payload exact");
            }
            finally
            {
                SaveSystem.ReleaseResult(ref r1);
                SaveSystem.ReleaseResult(ref r2);
                SaveSystem.ReleaseResult(ref r3);
            }

            // 2. Zero-GC Logic-Thread check across SaveAsync + IsPending + PollCompleted + LoadAsync + ReleaseResult
            ReadOnlySpan<byte> payload = "{\"hp\":100,\"mana\":50}"u8;
            long timeoutTicks = Stopwatch.Frequency * 5;
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int iter = 0; iter < 5; iter++)
            {
                SaveOpHandle hs = SaveSystem.SaveAsync(saves, "hot.bsav", payload, schemaVersion: 1, SavePayloadKind.JsonUtf8, PackCompressionMode.Lz4, createBackup: true, userTag: (ulong)iter);
                long deadline = Stopwatch.GetTimestamp() + timeoutTicks;
                SaveResult rs = default;
                bool gotSave = false;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    _ = SaveSystem.IsPending(saves, hs);
                    if (SaveSystem.PollCompleted(saves, out rs))
                    {
                        gotSave = true;
                        break;
                    }
                    Thread.Sleep(1);
                }
                Assert.True(gotSave && rs.IsSuccess, "Zero-GC SaveAsync completed");
                SaveSystem.ReleaseResult(ref rs);

                SaveOpHandle hl = SaveSystem.LoadAsync(saves, "hot.bsav", userTag: (ulong)iter);
                deadline = Stopwatch.GetTimestamp() + timeoutTicks;
                SaveResult rl = default;
                bool gotLoad = false;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    if (SaveSystem.PollCompleted(saves, out rl))
                    {
                        gotLoad = true;
                        break;
                    }
                    Thread.Sleep(1);
                }
                Assert.True(gotLoad && rl.IsSuccess && rl.DataLength == payload.Length, "Zero-GC LoadAsync completed");
                SaveSystem.ReleaseResult(ref rl);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "SaveSystem async Save/Load/Poll/Release hot loop allocates 0 GC bytes");

            // 3. Intentionally leave a completed LoadResult unpolled to verify Stop() frees it cleanly
            SaveOpHandle hLeakCheck = SaveSystem.LoadAsync(saves, "hot.bsav");
            long waitEnd = Stopwatch.GetTimestamp() + timeoutTicks;
            while (SaveSystem.IsPending(saves, hLeakCheck) && Stopwatch.GetTimestamp() < waitEnd)
                Thread.Sleep(1);
        }
        finally
        {
            SaveSystem.Stop(saves);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after async SaveSystem test (incl. unpolled result)");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live native blocks after SaveSystem test");
    }

    private static SaveResult WaitSaveResult(SaveSystemState* saves, SaveOpHandle expectedHandle, int timeoutMs = 5000)
    {
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * timeoutMs) / 1000;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (SaveSystem.PollCompleted(saves, out SaveResult r))
            {
                if (r.Handle == expectedHandle)
                    return r;
                SaveSystem.ReleaseResult(ref r);
            }
            Thread.Sleep(1);
        }
        throw new TimeoutException($"Timed out waiting for SaveOp {expectedHandle}");
    }

    private static void TestRawAsyncAndPathSanitization()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempSaveDir("rawasync");
        string logDir = CreateTempSaveDir("rawasynclog");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        SaveSystemState* saves = alloc.Persistent.Alloc<SaveSystemState>(1, 64);

        try
        {
            LogSystem.Start(log, ref alloc.Persistent, appName: "RawAsyncLog", minLevel: LogLevel.Info, directoryOverride: logDir, hookExceptions: false);
            SaveSystem.Start(saves, ref alloc.Persistent, appName: "RawAsyncTest", directoryOverride: tempDir, log: log);

            // 1. SaveRawAsync -> SaveRawAsync (creates .bak) -> delete primary -> LoadRawAsync recovers from .bak
            SaveOpHandle s1 = SaveSystem.SaveRawAsync(saves, "prefs.json", "{\"v\":1}"u8);
            SaveResult r1 = WaitSaveResult(saves, s1);
            SaveSystem.ReleaseResult(ref r1);
            Assert.True(r1.IsSuccess, "SaveRawAsync v1 ok");

            SaveOpHandle s2 = SaveSystem.SaveRawAsync(saves, "prefs.json", "{\"v\":2}"u8);
            SaveResult r2 = WaitSaveResult(saves, s2);
            SaveSystem.ReleaseResult(ref r2);
            Assert.True(r2.IsSuccess, "SaveRawAsync v2 ok");

            string rawPath = SaveSystem.GetSlotPath(saves, "prefs.json");
            File.Delete(rawPath);

            SaveOpHandle l1 = SaveSystem.LoadRawAsync(saves, "prefs.json");
            SaveResult rl1 = WaitSaveResult(saves, l1);
            try
            {
                Assert.True(rl1.IsSuccess && rl1.LoadedFromBackup == 1, "LoadRawAsync recovered from .bak");
                Assert.Equal("{\"v\":1}", rl1.DataAsUtf8String(), "LoadRawAsync recovered v1 content");
            }
            finally { SaveSystem.ReleaseResult(ref rl1); }

            // DeleteAsync removes primary & backup
            SaveOpHandle d1 = SaveSystem.DeleteAsync(saves, "prefs.json", deleteBackup: true);
            SaveResult rd1 = WaitSaveResult(saves, d1);
            SaveSystem.ReleaseResult(ref rd1);
            Assert.True(rd1.IsSuccess && !SaveSystem.Exists(saves, "prefs.json"), "DeleteAsync removed primary and backup");

            // 2. Long slot name (> 60 bytes) async vs sync parity & zero-GC check
            string longName = new string('a', 75) + "_profile.bsav";
            ReadOnlySpan<byte> payload = "{\"ok\":true}"u8;

            // Warmup once
            SaveOpHandle warm = SaveSystem.SaveAsync(saves, longName, payload);
            SaveResult rw = WaitSaveResult(saves, warm);
            SaveSystem.ReleaseResult(ref rw);

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            SaveOpHandle sl = SaveSystem.SaveAsync(saves, longName, payload);
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "SaveAsync with >60-char slot name allocates 0 GC bytes");

            SaveResult rsl = WaitSaveResult(saves, sl);
            SaveSystem.ReleaseResult(ref rsl);
            Assert.True(rsl.IsSuccess, "Long slot SaveAsync succeeded");
            Assert.True(SaveSystem.Exists(saves, longName), "Sync Exists sees long async slot");

            SaveResult syncLoad = SaveSystem.LoadSync(saves, longName);
            try
            {
                Assert.True(syncLoad.IsSuccess, "Sync LoadSync sees long async slot");
                Assert.Equal("{\"ok\":true}", syncLoad.DataAsUtf8String(), "Long slot payload matches");
            }
            finally { SaveSystem.ReleaseResult(ref syncLoad); }

            // 3. Path traversal containment
            string evilPath = SaveSystem.GetSlotPath(saves, "../../evil_slot.bsav");
            Assert.Equal(tempDir, Path.GetDirectoryName(evilPath) ?? string.Empty, "Traversal stripped to savesDir");
            Assert.Equal("evil_slot.bsav", Path.GetFileName(evilPath), "Sanitized filename exact");

            string dotPath = SaveSystem.GetSlotPath(saves, "..");
            Assert.Equal("save.bsav", Path.GetFileName(dotPath), "Dots-only falls back to save.bsav");
            Assert.Equal(0, saves->PendingCount.Value, "PendingCount == 0 after drain");

            LogSystem.Flush(log);
            string[] logLines = ReadAllLinesShared(LogSystem.GetFilePath(log));
            bool sawBackupLog = false;
            for (int i = 0; i < logLines.Length; i++)
            {
                if (logLines[i].Contains("recovered", StringComparison.OrdinalIgnoreCase) &&
                    logLines[i].Contains(".bak", StringComparison.OrdinalIgnoreCase))
                    sawBackupLog = true;
            }
            Assert.True(sawBackupLog, "Optional LogState logged .bak recovery warning");
        }
        finally
        {
            SaveSystem.Stop(saves);
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
            TryDeleteDir(logDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after raw/path test");
    }

    private static void WriteCustomBsavFile(string path, ushort fmtVer, byte payloadKind, byte compMode, int uncompBytes, byte[] payloadBytes, uint crc = 0)
    {
        var hdr = new BsavHeader
        {
            Magic = BsavHeader.MagicValue,
            FormatVersion = fmtVer,
            PayloadKind = payloadKind,
            Compression = compMode,
            GameSchemaVersion = 1,
            UncompressedBytes = uncompBytes,
            CompressedBytes = payloadBytes.Length,
            PayloadCrc32 = crc,
            SavedUtcTicks = 1,
        };
        byte[] all = new byte[sizeof(BsavHeader) + payloadBytes.Length];
        fixed (byte* p = all) *(BsavHeader*)p = hdr;
        Buffer.BlockCopy(payloadBytes, 0, all, sizeof(BsavHeader), payloadBytes.Length);
        File.WriteAllBytes(path, all);
    }

    private static void TestAdversarialBsavLz4BrotliAndChunkBounds()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempSaveDir("adv");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        SaveSystemState* saves = alloc.Persistent.Alloc<SaveSystemState>(1, 64);

        try
        {
            SaveSystem.Start(saves, ref alloc.Persistent, appName: "AdvSaveTest", maxSaveBytes: 4096, directoryOverride: tempDir);

            // 1. Missing primary + corrupt .bak -> MUST return Corrupted (not NotFound)
            string missingPrimaryPath = SaveSystem.GetSlotPath(saves, "only_bak.bsav");
            File.WriteAllBytes(missingPrimaryPath + ".bak", new byte[] { 1, 2, 3 });
            SaveResult rOnlyBak = SaveSystem.LoadSync(saves, "only_bak.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rOnlyBak.Status, "Missing primary + corrupt .bak returns Corrupted");
            Assert.True(rOnlyBak.DataPtr == null, "No leaked buffer on corrupt .bak");

            // 2. UncompressedBytes == 0 with CompressedBytes != 0 -> Corrupted
            string pZeroUncomp = SaveSystem.GetSlotPath(saves, "zero_uncomp.bsav");
            WriteCustomBsavFile(pZeroUncomp, BsavHeader.CurrentFormatVersion, (byte)SavePayloadKind.Binary, (byte)PackCompressionMode.None, 0, new byte[16]);
            SaveResult rZeroUncomp = SaveSystem.LoadSync(saves, "zero_uncomp.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rZeroUncomp.Status, "UncompressedBytes==0 && CompressedBytes>0 rejected");

            // 3. Invalid PayloadKind (e.g. Raw=0 or 99 inside BSAV) -> Corrupted
            string pBadKind = SaveSystem.GetSlotPath(saves, "bad_kind.bsav");
            WriteCustomBsavFile(pBadKind, BsavHeader.CurrentFormatVersion, (byte)99, (byte)PackCompressionMode.None, 4, new byte[4], NetHash.Crc32(new byte[4]));
            SaveResult rBadKind = SaveSystem.LoadSync(saves, "bad_kind.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rBadKind.Status, "Invalid BSAV PayloadKind rejected");

            // 4. UncompressedBytes > MaxSaveBytes -> Corrupted
            string pTooBig = SaveSystem.GetSlotPath(saves, "too_big.bsav");
            WriteCustomBsavFile(pTooBig, BsavHeader.CurrentFormatVersion, (byte)SavePayloadKind.Binary, (byte)PackCompressionMode.Lz4, 8192, new byte[8]);
            SaveResult rTooBig = SaveSystem.LoadSync(saves, "too_big.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rTooBig.Status, "UncompressedBytes > MaxSaveBytes rejected");

            // 5. Unknown Compression mode -> Corrupted
            string pBadComp = SaveSystem.GetSlotPath(saves, "bad_comp.bsav");
            WriteCustomBsavFile(pBadComp, BsavHeader.CurrentFormatVersion, (byte)SavePayloadKind.Binary, (byte)250, 4, new byte[4]);
            SaveResult rBadComp = SaveSystem.LoadSync(saves, "bad_comp.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rBadComp.Status, "Unknown Compression mode rejected");

            // 6. Corrupt Brotli stream -> Corrupted
            string pBadBrotli = SaveSystem.GetSlotPath(saves, "bad_brotli.bsav");
            WriteCustomBsavFile(pBadBrotli, BsavHeader.CurrentFormatVersion, (byte)SavePayloadKind.Binary, (byte)PackCompressionMode.Brotli, 32, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            SaveResult rBadBrotli = SaveSystem.LoadSync(saves, "bad_brotli.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rBadBrotli.Status, "Corrupt Brotli returns Corrupted");

            // 7. Corrupt LZ4 stream (STO-02): trailing 0xF0 token at EOF & 0xFF run integer overflow -> Corrupted
            string pLz4Eof = SaveSystem.GetSlotPath(saves, "bad_lz4_eof.bsav");
            WriteCustomBsavFile(pLz4Eof, BsavHeader.CurrentFormatVersion, (byte)SavePayloadKind.Binary, (byte)PackCompressionMode.Lz4, 32, new byte[] { 0xF0 });
            SaveResult rLz4Eof = SaveSystem.LoadSync(saves, "bad_lz4_eof.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rLz4Eof.Status, "Corrupt LZ4 0xF0 EOF returns Corrupted");

            byte[] ffRun = new byte[64];
            ffRun[0] = 0xF0;
            for (int i = 1; i < ffRun.Length; i++) ffRun[i] = 0xFF;
            string pLz4Ff = SaveSystem.GetSlotPath(saves, "bad_lz4_ff.bsav");
            WriteCustomBsavFile(pLz4Ff, BsavHeader.CurrentFormatVersion, (byte)SavePayloadKind.Binary, (byte)PackCompressionMode.Lz4, 64, ffRun);
            SaveResult rLz4Ff = SaveSystem.LoadSync(saves, "bad_lz4_ff.bsav");
            Assert.Equal((byte)SaveStatus.Corrupted, (byte)rLz4Ff.Status, "Corrupt LZ4 0xFF run returns Corrupted");

            // 8. SaveChunkReader adversarial bounds (payloadLen = int.MaxValue, truncated < 16 bytes)
            byte* badChunkBuf = stackalloc byte[32];
            BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(badChunkBuf + 0, 8), StringId.Hash("evil").Value);
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(badChunkBuf + 8, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(badChunkBuf + 10, 2), 0);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(badChunkBuf + 12, 4), int.MaxValue);
            var readerMaxLen = SaveChunkReader.Create(badChunkBuf, 32);
            Assert.True(!readerMaxLen.Next(out _), "SaveChunkReader rejects payloadLen=int.MaxValue");
            Assert.True(readerMaxLen.IsMalformed, "SaveChunkReader flags IsMalformed on overflow length");

            var readerShort = SaveChunkReader.Create(badChunkBuf, 8);
            Assert.True(!readerShort.Next(out _) && readerShort.IsMalformed, "SaveChunkReader flags IsMalformed on < 16 bytes");

            // 9. SaveChunkWriter.CreateInArena overflow
            var arenaWriter = SaveChunkWriter.CreateInArena(ref alloc.Scratch, 32);
            Assert.True(!arenaWriter.WriteChunk(StringId.Hash("big"), 1, new byte[64]), "Arena SaveChunkWriter rejects oversized chunk");
            Assert.True(arenaWriter.HasOverflow, "Arena SaveChunkWriter sets HasOverflow");
        }
        finally
        {
            SaveSystem.Stop(saves);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after adversarial BSAV/LZ4/Chunk test");
    }

    private static void TestMaxPendingOpsAndMaxSaveBytesLimits()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempSaveDir("limits");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        SaveSystemState* saves = alloc.Persistent.Alloc<SaveSystemState>(1, 64);

        try
        {
            SaveSystem.Start(saves, ref alloc.Persistent, appName: "LimitsTest", maxPendingOps: 4, maxSaveBytes: 4096, directoryOverride: tempDir);

            // 1. Payload > MaxSaveBytes returns Invalid immediately
            byte[] oversized = new byte[4097];
            SaveOpHandle bigOp = SaveSystem.SaveAsync(saves, "oversize.bsav", oversized);
            Assert.True(!bigOp.IsValid, "Oversized payload > MaxSaveBytes rejected with Invalid handle");

            // 2. MaxPendingOps=4 slot saturation -> 5th op returns Invalid until polled
            byte[] small = "{\"ok\":1}"u8.ToArray();
            var handles = new SaveOpHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = SaveSystem.SaveAsync(saves, $"slot_{i}.bsav", small);
                Assert.True(handles[i].IsValid, $"Slot {i} accepted");
            }
            SaveOpHandle fifth = SaveSystem.SaveAsync(saves, "slot_4.bsav", small);
            Assert.True(!fifth.IsValid, "5th concurrent op rejected when MaxPendingOps=4");

            for (int i = 0; i < 4; i++)
            {
                SaveResult r = WaitSaveResult(saves, handles[i]);
                SaveSystem.ReleaseResult(ref r);
                Assert.True(r.IsSuccess, $"Slot {i} completed");
            }

            SaveOpHandle afterPoll = SaveSystem.SaveAsync(saves, "slot_4.bsav", small);
            Assert.True(afterPoll.IsValid, "Slot reusable after PollCompleted");
            SaveResult rp = WaitSaveResult(saves, afterPoll);
            SaveSystem.ReleaseResult(ref rp);
        }
        finally
        {
            SaveSystem.Stop(saves);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after limits test");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after limits test");
    }
}
