using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;

namespace BringEngine.Core.Threading;

/// <summary>
/// 64-byte cache-line-padded 32-bit integer to eliminate false sharing across worker threads.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CachePaddedInt
{
    public int Value;
    private int _p0;
    private ulong _p1, _p2, _p3, _p4, _p5, _p6, _p7;
}

/// <summary>
/// 64-byte cache-line-padded 64-bit integer to eliminate false sharing across worker threads.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CachePaddedLong
{
    public long Value;
    private ulong _p1, _p2, _p3, _p4, _p5, _p6, _p7;
}

/// <summary>
/// Per-worker dedicated native LinearArena on its own cache line (zero contention, zero GC).
/// Slot 0 belongs to the calling thread; slots 1..WorkerCount belong to pool workers.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WorkerScratchSlot
{
    public LinearArena Arena;
    private ulong _pad0;
    private ulong _pad1;
}

public enum JobKind : byte
{
    Single = 0,
    ParallelFor = 1,
    Fence = 2,
}

internal static class JobStateConst
{
    public const int Free = 0;
    public const int Pending = 1;
    public const int Ready = 2;
    public const int Completed = 3;
}

[StructLayout(LayoutKind.Sequential)]
public struct JobDepLink
{
    public int ChildSlot;
    public int Next;
}

/// <summary>
/// Unmanaged job descriptor padded across cache lines to prevent false sharing between concurrent jobs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct JobSlot
{
    public uint Generation;
    public int State;
    public int PendingDeps;
    public int TotalItems;
    public int BatchSize;
    public int NextItem;
    public int RemainingChunks;
    public int CancelRequested;

    public int DepLock;
    public int FirstDependent;
    public JobKind Kind;
    private byte _b0, _b1, _b2;
    private int _padAlign;

    public void* UserData;
    public void* CallbackPtr;

    // Pad to >= 128 bytes (2 cache lines)
    private ulong _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6, _pad7;
}

/// <summary>
/// Unmanaged data-oriented multi-threaded job scheduler state.
/// Allocate inside EngineAllocators.Persistent (stable pinned native address) and initialize via JobSystem.Start.
/// Supports fork-join ParallelFor (with automatic serial threshold), async job graphs with dependencies,
/// cooperative cancellation, and lock-free per-worker native scratch arenas. Zero GC allocations per job.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct JobScheduler
{
    public const int MaxWorkers = 32;
    public const int DefaultMaxJobs = 1024;
    public const int DefaultMaxDepLinks = 4096;
    public const nuint DefaultWorkerScratchBytes = 256 * 1024;

    public JobSlot* Slots;
    public int MaxJobs;

    public JobDepLink* DepLinks;
    public int MaxDepLinks;

    public int* ReadyQueue;
    public int ReadyQueueMask;

    public WorkerScratchSlot* WorkerScratches;
    public int TotalThreadSlots; // 1 (caller) + WorkerCount
    public int WorkerCount;
    public int PoolSlot;
    public byte Active;

    // Cache-line-isolated atomics
    public CachePaddedInt QueueLock;
    public CachePaddedInt QueueHead;
    public CachePaddedInt QueueTail;
    public CachePaddedInt QueuedJobCount;

    public CachePaddedInt AllocLock;
    public CachePaddedInt AllocCursor;

    public CachePaddedInt DepPoolLock;
    public CachePaddedInt FreeDepHead;

    public CachePaddedLong SleepingMask;
    public CachePaddedInt ShutdownFlag;

    public CachePaddedLong TotalJobsScheduled;
    public CachePaddedLong TotalChunksExecuted;
}

/// <summary>
/// First-class, zero-OOP, zero-GC, NativeAOT data-oriented multithreading system for Bring Engine.
///
/// Design rule: DO NOT multithread tiny loops for the sake of it. Use ParallelFor with a sensible
/// minParallelCount (default 2 * batchSize) so small workloads stay single-threaded inline with zero
/// synchronization overhead, and reserve multi-threading for genuinely heavy SoA loops (hundreds/thousands
/// of independent items) or multi-frame background generation (Schedule / ScheduleParallelFor + IsComplete).
/// </summary>
public static unsafe class JobSystem
{
    private const int MaxSchedulerInstances = 8;

    private struct ManagedPool
    {
        public Thread[]? Workers;
        public AutoResetEvent[]? WakeEvents;
        public Exception? WorkerError;
        public bool InUse;
    }

    private struct WorkerStartParam
    {
        public nint SchedulerPtr;
        public int WorkerIndex; // 1..WorkerCount
    }

    private static readonly ManagedPool[] s_pools = new ManagedPool[MaxSchedulerInstances];
    private static readonly object s_poolLock = new();

    [ThreadStatic]
    private static int t_workerIndex; // 0 on external/Logic/Main threads, 1..WorkerCount on pool workers

    /// <summary>
    /// Recommended worker count leaving headroom for Main/Render Thread and Logic Thread
    /// (never oversubscribes logical cores).
    /// </summary>
    public static int RecommendedWorkerCount()
    {
        int cores = Environment.ProcessorCount;
        int workers = cores - 2;
        if (workers < 1) workers = 1;
        if (workers > JobScheduler.MaxWorkers) workers = JobScheduler.MaxWorkers;
        return workers;
    }

    /// <summary>
    /// Initializes a JobScheduler in stable native memory (e.g., EngineAllocators.Persistent) and spawns worker threads.
    /// Pass workerCount = -1 for RecommendedWorkerCount(), or 0 for deterministic single-threaded inline execution.
    /// </summary>
    public static void Start(
        JobScheduler* sched,
        ref LinearArena persistentArena,
        int workerCount = -1,
        int maxJobs = JobScheduler.DefaultMaxJobs,
        int maxDepLinks = JobScheduler.DefaultMaxDepLinks,
        nuint workerScratchBytes = JobScheduler.DefaultWorkerScratchBytes)
    {
        if (sched == null) throw new ArgumentNullException(nameof(sched));
        if (workerCount < 0) workerCount = RecommendedWorkerCount();
        if (workerCount > JobScheduler.MaxWorkers) workerCount = JobScheduler.MaxWorkers;
        if (maxJobs < 16) maxJobs = 16;
        if (maxDepLinks < 32) maxDepLinks = 32;

        // Next power of two >= maxJobs * 2 so ReadyQueue can never overflow
        int qCap = 64;
        while (qCap < maxJobs * 2) qCap <<= 1;

        int poolSlot = -1;
        lock (s_poolLock)
        {
            for (int i = 0; i < MaxSchedulerInstances; i++)
            {
                if (!s_pools[i].InUse)
                {
                    s_pools[i].InUse = true;
                    s_pools[i].WorkerError = null;
                    s_pools[i].Workers = workerCount > 0 ? new Thread[workerCount] : null;
                    s_pools[i].WakeEvents = workerCount > 0 ? new AutoResetEvent[workerCount] : null;
                    for (int w = 0; w < workerCount; w++)
                        s_pools[i].WakeEvents![w] = new AutoResetEvent(false);
                    poolSlot = i;
                    break;
                }
            }
        }
        if (poolSlot < 0)
            throw new InvalidOperationException("Maximum concurrent JobScheduler instances exceeded.");

        *sched = default;
        sched->Slots = persistentArena.Alloc<JobSlot>(maxJobs, 64);
        sched->MaxJobs = maxJobs;
        for (int i = 0; i < maxJobs; i++)
        {
            sched->Slots[i].Generation = 1;
            sched->Slots[i].State = JobStateConst.Free;
            sched->Slots[i].FirstDependent = -1;
        }

        sched->DepLinks = persistentArena.Alloc<JobDepLink>(maxDepLinks, 64);
        sched->MaxDepLinks = maxDepLinks;
        for (int i = 0; i < maxDepLinks - 1; i++)
            sched->DepLinks[i].Next = i + 1;
        sched->DepLinks[maxDepLinks - 1].Next = -1;
        sched->FreeDepHead.Value = 0;

        sched->ReadyQueue = persistentArena.Alloc<int>(qCap, 64);
        sched->ReadyQueueMask = qCap - 1;

        int totalSlots = workerCount + 1;
        sched->TotalThreadSlots = totalSlots;
        sched->WorkerCount = workerCount;
        sched->PoolSlot = poolSlot;
        sched->WorkerScratches = persistentArena.Alloc<WorkerScratchSlot>(totalSlots, 64);
        if (workerScratchBytes > 0)
        {
            for (int i = 0; i < totalSlots; i++)
                sched->WorkerScratches[i].Arena = LinearArena.Create(workerScratchBytes, StringId.Hash("JobScratch").Value ^ (ulong)i);
        }

        sched->Active = 1;

        if (workerCount > 0)
        {
            nint rawPtr = (nint)sched;
            for (int w = 0; w < workerCount; w++)
            {
                var param = new WorkerStartParam { SchedulerPtr = rawPtr, WorkerIndex = w + 1 };
                var thread = new Thread(WorkerEntry)
                {
                    Name = $"BringEngine.JobWorker.{w + 1}",
                    IsBackground = true,
                };
                s_pools[poolSlot].Workers![w] = thread;
                thread.Start(param);
            }
        }
    }

    /// <summary>
    /// Stops all worker threads cleanly, disposes per-worker scratch arenas, and frees sync handles.
    /// Zero native memory leaks.
    /// </summary>
    public static void Stop(JobScheduler* sched)
    {
        if (sched == null || sched->Active == 0) return;
        int poolSlot = sched->PoolSlot;
        int workers = sched->WorkerCount;

        Volatile.Write(ref sched->ShutdownFlag.Value, 1);
        Thread.MemoryBarrier();

        if (workers > 0)
        {
            AutoResetEvent[]? events = s_pools[poolSlot].WakeEvents;
            Thread[]? threads = s_pools[poolSlot].Workers;
            if (events != null)
            {
                for (int w = 0; w < workers; w++)
                    events[w].Set();
            }
            if (threads != null)
            {
                for (int w = 0; w < workers; w++)
                    threads[w]?.Join();
            }
            if (events != null)
            {
                for (int w = 0; w < workers; w++)
                    events[w].Dispose();
            }
        }

        if (sched->WorkerScratches != null)
        {
            for (int i = 0; i < sched->TotalThreadSlots; i++)
                sched->WorkerScratches[i].Arena.Dispose();
        }

        Exception? err = s_pools[poolSlot].WorkerError;
        lock (s_poolLock)
        {
            s_pools[poolSlot] = default;
        }

        sched->Active = 0;
        if (err != null)
            throw new InvalidOperationException("Unhandled exception occurred inside a JobSystem worker.", err);
    }

    /// <summary>
    /// Returns the dedicated lock-free LinearArena for the current workerIndex (0 = calling thread, 1..WorkerCount = pool workers).
    /// Always pair with SaveMarker() / Rollback() inside a job callback or call ResetAllWorkerScratch at the frame barrier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LinearArena* GetWorkerScratch(JobScheduler* sched, int workerIndex)
    {
        if (sched == null || sched->WorkerScratches == null) return null;
        if ((uint)workerIndex >= (uint)sched->TotalThreadSlots) workerIndex = 0;
        return &sched->WorkerScratches[workerIndex].Arena;
    }

    /// <summary>
    /// Resets all per-worker scratch arenas in O(1). Call ONLY when no jobs using worker scratch are in flight.
    /// </summary>
    public static void ResetAllWorkerScratch(JobScheduler* sched)
    {
        if (sched == null || sched->WorkerScratches == null) return;
        for (int i = 0; i < sched->TotalThreadSlots; i++)
        {
            if (sched->WorkerScratches[i].Arena.IsValid)
                sched->WorkerScratches[i].Arena.Reset();
        }
    }

    /// <summary>
    /// Fork-join parallel loop over [0..count) split into cache-contiguous [begin, end) chunks of size batchSize.
    /// Automatically executes inline on the calling thread with ZERO synchronization overhead whenever
    /// sched is null, WorkerCount == 0, count &lt; minParallelCount, or count &lt;= batchSize.
    /// Calling thread actively participates in executing chunks while waiting (zero wasted CPU).
    /// </summary>
    public static void ParallelFor(
        JobScheduler* sched,
        int count,
        int batchSize,
        void* userData,
        delegate*<void*, int, int, int, void> callback,
        int minParallelCount = 0)
    {
        if (count <= 0) return;
        if (batchSize < 1) batchSize = 1;
        if (minParallelCount <= 0) minParallelCount = batchSize * 2;

        if (sched == null || sched->Active == 0 || sched->WorkerCount <= 0 || count < minParallelCount || count <= batchSize)
        {
            callback(userData, 0, count, t_workerIndex);
            return;
        }

        JobHandle h = ScheduleParallelFor(sched, count, batchSize, userData, callback, dependency: default, minParallelCount: 0);
        Complete(sched, h);
    }

    /// <summary>
    /// Schedules a single asynchronous job on the worker pool, optionally waiting for a dependency handle.
    /// </summary>
    public static JobHandle Schedule(
        JobScheduler* sched,
        void* userData,
        delegate*<void*, int, void> callback,
        JobHandle dependency = default)
    {
        if (sched == null || sched->Active == 0 || sched->WorkerCount <= 0)
        {
            if (sched != null && sched->Active != 0 && dependency.IsValid)
                Complete(sched, dependency);
            callback(userData, 0);
            return JobHandle.Invalid;
        }

        int slotIdx = AllocSlot(sched);
        JobSlot* slot = sched->Slots + slotIdx;
        uint gen = slot->Generation;

        slot->Kind = JobKind.Single;
        slot->TotalItems = 1;
        slot->BatchSize = 1;
        slot->NextItem = 0;
        slot->RemainingChunks = 1;
        slot->CancelRequested = 0;
        slot->FirstDependent = -1;
        slot->DepLock = 0;
        slot->UserData = userData;
        slot->CallbackPtr = callback;
        slot->PendingDeps = 1; // guard until dependency wiring finishes
        Volatile.Write(ref slot->State, JobStateConst.Pending);

        Interlocked.Increment(ref sched->TotalJobsScheduled.Value);

        if (dependency.IsValid)
            AttachDependency(sched, dependency, slotIdx);

        if (Interlocked.Decrement(ref slot->PendingDeps) == 0)
            EnqueueReadyJob(sched, slotIdx, wakeCount: 1);

        return JobHandle.Create((uint)slotIdx, gen);
    }

    /// <summary>
    /// Schedules an asynchronous parallel-for range job over [0..count).
    /// Poll across frames with IsComplete(sched, handle) or block-and-help with Complete(sched, handle).
    /// </summary>
    public static JobHandle ScheduleParallelFor(
        JobScheduler* sched,
        int count,
        int batchSize,
        void* userData,
        delegate*<void*, int, int, int, void> callback,
        JobHandle dependency = default,
        int minParallelCount = 0)
    {
        if (count <= 0) return dependency;
        if (batchSize < 1) batchSize = 1;

        if (sched == null || sched->Active == 0 || sched->WorkerCount <= 0)
        {
            if (sched != null && sched->Active != 0 && dependency.IsValid)
                Complete(sched, dependency);
            callback(userData, 0, count, 0);
            return JobHandle.Invalid;
        }

        // If below minParallelCount, run as a single-chunk job so workers don't contend on tiny slices
        int effBatch = (minParallelCount > 0 && count < minParallelCount) ? count : batchSize;
        int chunks = (count + effBatch - 1) / effBatch;

        int slotIdx = AllocSlot(sched);
        JobSlot* slot = sched->Slots + slotIdx;
        uint gen = slot->Generation;

        slot->Kind = JobKind.ParallelFor;
        slot->TotalItems = count;
        slot->BatchSize = effBatch;
        slot->NextItem = 0;
        slot->RemainingChunks = chunks;
        slot->CancelRequested = 0;
        slot->FirstDependent = -1;
        slot->DepLock = 0;
        slot->UserData = userData;
        slot->CallbackPtr = callback;
        slot->PendingDeps = 1;
        Volatile.Write(ref slot->State, JobStateConst.Pending);

        Interlocked.Increment(ref sched->TotalJobsScheduled.Value);

        if (dependency.IsValid)
            AttachDependency(sched, dependency, slotIdx);

        if (Interlocked.Decrement(ref slot->PendingDeps) == 0)
            EnqueueReadyJob(sched, slotIdx, wakeCount: chunks < sched->WorkerCount ? chunks : sched->WorkerCount);

        return JobHandle.Create((uint)slotIdx, gen);
    }

    /// <summary>
    /// Combines two job handles into a single fence JobHandle that completes when both a and b have finished.
    /// Zero-GC DAG fan-in primitive.
    /// </summary>
    public static JobHandle CombineDependencies(JobScheduler* sched, JobHandle a, JobHandle b)
    {
        if (!a.IsValid) return b;
        if (!b.IsValid) return a;
        if (a == b) return a;
        ReadOnlySpan<JobHandle> pair = stackalloc JobHandle[] { a, b };
        return CombineDependencies(sched, pair);
    }

    /// <summary>
    /// Combines multiple job handles into a single fence JobHandle that completes when all inputs have finished.
    /// </summary>
    public static JobHandle CombineDependencies(JobScheduler* sched, ReadOnlySpan<JobHandle> deps)
    {
        if (sched == null || sched->Active == 0 || deps.Length == 0)
            return JobHandle.Invalid;

        int validCount = 0;
        JobHandle single = JobHandle.Invalid;
        for (int i = 0; i < deps.Length; i++)
        {
            if (deps[i].IsValid && !IsComplete(sched, deps[i]))
            {
                validCount++;
                single = deps[i];
            }
        }
        if (validCount == 0) return JobHandle.Invalid;
        if (validCount == 1) return single;

        int slotIdx = AllocSlot(sched);
        JobSlot* slot = sched->Slots + slotIdx;
        uint gen = slot->Generation;

        slot->Kind = JobKind.Fence;
        slot->TotalItems = 0;
        slot->BatchSize = 1;
        slot->NextItem = 0;
        slot->RemainingChunks = 0;
        slot->CancelRequested = 0;
        slot->FirstDependent = -1;
        slot->DepLock = 0;
        slot->UserData = null;
        slot->CallbackPtr = null;
        slot->PendingDeps = 1;
        Volatile.Write(ref slot->State, JobStateConst.Pending);

        for (int i = 0; i < deps.Length; i++)
        {
            if (deps[i].IsValid)
                AttachDependency(sched, deps[i], slotIdx);
        }

        if (Interlocked.Decrement(ref slot->PendingDeps) == 0)
            FinishJob(sched, slotIdx);

        return JobHandle.Create((uint)slotIdx, gen);
    }

    /// <summary>
    /// Non-blocking check whether a scheduled job has completed (or handle is invalid/retired).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsComplete(JobScheduler* sched, JobHandle handle)
    {
        if (sched == null || !handle.IsValid) return true;
        uint idx = handle.Index;
        if (idx >= (uint)sched->MaxJobs) return true;
        JobSlot* slot = sched->Slots + idx;
        uint gen = Volatile.Read(ref slot->Generation);
        if (gen != handle.Generation) return true;
        return Volatile.Read(ref slot->State) == JobStateConst.Completed;
    }

    /// <summary>
    /// Requests cooperative cancellation on a running or pending job (e.g. when aborting a background map build).
    /// Always call Complete(sched, handle) afterwards before freeing memory referenced by the job.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RequestCancel(JobScheduler* sched, JobHandle handle)
    {
        if (sched == null || !handle.IsValid) return;
        uint idx = handle.Index;
        if (idx >= (uint)sched->MaxJobs) return;
        JobSlot* slot = sched->Slots + idx;
        if (Volatile.Read(ref slot->Generation) == handle.Generation)
            Volatile.Write(ref slot->CancelRequested, 1);
    }

    /// <summary>
    /// Checks whether cooperative cancellation has been requested for the given job handle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCancelRequested(JobScheduler* sched, JobHandle handle)
    {
        if (sched == null || !handle.IsValid) return false;
        uint idx = handle.Index;
        if (idx >= (uint)sched->MaxJobs) return false;
        JobSlot* slot = sched->Slots + idx;
        if (Volatile.Read(ref slot->Generation) != handle.Generation) return false;
        return Volatile.Read(ref slot->CancelRequested) != 0;
    }

    /// <summary>
    /// Waits until the specified job completes, actively helping execute ready chunks on the calling thread
    /// (workerIndex = 0) so CPU cores stay saturated and nested waits never deadlock.
    /// </summary>
    public static void Complete(JobScheduler* sched, JobHandle handle)
    {
        if (sched == null || !handle.IsValid) return;

        int callerWorker = t_workerIndex;
        int spins = 0;
        while (!IsComplete(sched, handle))
        {
            if (TryExecuteOneChunk(sched, workerIndex: callerWorker))
            {
                spins = 0;
            }
            else
            {
                spins++;
                if (spins < 64)
                    Thread.SpinWait(16);
                else
                    Thread.Yield();
            }
        }

        if (sched->Active != 0)
        {
            Exception? err = s_pools[sched->PoolSlot].WorkerError;
            if (err != null)
            {
                s_pools[sched->PoolSlot].WorkerError = null;
                throw new InvalidOperationException("Unhandled exception occurred inside a JobSystem worker.", err);
            }
        }
    }

    /// <summary>
    /// Waits for all handles in the span to complete, helping execute ready chunks in the meantime.
    /// </summary>
    public static void CompleteAll(JobScheduler* sched, ReadOnlySpan<JobHandle> handles)
    {
        for (int i = 0; i < handles.Length; i++)
            Complete(sched, handles[i]);
    }

    // =========================================================================
    // INTERNAL LOCK-FREE / SPINLOCK SCHEDULER CORE
    // =========================================================================

    private static void WorkerEntry(object? arg)
    {
        WorkerStartParam p = (WorkerStartParam)arg!;
        JobScheduler* sched = (JobScheduler*)p.SchedulerPtr;
        int workerIdx = p.WorkerIndex; // 1..WorkerCount
        t_workerIndex = workerIdx;
        int bitIdx = workerIdx - 1;
        long bitMask = 1L << bitIdx;
        AutoResetEvent wakeEvt = s_pools[sched->PoolSlot].WakeEvents![bitIdx];

        while (true)
        {
            if (TryExecuteOneChunk(sched, workerIdx))
                continue;

            if (Volatile.Read(ref sched->ShutdownFlag.Value) != 0)
                break;

            // Brief spin to absorb back-to-back frame jobs with zero OS kernel wakeups
            bool didWork = false;
            for (int spin = 0; spin < 64; spin++)
            {
                Thread.SpinWait(20);
                if (Volatile.Read(ref sched->QueuedJobCount.Value) > 0)
                {
                    if (TryExecuteOneChunk(sched, workerIdx))
                    {
                        didWork = true;
                        break;
                    }
                }
                if (Volatile.Read(ref sched->ShutdownFlag.Value) != 0)
                    return;
            }
            if (didWork) continue;

            // Publish sleeping bit with full memory barrier, then double-check queue & shutdown (zero lost wakeups!)
            Interlocked.Or(ref sched->SleepingMask.Value, bitMask);

            if (Volatile.Read(ref sched->ShutdownFlag.Value) != 0)
            {
                Interlocked.And(ref sched->SleepingMask.Value, ~bitMask);
                break;
            }
            if (Volatile.Read(ref sched->QueuedJobCount.Value) > 0)
            {
                Interlocked.And(ref sched->SleepingMask.Value, ~bitMask);
                continue;
            }

            wakeEvt.WaitOne();
            Interlocked.And(ref sched->SleepingMask.Value, ~bitMask);
        }
    }

    private static int AllocSlot(JobScheduler* sched)
    {
        AcquireSpinLock(ref sched->AllocLock.Value);
        try
        {
            int max = sched->MaxJobs;
            int cursor = sched->AllocCursor.Value;
            for (int probe = 0; probe < max; probe++)
            {
                int idx = (cursor + probe) % max;
                JobSlot* s = sched->Slots + idx;
                int st = Volatile.Read(ref s->State);
                if (st == JobStateConst.Free || st == JobStateConst.Completed)
                {
                    // Increment generation under DepLock so stale handles immediately see retired/completed
                    AcquireSpinLock(ref s->DepLock);
                    uint nextGen = (s->Generation + 1) & HandlePacking.GenerationMask;
                    if (nextGen == 0) nextGen = 1;
                    Volatile.Write(ref s->Generation, nextGen);
                    Volatile.Write(ref s->State, JobStateConst.Pending);
                    ReleaseSpinLock(ref s->DepLock);

                    sched->AllocCursor.Value = (idx + 1) % max;
                    return idx;
                }
            }
            throw new OutOfMemoryException($"JobScheduler active job capacity ({max}) exhausted.");
        }
        finally
        {
            ReleaseSpinLock(ref sched->AllocLock.Value);
        }
    }

    private static void AttachDependency(JobScheduler* sched, JobHandle dep, int childSlotIdx)
    {
        uint depIdx = dep.Index;
        if (depIdx >= (uint)sched->MaxJobs) return;
        JobSlot* depSlot = sched->Slots + depIdx;

        // Pre-increment child's PendingDeps before publishing link so child cannot fire early
        Interlocked.Increment(ref sched->Slots[childSlotIdx].PendingDeps);

        bool alreadyDone;
        AcquireSpinLock(ref depSlot->DepLock);
        if (Volatile.Read(ref depSlot->Generation) != dep.Generation ||
            Volatile.Read(ref depSlot->State) == JobStateConst.Completed)
        {
            alreadyDone = true;
        }
        else
        {
            int linkIdx = AllocDepLink(sched);
            sched->DepLinks[linkIdx].ChildSlot = childSlotIdx;
            sched->DepLinks[linkIdx].Next = depSlot->FirstDependent;
            depSlot->FirstDependent = linkIdx;
            alreadyDone = false;
        }
        ReleaseSpinLock(ref depSlot->DepLock);

        if (alreadyDone)
            Interlocked.Decrement(ref sched->Slots[childSlotIdx].PendingDeps);
    }

    private static int AllocDepLink(JobScheduler* sched)
    {
        AcquireSpinLock(ref sched->DepPoolLock.Value);
        int head = sched->FreeDepHead.Value;
        if (head < 0)
        {
            ReleaseSpinLock(ref sched->DepPoolLock.Value);
            throw new OutOfMemoryException($"JobScheduler dependency link pool ({sched->MaxDepLinks}) exhausted.");
        }
        sched->FreeDepHead.Value = sched->DepLinks[head].Next;
        ReleaseSpinLock(ref sched->DepPoolLock.Value);
        return head;
    }

    private static void FreeDepLink(JobScheduler* sched, int linkIdx)
    {
        AcquireSpinLock(ref sched->DepPoolLock.Value);
        sched->DepLinks[linkIdx].Next = sched->FreeDepHead.Value;
        sched->FreeDepHead.Value = linkIdx;
        ReleaseSpinLock(ref sched->DepPoolLock.Value);
    }

    private static void EnqueueReadyJob(JobScheduler* sched, int slotIdx, int wakeCount)
    {
        JobSlot* slot = sched->Slots + slotIdx;
        if (slot->Kind == JobKind.Fence || slot->TotalItems <= 0)
        {
            FinishJob(sched, slotIdx);
            return;
        }

        Volatile.Write(ref slot->State, JobStateConst.Ready);

        AcquireSpinLock(ref sched->QueueLock.Value);
        int tail = sched->QueueTail.Value;
        sched->ReadyQueue[tail & sched->ReadyQueueMask] = slotIdx;
        sched->QueueTail.Value = tail + 1;
        // Full memory barrier inside QueueLock BEFORE inspecting SleepingMask (pairs with worker's Interlocked.Or)
        Interlocked.Increment(ref sched->QueuedJobCount.Value);
        ReleaseSpinLock(ref sched->QueueLock.Value);

        if (sched->WorkerCount > 0 && wakeCount > 0)
            WakeWorkers(sched, wakeCount);
    }

    private static void WakeWorkers(JobScheduler* sched, int desiredCount)
    {
        long mask = Interlocked.Read(ref sched->SleepingMask.Value);
        if (mask == 0) return;

        AutoResetEvent[]? events = s_pools[sched->PoolSlot].WakeEvents;
        if (events == null) return;

        int woken = 0;
        int workers = sched->WorkerCount;
        for (int w = 0; w < workers && woken < desiredCount; w++)
        {
            long bit = 1L << w;
            if ((mask & bit) != 0)
            {
                long prev = Interlocked.And(ref sched->SleepingMask.Value, ~bit);
                if ((prev & bit) != 0)
                {
                    events[w].Set();
                    woken++;
                }
            }
        }
    }

    private static bool TryExecuteOneChunk(JobScheduler* sched, int workerIndex)
    {
        if (Volatile.Read(ref sched->QueuedJobCount.Value) <= 0)
            return false;

        int slotIdx = -1;
        int begin = 0;
        int end = 0;
        JobKind kind = JobKind.Single;
        void* userData = null;
        void* cbPtr = null;

        AcquireSpinLock(ref sched->QueueLock.Value);
        while (sched->QueueHead.Value != sched->QueueTail.Value)
        {
            int headIdx = sched->QueueHead.Value;
            int candidate = sched->ReadyQueue[headIdx & sched->ReadyQueueMask];
            JobSlot* s = sched->Slots + candidate;

            if (s->NextItem >= s->TotalItems)
            {
                sched->QueueHead.Value = headIdx + 1;
                Interlocked.Decrement(ref sched->QueuedJobCount.Value);
                continue;
            }

            begin = s->NextItem;
            end = begin + s->BatchSize;
            if (end > s->TotalItems) end = s->TotalItems;
            s->NextItem = end;

            slotIdx = candidate;
            kind = s->Kind;
            userData = s->UserData;
            cbPtr = s->CallbackPtr;

            if (s->NextItem >= s->TotalItems)
            {
                sched->QueueHead.Value = headIdx + 1;
                Interlocked.Decrement(ref sched->QueuedJobCount.Value);
            }
            break;
        }
        ReleaseSpinLock(ref sched->QueueLock.Value);

        if (slotIdx < 0) return false;

        try
        {
            if (kind == JobKind.Single)
            {
                ((delegate*<void*, int, void>)cbPtr)(userData, workerIndex);
            }
            else if (kind == JobKind.ParallelFor)
            {
                ((delegate*<void*, int, int, int, void>)cbPtr)(userData, begin, end, workerIndex);
            }
        }
        catch (Exception ex)
        {
            if (sched->Active != 0)
                Interlocked.CompareExchange(ref s_pools[sched->PoolSlot].WorkerError, ex, null);
        }
        finally
        {
            Interlocked.Increment(ref sched->TotalChunksExecuted.Value);
            if (Interlocked.Decrement(ref sched->Slots[slotIdx].RemainingChunks) == 0)
                FinishJob(sched, slotIdx);
        }

        return true;
    }

    private static void FinishJob(JobScheduler* sched, int slotIdx)
    {
        JobSlot* slot = sched->Slots + slotIdx;

        AcquireSpinLock(ref slot->DepLock);
        Volatile.Write(ref slot->State, JobStateConst.Completed);
        int link = slot->FirstDependent;
        slot->FirstDependent = -1;
        ReleaseSpinLock(ref slot->DepLock);

        while (link >= 0)
        {
            int childIdx = sched->DepLinks[link].ChildSlot;
            int next = sched->DepLinks[link].Next;
            FreeDepLink(sched, link);

            if (Interlocked.Decrement(ref sched->Slots[childIdx].PendingDeps) == 0)
            {
                JobSlot* child = sched->Slots + childIdx;
                int chunks = child->RemainingChunks > 0 ? child->RemainingChunks : 1;
                int wake = chunks < sched->WorkerCount ? chunks : sched->WorkerCount;
                EnqueueReadyJob(sched, childIdx, wake);
            }

            link = next;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireSpinLock(ref int lockWord)
    {
        if (Interlocked.CompareExchange(ref lockWord, 1, 0) == 0)
            return;
        SpinWait sw = default;
        while (Interlocked.CompareExchange(ref lockWord, 1, 0) != 0)
            sw.SpinOnce();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseSpinLock(ref int lockWord)
    {
        Volatile.Write(ref lockWord, 0);
    }
}
