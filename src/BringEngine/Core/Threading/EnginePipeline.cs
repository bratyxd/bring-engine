using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Simulation.Input;

namespace BringEngine.Core.Threading;

/// <summary>
/// Immutable per-frame inputs handed from Main/Render Thread to Logic Thread at the frame barrier.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PipelineFrameInput
{
    public InputState Input;
    public float DeltaTime;
    public float ViewportWidth;
    public float ViewportHeight;
    public RenderStats PrevRenderStats;
    public double PrevRenderCpuMs;
    public double PrevGpuMs;
    public RenderShaderDefaults BuiltinShaders;
}

/// <summary>
/// Unmanaged 2-thread pipelined engine state (Logic/Update Thread running Frame N concurrently
/// with Main/Render Thread submitting Frame N-1 to SDL3_gpu).
/// Zero-OOP, zero per-frame GC allocations, NativeAOT compatible.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct EnginePipelineState
{
    public RenderPacket Packet0;
    public RenderPacket Packet1;
    public PipelineFrameInput PendingInput;

    public void* UserContext;
    public delegate*<void*, ref PipelineFrameInput, ref RenderPacket, void> LogicCallback;

    public int SyncSlot;
    public int WriteSlot;             // 0 or 1: Logic writes Packet[WriteSlot] using alloc.Frame
    public byte HasRenderablePacket;  // 1 once at least one packet has been extracted
    public byte Active;
    public int ShutdownFlag;
    internal int StopClaimed;

    public double LastLogicMs;
    public double LastRenderCpuMs;
    public double LastGpuMs;
    public long LastGpuWaitTicks;
    public ulong LogicFramesCompleted;
    public ulong RenderFramesSubmitted;
    public int LogicThreadId;
}

/// <summary>
/// Static zero-OOP orchestrator for Bring Engine's 2-thread pipelined loop:
/// - Main Thread owns SDL3 window, event polling, swapchain acquisition, RenderPacket submission, and FramePacer.
/// - Dedicated Logic Worker Thread owns Simulation, ViewSync, EngineWorldSystem.StepCoreSystems, and ExtractRenderPacket.
/// - At the frame barrier, Frame <-> FrameBack arenas and RenderPacket[0/1] swap cleanly with zero data races.
/// </summary>
public static unsafe class EnginePipelineSystem
{
    private const int MaxPipelineSlots = 8;

    private struct SyncHandles
    {
        public AutoResetEvent? LogicStart;
        public AutoResetEvent? LogicDone;
        public Thread? LogicThread;
        public Exception? WorkerError;
        public bool InUse;
    }

    private static readonly SyncHandles[] s_sync = new SyncHandles[MaxPipelineSlots];
    private static readonly object s_slotLock = new();

    /// <summary>
    /// Allocates sync primitives and starts the dedicated Logic/Update worker thread.
    /// IMPORTANT: statePtr MUST point to stable pinned/native memory (e.g., EngineAllocators.Persistent)!
    /// </summary>
    public static void Start(
        EnginePipelineState* statePtr,
        void* userContext,
        delegate*<void*, ref PipelineFrameInput, ref RenderPacket, void> logicCallback,
        in RenderShaderDefaults builtinShaders)
    {
        if (statePtr == null) throw new ArgumentNullException(nameof(statePtr));
        if (logicCallback == null) throw new ArgumentNullException(nameof(logicCallback));

        int slot = -1;
        lock (s_slotLock)
        {
            for (int i = 0; i < MaxPipelineSlots; i++)
            {
                if (!s_sync[i].InUse)
                {
                    s_sync[i].InUse = true;
                    s_sync[i].LogicStart = new AutoResetEvent(false);
                    s_sync[i].LogicDone = new AutoResetEvent(false);
                    s_sync[i].WorkerError = null;
                    slot = i;
                    break;
                }
            }
        }
        if (slot < 0)
            throw new InvalidOperationException("Maximum concurrent EnginePipeline instances exceeded.");

        *statePtr = default;
        statePtr->UserContext = userContext;
        statePtr->LogicCallback = logicCallback;
        statePtr->SyncSlot = slot;
        statePtr->WriteSlot = 0;
        statePtr->PendingInput.BuiltinShaders = builtinShaders;
        statePtr->Active = 1;
        statePtr->ShutdownFlag = 0;

        try
        {
            nint rawPtr = (nint)statePtr;
            var thread = new Thread(LogicWorkerEntry)
            {
                Name = "BringEngine.LogicThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            s_sync[slot].LogicThread = thread;
            thread.Start(rawPtr);
        }
        catch
        {
            statePtr->Active = 0;
            s_sync[slot].LogicStart?.Dispose();
            s_sync[slot].LogicDone?.Dispose();
            lock (s_slotLock) { s_sync[slot] = default; }
            throw;
        }
    }

    private static void LogicWorkerEntry(object? arg)
    {
        EnginePipelineState* state = (EnginePipelineState*)(nint)arg!;
        int slot = state->SyncSlot;
        state->LogicThreadId = Environment.CurrentManagedThreadId;

        AutoResetEvent startEvt = s_sync[slot].LogicStart!;
        AutoResetEvent doneEvt = s_sync[slot].LogicDone!;
        double ticksToMs = 1000.0 / Stopwatch.Frequency;

        while (true)
        {
            startEvt.WaitOne();
            if (Volatile.Read(ref state->ShutdownFlag) != 0)
                break;

            try
            {
                long t0 = Stopwatch.GetTimestamp();
                ref RenderPacket dstPacket = ref (state->WriteSlot == 0 ? ref state->Packet0 : ref state->Packet1);
                state->LogicCallback(state->UserContext, ref state->PendingInput, ref dstPacket);
                long t1 = Stopwatch.GetTimestamp();
                state->LastLogicMs = (t1 - t0) * ticksToMs;
                state->LogicFramesCompleted++;
            }
            catch (Exception ex)
            {
                s_sync[slot].WorkerError = ex;
            }
            finally
            {
                doneEvt.Set();
            }
        }
    }

    /// <summary>
    /// Executes one full pipelined frame:
    /// 1. Kicks off Logic Thread (Frame N) into Packet[WriteSlot] / alloc.Frame.
    /// 2. Simultaneously runs Render Thread submission (Frame N-1) from Packet[ReadSlot] / alloc.FrameBack on Main Thread.
    /// 3. Synchronizes at the clean frame barrier, swaps double-buffered arenas & packets, and updates FrameProfiler cleanly.
    /// </summary>
    public static void StepPipelinedFrame(
        EnginePipelineState* state,
        ref GpuContext gpu,
        ref EngineAllocators alloc,
        in PipelineFrameInput frameInput,
        bool captureToOffscreen = false,
        FrameProfiler* profiler = null)
    {
        if (state == null || Volatile.Read(ref state->Active) == 0)
            throw new InvalidOperationException("EnginePipeline is not active.");

        int slot = state->SyncSlot;
        state->PendingInput = frameInput;

        if (profiler != null)
            profiler->PipelinedTwoThread = 1;

        // 1. Kick off Logic Thread for Frame N (writes Packet[WriteSlot] in alloc.Frame)
        s_sync[slot].LogicStart!.Set();

        // 2. Simultaneously submit Frame N-1 on Main/Render Thread (reads Packet[ReadSlot] in alloc.FrameBack)
        RenderSubmitTimings submitTimings = default;
        Exception? renderErr = null;
        try
        {
            if (state->HasRenderablePacket != 0)
            {
                int readSlot = state->WriteSlot ^ 1;
                ref readonly RenderPacket readPacket = ref (readSlot == 0 ? ref state->Packet0 : ref state->Packet1);
                if (readPacket.Valid != 0)
                {
                    bool measureGpu = profiler != null && profiler->Enabled != 0 && profiler->MeasureGpuWithFence != 0;
                    submitTimings = BatchRenderer.SubmitRenderPacket(ref gpu, in readPacket, captureToOffscreen, measureGpu);
                    state->RenderFramesSubmitted++;
                }
            }
        }
        catch (Exception ex)
        {
            renderErr = ex;
        }
        finally
        {
            // 3. Rendezvous barrier: ALWAYS wait for Logic Thread to complete Frame N before returning
            s_sync[slot].LogicDone!.WaitOne();
        }

        Exception? workerErr = Interlocked.Exchange(ref s_sync[slot].WorkerError, null);
        if (workerErr != null || renderErr != null)
        {
            // Reset Frame arena so partial Frame N allocations do not accumulate on recovery
            alloc.BeginFrame();
            if (workerErr != null)
            {
                if (state->WriteSlot == 0) state->Packet0.Valid = 0;
                else state->Packet1.Valid = 0;
            }
            if (renderErr != null && workerErr != null)
                throw new AggregateException("Both render and logic threads encountered unhandled exceptions.", renderErr, workerErr);
            if (renderErr != null)
                ExceptionDispatchInfo.Capture(renderErr).Throw();
            throw new InvalidOperationException("Logic thread encountered an unhandled exception.", workerErr);
        }

        state->LastRenderCpuMs = submitTimings.TotalRenderCpuMs;
        state->LastGpuMs = submitTimings.GpuMs;
        state->LastGpuWaitTicks = submitTimings.GpuWaitTicks;

        // 4. Record independent pipelined stage timings cleanly on Main Thread (zero locks!)
        if (profiler != null && profiler->Enabled != 0)
        {
            ProfilerSystem.RecordLogicTime(ref *profiler, state->LastLogicMs);
            ProfilerSystem.RecordRenderTime(ref *profiler, state->LastRenderCpuMs);
            if (submitTimings.GpuMs > 0.0 || submitTimings.GpuWaitTicks > 0)
                ProfilerSystem.RecordGpuTime(ref *profiler, submitTimings.GpuMs, submitTimings.GpuWaitTicks);
        }

        // 5. Swap double-buffered Frame arenas & RenderPackets for next frame!
        //    What Logic just wrote into alloc.Frame becomes alloc.FrameBack for next frame's Render Thread.
        alloc.SwapFrameArenas();
        state->WriteSlot ^= 1;
        state->HasRenderablePacket = 1;
    }

    /// <summary>
    /// Invalidates the pending back-buffer packet before a Single scene transition or bundle unload
    /// so Render Thread never references freed GPU mesh/texture slots on the following frame.
    /// Call ONLY at the frame barrier (between StepPipelinedFrame calls).
    /// </summary>
    public static void FlushBeforeSceneTransition(EnginePipelineState* state, ref GpuContext gpu)
    {
        if (state != null)
        {
            state->Packet0.Valid = 0;
            state->Packet1.Valid = 0;
            state->HasRenderablePacket = 0;
        }
        if (gpu.Initialized != 0 && gpu.Device != nint.Zero)
            SDL3.SDL.WaitForGPUIdle(gpu.Device);
    }

    public static void Stop(EnginePipelineState* state)
    {
        if (state == null || Volatile.Read(ref state->Active) == 0) return;
        if (Interlocked.CompareExchange(ref state->StopClaimed, 1, 0) != 0) return;
        Volatile.Write(ref state->Active, 0);
        int slot = state->SyncSlot;

        Volatile.Write(ref state->ShutdownFlag, 1);
        s_sync[slot].LogicStart?.Set();
        s_sync[slot].LogicThread?.Join();

        s_sync[slot].LogicStart?.Dispose();
        s_sync[slot].LogicDone?.Dispose();
        lock (s_slotLock)
        {
            s_sync[slot] = default;
        }
    }
}
