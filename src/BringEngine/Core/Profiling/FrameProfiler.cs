using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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
/// Zero-GC, headless-safe Pipelined Frame Profiler tracking each concurrent stage independently:
/// - LogicCpuMs  / SmoothedLogicMs  (Logic/Update Thread: Sim + ViewSync + Core + Packet Extract, Frame N)
/// - RenderCpuMs / SmoothedRenderMs (Render Thread: GPU Upload + Command Encoding, Frame N-1)
/// - GpuFrameMs  / SmoothedGpuMs    (GPU Hardware execution via SDL3_gpu fence, Frame N-1)
/// - Wall-to-wall total frame time (ms) and smoothed / instant FPS
/// - Hierarchical named CPU scopes per frame (for both engine & game code)
/// Because Logic, Render, and GPU run pipelined 1 frame apart, each stage is measured independently
/// against the full target frame budget (e.g. 16.67ms at 60 FPS).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FrameProfiler
{
    public const int HistoryLength = 60;
    public const int MaxScopesPerFrame = 64;

    public byte Enabled;
    public byte MeasureGpuWithFence;
    public byte PipelinedTwoThread; // 1 when running on EnginePipeline (dedicated Logic + Render threads)

    // Rolling 60-frame history rings (independent per pipelined stage!)
    public fixed double LogicHistoryMs[HistoryLength];
    public fixed double RenderHistoryMs[HistoryLength];
    public fixed double GpuHistoryMs[HistoryLength];
    public fixed double TotalHistoryMs[HistoryLength];
    public int HistoryCursor;
    public int HistoryCount;

    // Current frame timings
    public long FrameStartTick;
    public long LastFrameEndTick;
    public long CpuEndTick;
    public long GpuWaitTicks;

    public double LogicCpuMs;    // Logic / Update thread time (Frame N)
    public double RenderCpuMs;   // Render thread CPU encoding/upload time (Frame N-1)
    public double CpuFrameMs;    // Backward-compatible alias for LogicCpuMs (or total non-GPU CPU in single-thread)
    public double GpuFrameMs;    // GPU hardware execution time (Frame N-1)
    public double FrameTotalMs;

    private byte _hasExplicitLogicMs;
    private byte _hasExplicitRenderMs;

    // Smoothed display timings (rolling average over up to 30 frames)
    public double SmoothedLogicMs;
    public double SmoothedRenderMs;
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
    public int PublishSeq;

    public static FrameProfiler Create(ref LinearArena persistentArena, bool enabled = true, bool measureGpuWithFence = true)
    {
        ArenaMarker mark = persistentArena.SaveMarker();
        try
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
        catch
        {
            persistentArena.Rollback(mark);
            throw;
        }
    }

    internal byte HasExplicitLogicMs { readonly get => _hasExplicitLogicMs; set => _hasExplicitLogicMs = value; }
    internal byte HasExplicitRenderMs { readonly get => _hasExplicitRenderMs; set => _hasExplicitRenderMs = value; }
}

public static unsafe class ProfilerSystem
{
    private static readonly double s_ticksToMs = 1000.0 / Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginFrame(ref FrameProfiler p)
    {
        if (p.Enabled == 0) return;

        // Seqlock-guarded publish so concurrent readers never observe torn LastFrameScopes snapshots
        int prevCount = Volatile.Read(ref p.ActiveScopeCount);
        if (prevCount < 0) prevCount = 0;
        if (prevCount > FrameProfiler.MaxScopesPerFrame) prevCount = FrameProfiler.MaxScopesPerFrame;

        int seq = Volatile.Read(ref p.PublishSeq);
        Volatile.Write(ref p.PublishSeq, seq + 1);
        Thread.MemoryBarrier();

        Volatile.Write(ref p.LastFrameScopeCount, 0);
        if (prevCount > 0 && p.ActiveScopes != null && p.LastFrameScopes != null)
        {
            nuint bytes = (nuint)(prevCount * sizeof(ProfileScopeSample));
            Buffer.MemoryCopy(p.ActiveScopes, p.LastFrameScopes, bytes, bytes);
        }
        Volatile.Write(ref p.LastFrameScopeCount, prevCount);

        Thread.MemoryBarrier();
        Volatile.Write(ref p.PublishSeq, seq + 2);

        Volatile.Write(ref p.ActiveScopeCount, 0);
        Volatile.Write(ref p.CurrentDepth, 0);
        Volatile.Write(ref p.GpuWaitTicks, 0);
        p.GpuFrameMs = 0.0;
        p.LogicCpuMs = 0.0;
        p.RenderCpuMs = 0.0;
        p.CpuFrameMs = 0.0;
        p.HasExplicitLogicMs = 0;
        p.HasExplicitRenderMs = 0;
        p.CpuEndTick = 0;
        p.FrameStartTick = Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BeginScope(ref FrameProfiler p, ReadOnlySpan<char> name)
    {
        if (p.Enabled == 0 || p.ActiveScopes == null)
            return -1;

        int idx;
        while (true)
        {
            int cur = Volatile.Read(ref p.ActiveScopeCount);
            if (cur >= FrameProfiler.MaxScopesPerFrame)
                return -1;
            if (Interlocked.CompareExchange(ref p.ActiveScopeCount, cur + 1, cur) == cur)
            {
                idx = cur;
                break;
            }
        }

        int depth = Interlocked.Increment(ref p.CurrentDepth) - 1;
        p.ActiveScopes[idx] = new ProfileScopeSample
        {
            Name = FixedString64.From(name),
            StartTick = Stopwatch.GetTimestamp(),
            DurationMs = 0.0,
            Depth = depth,
            Completed = 0,
        };
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndScope(ref FrameProfiler p, int scopeIndex)
    {
        int count = Volatile.Read(ref p.ActiveScopeCount);
        if (p.Enabled == 0 || p.ActiveScopes == null || scopeIndex < 0 || scopeIndex >= count)
            return;

        long now = Stopwatch.GetTimestamp();
        ref ProfileScopeSample s = ref p.ActiveScopes[scopeIndex];
        s.DurationMs = (now - s.StartTick) * s_ticksToMs;
        s.Completed = 1;
        while (true)
        {
            int d = Volatile.Read(ref p.CurrentDepth);
            if (d <= 0) break;
            if (Interlocked.CompareExchange(ref p.CurrentDepth, d - 1, d) == d) break;
        }
    }

    /// <summary>
    /// Records the Logic/Update thread duration for Frame N (called by EnginePipeline or single-threaded caller).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordLogicTime(ref FrameProfiler p, double logicMs)
    {
        if (p.Enabled == 0) return;
        p.LogicCpuMs = logicMs < 0.0 ? 0.0 : logicMs;
        p.CpuFrameMs = p.LogicCpuMs;
        p.HasExplicitLogicMs = 1;
    }

    /// <summary>
    /// Records the Render thread CPU duration (upload + command recording, excluding GPU wait/pacing).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordRenderTime(ref FrameProfiler p, double renderMs)
    {
        if (p.Enabled == 0) return;
        p.RenderCpuMs = renderMs < 0.0 ? 0.0 : renderMs;
        p.HasExplicitRenderMs = 1;
    }

    /// <summary>
    /// Called by BatchRenderer when GPU fence completion is measured.
    /// Subtracts waitTicks from CPU time so CPU timings exclude GPU waits cleanly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordGpuTime(ref FrameProfiler p, double gpuMs, long waitTicks)
    {
        if (p.Enabled == 0) return;
        p.GpuFrameMs = gpuMs < 0.0 ? 0.0 : gpuMs;
        Interlocked.Add(ref p.GpuWaitTicks, waitTicks);
    }

    /// <summary>
    /// Marks end of CPU work BEFORE frame pacing / VSync sleep (single-threaded fallback when Logic/Render aren't explicitly timed).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndCpuWork(ref FrameProfiler p)
    {
        if (p.Enabled == 0) return;
        p.CpuEndTick = Stopwatch.GetTimestamp();
        long pureCpuTicks = (p.CpuEndTick - p.FrameStartTick) - Volatile.Read(ref p.GpuWaitTicks);
        if (pureCpuTicks < 0) pureCpuTicks = 0;
        double totalCpuMs = pureCpuTicks * s_ticksToMs;

        if (p.HasExplicitLogicMs == 0)
        {
            // Separate pure Logic time from Render thread time when running single-threaded
            double logicMs = p.HasExplicitRenderMs != 0 ? totalCpuMs - p.RenderCpuMs : totalCpuMs;
            p.LogicCpuMs = logicMs < 0.0 ? 0.0 : logicMs;
            p.CpuFrameMs = p.LogicCpuMs;
        }
        else
        {
            p.CpuFrameMs = p.LogicCpuMs;
        }
    }

    /// <summary>
    /// Marks end of full wall-to-wall frame (AFTER frame pacing / VSync) and updates rolling FPS & per-stage averages.
    /// </summary>
    public static void EndFrame(ref FrameProfiler p)
    {
        if (p.Enabled == 0) return;

        long now = Stopwatch.GetTimestamp();
        if (p.CpuEndTick == 0 && p.HasExplicitLogicMs == 0)
            EndCpuWork(ref p);

        long wallTicks = p.ProfiledFrames > 0 ? (now - p.LastFrameEndTick) : (now - p.FrameStartTick);
        if (wallTicks < 1) wallTicks = 1;
        p.LastFrameEndTick = now;

        p.FrameTotalMs = wallTicks * s_ticksToMs;
        p.InstantFps = p.FrameTotalMs > 1e-6 ? 1000.0 / p.FrameTotalMs : 0.0;

        int cursor = p.HistoryCursor;
        p.LogicHistoryMs[cursor] = p.LogicCpuMs;
        p.RenderHistoryMs[cursor] = p.RenderCpuMs;
        p.GpuHistoryMs[cursor] = p.GpuFrameMs;
        p.TotalHistoryMs[cursor] = p.FrameTotalMs;
        p.HistoryCursor = (cursor + 1) % FrameProfiler.HistoryLength;
        if (p.HistoryCount < FrameProfiler.HistoryLength) p.HistoryCount++;
        p.ProfiledFrames++;

        // Compute smoothed rolling window (last min(30, HistoryCount) frames)
        int window = p.HistoryCount < 30 ? p.HistoryCount : 30;
        double sumLogic = 0.0, sumRender = 0.0, sumGpu = 0.0, sumTot = 0.0;
        for (int i = 0; i < window; i++)
        {
            int idx = (p.HistoryCursor - 1 - i + FrameProfiler.HistoryLength) % FrameProfiler.HistoryLength;
            sumLogic += p.LogicHistoryMs[idx];
            sumRender += p.RenderHistoryMs[idx];
            sumGpu += p.GpuHistoryMs[idx];
            sumTot += p.TotalHistoryMs[idx];
        }

        double inv = 1.0 / window;
        p.SmoothedLogicMs = sumLogic * inv;
        p.SmoothedRenderMs = sumRender * inv;
        p.SmoothedCpuMs = p.SmoothedLogicMs;
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
            ref int seqRef = ref Unsafe.AsRef(in p.PublishSeq);
            ref int countRef = ref Unsafe.AsRef(in p.LastFrameScopeCount);

            for (int attempt = 0; attempt < 16; attempt++)
            {
                int seq1 = Volatile.Read(ref seqRef);
                if ((seq1 & 1) != 0)
                {
                    Thread.SpinWait(8);
                    continue;
                }
                Thread.MemoryBarrier();

                int count = Volatile.Read(ref countRef);
                if (count < 0) count = 0;
                if (count > FrameProfiler.MaxScopesPerFrame) count = FrameProfiler.MaxScopesPerFrame;

                bool found = false;
                double foundMs = 0.0;
                for (int i = 0; i < count; i++)
                {
                    ProfileScopeSample sample = p.LastFrameScopes[i];
                    if (sample.Name.Equals(target))
                    {
                        found = true;
                        foundMs = sample.DurationMs;
                        break;
                    }
                }

                Thread.MemoryBarrier();
                int seq2 = Volatile.Read(ref seqRef);
                if (seq1 == seq2)
                {
                    durationMs = found ? foundMs : 0.0;
                    return found;
                }
            }
        }
        durationMs = 0.0;
        return false;
    }
}
