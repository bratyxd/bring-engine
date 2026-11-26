using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;

namespace BringEngine.Networking;

public enum HttpMethodKind : byte
{
    Get = 0,
    Post = 1,
    Put = 2,
    Delete = 3,
    Patch = 4,
    Head = 5,
}

public enum HttpRequestStatus : byte
{
    Pending = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
    TimedOut = 4,
}

/// <summary>
/// Unmanaged HTTP header key/value pair (zero GC on caller side).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct HttpHeader
{
    public FixedString64 Name;
    public FixedString256 Value;

    public static HttpHeader Create(ReadOnlySpan<char> name, ReadOnlySpan<char> value) => new HttpHeader
    {
        Name = FixedString64.From(name),
        Value = FixedString256.From(value),
    };
}

/// <summary>
/// Completed HTTP response delivered by HttpSystem.PollCompleted.
/// Caller MUST call HttpSystem.ReleaseResponse(ref response) when finished so BodyPtr is freed with zero leaks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HttpResponse
{
    public HttpRequestHandle Handle;
    public ulong UserTag;
    public int StatusCode;
    public int BodyLength;
    public byte* BodyPtr;
    public nuint BodyAllocSize;
    public HttpRequestStatus Status;
    public FixedString256 Error;

    public readonly bool IsSuccess => Status == HttpRequestStatus.Completed && StatusCode >= 200 && StatusCode <= 299;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> AsBodySpan()
        => BodyPtr == null || BodyLength <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(BodyPtr, BodyLength);

    public readonly string BodyAsUtf8String()
        => BodyPtr == null || BodyLength <= 0 ? string.Empty : Encoding.UTF8.GetString(BodyPtr, BodyLength);
}

[StructLayout(LayoutKind.Sequential)]
internal struct HttpSlotMeta
{
    public uint Generation;
    public int State; // 0 = Free, 1 = Pending, 2 = QueuedForPoll
}

/// <summary>
/// Unmanaged HTTP client state allocated in stable native memory (e.g., EngineAllocators.Persistent).
/// Managed SocketsHttpHandler/HttpClient instances live in an internal slot table; all per-frame polling
/// reads directly from native memory with 0 GC allocations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HttpClientState
{
    public const int DefaultMaxConcurrent = 64;
    public const int DefaultTimeoutMs = 15000;
    public const int DefaultMaxResponseBytes = 16 * 1024 * 1024;

    internal HttpSlotMeta* Slots;
    internal HttpResponse* CompletedQueue;
    internal int CompletedMask;
    internal int CompletedHead;
    internal int CompletedTail;
    internal CachePaddedInt QueueLock;
    internal CachePaddedInt SlotLock;

    public int MaxConcurrent;
    public int DefaultTimeoutMsValue;
    public int PoolSlot;
    public byte Active;

    public CachePaddedInt PendingCount;
    public CachePaddedLong TotalRequestsSent;
    public CachePaddedLong TotalResponsesCompleted;
    public CachePaddedLong TotalBytesSent;
    public CachePaddedLong TotalBytesReceived;
}

/// <summary>
/// Static zero-OOP asynchronous HTTP/HTTPS client system backed by .NET 10 SocketsHttpHandler.
/// Background I/O runs off the Logic/Render threads; responses are copied into tracked native memory and
/// drained via zero-GC HttpSystem.PollCompleted on the Logic Thread.
/// </summary>
public static unsafe class HttpSystem
{
    private const int MaxInstances = 8;

    private struct ManagedRequestSlot
    {
        public CancellationTokenSource? Cts;
        public Task? InFlightTask;
    }

    private struct ManagedHttpPool
    {
        public HttpClient? Client;
        public ManagedRequestSlot[]? Requests;
        public bool InUse;
    }

    private static readonly ManagedHttpPool[] s_pools = new ManagedHttpPool[MaxInstances];
    private static readonly object s_poolLock = new();

    /// <summary>
    /// Initializes an HttpClientState inside stable pinned native memory (e.g., EngineAllocators.Persistent).
    /// </summary>
    public static void Start(
        HttpClientState* state,
        ref LinearArena persistentArena,
        int maxConcurrent = HttpClientState.DefaultMaxConcurrent,
        int defaultTimeoutMs = HttpClientState.DefaultTimeoutMs)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (maxConcurrent < 4) maxConcurrent = 4;
        if (defaultTimeoutMs <= 0) defaultTimeoutMs = HttpClientState.DefaultTimeoutMs;

        int qCap = 8;
        while (qCap < maxConcurrent * 2) qCap <<= 1;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // Per-request CTS controls exact timeouts
        };

        int poolSlot = -1;
        lock (s_poolLock)
        {
            for (int i = 0; i < MaxInstances; i++)
            {
                if (!s_pools[i].InUse)
                {
                    s_pools[i].InUse = true;
                    s_pools[i].Client = client;
                    s_pools[i].Requests = new ManagedRequestSlot[maxConcurrent];
                    poolSlot = i;
                    break;
                }
            }
        }
        if (poolSlot < 0)
        {
            client.Dispose();
            throw new InvalidOperationException("Maximum concurrent HttpClientState instances exceeded.");
        }

        *state = default;
        state->Slots = persistentArena.Alloc<HttpSlotMeta>(maxConcurrent, 64);
        for (int i = 0; i < maxConcurrent; i++)
        {
            state->Slots[i].Generation = 1;
            state->Slots[i].State = 0;
        }
        state->CompletedQueue = persistentArena.Alloc<HttpResponse>(qCap, 64);
        state->CompletedMask = qCap - 1;
        state->MaxConcurrent = maxConcurrent;
        state->DefaultTimeoutMsValue = defaultTimeoutMs;
        state->PoolSlot = poolSlot;
        state->Active = 1;
    }

    /// <summary>
    /// Cancels all in-flight HTTP requests, waits for background tasks to drain safely, disposes the pooled
    /// HttpClient, and frees any unpolled native response buffers so NativeMemoryTracker.LiveBytes == 0.
    /// </summary>
    public static void Stop(HttpClientState* state)
    {
        if (state == null || state->Active == 0) return;
        int poolSlot = state->PoolSlot;

        state->Active = 0;
        Thread.MemoryBarrier();

        ManagedRequestSlot[]? reqs = s_pools[poolSlot].Requests;
        if (reqs != null)
        {
            for (int i = 0; i < reqs.Length; i++)
            {
                try { reqs[i].Cts?.Cancel(); } catch { /* ignore shutdown races */ }
            }
            for (int i = 0; i < reqs.Length; i++)
            {
                Task? t = reqs[i].InFlightTask;
                if (t != null)
                {
                    try { t.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore task cancellation */ }
                }
                reqs[i].Cts?.Dispose();
                reqs[i] = default;
            }
        }

        // Free any response buffers still sitting unpolled in the native completion ring buffer
        while (TryDequeueCompletedRaw(state, out HttpResponse leftover))
            ReleaseResponse(ref leftover);

        s_pools[poolSlot].Client?.Dispose();
        lock (s_poolLock)
        {
            s_pools[poolSlot] = default;
        }
    }

    public static HttpRequestHandle Get(
        HttpClientState* state,
        ReadOnlySpan<char> url,
        ReadOnlySpan<HttpHeader> headers = default,
        int timeoutMs = 0,
        int maxResponseBytes = HttpClientState.DefaultMaxResponseBytes,
        ulong userTag = 0)
        => Send(state, HttpMethodKind.Get, url, default, default, headers, timeoutMs, maxResponseBytes, userTag);

    public static HttpRequestHandle PostJson(
        HttpClientState* state,
        ReadOnlySpan<char> url,
        ReadOnlySpan<byte> utf8Json,
        ReadOnlySpan<HttpHeader> headers = default,
        int timeoutMs = 0,
        int maxResponseBytes = HttpClientState.DefaultMaxResponseBytes,
        ulong userTag = 0)
        => Send(state, HttpMethodKind.Post, url, utf8Json, "application/json", headers, timeoutMs, maxResponseBytes, userTag);

    /// <summary>
    /// Dispatches an asynchronous HTTP/HTTPS request on background I/O threads and immediately returns a generational handle.
    /// Poll completion on the Logic Thread with HttpSystem.PollCompleted.
    /// </summary>
    public static HttpRequestHandle Send(
        HttpClientState* state,
        HttpMethodKind method,
        ReadOnlySpan<char> url,
        ReadOnlySpan<byte> body = default,
        ReadOnlySpan<char> contentType = default,
        ReadOnlySpan<HttpHeader> headers = default,
        int timeoutMs = 0,
        int maxResponseBytes = HttpClientState.DefaultMaxResponseBytes,
        ulong userTag = 0)
    {
        if (state == null || state->Active == 0 || url.IsEmpty)
            return HttpRequestHandle.Invalid;

        int slotIdx = -1;
        uint gen = 0;

        AcquireSpinLock(ref state->SlotLock.Value);
        for (int i = 0; i < state->MaxConcurrent; i++)
        {
            if (state->Slots[i].State == 0)
            {
                state->Slots[i].State = 1;
                gen = state->Slots[i].Generation;
                slotIdx = i;
                break;
            }
        }
        ReleaseSpinLock(ref state->SlotLock.Value);

        if (slotIdx < 0)
            return HttpRequestHandle.Invalid;

        HttpRequestHandle handle = HttpRequestHandle.Create((uint)slotIdx, gen);
        int effTimeout = timeoutMs > 0 ? timeoutMs : state->DefaultTimeoutMsValue;
        if (maxResponseBytes <= 0) maxResponseBytes = HttpClientState.DefaultMaxResponseBytes;

        HttpRequestMessage msg;
        try
        {
            var uri = new Uri(new string(url), UriKind.Absolute);
            msg = new HttpRequestMessage(ToHttpMethod(method), uri);

            if (!body.IsEmpty)
            {
                byte[] copy = body.ToArray();
                var content = new ByteArrayContent(copy);
                if (!contentType.IsEmpty)
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(new string(contentType));
                msg.Content = content;
                Interlocked.Add(ref state->TotalBytesSent.Value, copy.Length);
            }

            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i].Name.Length <= 0) continue;
                string k = headers[i].Name.ToString();
                string v = headers[i].Value.ToString();
                if (!msg.Headers.TryAddWithoutValidation(k, v) && msg.Content != null)
                    msg.Content.Headers.TryAddWithoutValidation(k, v);
            }
        }
        catch (Exception ex)
        {
            var fail = new HttpResponse
            {
                Handle = handle,
                UserTag = userTag,
                Status = HttpRequestStatus.Failed,
                Error = FixedString256.From(ex.Message),
            };
            EnqueueCompleted(state, slotIdx, in fail);
            return handle;
        }

        var cts = new CancellationTokenSource();
        int poolSlot = state->PoolSlot;
        ManagedRequestSlot[] reqs = s_pools[poolSlot].Requests!;
        reqs[slotIdx].Cts = cts;

        Interlocked.Increment(ref state->PendingCount.Value);
        Interlocked.Increment(ref state->TotalRequestsSent.Value);

        nint statePtr = (nint)state;
        HttpClient client = s_pools[poolSlot].Client!;
        reqs[slotIdx].InFlightTask = Task.Run(() => HttpAsyncWorker.ExecuteRequestAsync(
            statePtr, client, msg, cts, slotIdx, handle, userTag, effTimeout, maxResponseBytes));

        return handle;
    }

    /// <summary>
    /// Requests cancellation of an in-flight HTTP request. Non-blocking.
    /// </summary>
    public static void Cancel(HttpClientState* state, HttpRequestHandle handle)
    {
        if (state == null || state->Active == 0 || !handle.IsValid) return;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxConcurrent) return;

        if (Volatile.Read(ref state->Slots[idx].Generation) != handle.Generation) return;
        if (Volatile.Read(ref state->Slots[idx].State) != 1) return;

        CancellationTokenSource? cts = s_pools[state->PoolSlot].Requests?[idx].Cts;
        try { cts?.Cancel(); } catch { /* ignore already disposed */ }
    }

    /// <summary>
    /// Non-blocking zero-GC check whether a request handle is still pending I/O.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPending(HttpClientState* state, HttpRequestHandle handle)
    {
        if (state == null || !handle.IsValid) return false;
        uint idx = handle.Index;
        if (idx >= (uint)state->MaxConcurrent) return false;
        if (Volatile.Read(ref state->Slots[idx].Generation) != handle.Generation) return false;
        return Volatile.Read(ref state->Slots[idx].State) == 1;
    }

    /// <summary>
    /// Pops the next completed HTTP response from the native completion ring buffer.
    /// Allocates 0 GC bytes on the calling thread. Caller MUST call ReleaseResponse(ref response).
    /// </summary>
    public static bool PollCompleted(HttpClientState* state, out HttpResponse response)
    {
        if (state == null)
        {
            response = default;
            return false;
        }
        if (!TryDequeueCompletedRaw(state, out response))
            return false;

        // Release request slot & dispose completed CTS cleanly now that caller owns the response (0 GC)
        uint idx = response.Handle.Index;
        if (idx < (uint)state->MaxConcurrent)
        {
            ManagedRequestSlot[]? reqs = s_pools[state->PoolSlot].Requests;
            if (reqs != null)
            {
                reqs[idx].Cts?.Dispose();
                reqs[idx] = default;
            }

            AcquireSpinLock(ref state->SlotLock.Value);
            uint nextGen = (state->Slots[idx].Generation + 1) & HandlePacking.GenerationMask;
            if (nextGen == 0) nextGen = 1;
            Volatile.Write(ref state->Slots[idx].Generation, nextGen);
            Volatile.Write(ref state->Slots[idx].State, 0);
            ReleaseSpinLock(ref state->SlotLock.Value);
        }

        return true;
    }

    /// <summary>
    /// Frees the native body buffer owned by an HttpResponse. Zero GC, idempotent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReleaseResponse(ref HttpResponse response)
    {
        if (response.BodyPtr != null && response.BodyAllocSize > 0)
            NativeMemoryTracker.AlignedFree(response.BodyPtr, response.BodyAllocSize);
        response.BodyPtr = null;
        response.BodyLength = 0;
        response.BodyAllocSize = 0;
    }

    // =========================================================================
    // INTERNAL NATIVE RING BUFFER & WORKER HOOKS
    // =========================================================================

    internal static void CompleteRequestFromWorker(
        nint statePtr,
        int slotIdx,
        HttpRequestHandle handle,
        ulong userTag,
        HttpRequestStatus status,
        int statusCode,
        nint bodyPtr,
        int bodyLen,
        nuint bodyCap,
        in FixedString256 error)
    {
        HttpClientState* state = (HttpClientState*)statePtr;
        if (bodyLen > 0)
            Interlocked.Add(ref state->TotalBytesReceived.Value, bodyLen);
        Interlocked.Decrement(ref state->PendingCount.Value);
        Interlocked.Increment(ref state->TotalResponsesCompleted.Value);

        var resp = new HttpResponse
        {
            Handle = handle,
            UserTag = userTag,
            Status = status,
            StatusCode = statusCode,
            BodyPtr = (byte*)bodyPtr,
            BodyLength = bodyLen,
            BodyAllocSize = bodyCap,
            Error = error,
        };
        EnqueueCompleted(state, slotIdx, in resp);
    }

    private static void EnqueueCompleted(HttpClientState* state, int slotIdx, in HttpResponse resp)
    {
        Volatile.Write(ref state->Slots[slotIdx].State, 2);

        AcquireSpinLock(ref state->QueueLock.Value);
        int tail = state->CompletedTail;
        state->CompletedQueue[tail & state->CompletedMask] = resp;
        state->CompletedTail = tail + 1;
        ReleaseSpinLock(ref state->QueueLock.Value);
    }

    private static bool TryDequeueCompletedRaw(HttpClientState* state, out HttpResponse resp)
    {
        AcquireSpinLock(ref state->QueueLock.Value);
        int head = state->CompletedHead;
        if (head == state->CompletedTail)
        {
            ReleaseSpinLock(ref state->QueueLock.Value);
            resp = default;
            return false;
        }
        resp = state->CompletedQueue[head & state->CompletedMask];
        state->CompletedHead = head + 1;
        ReleaseSpinLock(ref state->QueueLock.Value);
        return true;
    }

    private static HttpMethod ToHttpMethod(HttpMethodKind kind) => kind switch
    {
        HttpMethodKind.Get => HttpMethod.Get,
        HttpMethodKind.Post => HttpMethod.Post,
        HttpMethodKind.Put => HttpMethod.Put,
        HttpMethodKind.Delete => HttpMethod.Delete,
        HttpMethodKind.Patch => HttpMethod.Patch,
        HttpMethodKind.Head => HttpMethod.Head,
        _ => HttpMethod.Get,
    };

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

file static class HttpAsyncWorker
{
    public static async Task ExecuteRequestAsync(
        nint statePtr,
        HttpClient client,
        HttpRequestMessage msg,
        CancellationTokenSource userCts,
        int slotIdx,
        HttpRequestHandle handle,
        ulong userTag,
        int timeoutMs,
        int maxResponseBytes)
    {
        HttpRequestStatus status = HttpRequestStatus.Failed;
        int statusCode = 0;
        nint bodyPtr = nint.Zero;
        int bodyLen = 0;
        nuint bodyCap = 0;
        FixedString256 error = default;

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, timeoutCts.Token);

        try
        {
            using (msg)
            using (HttpResponseMessage httpResp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false))
            {
                statusCode = (int)httpResp.StatusCode;
                await using Stream stream = await httpResp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);

                long? contentLen = httpResp.Content.Headers.ContentLength;
                if (contentLen.HasValue && contentLen.Value > maxResponseBytes)
                {
                    status = HttpRequestStatus.Failed;
                    error = FixedString256.From("HTTP response exceeded MaxResponseBytes limit.");
                }
                else
                {
                    byte[] chunk = ArrayPool<byte>.Shared.Rent(16384);
                    try
                    {
                        if (contentLen.HasValue && contentLen.Value > 0)
                        {
                            bodyCap = (nuint)contentLen.Value;
                            bodyPtr = AllocNative(bodyCap);
                        }

                        bool exceeded = false;
                        while (true)
                        {
                            int n = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), linkedCts.Token).ConfigureAwait(false);
                            if (n <= 0) break;

                            if (bodyLen + n > maxResponseBytes)
                            {
                                FreeNative(ref bodyPtr, ref bodyCap);
                                bodyLen = 0;
                                status = HttpRequestStatus.Failed;
                                error = FixedString256.From("HTTP response exceeded MaxResponseBytes limit.");
                                exceeded = true;
                                break;
                            }

                            if ((nuint)(bodyLen + n) > bodyCap)
                            {
                                nuint newCap = bodyCap == 0 ? 4096u : bodyCap * 2u;
                                while (newCap < (nuint)(bodyLen + n)) newCap *= 2u;
                                if (newCap > (nuint)maxResponseBytes) newCap = (nuint)maxResponseBytes;
                                bodyPtr = GrowNative(bodyPtr, bodyLen, bodyCap, newCap);
                                bodyCap = newCap;
                            }

                            CopyToNative(chunk, n, bodyPtr, bodyLen, bodyCap);
                            bodyLen += n;
                        }

                        if (!exceeded)
                            status = HttpRequestStatus.Completed;
                    }
                    catch
                    {
                        FreeNative(ref bodyPtr, ref bodyCap);
                        bodyLen = 0;
                        throw;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (userCts.IsCancellationRequested)
            {
                status = HttpRequestStatus.Cancelled;
                error = FixedString256.From("Request cancelled.");
            }
            else
            {
                status = HttpRequestStatus.TimedOut;
                error = FixedString256.From("Request timed out.");
            }
        }
        catch (Exception ex)
        {
            status = HttpRequestStatus.Failed;
            error = FixedString256.From(ex.Message);
        }

        HttpSystem.CompleteRequestFromWorker(
            statePtr, slotIdx, handle, userTag, status, statusCode, bodyPtr, bodyLen, bodyCap, in error);
    }

    private static unsafe nint AllocNative(nuint bytes)
        => (nint)NativeMemoryTracker.AlignedAlloc(bytes, 16);

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
