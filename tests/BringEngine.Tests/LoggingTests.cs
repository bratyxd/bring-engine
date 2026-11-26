using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Tests;

public static unsafe class LoggingTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== Core Logging: Platform Paths, 10k FIFO Trimming, Multi-File Handles & Zero-GC Interpolation ===");
        Assert.Run("LogSystem_PlatformPathDefaultFileFormatAndNewlineSanitization", &TestPlatformPathAndFormat);
        Assert.Run("LogSystem_Default10kFifoEvictionAndRestartPersistence", &TestDefault10kFifoAndPersistence);
        Assert.Run("LogSystem_MultipleFilesCustomLimitsAndCloseFile", &TestMultipleFilesAndCustomLimits);
        Assert.Run("LogSystem_ZeroGcInterpolatedHotLoopFilteredSkipAndMultiThread", &TestZeroGcFilteredSkipAndMultiThread);
        Assert.Run("LogSystem_DedicatedWriterThreadAndAutoExceptionErrorLogging", &TestDedicatedThreadAndAutoExceptionLogging);
        Assert.Run("LogSystem_OverflowDropNewest_SlotExhaustionAndGenerationReuse", &TestOverflowDropNewestSlotExhaustionAndGenReuse);
        Assert.Run("LogSystem_TruncationNonAsciiZeroGcAllHandlesAndRuntimeMinLevel", &TestTruncationNonAsciiAllHandlesAndMinLevel);
        Assert.Run("LogSystem_UnobservedCancellationSkipAndCloseRace", &TestUnobservedCancellationSkipAndCloseRace);
    }

    private static string CreateTempLogDir(string suffix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"bring_log_test_{suffix}_{Guid.NewGuid():N}");
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

    private static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static void TestPlatformPathAndFormat()
    {
        NativeMemoryTracker.ResetStatsForTest();

        // 1. Verify dynamic OS-standard platform log path resolution (headless-safe)
        string resolved = LogSystem.ResolvePlatformLogDirectory("BringTestGame");
        Assert.True(!string.IsNullOrWhiteSpace(resolved), "Resolved platform log dir non-empty");
        Assert.True(resolved.Contains("BringTestGame", StringComparison.Ordinal), "Platform path contains app name");
        if (OperatingSystem.IsWindows())
            Assert.True(resolved.EndsWith(Path.Combine("BringTestGame", "Logs"), StringComparison.OrdinalIgnoreCase), "Windows uses LocalAppData/<App>/Logs");
        else if (OperatingSystem.IsMacOS())
            Assert.True(resolved.Contains(Path.Combine("Library", "Application Support", "BringTestGame", "Logs"), StringComparison.Ordinal), "macOS uses ~/Library/Application Support/<App>/Logs");
        else
            Assert.True(resolved.EndsWith(Path.Combine("BringTestGame", "logs"), StringComparison.Ordinal), "Linux uses XDG_DATA_HOME/<App>/logs");

        // 2. Verify default file creation, timestamp + priority format, level filtering, and CR/LF sanitization
        string tempDir = CreateTempLogDir("format");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);

        try
        {
            LogSystem.Start(log, ref alloc.Persistent, appName: "BringTestGame", minLevel: LogLevel.Info, directoryOverride: tempDir);
            string defPath = LogSystem.GetFilePath(log);
            Assert.True(defPath.EndsWith(LogState.DefaultFileName, StringComparison.Ordinal), "Default filename is engine.log");

            LogSystem.Debug(log, "filtered-out-debug");
            LogSystem.Info(log, "Engine boot complete");
            LogSystem.Warn(log, $"Latency spike: {42}ms");
            LogSystem.Error(log, "Line1\nLine2\r\nLine3");
            LogSystem.Flush(log);

            string[] lines = ReadAllLinesShared(defPath);
            Assert.Equal(3, lines.Length, "Filtered Debug skipped; 3 lines written (CR/LF sanitized to single line)");

            var rx = new Regex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[(TRACE|DEBUG|INFO |WARN |ERROR|FATAL)\] .+$");
            for (int i = 0; i < lines.Length; i++)
                Assert.True(rx.IsMatch(lines[i]), $"Line {i} matches pretty timestamp + level format: {lines[i]}");

            Assert.True(lines[0].Contains("[INFO ] Engine boot complete", StringComparison.Ordinal), "INFO line");
            Assert.True(lines[1].Contains("[WARN ] Latency spike: 42ms", StringComparison.Ordinal), "WARN interpolated line");
            Assert.True(lines[2].Contains("[ERROR] Line1 Line2  Line3", StringComparison.Ordinal), "ERROR CR/LF sanitized");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after format test");
    }

    private static void TestDefault10kFifoAndPersistence()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempLogDir("fifo10k");

        try
        {
            // Session 1: write 12,500 lines using default 10,000-line cap -> oldest 2,500 deleted from line 1
            var alloc1 = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
            LogState* log1 = alloc1.Persistent.Alloc<LogState>(1, 64);
            string defPath;
            try
            {
                LogSystem.Start(log1, ref alloc1.Persistent, appName: "FifoTest", minLevel: LogLevel.Info, directoryOverride: tempDir);
                defPath = LogSystem.GetFilePath(log1);

                for (int i = 0; i < 12500; i++)
                    LogSystem.Info(log1, $"seq={i}");

                LogSystem.Flush(log1);
                Assert.True(log1->TotalCompactions.Value >= 2, "Amortized compaction triggered during 12.5k burst");
            }
            finally
            {
                LogSystem.Stop(log1);
                alloc1.Dispose();
            }

            string[] lines1 = File.ReadAllLines(defPath);
            Assert.Equal(LogState.DefaultMaxLines, lines1.Length, "Default cap enforces exact 10,000 lines");
            Assert.True(lines1[0].EndsWith("seq=2500", StringComparison.Ordinal), $"Oldest lines deleted from top (first={lines1[0]})");
            Assert.True(lines1[^1].EndsWith("seq=12499", StringComparison.Ordinal), $"Newest line preserved (last={lines1[^1]})");

            // Session 2: reopen existing file and write 100 more lines -> persists 10,000 cap across restarts
            var alloc2 = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
            LogState* log2 = alloc2.Persistent.Alloc<LogState>(1, 64);
            try
            {
                LogSystem.Start(log2, ref alloc2.Persistent, appName: "FifoTest", minLevel: LogLevel.Info, directoryOverride: tempDir);
                for (int i = 12500; i < 12600; i++)
                    LogSystem.Info(log2, $"seq={i}");
                LogSystem.Flush(log2);
            }
            finally
            {
                LogSystem.Stop(log2);
                alloc2.Dispose();
            }

            string[] lines2 = File.ReadAllLines(defPath);
            Assert.Equal(LogState.DefaultMaxLines, lines2.Length, "Restart preserves exact 10,000 line cap");
            Assert.True(lines2[0].EndsWith("seq=2600", StringComparison.Ordinal), $"Restart evicted oldest 100 lines (first={lines2[0]})");
            Assert.True(lines2[^1].EndsWith("seq=12599", StringComparison.Ordinal), $"Restart kept newest line (last={lines2[^1]})");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after 10k FIFO persistence test");
    }

    private static void TestMultipleFilesAndCustomLimits()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempLogDir("multifile");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);

        try
        {
            LogSystem.Start(log, ref alloc.Persistent, appName: "MultiTest", minLevel: LogLevel.Trace, directoryOverride: tempDir);

            // Secondary file with default 10,000 limit
            LogFileHandle netFile = LogSystem.OpenFile(log, "network.log");
            Assert.True(netFile.IsValid, "network.log handle valid");

            // Secondary file with custom 50-line limit
            LogFileHandle combatFile = LogSystem.OpenFile(log, "combat.log", maxLines: 50);
            Assert.True(combatFile.IsValid, "combat.log handle valid");

            string defPath = LogSystem.GetFilePath(log);
            string netPath = LogSystem.GetFilePath(log, netFile);
            string combatPath = LogSystem.GetFilePath(log, combatFile);

            LogSystem.Info(log, "default-only-line");
            for (int i = 0; i < 20; i++)
                LogSystem.Info(log, netFile, $"net-pkt-{i}");
            for (int i = 0; i < 120; i++)
                LogSystem.Warn(log, combatFile, $"hit-{i}");

            LogSystem.CloseFile(log, combatFile);
            // Writing to closed handle must be safely ignored (no crash, no write)
            LogSystem.Error(log, combatFile, "after-close-ignored");
            LogSystem.Flush(log);

            string[] defLines = ReadAllLinesShared(defPath);
            string[] netLines = ReadAllLinesShared(netPath);
            string[] combatLines = ReadAllLinesShared(combatPath);

            Assert.Equal(1, defLines.Length, "Default file isolated");
            Assert.Equal(20, netLines.Length, "network.log kept all 20 lines (default 10k limit)");
            Assert.Equal(50, combatLines.Length, "combat.log enforced custom 50-line cap");
            Assert.True(combatLines[0].EndsWith("hit-70", StringComparison.Ordinal), "combat.log evicted first 70 lines FIFO");
            Assert.True(combatLines[^1].EndsWith("hit-119", StringComparison.Ordinal), "combat.log kept latest line and ignored post-close log");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after multi-file test");
    }

    private static int IncrementSideEffect(ref int counter)
    {
        counter++;
        return counter;
    }

    private static void TestZeroGcFilteredSkipAndMultiThread()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempLogDir("zerogc");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);

        try
        {
            LogSystem.Start(log, ref alloc.Persistent, appName: "ZeroGcTest", defaultMaxLines: 2000, minLevel: LogLevel.Info, directoryOverride: tempDir);
            string defPath = LogSystem.GetFilePath(log);

            FixVec3 pos = new FixVec3(Fix32.FromInt(12), Fix32.FromRatio(-5, 2), Fix32.FromInt(3));
            EntityId eid = EntityId.Create(9, 2);
            FixedString64 name = FixedString64.From("BossUnit");

            // Warmup JIT for interpolated handlers
            LogSystem.Info(log, $"Warmup {0} ent={eid} pos={pos} name={name} hp={99.5f:F1}");
            LogSystem.Trace(log, $"Filtered warmup {0}");
            LogSystem.Flush(log);

            // 1. Filtered-out Trace/Debug MUST skip argument evaluation AND allocate 0 GC bytes
            int sideEffect = 0;
            long gcBeforeSkip = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++)
            {
                LogSystem.Trace(log, $"Skip {IncrementSideEffect(ref sideEffect)} pos={pos}");
                LogSystem.Debug(log, $"Skip {IncrementSideEffect(ref sideEffect)} ent={eid}");
                LogSystem.Write(log, LogLevel.Debug, $"Skip {IncrementSideEffect(ref sideEffect)}");
            }
            long gcAfterSkip = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0, sideEffect, "Filtered-out interpolated logs skip argument evaluation completely");
            Assert.Equal(0L, gcAfterSkip - gcBeforeSkip, "Filtered-out logs allocate 0 GC bytes");

            // 2. Active interpolated hot loop MUST allocate 0 GC bytes on the calling thread
            long gcBeforeHot = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 400; i++)
            {
                LogSystem.Info(log, $"Tick {i} ent={eid} name={name} pos={pos} dt={0.0166f:F4}");
                LogSystem.Info(log, "Static UTF-8 literal"u8);
            }
            long gcAfterHot = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfterHot - gcBeforeHot, "Active interpolated & UTF-8 logging hot loop allocates 0 GC bytes");

            // 3. Multi-threaded MPSC logging across 4 concurrent threads (zero corruption, zero drops)
            const int threads = 4;
            const int perThread = 200;
            Thread[] workers = new Thread[threads];
            nint logPtr = (nint)log;
            for (int t = 0; t < threads; t++)
            {
                int tid = t;
                workers[t] = new Thread(() =>
                {
                    LogState* l = (LogState*)logPtr;
                    for (int k = 0; k < perThread; k++)
                        LogSystem.Info(l, $"mt worker={tid} step={k}");
                });
                workers[t].Start();
            }
            for (int t = 0; t < threads; t++) workers[t].Join();

            LogSystem.Flush(log);
            Assert.Equal(0L, log->TotalDropped.Value, "Zero dropped entries under BlockUntilSpace multi-thread burst");

            string[] allLines = ReadAllLinesShared(defPath);
            // 1 warmup + 800 hot loop + 800 multithreaded = 1601 lines (< 2000 limit)
            Assert.Equal(1601, allLines.Length, "All 1601 lines written cleanly across threads");
            Assert.True(allLines[1].Contains("ent=Entity(9:g2) name=BossUnit pos=(12.00, -2.50, 3.00)", StringComparison.Ordinal),
                $"Bring Engine custom types formatted cleanly: {allLines[1]}");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after zero-GC & multi-thread logging test");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live native blocks after logging test");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowHelperForAutoLogTest()
    {
        throw new InvalidOperationException("auto-logged-boom-42");
    }

    private static void TestDedicatedThreadAndAutoExceptionLogging()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempLogDir("autoex");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);

        try
        {
            LogSystem.Start(log, ref alloc.Persistent, appName: "AutoExTest", minLevel: LogLevel.Info, directoryOverride: tempDir);
            string defPath = LogSystem.GetFilePath(log);

            // 1. Verify logger runs on its own dedicated background thread (never gameplay thread)
            LogSystem.Flush(log);
            int writerTid = Volatile.Read(ref log->WriterThreadId);
            Assert.True(writerTid != 0 && writerTid != Environment.CurrentManagedThreadId,
                $"LogWriter runs on dedicated separate thread (writer={writerTid}, caller={Environment.CurrentManagedThreadId})");

            // 2. Throw a normal exception WITHOUT calling LogSystem.Error -> MUST be auto-logged as [ERROR]!
            try
            {
                ThrowHelperForAutoLogTest();
            }
            catch (InvalidOperationException)
            {
                // Intentionally silent catch — engine must have logged it automatically
            }

            // 3. OperationCanceledException is skipped by default (normal HTTP/WS cancel flow, not an error)
            try
            {
                throw new OperationCanceledException("expected-cancel-skip");
            }
            catch (OperationCanceledException)
            {
            }

            // 4. Explicit LogSystem.LogException helper with context
            try
            {
                throw new ArgumentException("bad-slot-arg");
            }
            catch (ArgumentException ex)
            {
                LogSystem.LogException(log, ex, "ManualContext");
            }

            LogSystem.Flush(log);

            string[] lines = ReadAllLinesShared(defPath);
            bool sawAutoBoom = false;
            bool sawCancel = false;
            bool sawManualCtx = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("[ERROR] [Exception] InvalidOperationException: auto-logged-boom-42", StringComparison.Ordinal) &&
                    lines[i].Contains("ThrowHelperForAutoLogTest", StringComparison.Ordinal))
                    sawAutoBoom = true;
                if (lines[i].Contains("expected-cancel-skip", StringComparison.Ordinal))
                    sawCancel = true;
                if (lines[i].Contains("[ERROR] [Exception] ManualContext | ArgumentException: bad-slot-arg", StringComparison.Ordinal))
                    sawManualCtx = true;
            }

            Assert.True(sawAutoBoom, "Thrown exception automatically logged as [ERROR] with throw site without user call");
            Assert.True(!sawCancel, "Expected OperationCanceledException skipped by default");
            Assert.True(sawManualCtx, "Explicit LogSystem.LogException logged with context");
            Assert.True(log->TotalExceptionsLogged.Value >= 2, "TotalExceptionsLogged tracked");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        // 5. After Stop(), exception hooks are detached cleanly -> throwing causes no crash/error
        try { throw new InvalidOperationException("post-stop-safe"); } catch (InvalidOperationException) { }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after auto-exception logging test");
    }

    private static void TestOverflowDropNewestSlotExhaustionAndGenReuse()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempLogDir("dropnewest");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);

        try
        {
            // Small ring (64) with DropNewest policy
            LogSystem.Start(
                log, ref alloc.Persistent, appName: "DropTest",
                minLevel: LogLevel.Info,
                overflowPolicy: LogOverflowPolicy.DropNewest,
                maxFiles: 3,
                queueCapacity: 64,
                directoryOverride: tempDir,
                hookExceptions: false);

            // Burst heavily without waiting -> non-blocking, TotalDropped > 0
            for (int i = 0; i < 8000; i++)
                LogSystem.Info(log, $"burst-{i}-payload-padding-to-exercise-ring");
            LogSystem.Flush(log);
            Assert.True(log->TotalWritten.Value > 0, "DropNewest wrote entries");
            Assert.True(log->TotalLogged.Value + log->TotalDropped.Value == 8000, "Logged + Dropped == total attempts");

            // Slot exhaustion + duplicate filename rejection + generation reuse
            LogFileHandle dupDefault = LogSystem.OpenFile(log, LogState.DefaultFileName);
            Assert.True(!dupDefault.IsValid, "Duplicate OpenFile for engine.log rejected");

            LogFileHandle f1 = LogSystem.OpenFile(log, "slot_a.log");
            LogFileHandle f2 = LogSystem.OpenFile(log, "slot_b.log");
            Assert.True(f1.IsValid && f2.IsValid, "Slots 1 & 2 opened");

            LogFileHandle fDup = LogSystem.OpenFile(log, "slot_a.log");
            Assert.True(!fDup.IsValid, "Duplicate OpenFile for slot_a.log rejected");

            LogFileHandle f3 = LogSystem.OpenFile(log, "slot_c.log");
            Assert.True(!f3.IsValid, "MaxFiles=3 exhaustion returns Invalid");

            LogSystem.CloseFile(log, f1);
            LogFileHandle f1Reopened = LogSystem.OpenFile(log, "slot_a2.log");
            Assert.True(f1Reopened.IsValid && f1Reopened.Index == f1.Index && f1Reopened.Generation != f1.Generation,
                "Reopened slot increments generation");

            // Stale handle write & close must not affect reopened slot
            LogSystem.Info(log, f1, "stale-write-ignored");
            LogSystem.CloseFile(log, f1);
            LogSystem.Info(log, f1Reopened, "active-write-preserved");
            LogSystem.Flush(log);

            string[] linesA2 = ReadAllLinesShared(LogSystem.GetFilePath(log, f1Reopened));
            Assert.Equal(1, linesA2.Length, "Reopened slot kept only active write");
            Assert.True(linesA2[0].Contains("active-write-preserved", StringComparison.Ordinal), "Active write content exact");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after DropNewest/slot test");
    }

    private static void TestTruncationNonAsciiAllHandlesAndMinLevel()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempLogDir("trunc");

        // Pre-seed oversized engine.log (> 20 lines) to test startup compaction
        string seedPath = Path.Combine(tempDir, LogState.DefaultFileName);
        var seedLines = new string[35];
        for (int i = 0; i < 35; i++) seedLines[i] = $"[2026-01-01 00:00:00.000] [INFO ] seed-{i}";
        File.WriteAllLines(seedPath, seedLines);

        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);

        try
        {
            LogSystem.Start(log, ref alloc.Persistent, appName: "TruncTest", defaultMaxLines: 20, minLevel: LogLevel.Warn, directoryOverride: tempDir, hookExceptions: false);
            Assert.Equal((byte)LogLevel.Warn, (byte)LogSystem.GetMinLevel(log), "Initial MinLevel == Warn");
            LogSystem.Info(log, "filtered-before-minlevel-change");
            LogSystem.SetMinLevel(log, LogLevel.Info);
            Assert.Equal((byte)LogLevel.Info, (byte)LogSystem.GetMinLevel(log), "Runtime MinLevel updated to Info");

            // Huge non-ASCII string (> 492 bytes) -> fast O(1) bounded truncation with '...' & valid UTF-8
            string hugeUni = new string('界', 2000);
            LogSystem.Info(log, $"uni={hugeUni}");

            // Warmup & zero-GC verification across all 25 engine handle structs
            var eH = EntityId.Create(1, 2);
            var sH = SceneId.Create(2, 3);
            var bH = BundleId.Create(3, 4);
            var mH = MeshHandle.Create(4, 5);
            var tH = TextureHandle.Create(5, 6);
            var matH = MaterialHandle.Create(6, 7);
            var shH = ShaderHandle.Create(7, 8);
            var fH = FontHandle.Create(8, 9);
            var gH = GltfHandle.Create(9, 10);
            var acH = AnimClipHandle.Create(10, 11);
            var skH = SkeletonHandle.Create(11, 12);
            var apH = AnimPlayerHandle.Create(12, 13);
            var sapH = SpriteAnimPlayerHandle.Create(13, 14);
            var twH = TweenHandle.Create(14, 15);
            var emH = EmitterHandle.Create(15, 16);
            var camH = CameraHandle.Create(16, 17);
            var spH = SpriteHandle.Create(17, 18);
            var txtH = TextHandle.Create(18, 19);
            var jH = JobHandle.Create(19, 20);
            var httpH = HttpRequestHandle.Create(20, 21);
            var wsH = WebSocketHandle.Create(21, 22);
            var lfH = LogFileHandle.Create(22, 23);
            var svH = SaveOpHandle.Create(23, 24);
            var auCH = AudioClipHandle.Create(24, 25);
            var auVH = AudioVoiceHandle.Create(25, 26);

            LogSystem.Info(log, $"H1 {eH} {sH} {bH} {mH} {tH} {matH} {shH} {fH} {gH}");
            LogSystem.Info(log, $"H2 {acH} {skH} {apH} {sapH} {twH} {emH} {camH} {spH} {txtH}");
            LogSystem.Info(log, $"H3 {jH} {httpH} {wsH} {lfH} {svH} {auCH} {auVH}");
            LogSystem.Flush(log);

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
            {
                LogSystem.Info(log, $"H1 {eH} {sH} {bH} {mH} {tH} {matH} {shH} {fH} {gH}");
                LogSystem.Info(log, $"H2 {acH} {skH} {apH} {sapH} {twH} {emH} {camH} {spH} {txtH}");
                LogSystem.Info(log, $"H3 {jH} {httpH} {wsH} {lfH} {svH} {auCH} {auVH}");
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "Formatting all 25 engine handle structs allocates 0 GC bytes");

            LogSystem.Flush(log);
            byte[] rawBytes = ReadAllBytesShared(seedPath);
            string decoded = Encoding.UTF8.GetString(rawBytes);
            Assert.True(!decoded.Contains('\uFFFD'), "Truncated non-ASCII log has zero invalid UTF-8 replacement chars");
            string[] finalLines = ReadAllLinesShared(seedPath);
            Assert.Equal(20, finalLines.Length, "Startup + runtime compaction enforced exact 20 lines");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after truncation/all-handles test");
    }

    private static void TestUnobservedCancellationSkipAndCloseRace()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tempDir = CreateTempLogDir("unobs");
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);

        try
        {
            LogSystem.Start(log, ref alloc.Persistent, appName: "UnobsTest", minLevel: LogLevel.Info, directoryOverride: tempDir, hookExceptions: true, includeCancellationExceptions: false);

            // Spawn faulted task with OperationCanceledException and allow finalization
            static void CreateCanceledTask()
            {
                _ = Task.FromException(new OperationCanceledException("unobserved-cancel-skip"));
            }
            CreateCanceledTask();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            LogSystem.Flush(log);

            string[] lines = ReadAllLinesShared(LogSystem.GetFilePath(log));
            for (int i = 0; i < lines.Length; i++)
                Assert.True(!lines[i].Contains("unobserved-cancel-skip", StringComparison.Ordinal), "Unobserved cancellation skipped");

            // Concurrent CloseFile race on the same handle
            LogFileHandle raceH = LogSystem.OpenFile(log, "race.log");
            Assert.True(raceH.IsValid, "race.log opened");
            nint lp = (nint)log;
            var t1 = new Thread(() => LogSystem.CloseFile((LogState*)lp, raceH));
            var t2 = new Thread(() => LogSystem.CloseFile((LogState*)lp, raceH));
            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            LogFileHandle reopened = LogSystem.OpenFile(log, "race2.log");
            Assert.True(reopened.IsValid, "Can reopen after concurrent CloseFile");
            LogSystem.Info(log, reopened, "after-concurrent-close");
            LogSystem.Flush(log);
            Assert.Equal(1, ReadAllLinesShared(LogSystem.GetFilePath(log, reopened)).Length, "Reopened file unaffected by concurrent CloseFile");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            TryDeleteDir(tempDir);
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after unobserved/close-race test");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after unobserved/close-race test");
    }
}
