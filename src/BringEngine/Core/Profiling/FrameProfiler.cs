using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Memory;

namespace BringEngine.Core.Profiling;

[StructLayout(LayoutKind.Sequential)]
public struct ProfileScopeSample
{
    public FixedString64 Name;
    public long StartTick;
    public double DurationMs;
    public int Depth;
    public byte Completed;
}

/// <summary>
/// Zero-GC, headless-safe Frame Profiler tracking:
/// - Exact & smoothed CPU frame time (ms, excluding VSync/FPS-cap sleep and GPU fence wait)
/// - Exact & smoothed GPU frame time (ms, measured via SDL3_gpu fence completion)
/// - Wall-to-wall total frame time (ms) and Current/Instant FPS
/// - Hierarchical named CPU scopes per frame (for both engine & game code)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FrameProfiler
{
    public const int HistoryLength = 60;
    public const int MaxScopesPerFrame = 64;

    public byte Enabled;
    public byte MeasureGpuWithFence;

    // Rolling 60-frame history rings
    public fixed double CpuHistoryMs[HistoryLength];
    public fixed double GpuHistoryMs[HistoryLength];
    public fixed double TotalHistoryMs[HistoryLength];
    public int HistoryCursor;
    public int HistoryCount;

    // Current frame timings
    public long FrameStartTick;
    public long LastFrameEndTick;
    public long CpuEndTick;
    public long GpuWaitTicks;

    public double CpuFrameMs;
    public double GpuFrameMs;
    public double FrameTotalMs;

    // Smoothed display timings (rolling average over up to 30 frames)
    public double SmoothedCpuMs;
    public double SmoothedGpuMs;
    public double SmoothedTotalMs;
    public double CurrentFps;
    public double InstantFps;
    public ulong ProfiledFrames;

    // Named CPU scopes (allocated from Persistent LinearArena)
    public ProfileScopeSample* ActiveScopes;
    public int ActiveScopeCount;
    public int CurrentDepth;

    public ProfileScopeSample* LastFrameScopes;
    public int LastFrameScopeCount;

    public static FrameProfiler Create(ref LinearArena persistentArena, bool enabled = true, bool measureGpuWithFence = true)
    {
        return new FrameProfiler
        {
            Enabled = (byte)(enabled ? 1 : 0),
            MeasureGpuWithFence = (byte)(measureGpuWithFence ? 1 : 0),
            ActiveScopes = persistentArena.Alloc<ProfileScopeSample>(MaxScopesPerFrame, 64),
            LastFrameScopes = persistentArena.Alloc<ProfileScopeSample>(MaxScopesPerFrame, 64),
            LastFrameEndTick = Stopwatch.GetTimestamp(),
        };
    }
}

public static unsafe class ProfilerSystem
{
    private static readonly double s_ticksToMs = 1000.0 / Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginFrame(ref FrameProfiler p)
    {
        if (p.Enabled == 0) return;

        // Snapshot previous frame scopes for UI / inspection
        p.LastFrameScopeCount = p.ActiveScopeCount;
        if (p.ActiveScopeCount > 0 && p.ActiveScopes != null && p.LastFrameScopes != null)
        {
            nuint bytes = (nuint)(p.ActiveScopeCount * sizeof(ProfileScopeSample));
            Buffer.MemoryCopy(p.ActiveScopes, p.LastFrameScopes, bytes, bytes);
        }

        p.ActiveScopeCount = 0;
        p.CurrentDepth = 0;
        p.GpuWaitTicks = 0;
        p.GpuFrameMs = 0.0;
        p.CpuEndTick = 0;
        p.FrameStartTick = Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BeginScope(ref FrameProfiler p, ReadOnlySpan<char> name)
    {
        if (p.Enabled == 0 || p.ActiveScopes == null || p.ActiveScopeCount >= FrameProfiler.MaxScopesPerFrame)
            return -1;

        int idx = p.ActiveScopeCount++;
        p.ActiveScopes[idx] = new ProfileScopeSample
        {
            Name = FixedString64.From(name),
            StartTick = Stopwatch.GetTimestamp(),
            DurationMs = 0.0,
            Depth = p.CurrentDepth++,
            Completed = 0,
        };
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndScope(ref FrameProfiler p, int scopeIndex)
    {
        if (p.Enabled == 0 || scopeIndex < 0 || scopeIndex >= p.ActiveScopeCount)
            return;

        long now = Stopwatch.GetTimestamp();
        ref ProfileScopeSample s = ref p.ActiveScopes[scopeIndex];
        s.DurationMs = (now - s.StartTick) * s_ticksToMs;
        s.Completed = 1;
        if (p.CurrentDepth > 0) p.CurrentDepth--;
    }

    /// <summary>
    /// Called by BatchRenderer when GPU fence completion is measured.
    /// Subtracts waitTicks from CPU time so CpuFrameMs measures pure CPU work cleanly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordGpuTime(ref FrameProfiler p, double gpuMs, long waitTicks)
    {
        if (p.Enabled == 0) return;
        p.GpuFrameMs += gpuMs;
        p.GpuWaitTicks += waitTicks;
    }

    /// <summary>
    /// Marks end of CPU work BEFORE frame pacing / VSync sleep.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndCpuWork(ref FrameProfiler p)
    {
        if (p.Enabled == 0) return;
        p.CpuEndTick = Stopwatch.GetTimestamp();
        long pureCpuTicks = (p.CpuEndTick - p.FrameStartTick) - p.GpuWaitTicks;
        if (pureCpuTicks < 0) pureCpuTicks = 0;
        p.CpuFrameMs = pureCpuTicks * s_ticksToMs;
    }

    /// <summary>
    /// Marks end of full wall-to-wall frame (AFTER frame pacing / VSync) and updates rolling FPS & averages.
    /// </summary>
    public static void EndFrame(ref FrameProfiler p)
    {
        if (p.Enabled == 0) return;

        long now = Stopwatch.GetTimestamp();
        if (p.CpuEndTick == 0)
            EndCpuWork(ref p);

        long wallTicks = p.ProfiledFrames > 0 ? (now - p.LastFrameEndTick) : (now - p.FrameStartTick);
        if (wallTicks < 1) wallTicks = 1;
        p.LastFrameEndTick = now;

        p.FrameTotalMs = wallTicks * s_ticksToMs;
        p.InstantFps = p.FrameTotalMs > 1e-6 ? 1000.0 / p.FrameTotalMs : 0.0;

        int cursor = p.HistoryCursor;
        p.CpuHistoryMs[cursor] = p.CpuFrameMs;
        p.GpuHistoryMs[cursor] = p.GpuFrameMs;
        p.TotalHistoryMs[cursor] = p.FrameTotalMs;
        p.HistoryCursor = (cursor + 1) % FrameProfiler.HistoryLength;
        if (p.HistoryCount < FrameProfiler.HistoryLength) p.HistoryCount++;
        p.ProfiledFrames++;

        // Compute smoothed rolling window (last min(30, HistoryCount) frames)
        int window = p.HistoryCount < 30 ? p.HistoryCount : 30;
        double sumCpu = 0.0, sumGpu = 0.0, sumTot = 0.0;
        for (int i = 0; i < window; i++)
        {
            int idx = (p.HistoryCursor - 1 - i + FrameProfiler.HistoryLength) % FrameProfiler.HistoryLength;
            sumCpu += p.CpuHistoryMs[idx];
            sumGpu += p.GpuHistoryMs[idx];
            sumTot += p.TotalHistoryMs[idx];
        }

        double inv = 1.0 / window;
        p.SmoothedCpuMs = sumCpu * inv;
        p.SmoothedGpuMs = sumGpu * inv;
        p.SmoothedTotalMs = sumTot * inv;
        p.CurrentFps = p.SmoothedTotalMs > 1e-6 ? 1000.0 / p.SmoothedTotalMs : 0.0;
    }

    public static void ResetHistory(ref FrameProfiler p)
    {
        p.HistoryCursor = 0;
        p.HistoryCount = 0;
        p.ProfiledFrames = 0;
        p.LastFrameEndTick = Stopwatch.GetTimestamp();
    }

    public static bool TryGetLastScopeMs(in FrameProfiler p, ReadOnlySpan<char> scopeName, out double durationMs)
    {
        if (p.LastFrameScopes != null)
        {
            FixedString64 target = FixedString64.From(scopeName);
            for (int i = 0; i < p.LastFrameScopeCount; i++)
            {
                if (p.LastFrameScopes[i].Name.Equals(target))
                {
                    durationMs = p.LastFrameScopes[i].DurationMs;
                    return true;
                }
            }
        }
        durationMs = 0.0;
        return false;
    }
}
