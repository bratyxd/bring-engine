using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BringEngine.Core.Handles;
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

    private struct ManagedConnection
    {
        public ClientWebSocket? Socket;
        public CancellationTokenSource? Cts;
        public AutoResetEvent? SendWake;
        public Task? IoTask;
    }

    private struct ManagedWsPool
    {
        public ManagedConnection[]? Connections;
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
        int maxMessageBytes = WebSocketClientState.DefaultMaxMessageBytes)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (maxConnections < 1) maxConnections = 1;
        if (maxMessageBytes < 1024) maxMessageBytes = 1024;

        int evCap = 16;
        while (evCap < eventQueueCapacity) evCap <<= 1;

        int outCap = 16;
        while (outCap < outboundQueueCapacity) outCap <<= 1;

        int poolSlot = -1;
        lock (s_poolLock)
        {
            for (int i = 0; i < MaxInstances; i++)
            {
                if (!s_pools[i].InUse)
                {
                    s_pools[i].InUse = true;
                    s_pools[i].Connections = new ManagedConnection[maxConnections];
                    poolSlot = i;
                    break;
                }
            }
        }
        if (poolSlot < 0)
            throw new InvalidOperationException("Maximum concurrent WebSocketClientState instances exceeded.");

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
        state->Active = 1;
    }

    public static void Stop(WebSocketClientState* state)
    {
        if (state == null || state->Active == 0) return;
        int poolSlot = state->PoolSlot;

        state->Active = 0;
        Thread.MemoryBarrier();

        ManagedConnection[]? conns = s_pools[poolSlot].Connections;
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
                conns[i].Socket?.Dispose();
                conns[i].Cts?.Dispose();
                conns[i].SendWake?.Dispose();
                conns[i] = default;

                DrainOutboundQueue(state->Connections + i);
            }
        }

        while (PollEvent(state, out WebSocketEvent ev))
            ReleaseEvent(ref ev);

        lock (s_poolLock)
        {
            s_pools[poolSlot] = default;
        }
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
        if (state == null || state->Active == 0 || url.IsEmpty)
            return WebSocketHandle.Invalid;

        int slotIdx = -1;
        uint gen = 0;
        AcquireSpinLock(ref state->SlotLock.Value);
        for (int i = 0; i < state->MaxConnections; i++)
        {
            if (Volatile.Read(ref state->Connections[i].InUse) == 0)
            {
                WsConnectionMeta* c = state->Connections + i;
                uint nextGen = (c->Generation + 1) & HandlePacking.GenerationMask;
                if (nextGen == 0) nextGen = 1;
                c->Generation = nextGen;
                c->InUse = 1;
                c->ConnState = (int)WebSocketStateKind.Connecting;
                c->CloseRequested = 0;
                c->OutHead = 0;
                c->OutTail = 0;
                c->OutLock = 0;
                gen = nextGen;
                slotIdx = i;
                break;
            }
        }
        ReleaseSpinLock(ref state->SlotLock.Value);

        if (slotIdx < 0)
            return WebSocketHandle.Invalid;

        WebSocketHandle handle = WebSocketHandle.Create((uint)slotIdx, gen);
        Uri uri;
        try
        {
            uri = new Uri(new string(url), UriKind.Absolute);
        }
        catch (Exception ex)
        {
            PushEvent(state, new WebSocketEvent
            {
                Handle = handle,
                Kind = WebSocketEventKind.Error,
                Info = FixedString256.From(ex.Message),
            });
            Volatile.Write(ref state->Connections[slotIdx].ConnState, (int)WebSocketStateKind.Faulted);
            Volatile.Write(ref state->Connections[slotIdx].InUse, 0);
            return handle;
        }

        var ws = new ClientWebSocket();
        if (keepAliveMs > 0)
            ws.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(keepAliveMs);
        if (!subProtocol.IsEmpty)
            ws.Options.AddSubProtocol(new string(subProtocol));
        for (int i = 0; i < headers.Length; i++)
        {
            if (headers[i].Name.Length > 0)
                ws.Options.SetRequestHeader(headers[i].Name.ToString(), headers[i].Value.ToString());
        }

        var cts = new CancellationTokenSource();
        var wake = new AutoResetEvent(false);

        int poolSlot = state->PoolSlot;
        ManagedConnection[] conns = s_pools[poolSlot].Connections!;
        // Wait for any previous finished task on this slot before disposing old handles
        Task? prevTask = conns[slotIdx].IoTask;
        if (prevTask != null && !prevTask.IsCompleted)
        {
            try { prevTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        conns[slotIdx].Socket?.Dispose();
        conns[slotIdx].Cts?.Dispose();
        conns[slotIdx].SendWake?.Dispose();
        conns[slotIdx] = new ManagedConnection
        {
            Socket = ws,
            Cts = cts,
            SendWake = wake,
        };

        Interlocked.Increment(ref state->ActiveConnectionCount.Value);
        nint statePtr = (nint)state;
        conns[slotIdx].IoTask = Task.Run(() => WsAsyncWorker.RunConnectionAsync(statePtr, slotIdx, handle, uri, ws, cts, wake, connectTimeoutMs));
        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WebSocketStateKind GetState(WebSocketClientState* state, WebSocketHandle handle)
    {
        if (state == null || !handle.IsValid) return WebSocketStateKind.Disconnected;
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
        if (state == null || state->Active == 0 || !handle.IsValid) return;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxConnections) return;
        WsConnectionMeta* c = state->Connections + idx;
        if (Volatile.Read(ref c->Generation) != handle.Generation || Volatile.Read(ref c->InUse) == 0)
            return;

        Volatile.Write(ref c->CloseRequested, 1);

        var frame = new WsOutboundFrame
        {
            IsText = 2,
            CloseStatus = closeStatus,
            CloseReason = reason.IsEmpty ? default : FixedString256.From(reason),
        };

        AcquireSpinLock(ref c->OutLock);
        int count = c->OutTail - c->OutHead;
        if (count <= c->OutMask)
        {
            c->OutQueue[c->OutTail & c->OutMask] = frame;
            c->OutTail++;
        }
        ReleaseSpinLock(ref c->OutLock);

        int st = Volatile.Read(ref c->ConnState);
        if (st == (int)WebSocketStateKind.Connecting)
        {
            try { s_pools[state->PoolSlot].Connections?[idx].Cts?.Cancel(); } catch { }
        }
        else if (st == (int)WebSocketStateKind.Open)
        {
            Volatile.Write(ref c->ConnState, (int)WebSocketStateKind.Closing);
        }

        try { s_pools[state->PoolSlot].Connections?[idx].SendWake?.Set(); } catch { }
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
        if (state == null || state->Active == 0 || !handle.IsValid) return false;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxConnections) return false;

        WsConnectionMeta* c = state->Connections + idx;
        if (Volatile.Read(ref c->Generation) != handle.Generation) return false;
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
        if (Volatile.Read(ref c->ConnState) == (int)WebSocketStateKind.Open && (c->OutTail - c->OutHead) <= c->OutMask)
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

        // AutoResetEvent.Set() allocates 0 managed bytes and never inlines continuations onto the Logic Thread
        try { s_pools[state->PoolSlot].Connections?[idx].SendWake?.Set(); } catch { }
        return true;
    }

    internal static bool IsActive(nint statePtr) => ((WebSocketClientState*)statePtr)->Active != 0;
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
        if (kind == WebSocketEventKind.TextMessage || kind == WebSocketEventKind.BinaryMessage)
        {
            Interlocked.Increment(ref state->TotalMessagesReceived.Value);
            Interlocked.Add(ref state->TotalBytesReceived.Value, dataLen);
        }

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
        PushEvent(state, in ev);
    }

    internal static void FinishConnectionFromWorker(nint statePtr, int slotIdx)
    {
        WebSocketClientState* state = (WebSocketClientState*)statePtr;
        WsConnectionMeta* c = state->Connections + slotIdx;
        DrainOutboundQueue(c);
        int curState = Volatile.Read(ref c->ConnState);
        if (curState != (int)WebSocketStateKind.Faulted)
            Volatile.Write(ref c->ConnState, (int)WebSocketStateKind.Closed);
        Volatile.Write(ref c->InUse, 0);
        Interlocked.Decrement(ref state->ActiveConnectionCount.Value);
    }

    internal static void SendLoopBlocking(
        nint statePtr,
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
            while (!ct.IsCancellationRequested && state->Active != 0)
            {
                if (!TryDequeueOutbound(c, out WsOutboundFrame frame))
                {
                    if (Volatile.Read(ref c->CloseRequested) != 0)
                    {
                        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        {
                            ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct)
                              .GetAwaiter().GetResult();
                        }
                        break;
                    }
                    sendWake.WaitOne(50);
                    continue;
                }

                try
                {
                    if (frame.IsText == 2)
                    {
                        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        {
                            string reason = frame.CloseReason.Length > 0 ? frame.CloseReason.ToString() : string.Empty;
                            ws.CloseOutputAsync((WebSocketCloseStatus)frame.CloseStatus, reason, ct)
                              .GetAwaiter().GetResult();
                        }
                        break;
                    }

                    if (ws.State != WebSocketState.Open)
                        break;

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
                            int chunk = remaining < sendBuf.Length ? remaining : sendBuf.Length;
                            new ReadOnlySpan<byte>(frame.DataPtr + offset, chunk).CopyTo(sendBuf);
                            offset += chunk;
                            remaining -= chunk;
                            bool endOfMsg = remaining == 0;
                            ws.SendAsync(new ReadOnlyMemory<byte>(sendBuf, 0, chunk), msgType, endOfMsg, ct)
                              .AsTask().GetAwaiter().GetResult();
                        }
                    }

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

    private static void PushEvent(WebSocketClientState* state, in WebSocketEvent ev)
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
            Interlocked.Increment(ref state->DroppedEvents.Value);
            if (ev.DataPtr != null && ev.DataAllocSize > 0)
                NativeMemoryTracker.AlignedFree(ev.DataPtr, ev.DataAllocSize);
        }
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

    private static void DrainOutboundQueue(WsConnectionMeta* c)
    {
        while (TryDequeueOutbound(c, out WsOutboundFrame frame))
        {
            if (frame.DataPtr != null && frame.DataAllocSize > 0)
                NativeMemoryTracker.AlignedFree(frame.DataPtr, frame.DataAllocSize);
        }
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
        int slotIdx,
        WebSocketHandle handle,
        Uri uri,
        ClientWebSocket ws,
        CancellationTokenSource cts,
        AutoResetEvent sendWake,
        int connectTimeoutMs)
    {
        try
        {
            using (var connectTimeout = new CancellationTokenSource(connectTimeoutMs > 0 ? connectTimeoutMs : 10000))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, connectTimeout.Token))
            {
                await ws.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
            }

            WebSocketSystem.SetConnState(statePtr, slotIdx, WebSocketStateKind.Open);
            WebSocketSystem.PushEventFromWorker(statePtr, handle, WebSocketEventKind.Connected);

            Task sendTask = Task.Factory.StartNew(
                () => WebSocketSystem.SendLoopBlocking(statePtr, slotIdx, ws, cts.Token, sendWake),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await ReceiveLoopAsync(statePtr, slotIdx, handle, ws, cts.Token).ConfigureAwait(false);

            WebSocketSystem.MarkCloseRequested(statePtr, slotIdx);
            try { sendWake.Set(); } catch { }
            await sendTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (WebSocketSystem.IsActive(statePtr))
            {
                WebSocketSystem.SetConnState(statePtr, slotIdx, WebSocketStateKind.Closed);
                FixedString256 info = FixedString256.From("Connection closed.");
                WebSocketSystem.PushEventFromWorker(statePtr, handle, WebSocketEventKind.Closed, closeStatus: 1000, info: in info);
            }
        }
        catch (Exception ex)
        {
            WebSocketSystem.SetConnState(statePtr, slotIdx, WebSocketStateKind.Faulted);
            if (WebSocketSystem.IsActive(statePtr))
            {
                FixedString256 info = FixedString256.From(ex.Message);
                WebSocketSystem.PushEventFromWorker(statePtr, handle, WebSocketEventKind.Error, info: in info);
            }
        }
        finally
        {
            WebSocketSystem.FinishConnectionFromWorker(statePtr, slotIdx);
        }
    }

    private static async Task ReceiveLoopAsync(
        nint statePtr,
        int slotIdx,
        WebSocketHandle handle,
        ClientWebSocket ws,
        CancellationToken ct)
    {
        int maxMsgBytes = WebSocketSystem.GetMaxMessageBytes(statePtr);
        byte[] recvBuf = ArrayPool<byte>.Shared.Rent(16384);

        nint msgPtr = nint.Zero;
        int msgLen = 0;
        nuint msgCap = 0;

        try
        {
            while (!ct.IsCancellationRequested && WebSocketSystem.IsActive(statePtr) &&
                   (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent))
            {
                ValueWebSocketReceiveResult res = await ws.ReceiveAsync(recvBuf.AsMemory(), ct).ConfigureAwait(false);

                if (res.MessageType == WebSocketMessageType.Close)
                {
                    FreeNative(ref msgPtr, ref msgCap);
                    msgLen = 0;

                    ushort closeCode = (ushort)(ws.CloseStatus ?? WebSocketCloseStatus.NormalClosure);
                    string desc = ws.CloseStatusDescription ?? string.Empty;

                    if (ws.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct).ConfigureAwait(false);
                        }
                        catch { }
                    }

                    WebSocketSystem.SetConnState(statePtr, slotIdx, WebSocketStateKind.Closed);
                    FixedString256 info = FixedString256.From(desc);
                    WebSocketSystem.PushEventFromWorker(statePtr, handle, WebSocketEventKind.Closed, closeStatus: closeCode, info: in info);
                    return;
                }

                if (res.Count > 0)
                {
                    if (msgLen + res.Count > maxMsgBytes)
                    {
                        FreeNative(ref msgPtr, ref msgCap);
                        msgLen = 0;
                        throw new InvalidOperationException("WebSocket message exceeded MaxMessageBytes limit.");
                    }

                    if ((nuint)(msgLen + res.Count) > msgCap)
                    {
                        nuint newCap = msgCap == 0 ? (nuint)res.Count : msgCap * 2u;
                        while (newCap < (nuint)(msgLen + res.Count)) newCap *= 2u;
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

                    WebSocketSystem.PushEventFromWorker(statePtr, handle, evKind, dataPtr: outPtr, dataLen: outLen, dataCap: outCap);
                }
            }
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
