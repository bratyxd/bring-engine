using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Rendering.Gpu;
using SDL3;

namespace BringEngine.Rendering.Platform;

/// <summary>
/// Data-driven frame pacing & VSync controller.
/// Defaults: TargetFps = 60, VSyncEnabled = true (1).
/// Set TargetFps = 0 for uncapped frame rate.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FramePacer
{
    public const int DefaultTargetFps = 60;

    public int TargetFps;          // 0 = uncapped, default = 60
    public byte VSyncEnabled;      // 1 = VSync ON (default), 0 = VSync OFF
    public byte EffectiveVSync;    // Actual swapchain present mode state
    public long FrameStartTick;
    public double LastSleepMs;

    public static FramePacer CreateDefault() => new FramePacer
    {
        TargetFps = DefaultTargetFps,
        VSyncEnabled = 1,
        EffectiveVSync = 1,
        FrameStartTick = Stopwatch.GetTimestamp(),
    };

    public static FramePacer Create(int targetFps = DefaultTargetFps, bool vsync = true) => new FramePacer
    {
        TargetFps = targetFps < 0 ? 0 : targetFps,
        VSyncEnabled = (byte)(vsync ? 1 : 0),
        EffectiveVSync = (byte)(vsync ? 1 : 0),
        FrameStartTick = Stopwatch.GetTimestamp(),
    };
}

public static unsafe class FramePacerSystem
{
    private static readonly double s_ticksToNs = 1_000_000_000.0 / Stopwatch.Frequency;
    private static readonly double s_ticksToMs = 1000.0 / Stopwatch.Frequency;

    public static void ApplyVSync(ref FramePacer pacer, ref GpuContext gpu, bool enabled)
    {
        pacer.VSyncEnabled = (byte)(enabled ? 1 : 0);
        GpuSystem.SetVSync(ref gpu, enabled);
        pacer.EffectiveVSync = gpu.VSyncEnabled;
    }

    public static void ToggleVSync(ref FramePacer pacer, ref GpuContext gpu)
        => ApplyVSync(ref pacer, ref gpu, pacer.VSyncEnabled == 0);

    public static void SetTargetFps(ref FramePacer pacer, int targetFps)
        => pacer.TargetFps = targetFps < 0 ? 0 : targetFps;

    /// <summary>
    /// Cycles through common FPS cap presets: 60 -> 120 -> 144 -> Uncapped (0) -> 30 -> 60.
    /// </summary>
    public static int CycleFpsCapPreset(ref FramePacer pacer)
    {
        pacer.TargetFps = pacer.TargetFps switch
        {
            60 => 120,
            120 => 144,
            144 => 0,
            0 => 30,
            _ => 60,
        };
        return pacer.TargetFps;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginFrame(ref FramePacer pacer)
    {
        pacer.FrameStartTick = Stopwatch.GetTimestamp();
        pacer.LastSleepMs = 0.0;
    }

    /// <summary>
    /// Sleeps the remaining frame budget to honor TargetFps using high-precision SDL3 DelayPrecise
    /// (or Stopwatch spin-sleep in headless mode). No-op when TargetFps == 0.
    /// </summary>
    public static void EndFrameAndPace(ref FramePacer pacer, bool useSdlPreciseDelay = true)
    {
        if (pacer.TargetFps <= 0)
        {
            pacer.LastSleepMs = 0.0;
            return;
        }

        double targetNs = 1_000_000_000.0 / pacer.TargetFps;
        // When VSync is active in windowed/fullscreen presentation, leave a small 0.4ms margin so CPU delay never
        // overshoots the monitor's VBlank deadline (prevents 60<->30 FPS beat judder & presentation flicker).
        if (useSdlPreciseDelay && pacer.EffectiveVSync != 0 && targetNs > 2_000_000.0)
            targetNs -= 400_000.0;

        long now = Stopwatch.GetTimestamp();
        double elapsedNs = (now - pacer.FrameStartTick) * s_ticksToNs;
        double remainingNs = targetNs - elapsedNs;

        if (remainingNs <= 0.0)
        {
            pacer.LastSleepMs = 0.0;
            return;
        }

        long sleepStart = now;
        if (useSdlPreciseDelay)
        {
            SDL.DelayPrecise((ulong)remainingNs);
        }
        else
        {
            // Headless-safe high-precision wait: coarse sleep for >2ms, short spin for final sub-ms
            double remainingMs = remainingNs * 1e-6;
            if (remainingMs > 2.0)
                Thread.Sleep((int)(remainingMs - 1.5));

            long targetTick = pacer.FrameStartTick + (long)(Stopwatch.Frequency / (double)pacer.TargetFps);
            while (Stopwatch.GetTimestamp() < targetTick)
                Thread.SpinWait(32);
        }

        pacer.LastSleepMs = (Stopwatch.GetTimestamp() - sleepStart) * s_ticksToMs;
    }
}
