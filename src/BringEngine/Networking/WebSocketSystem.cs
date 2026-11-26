using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

namespace BringEngine.Networking;

public enum WebSocketStateKind : byte
{
    Disconnected = 0,
    Connecting = 1,
    Open = 2,
    Closing = 3,
    Closed = 4,
    Faulted = 5,
}

public enum WebSocketEventKind : byte
{
    None = 0,
    Connected = 1,
    TextMessage = 2,
    BinaryMessage = 3,
    Closed = 4,
    Error = 5,
}

/// <summary>
/// WebSocket event delivered by WebSocketSystem.PollEvent.
/// Caller MUST call WebSocketSystem.ReleaseEvent(ref ev) when finished so DataPtr is freed with zero leaks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct WebSocketEvent
{
    public WebSocketHandle Handle;
    public WebSocketEventKind Kind;
    public ushort CloseStatus;
    public int DataLength;
    public byte* DataPtr;
    public nuint DataAllocSize;
    public FixedString256 Info;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> AsDataSpan()
        => DataPtr == null || DataLength <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(DataPtr, DataLength);

    public readonly string DataAsUtf8String()
        => DataPtr == null || DataLength <= 0 ? string.Empty : Encoding.UTF8.GetString(DataPtr, DataLength);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WsOutboundFrame
{
    public byte* DataPtr;
    public int DataLength;
    public nuint DataAllocSize;
    public byte IsText; // 0 = binary, 1 = text, 2 = close handshake
    public ushort CloseStatus;
    public FixedString256 CloseReason;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WsConnectionMeta
{
    public uint Generation;
    public int InUse;
    public int ConnState; // (int)WebSocketStateKind
    public int CloseRequested;
    public ushort RequestedCloseStatus;
    public FixedString256 RequestedCloseReason;

    public WsOutboundFrame* OutQueue;
    public int OutMask;
    public int OutHead;
    public int OutTail;
    public int OutLock;
}

/// <summary>
/// Unmanaged WebSocket client pool state allocated in stable native memory (e.g., EngineAllocators.Persistent).
/// Supports ws:// and wss:// connections with background send/receive loops and 0-GC Send/PollEvent on the Logic Thread.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct WebSocketClientState
{
    public const int DefaultMaxConnections = 16;
    public const int DefaultEventQueueCapacity = 256;
    public const int DefaultOutboundQueueCapacity = 128;
    public const int DefaultMaxMessageBytes = 1 * 1024 * 1024;

    internal WsConnectionMeta* Connections;
    internal WebSocketEvent* EventQueue;
    internal int EventMask;
    internal int EventHead;
    internal int EventTail;
    internal CachePaddedInt EventLock;
    internal CachePaddedInt SlotLock;

    public int MaxConnections;
    public int MaxMessageBytes;
    public int PoolSlot;
    public LogState* Log;
    public byte Active;

    public CachePaddedInt ActiveConnectionCount;
    public CachePaddedLong TotalMessagesSent;
    public CachePaddedLong TotalMessagesReceived;
    public CachePaddedLong TotalBytesSent;
    public CachePaddedLong TotalBytesReceived;
    public CachePaddedLong DroppedEvents;
}

/// <summary>
/// Static zero-OOP WebSocket (ws:// and wss://) client system backed by .NET 10 ClientWebSocket.
/// - Background receive loop automatically reassembles multi-frame RFC 6455 messages into native buffers.
/// - Logic Thread SendBinary/SendText/PollEvent/ReleaseEvent execute with 0 GC bytes allocated.
/// - Stop() drains and frees all pending native buffers for 0 native memory leaks.
/// </summary>
public static unsafe class WebSocketSystem
{
    private const int MaxInstances = 8;

    internal struct ManagedConnection
    {
        public ClientWebSocket? Socket;
        public CancellationTokenSource? Cts;
        public AutoResetEvent? SendWake;
        public Task? IoTask;
    }

    internal sealed class ManagedWsGuard
    {
        public volatile bool Stopped;
        public int ActiveWorkers;
        public ManagedConnection[]? Connections;
    }

    private struct ManagedWsPool
    {
        public ManagedWsGuard? Guard;
        public bool InUse;
    }

    private static readonly ManagedWsPool[] s_pools = new ManagedWsPool[MaxInstances];
    private static readonly object s_poolLock = new();

    public static void Start(
        WebSocketClientState* state,
        ref LinearArena persistentArena,
        int maxConnections = WebSocketClientState.DefaultMaxConnections,
        int eventQueueCapacity = WebSocketClientState.DefaultEventQueueCapacity,
        int outboundQueueCapacity = WebSocketClientState.DefaultOutboundQueueCapacity,
        int maxMessageBytes = WebSocketClientState.DefaultMaxMessageBytes,
        LogState* log = null)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (maxConnections < 1) maxConnections = 1;
        int maxHandleSlots = (int)HandlePacking.IndexMask + 1;
        if (maxConnections > maxHandleSlots) maxConnections = maxHandleSlots;
        if (maxMessageBytes < 1024) maxMessageBytes = 1024;
        if (eventQueueCapacity < 16) eventQueueCapacity = 16;
        if (eventQueueCapacity > 0x2000_0000) eventQueueCapacity = 0x2000_0000;
        if (outboundQueueCapacity < 16) outboundQueueCapacity = 16;
        if (outboundQueueCapacity > 0x2000_0000) outboundQueueCapacity = 0x2000_0000;

        int evCap = 16;
        while (evCap < eventQueueCapacity) evCap <<= 1;

        int outCap = 16;
        while (outCap < outboundQueueCapacity) outCap <<= 1;

        var guard = new ManagedWsGuard
        {
            Connections = new ManagedConnection[maxConnections],
        };

        int poolSlot = -1;
        lock (s_poolLock)
        {
            for (int i = 0; i < MaxInstances; i++)
            {
                if (!s_pools[i].InUse)
                {
                    s_pools[i].InUse = true;
                    s_pools[i].Guard = guard;
                    poolSlot = i;
                    break;
                }
            }
        }
        if (poolSlot < 0)
            throw new InvalidOperationException("Maximum concurrent WebSocketClientState instances exceeded.");

        try
        {
            *state = default;
            state->Connections = persistentArena.Alloc<WsConnectionMeta>(maxConnections, 64);
            for (int i = 0; i < maxConnections; i++)
            {
                state->Connections[i].Generation = 1;
                state->Connections[i].InUse = 0;
                state->Connections[i].ConnState = (int)WebSocketStateKind.Disconnected;
                state->Connections[i].OutQueue = persistentArena.Alloc<WsOutboundFrame>(outCap, 64);
                state->Connections[i].OutMask = outCap - 1;
            }

            state->EventQueue = persistentArena.Alloc<WebSocketEvent>(evCap, 64);
            state->EventMask = evCap - 1;
            state->MaxConnections = maxConnections;
            state->MaxMessageBytes = maxMessageBytes;
            state->PoolSlot = poolSlot;
            state->Log = log;
            Volatile.Write(ref state->Active, 1);
        }
        catch
        {
            lock (s_poolLock)
            {
                s_pools[poolSlot] = default;
            }
            throw;
        }

        LogSystem.Info(log, $"WebSocketSystem started (maxConnections={maxConnections}, maxMessageBytes={maxMessageBytes})");
    }

    public static void Stop(WebSocketClientState* state)
    {
        if (state == null || Volatile.Read(ref state->Active) == 0) return;

        int poolSlot;
        ManagedWsGuard? guard;
        ManagedConnection[]? conns;
        AcquireSpinLock(ref state->SlotLock.Value);
        if (Volatile.Read(ref state->Active) == 0)
        {
            ReleaseSpinLock(ref state->SlotLock.Value);
            return;
        }
        Volatile.Write(ref state->Active, 0);
        poolSlot = state->PoolSlot;
        guard = (uint)poolSlot < MaxInstances ? s_pools[poolSlot].Guard : null;
        conns = guard?.Connections;
        if (guard != null)
        {
            guard.Stopped = true;
            guard.Connections = null;
        }
        ReleaseSpinLock(ref state->SlotLock.Value);

        if (conns != null)
        {
            for (int i = 0; i < conns.Length; i++)
            {
                try { conns[i].Cts?.Cancel(); } catch { }
                try { conns[i].SendWake?.Set(); } catch { }
            }
            for (int i = 0; i < conns.Length; i++)
            {
                Task? t = conns[i].IoTask;
                if (t != null)
                {
                    try { t.Wait(TimeSpan.FromSeconds(5)); } catch { }
                }
                if (t == null || t.IsCompleted)
                {
                    try { conns[i].Socket?.Dispose(); } catch { }
                    try { conns[i].Cts?.Dispose(); } catch { }
                    try { conns[i].SendWake?.Dispose(); } catch { }
                }
                conns[i] = default;
            }
        }

        for (int i = 0; i < state->MaxConnections; i++)
        {
            WsConnectionMeta* c = state->Connections + i;
            AcquireSpinLock(ref c->OutLock);
            Volatile.Write(ref c->ConnState, (int)WebSocketStateKind.Closed);
            Volatile.Write(ref c->InUse, 0);
            DrainOutboundQueueLocked(c);
            ReleaseSpinLock(ref c->OutLock);
        }

        Interlocked.Exchange(ref state->ActiveConnectionCount.Value, 0);

        while (PollEvent(state, out WebSocketEvent ev))
            ReleaseEvent(ref ev);

        if ((uint)poolSlot < MaxInstances)
        {
            lock (s_poolLock)
            {
                s_pools[poolSlot] = default;
            }
        }

        LogSystem.Info(state->Log,
            $"WebSocketSystem stopped (sent={Interlocked.Read(ref state->TotalMessagesSent.Value)}, recv={Interlocked.Read(ref state->TotalMessagesReceived.Value)}, dropped={Interlocked.Read(ref state->DroppedEvents.Value)})");
    }

    /// <summary>
    /// Opens an asynchronous ws:// or wss:// WebSocket connection and returns a generational handle immediately.
    /// Watch for WebSocketEventKind.Connected or WebSocketEventKind.Error via PollEvent.
    /// </summary>
    public static WebSocketHandle Connect(
        WebSocketClientState* state,
        ReadOnlySpan<char> url,
        ReadOnlySpan<HttpHeader> headers = default,
        ReadOnlySpan<char> subProtocol = default,
        int keepAliveMs = 20000,
        int connectTimeoutMs = 10000)
    {
        if (state == null || Volatile.Read(ref state->Active) == 0 || url.IsEmpty)
            return WebSocketHandle.Invalid;

        // Construct Uri, ClientWebSocket, headers, CTS, and AutoResetEvent BEFORE acquiring SlotLock
        Uri? uri = null;
        ClientWebSocket? ws = null;
        CancellationTokenSource? cts = null;
        AutoResetEvent? wake = null;
        string? setupError = null;
        try
        {
            uri = new Uri(new string(url), UriKind.Absolute);
            ws = new ClientWebSocket();
            if (keepAliveMs > 0)
                ws.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(keepAliveMs);
            if (!subProtocol.IsEmpty)
                ws.Options.AddSubProtocol(new string(subProtocol));
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i].Name.Length > 0)
                    ws.Options.SetRequestHeader(headers[i].Name.ToString(), headers[i].Value.ToString());
            }
            cts = new CancellationTokenSource();
            wake = new AutoResetEvent(false);
        }
        catch (Exception ex)
        {
            try { ws?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }
            try { wake?.Dispose(); } catch { }
            ws = null;
            cts = null;
            wake = null;
            uri = null;
            setupError = ex.Message;
        }

        int slotIdx = -1;
        uint gen = 0;
        bool inactive = false;
        bool poolFull = false;
        WebSocketHandle handle = WebSocketHandle.Invalid;
        ClientWebSocket? oldWs = null;
        CancellationTokenSource? oldCts = null;
        AutoResetEvent? oldWake = null;

        AcquireSpinLock(ref state->SlotLock.Value);
        try
        {
            ManagedWsGuard? guard = (uint)state->PoolSlot < MaxInstances ? s_pools[state->PoolSlot].Guard : null;
            ManagedConnection[]? conns = guard?.Connections;
            if (Volatile.Read(ref state->Active) == 0 || guard == null || guard.Stopped || conns == null)
            {
                inactive = true;
            }
            else
            {
                // Pick a free slot whose previous worker is completely finished (never block Logic Thread on IoTask.Wait)
                for (int i = 0; i < state->MaxConnections; i++)
                {
                    if (Volatile.Read(ref state->Connections[i].InUse) == 0 &&
                        (conns[i].IoTask == null || conns[i].IoTask!.IsCompleted))
                    {
                        slotIdx = i;
                        break;
                    }
                }

                if (slotIdx < 0)
                {
                    poolFull = true;
                }
                else
                {
                    oldWs = conns[slotIdx].Socket;
                    oldCts = conns[slotIdx].Cts;
                    oldWake = conns[slotIdx].SendWake;

                    WsConnectionMeta* c = state->Connections + slotIdx;
                    AcquireSpinLock(ref c->OutLock);
                    uint nextGen = (c->Generation + 1) & HandlePacking.GenerationMask;
                    if (nextGen == 0) nextGen = 1;
                    c->Generation = nextGen;
                    c->InUse = setupError != null ? 0 : 1;
                    c->ConnState = setupError != null ? (int)WebSocketStateKind.Faulted : (int)WebSocketStateKind.Connecting;
                    c->CloseRequested = 0;
                    c->RequestedCloseStatus = 0;
                    c->RequestedCloseReason = default;
                    c->OutHead = 0;
                    c->OutTail = 0;
                    ReleaseSpinLock(ref c->OutLock);

                    gen = nextGen;
                    handle = WebSocketHandle.Create((uint)slotIdx, gen);

                    if (setupError != null)
                    {
                        conns[slotIdx] = default;
                        PushEvent(state, new WebSocketEvent
                        {
                            Handle = handle,
                            Kind = WebSocketEventKind.Error,
                            Info = FixedString256.From(setupError),
                        });
                    }
                    else
                    {
                        conns[slotIdx] = new ManagedConnection
                        {
                            Socket = ws,
                            Cts = cts,
                            SendWake = wake,
                        };

                        Interlocked.Increment(ref state->ActiveConnectionCount.Value);
                        Interlocked.Increment(ref guard.ActiveWorkers);
                        nint statePtr = (nint)state;
                        int capturedSlot = slotIdx;
                        WebSocketHandle capturedHandle = handle;
                        ManagedWsGuard capturedGuard = guard;
                        Uri capturedUri = uri!;
                        ClientWebSocket capturedWs = ws!;
                        CancellationTokenSource capturedCts = cts!;
                        AutoResetEvent capturedWake = wake!;
                        conns[slotIdx].IoTask = Task.Run(() => WsAsyncWorker.RunConnectionAsync(
                            statePtr, capturedGuard, capturedSlot, capturedHandle, capturedUri, capturedWs, capturedCts, capturedWake, connectTimeoutMs));
                    }
                }
            }
        }
        finally
        {
            ReleaseSpinLock(ref state->SlotLock.Value);
        }

        if (oldWs != null) { try { oldWs.Dispose(); } catch { } }
        if (oldCts != null) { try { oldCts.Dispose(); } catch { } }
        if (oldWake != null) { try { oldWake.Dispose(); } catch { } }

        if (inactive)
        {
            try { ws?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }
            try { wake?.Dispose(); } catch { }
            return WebSocketHandle.Invalid;
        }
        if (poolFull)
        {
            try { ws?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }
            try { wake?.Dispose(); } catch { }
            LogSystem.Warn(state->Log, $"WebSocketSystem connection pool full (maxConnections={state->MaxConnections})");
            return WebSocketHandle.Invalid;
        }
        if (setupError != null)
        {
            LogSystem.Warn(state->Log, $"WebSocket {handle} Connect setup failed: {setupError}");
            return handle;
        }

        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WebSocketStateKind GetState(WebSocketClientState* state, WebSocketHandle handle)
    {
        if (state == null || Volatile.Read(ref state->Active) == 0 || !handle.IsValid) return WebSocketStateKind.Disconnected;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxConnections) return WebSocketStateKind.Disconnected;
        WsConnectionMeta* c = state->Connections + idx;
        if (Volatile.Read(ref c->Generation) != handle.Generation) return WebSocketStateKind.Disconnected;
        return (WebSocketStateKind)Volatile.Read(ref c->ConnState);
    }

    /// <summary>
    /// Enqueues a binary WebSocket message into native memory for background transmission.
    /// Allocates 0 GC bytes on the calling (Logic) thread.
    /// </summary>
    public static bool SendBinary(WebSocketClientState* state, WebSocketHandle handle, ReadOnlySpan<byte> payload)
        => EnqueueOutbound(state, handle, payload, isText: 0);

    /// <summary>
    /// Enqueues a UTF-8 text WebSocket message into native memory for background transmission.
    /// Allocates 0 GC bytes on the calling (Logic) thread.
    /// </summary>
    public static bool SendText(WebSocketClientState* state, WebSocketHandle handle, ReadOnlySpan<byte> utf8Payload)
        => EnqueueOutbound(state, handle, utf8Payload, isText: 1);

    /// <summary>
    /// Encodes UTF-16 text to UTF-8 on the stack/native memory and enqueues it for transmission (0 GC bytes).
    /// </summary>
    public static bool SendText(WebSocketClientState* state, WebSocketHandle handle, ReadOnlySpan<char> text)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount <= 1024)
        {
            Span<byte> tmp = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(text, tmp);
            return EnqueueOutbound(state, handle, tmp, isText: 1);
        }

        nuint allocSize = (nuint)byteCount;
        byte* buf = (byte*)NativeMemoryTracker.AlignedAlloc(allocSize, 16);
        try
        {
            Encoding.UTF8.GetBytes(text, new Span<byte>(buf, byteCount));
            return EnqueueOutbound(state, handle, new ReadOnlySpan<byte>(buf, byteCount), isText: 1);
        }
        finally
        {
            NativeMemoryTracker.AlignedFree(buf, allocSize);
        }
    }

    /// <summary>
    /// Initiates a clean RFC 6455 close handshake on the background send loop (0 GC bytes on caller).
    /// </summary>
    public static void Close(
        WebSocketClientState* state,
        WebSocketHandle handle,
        ushort closeStatus = 1000,
        ReadOnlySpan<char> reason = default)
    {
        if (state == null || Volatile.Read(ref state->Active) == 0 || !handle.IsValid) return;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxConnections) return;
        WsConnectionMeta* c = state->Connections + idx;
        if (Volatile.Read(ref c->Generation) != handle.Generation || Volatile.Read(ref c->InUse) == 0)
            return;

        FixedString256 fixedReason = reason.IsEmpty ? default : FixedString256.From(reason);
        var frame = new WsOutboundFrame
        {
            IsText = 2,
            CloseStatus = closeStatus,
            CloseReason = fixedReason,
        };

        bool cancelConnect = false;
        CancellationTokenSource? ctsToCancel = null;
        AutoResetEvent? wakeToSignal = null;

        AcquireSpinLock(ref state->SlotLock.Value);
        try
        {
            ManagedWsGuard? guard = (uint)state->PoolSlot < MaxInstances ? s_pools[state->PoolSlot].Guard : null;
            ManagedConnection[]? conns = guard?.Connections;
            if (Volatile.Read(ref state->Active) != 0 && guard != null && !guard.Stopped && conns != null && idx < (uint)conns.Length)
            {
                bool matched = false;
                AcquireSpinLock(ref c->OutLock);
                if (Volatile.Read(ref c->Generation) == handle.Generation && Volatile.Read(ref c->InUse) != 0)
                {
                    matched = true;
                    Volatile.Write(ref c->CloseRequested, 1);
                    c->RequestedCloseStatus = closeStatus;
                    c->RequestedCloseReason = fixedReason;

                    int count = c->OutTail - c->OutHead;
                    if (count <= c->OutMask)
                    {
                        c->OutQueue[c->OutTail & c->OutMask] = frame;
                        c->OutTail++;
                    }

                    int st = Volatile.Read(ref c->ConnState);
                    if (st == (int)WebSocketStateKind.Connecting)
                        cancelConnect = true;
                    else if (st == (int)WebSocketStateKind.Open)
                        Volatile.Write(ref c->ConnState, (int)WebSocketStateKind.Closing);
                }
                ReleaseSpinLock(ref c->OutLock);

                if (matched)
                {
                    if (cancelConnect)
                        ctsToCancel = conns[idx].Cts;
                    wakeToSignal = conns[idx].SendWake;
                }
            }
        }
        finally
        {
            ReleaseSpinLock(ref state->SlotLock.Value);
        }

        if (ctsToCancel != null)
        {
            try { ctsToCancel.Cancel(); } catch { }
        }
        if (wakeToSignal != null)
        {
            try { wakeToSignal.Set(); } catch { }
        }
    }

    /// <summary>
    /// Pops the next WebSocket event from the native ring buffer (0 GC bytes).
    /// Caller MUST call ReleaseEvent(ref ev) to free any native message payload.
    /// </summary>
    public static bool PollEvent(WebSocketClientState* state, out WebSocketEvent ev)
    {
        if (state == null)
        {
            ev = default;
            return false;
        }
        AcquireSpinLock(ref state->EventLock.Value);
        int head = state->EventHead;
        if (head == state->EventTail)
        {
            ReleaseSpinLock(ref state->EventLock.Value);
            ev = default;
            return false;
        }
        ev = state->EventQueue[head & state->EventMask];
        state->EventHead = head + 1;
        ReleaseSpinLock(ref state->EventLock.Value);
        return true;
    }

    /// <summary>
    /// Frees the native payload owned by a WebSocketEvent. Zero GC, idempotent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReleaseEvent(ref WebSocketEvent ev)
    {
        if (ev.DataPtr != null && ev.DataAllocSize > 0)
            NativeMemoryTracker.AlignedFree(ev.DataPtr, ev.DataAllocSize);
        ev.DataPtr = null;
        ev.DataLength = 0;
        ev.DataAllocSize = 0;
    }

    // =========================================================================
    // INTERNAL BACKGROUND SEND/RECEIVE LOOPS & NATIVE QUEUES
    // =========================================================================

    private static bool EnqueueOutbound(WebSocketClientState* state, WebSocketHandle handle, ReadOnlySpan<byte> payload, byte isText)
    {
        if (state == null || Volatile.Read(ref state->Active) == 0 || !handle.IsValid) return false;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxConnections) return false;

        WsConnectionMeta* c = state->Connections + idx;
        if (Volatile.Read(ref c->Generation) != handle.Generation || Volatile.Read(ref c->InUse) == 0) return false;
        if (Volatile.Read(ref c->ConnState) != (int)WebSocketStateKind.Open) return false;
        if (payload.Length > state->MaxMessageBytes) return false;

        byte* ptr = null;
        nuint allocSize = 0;
        if (payload.Length > 0)
        {
            allocSize = (nuint)payload.Length;
            ptr = (byte*)NativeMemoryTracker.AlignedAlloc(allocSize, 16);
            payload.CopyTo(new Span<byte>(ptr, payload.Length));
        }

        bool queued = false;
        AcquireSpinLock(ref c->OutLock);
        if (Volatile.Read(ref state->Active) != 0 &&
            Volatile.Read(ref c->Generation) == handle.Generation &&
            Volatile.Read(ref c->InUse) != 0 &&
            Volatile.Read(ref c->ConnState) == (int)WebSocketStateKind.Open &&
            (c->OutTail - c->OutHead) <= c->OutMask)
        {
            c->OutQueue[c->OutTail & c->OutMask] = new WsOutboundFrame
            {
                DataPtr = ptr,
                DataLength = payload.Length,
                DataAllocSize = allocSize,
                IsText = isText,
            };
            c->OutTail++;
            queued = true;
        }
        ReleaseSpinLock(ref c->OutLock);

        if (!queued)
        {
            if (ptr != null && allocSize > 0)
                NativeMemoryTracker.AlignedFree(ptr, allocSize);
            return false;
        }

        // Snapshot SendWake under SlotLock after verifying handle generation to avoid ABA wake/UAF (0 GC)
        AutoResetEvent? wakeToSignal = null;
        AcquireSpinLock(ref state->SlotLock.Value);
        if (Volatile.Read(ref state->Active) != 0 &&
            Volatile.Read(ref c->Generation) == handle.Generation &&
            Volatile.Read(ref c->InUse) != 0)
        {
            ManagedWsGuard? guard = (uint)state->PoolSlot < MaxInstances ? s_pools[state->PoolSlot].Guard : null;
            ManagedConnection[]? conns = guard?.Connections;
            if (guard != null && !guard.Stopped && conns != null && idx < (uint)conns.Length)
                wakeToSignal = conns[idx].SendWake;
        }
        ReleaseSpinLock(ref state->SlotLock.Value);

        if (wakeToSignal != null)
        {
            try { wakeToSignal.Set(); } catch { }
        }
        return true;
    }

    internal static bool IsActive(nint statePtr) => Volatile.Read(ref ((WebSocketClientState*)statePtr)->Active) != 0;
    internal static int GetMaxMessageBytes(nint statePtr) => ((WebSocketClientState*)statePtr)->MaxMessageBytes;

    internal static void SetConnState(nint statePtr, int slotIdx, WebSocketStateKind kind)
    {
        WsConnectionMeta* c = ((WebSocketClientState*)statePtr)->Connections + slotIdx;
        Volatile.Write(ref c->ConnState, (int)kind);
    }

    internal static void MarkCloseRequested(nint statePtr, int slotIdx)
    {
        WsConnectionMeta* c = ((WebSocketClientState*)statePtr)->Connections + slotIdx;
        Volatile.Write(ref c->CloseRequested, 1);
    }

    internal static void GetRequestedClose(nint statePtr, int slotIdx, out ushort status, out FixedString256 reason)
    {
        WsConnectionMeta* c = ((WebSocketClientState*)statePtr)->Connections + slotIdx;
        AcquireSpinLock(ref c->OutLock);
        status = c->RequestedCloseStatus;
        reason = c->RequestedCloseReason;
        ReleaseSpinLock(ref c->OutLock);
    }

    internal static void PushEventFromWorker(
        nint statePtr,
        WebSocketHandle handle,
        WebSocketEventKind kind,
        ushort closeStatus = 0,
        nint dataPtr = default,
        int dataLen = 0,
        nuint dataCap = 0,
        in FixedString256 info = default)
    {
        WebSocketClientState* state = (WebSocketClientState*)statePtr;
        var ev = new WebSocketEvent
        {
            Handle = handle,
            Kind = kind,
            CloseStatus = closeStatus,
            DataPtr = (byte*)dataPtr,
            DataLength = dataLen,
            DataAllocSize = dataCap,
            Info = info,
        };
        if (PushEvent(state, in ev) && (kind == WebSocketEventKind.TextMessage || kind == WebSocketEventKind.BinaryMessage))
        {
            Interlocked.Increment(ref state->TotalMessagesReceived.Value);
            Interlocked.Add(ref state->TotalBytesReceived.Value, dataLen);
        }

        if (kind == WebSocketEventKind.Connected)
            LogSystem.Info(state->Log, $"WebSocket {handle} connected");
        else if (kind == WebSocketEventKind.Closed)
            LogSystem.Info(state->Log, $"WebSocket {handle} closed (code={closeStatus}): {info}");
        else if (kind == WebSocketEventKind.Error)
            LogSystem.Warn(state->Log, $"WebSocket {handle} error: {info}");
    }

    internal static void FinalizeConnectionStateFromWorker(nint statePtr, int slotIdx, bool faulted)
    {
        WebSocketClientState* state = (WebSocketClientState*)statePtr;
        WsConnectionMeta* c = state->Connections + slotIdx;
        AcquireSpinLock(ref c->OutLock);
        if (faulted)
            Volatile.Write(ref c->ConnState, (int)WebSocketStateKind.Faulted);
        else if (Volatile.Read(ref c->ConnState) != (int)WebSocketStateKind.Faulted)
            Volatile.Write(ref c->ConnState, (int)WebSocketStateKind.Closed);
        DrainOutboundQueueLocked(c);
        ReleaseSpinLock(ref c->OutLock);
        Interlocked.Decrement(ref state->ActiveConnectionCount.Value);
    }

    internal static void ReleaseSlotFromWorker(nint statePtr, int slotIdx)
    {
        WebSocketClientState* state = (WebSocketClientState*)statePtr;
        WsConnectionMeta* c = state->Connections + slotIdx;
        Volatile.Write(ref c->InUse, 0);
    }

    internal static void SendLoopBlocking(
        nint statePtr,
        ManagedWsGuard guard,
        int slotIdx,
        ClientWebSocket ws,
        CancellationToken ct,
        AutoResetEvent sendWake)
    {
        WebSocketClientState* state = (WebSocketClientState*)statePtr;
        WsConnectionMeta* c = state->Connections + slotIdx;
        byte[] sendBuf = ArrayPool<byte>.Shared.Rent(16384);

        try
        {
            while (!ct.IsCancellationRequested && !guard.Stopped && IsActive(statePtr))
            {
                if (!TryDequeueOutbound(c, out WsOutboundFrame frame))
                {
                    if (Volatile.Read(ref c->CloseRequested) != 0)
                    {
                        if (guard.Stopped) break;
                        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        {
                            GetRequestedClose(statePtr, slotIdx, out ushort closeCode, out FixedString256 closeReason);
                            WebSocketCloseStatus code = closeCode != 0 ? (WebSocketCloseStatus)closeCode : WebSocketCloseStatus.NormalClosure;
                            string rsn = closeReason.Length > 0 ? closeReason.ToString() : string.Empty;
                            ws.CloseOutputAsync(code, rsn, ct).GetAwaiter().GetResult();
                        }
                        break;
                    }
                    sendWake.WaitOne(50);
                    if (guard.Stopped) break;
                    continue;
                }

                try
                {
                    if (frame.IsText == 2)
                    {
                        if (guard.Stopped) break;
                        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        {
                            WebSocketCloseStatus code = frame.CloseStatus != 0 ? (WebSocketCloseStatus)frame.CloseStatus : WebSocketCloseStatus.NormalClosure;
                            string reason = frame.CloseReason.Length > 0 ? frame.CloseReason.ToString() : string.Empty;
                            ws.CloseOutputAsync(code, reason, ct)
                              .GetAwaiter().GetResult();
                        }
                        break;
                    }

                    if (ws.State != WebSocketState.Open)
                    {
                        if (guard.Stopped) break;
                        if (ws.State == WebSocketState.CloseReceived)
                        {
                            WebSocketCloseStatus code = WebSocketCloseStatus.NormalClosure;
                            string rsn = string.Empty;
                            if (Volatile.Read(ref c->CloseRequested) != 0)
                            {
                                GetRequestedClose(statePtr, slotIdx, out ushort closeCode, out FixedString256 closeReason);
                                if (closeCode != 0) code = (WebSocketCloseStatus)closeCode;
                                if (closeReason.Length > 0) rsn = closeReason.ToString();
                            }
                            ws.CloseOutputAsync(code, rsn, ct).GetAwaiter().GetResult();
                        }
                        break;
                    }

                    var msgType = frame.IsText != 0 ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                    int remaining = frame.DataLength;
                    int offset = 0;

                    if (remaining == 0)
                    {
                        ws.SendAsync(ReadOnlyMemory<byte>.Empty, msgType, endOfMessage: true, ct)
                          .AsTask().GetAwaiter().GetResult();
                    }
                    else
                    {
                        while (remaining > 0)
                        {
                            if (guard.Stopped) break;
                            int chunk = remaining < sendBuf.Length ? remaining : sendBuf.Length;
                            new ReadOnlySpan<byte>(frame.DataPtr + offset, chunk).CopyTo(sendBuf);
                            offset += chunk;
                            remaining -= chunk;
                            bool endOfMsg = remaining == 0;
                            ws.SendAsync(new ReadOnlyMemory<byte>(sendBuf, 0, chunk), msgType, endOfMsg, ct)
                              .AsTask().GetAwaiter().GetResult();
                        }
                    }

                    if (guard.Stopped) break;
                    Interlocked.Increment(ref state->TotalMessagesSent.Value);
                    Interlocked.Add(ref state->TotalBytesSent.Value, frame.DataLength);
                }
                finally
                {
                    if (frame.DataPtr != null && frame.DataAllocSize > 0)
                        NativeMemoryTracker.AlignedFree(frame.DataPtr, frame.DataAllocSize);
                }
            }
        }
        catch
        {
            // Connection dropped or cancelled; ReceiveLoop handles event reporting
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sendBuf);
        }
    }

    private static bool PushEvent(WebSocketClientState* state, in WebSocketEvent ev)
    {
        bool pushed = false;
        AcquireSpinLock(ref state->EventLock.Value);
        if ((state->EventTail - state->EventHead) <= state->EventMask)
        {
            state->EventQueue[state->EventTail & state->EventMask] = ev;
            state->EventTail++;
            pushed = true;
        }
        ReleaseSpinLock(ref state->EventLock.Value);

        if (!pushed)
        {
            long dropped = Interlocked.Increment(ref state->DroppedEvents.Value);
            if (dropped == 1 || (dropped & (dropped - 1)) == 0)
                LogSystem.Warn(state->Log, $"WebSocketSystem event queue full, dropped {ev.Kind} for {ev.Handle} (totalDropped={dropped})");
            if (ev.DataPtr != null && ev.DataAllocSize > 0)
                NativeMemoryTracker.AlignedFree(ev.DataPtr, ev.DataAllocSize);
        }
        return pushed;
    }

    private static bool TryDequeueOutbound(WsConnectionMeta* c, out WsOutboundFrame frame)
    {
        AcquireSpinLock(ref c->OutLock);
        int head = c->OutHead;
        if (head == c->OutTail)
        {
            ReleaseSpinLock(ref c->OutLock);
            frame = default;
            return false;
        }
        frame = c->OutQueue[head & c->OutMask];
        c->OutHead = head + 1;
        ReleaseSpinLock(ref c->OutLock);
        return true;
    }

    private static void DrainOutboundQueueLocked(WsConnectionMeta* c)
    {
        int head = c->OutHead;
        int tail = c->OutTail;
        while (head != tail)
        {
            WsOutboundFrame frame = c->OutQueue[head & c->OutMask];
            head++;
            if (frame.DataPtr != null && frame.DataAllocSize > 0)
                NativeMemoryTracker.AlignedFree(frame.DataPtr, frame.DataAllocSize);
        }
        c->OutHead = head;
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

file static class WsAsyncWorker
{
    public static async Task RunConnectionAsync(
        nint statePtr,
        WebSocketSystem.ManagedWsGuard guard,
        int slotIdx,
        WebSocketHandle handle,
        Uri uri,
        ClientWebSocket ws,
        CancellationTokenSource cts,
        AutoResetEvent sendWake,
        int connectTimeoutMs)
    {
        Task? sendTask = null;
        CancellationTokenSource? loopCts = null;
        bool connectTimedOut = false;
        bool stateFinalized = false;
        WebSocketEventKind terminalEvent = WebSocketEventKind.None;
        ushort terminalCloseCode = 0;
        FixedString256 terminalInfo = default;

        static async Task StopSendTaskAsync(Task? st, CancellationTokenSource? lcts, AutoResetEvent sw)
        {
            if (st != null)
            {
                try { lcts?.Cancel(); } catch { }
                try { sw.Set(); } catch { }
                try { await st.ConfigureAwait(false); } catch { }
            }
        }

        try
        {
            using (var connectTimeout = new CancellationTokenSource(connectTimeoutMs > 0 ? connectTimeoutMs : 10000))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, connectTimeout.Token))
            {
                try
                {
                    await ws.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (connectTimeout.IsCancellationRequested && (guard.Stopped || !cts.IsCancellationRequested))
                {
                    connectTimedOut = true;
                    throw;
                }
            }

            if (guard.Stopped || !WebSocketSystem.IsActive(statePtr))
                return;
            if (cts.IsCancellationRequested)
                throw new OperationCanceledException(cts.Token);

            WebSocketSystem.SetConnState(statePtr, slotIdx, WebSocketStateKind.Open);
            WebSocketSystem.PushEventFromWorker(statePtr, handle, WebSocketEventKind.Connected);

            loopCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            CancellationToken loopCt = loopCts.Token;

            sendTask = Task.Factory.StartNew(
                () => WebSocketSystem.SendLoopBlocking(statePtr, guard, slotIdx, ws, loopCt, sendWake),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            (bool sawClose, ushort closeCode, string closeDesc) =
                await ReceiveLoopAsync(statePtr, guard, slotIdx, handle, ws, loopCt).ConfigureAwait(false);

            if (!guard.Stopped)
                WebSocketSystem.MarkCloseRequested(statePtr, slotIdx);
            try { sendWake.Set(); } catch { }
            await sendTask.ConfigureAwait(false);
            sendTask = null;

            if (!guard.Stopped && WebSocketSystem.IsActive(statePtr))
            {
                if (ws.State == WebSocketState.Aborted && !sawClose)
                {
                    WebSocketSystem.FinalizeConnectionStateFromWorker(statePtr, slotIdx, faulted: true);
                    stateFinalized = true;
                    terminalEvent = WebSocketEventKind.Error;
                    terminalInfo = FixedString256.From("WebSocket connection aborted.");
                }
                else
                {
                    if (!sawClose)
                    {
                        if (ws.CloseStatus.HasValue)
                        {
                            closeCode = (ushort)ws.CloseStatus.Value;
                            closeDesc = ws.CloseStatusDescription ?? string.Empty;
                        }
                        else
                        {
                            WebSocketSystem.GetRequestedClose(statePtr, slotIdx, out ushort reqCode, out FixedString256 reqReason);
                            closeCode = reqCode != 0 ? reqCode : (ushort)1000;
                            closeDesc = reqReason.Length > 0 ? reqReason.ToString() : "Connection closed.";
                        }
                    }
                    WebSocketSystem.FinalizeConnectionStateFromWorker(statePtr, slotIdx, faulted: false);
                    stateFinalized = true;
                    terminalEvent = WebSocketEventKind.Closed;
                    terminalCloseCode = closeCode;
                    terminalInfo = FixedString256.From(closeDesc);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await StopSendTaskAsync(sendTask, loopCts, sendWake).ConfigureAwait(false);
            sendTask = null;
            if (!guard.Stopped && WebSocketSystem.IsActive(statePtr))
            {
                if (connectTimedOut)
                {
                    WebSocketSystem.FinalizeConnectionStateFromWorker(statePtr, slotIdx, faulted: true);
                    stateFinalized = true;
                    terminalEvent = WebSocketEventKind.Error;
                    terminalInfo = FixedString256.From("Connection timed out.");
                }
                else
                {
                    WebSocketSystem.GetRequestedClose(statePtr, slotIdx, out ushort reqCode, out FixedString256 reqReason);
                    terminalCloseCode = reqCode != 0 ? reqCode : (ushort)1000;
                    terminalInfo = reqReason.Length > 0 ? reqReason : FixedString256.From("Connection closed.");
                    WebSocketSystem.FinalizeConnectionStateFromWorker(statePtr, slotIdx, faulted: false);
                    stateFinalized = true;
                    terminalEvent = WebSocketEventKind.Closed;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            await StopSendTaskAsync(sendTask, loopCts, sendWake).ConfigureAwait(false);
            sendTask = null;
            if (!guard.Stopped && WebSocketSystem.IsActive(statePtr))
            {
                WebSocketSystem.FinalizeConnectionStateFromWorker(statePtr, slotIdx, faulted: false);
                stateFinalized = true;
                terminalEvent = WebSocketEventKind.Closed;
                terminalCloseCode = 1000;
                terminalInfo = FixedString256.From("Connection closed.");
            }
        }
        catch (Exception ex)
        {
            await StopSendTaskAsync(sendTask, loopCts, sendWake).ConfigureAwait(false);
            sendTask = null;
            if (!guard.Stopped)
            {
                WebSocketSystem.FinalizeConnectionStateFromWorker(statePtr, slotIdx, faulted: true);
                stateFinalized = true;
                if (WebSocketSystem.IsActive(statePtr))
                {
                    terminalEvent = WebSocketEventKind.Error;
                    terminalInfo = FixedString256.From(ex.Message);
                }
            }
        }
        finally
        {
            await StopSendTaskAsync(sendTask, loopCts, sendWake).ConfigureAwait(false);
            loopCts?.Dispose();

            if (!guard.Stopped && !stateFinalized)
                WebSocketSystem.FinalizeConnectionStateFromWorker(statePtr, slotIdx, faulted: false);

            try { ws.Dispose(); } catch { }
            try { cts.Dispose(); } catch { }
            try { sendWake.Dispose(); } catch { }

            if (terminalEvent != WebSocketEventKind.None && !guard.Stopped && WebSocketSystem.IsActive(statePtr))
            {
                WebSocketSystem.PushEventFromWorker(statePtr, handle, terminalEvent, closeStatus: terminalCloseCode, info: in terminalInfo);
            }

            // Release InUse ONLY as the final atomic store right before ActiveWorkers decrement so Connect()
            // can never reclaim this slot while the worker still touches ws/cts/sendWake/statePtr.
            if (!guard.Stopped)
                WebSocketSystem.ReleaseSlotFromWorker(statePtr, slotIdx);

            Interlocked.Decrement(ref guard.ActiveWorkers);
        }
    }

    private static async Task<(bool SawClose, ushort CloseCode, string CloseDesc)> ReceiveLoopAsync(
        nint statePtr,
        WebSocketSystem.ManagedWsGuard guard,
        int slotIdx,
        WebSocketHandle handle,
        ClientWebSocket ws,
        CancellationToken ct)
    {
        _ = slotIdx;
        int maxMsgBytes = WebSocketSystem.GetMaxMessageBytes(statePtr);
        byte[] recvBuf = ArrayPool<byte>.Shared.Rent(16384);

        nint msgPtr = nint.Zero;
        int msgLen = 0;
        nuint msgCap = 0;

        try
        {
            while (!ct.IsCancellationRequested && !guard.Stopped && WebSocketSystem.IsActive(statePtr) &&
                   (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent))
            {
                ValueWebSocketReceiveResult res = await ws.ReceiveAsync(recvBuf.AsMemory(), ct).ConfigureAwait(false);

                if (res.MessageType == WebSocketMessageType.Close)
                {
                    FreeNative(ref msgPtr, ref msgCap);
                    msgLen = 0;

                    ushort closeCode = (ushort)(ws.CloseStatus ?? WebSocketCloseStatus.NormalClosure);
                    string desc = ws.CloseStatusDescription ?? string.Empty;
                    return (true, closeCode, desc);
                }

                if (res.Count > 0)
                {
                    long needed = (long)msgLen + res.Count;
                    if (needed > maxMsgBytes)
                    {
                        FreeNative(ref msgPtr, ref msgCap);
                        msgLen = 0;
                        throw new InvalidOperationException("WebSocket message exceeded MaxMessageBytes limit.");
                    }

                    if ((nuint)needed > msgCap)
                    {
                        nuint newCap = msgCap == 0 ? (nuint)res.Count : msgCap * 2u;
                        while (newCap < (nuint)needed) newCap *= 2u;
                        if (newCap > (nuint)maxMsgBytes) newCap = (nuint)maxMsgBytes;
                        msgPtr = GrowNative(msgPtr, msgLen, msgCap, newCap);
                        msgCap = newCap;
                    }

                    CopyToNative(recvBuf, res.Count, msgPtr, msgLen, msgCap);
                    msgLen += res.Count;
                }

                if (res.EndOfMessage)
                {
                    WebSocketEventKind evKind = res.MessageType == WebSocketMessageType.Text
                        ? WebSocketEventKind.TextMessage
                        : WebSocketEventKind.BinaryMessage;

                    nint outPtr = msgPtr;
                    int outLen = msgLen;
                    nuint outCap = msgCap;
                    msgPtr = nint.Zero;
                    msgLen = 0;
                    msgCap = 0;

                    if (guard.Stopped)
                    {
                        FreeNative(ref outPtr, ref outCap);
                        return (false, 0, string.Empty);
                    }

                    WebSocketSystem.PushEventFromWorker(statePtr, handle, evKind, dataPtr: outPtr, dataLen: outLen, dataCap: outCap);
                }
            }
            return (false, 0, string.Empty);
        }
        finally
        {
            FreeNative(ref msgPtr, ref msgCap);
            ArrayPool<byte>.Shared.Return(recvBuf);
        }
    }

    private static unsafe void FreeNative(ref nint ptr, ref nuint cap)
    {
        if (ptr != nint.Zero && cap > 0)
            NativeMemoryTracker.AlignedFree((void*)ptr, cap);
        ptr = nint.Zero;
        cap = 0;
    }

    private static unsafe nint GrowNative(nint oldPtr, int oldLen, nuint oldCap, nuint newCap)
    {
        byte* next = (byte*)NativeMemoryTracker.AlignedAlloc(newCap, 16);
        if (oldPtr != nint.Zero)
        {
            if (oldLen > 0)
                Buffer.MemoryCopy((void*)oldPtr, next, (long)newCap, oldLen);
            NativeMemoryTracker.AlignedFree((void*)oldPtr, oldCap);
        }
        return (nint)next;
    }

    private static unsafe void CopyToNative(byte[] src, int count, nint dstPtr, int dstOffset, nuint dstCap)
    {
        fixed (byte* s = src)
            Buffer.MemoryCopy(s, (byte*)dstPtr + dstOffset, (long)(dstCap - (nuint)dstOffset), count);
    }
}
