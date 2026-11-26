using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
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
            Assert.True(resolved.Contains(Path.Combine("Library", "Logs", "BringTestGame"), StringComparison.Ordinal), "macOS uses ~/Library/Logs/<App>");
        else
            Assert.True(resolved.EndsWith(Path.Combine("BringTestGame", "logs"), StringComparison.Ordinal), "Linux uses XDG_STATE_HOME/<App>/logs");

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
}
