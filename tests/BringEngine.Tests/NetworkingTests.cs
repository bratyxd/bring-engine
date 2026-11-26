using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Networking;

namespace BringEngine.Tests;

public static class NetworkingTests
{
    public static unsafe void RunAll()
    {
        Console.WriteLine("=== Networking: NetSerialization, LoopbackTransport, HttpSystem & WebSocketSystem ===");
        Assert.Run("NetSerialization_PrimitivesFixedMathVarintsCrcAndZeroGc", &TestNetSerialization);
        Assert.Run("NetSerialization_EdgeCasesOverflowVarintAndCrc32", &TestNetSerializationEdgeCases);
        Assert.Run("NetTransport_Loopback_DeterministicLatencyLossOrderedAndZeroGc", &TestLoopbackTransport);
        Assert.Run("NetTransport_LoopbackEdgeCases_SequencedMixed_Saturation_DisconnectDrain", &TestLoopbackTransportEdgeCases);
        Assert.Run("HttpSystem_LoopbackGetPostHeadersCancelTimeoutAndZeroGcPoll", &TestHttpSystem);
        Assert.Run("HttpSystem_EdgeCases_SlotExhaustion_MaxBytes_Methods_Headers_AndStop", &TestHttpSystemEdgeCases);
        Assert.Run("WebSocketSystem_LoopbackTextBinaryFragmentedCloseAndZeroGcHotLoop", &TestWebSocketSystem);
        Assert.Run("WebSocketSystem_EdgeCases_Timeouts_CloseDuringSend_QueueOverflow_AndStop", &TestWebSocketSystemEdgeCases);
    }

    // =========================================================================
    // 1. NetWriter / NetReader / NetHash.Crc32
    // =========================================================================

    private static unsafe void TestNetSerialization()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

        try
        {
            // Standard IEEE 802.3 CRC32 test vector: "123456789" -> 0xCBF43926
            ReadOnlySpan<byte> crcVec = "123456789"u8;
            Assert.Equal(0xCBF43926u, NetHash.Crc32(crcVec), "IEEE 802.3 CRC32 vector");

            byte* buf = alloc.Persistent.Alloc<byte>(512, 64);
            var writer = NetWriter.Create(buf, 512);

            FixedString64 s64 = FixedString64.From("BringNetPlayer");
            FixedString256 s256 = FixedString256.From("https://example.invalid/match/room-77");
            EntityId eid = EntityId.Create(42, 7);
            StringId sid = StringId.Hash("net.packet.input");
            Fix32 f32 = Fix32.FromRatio(-1234, 100);
            Fix64 f64 = Fix64.FromRatio(987654321L, 1000L);
            FixVec2 fv2 = new FixVec2(Fix32.FromInt(5), Fix32.FromRatio(-3, 2));
            FixVec3 fv3 = new FixVec3(Fix32.FromInt(12), Fix32.FromInt(-4), Fix32.FromRatio(7, 8));
            FixQuat fq = FixQuat.FromAxisAngle(FixVec3.UnitY, Fix32.HalfPi);
            ReadOnlySpan<byte> payload = stackalloc byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };

            writer.WriteByte(0xAB);
            writer.WriteBool(true);
            writer.WriteInt16(-12345);
            writer.WriteUInt16(54321);
            writer.WriteInt32(-987654321);
            writer.WriteUInt32(0xCAFEBABEu);
            writer.WriteInt64(-1234567890123456789L);
            writer.WriteUInt64(0x0123456789ABCDEFUL);
            writer.WriteFloat(3.5f);
            writer.WriteDouble(-128.25);
            writer.WriteVarUInt32(0u);
            writer.WriteVarUInt32(127u);
            writer.WriteVarUInt32(16384u);
            writer.WriteVarUInt32(uint.MaxValue);
            writer.WriteVarInt32(0);
            writer.WriteVarInt32(-1);
            writer.WriteVarInt32(1);
            writer.WriteVarInt32(-50000);
            writer.WriteVarInt32(int.MinValue);
            writer.WriteFix32(f32);
            writer.WriteFix64(f64);
            writer.WriteFixVec2(in fv2);
            writer.WriteFixVec3(in fv3);
            writer.WriteFixQuat(in fq);
            writer.WriteEntityId(eid);
            writer.WriteStringId(sid);
            writer.WriteBytes(payload);
            writer.WriteFixedString64(in s64);
            writer.WriteFixedString256(in s256);

            Assert.True(!writer.HasOverflow, "Writer did not overflow");
            int written = writer.BytesWritten;
            Assert.True(written > 0 && written < 512, "Written length valid");

            var reader = NetReader.Create(buf, written);
            Assert.Equal((byte)0xAB, reader.ReadByte(), "Byte");
            Assert.Equal(true, reader.ReadBool(), "Bool");
            Assert.Equal((short)-12345, reader.ReadInt16(), "Int16");
            Assert.Equal((ushort)54321, reader.ReadUInt16(), "UInt16");
            Assert.Equal(-987654321, reader.ReadInt32(), "Int32");
            Assert.Equal(0xCAFEBABEu, reader.ReadUInt32(), "UInt32");
            Assert.Equal(-1234567890123456789L, reader.ReadInt64(), "Int64");
            Assert.Equal(0x0123456789ABCDEFUL, reader.ReadUInt64(), "UInt64");
            Assert.Equal(BitConverter.SingleToInt32Bits(3.5f), BitConverter.SingleToInt32Bits(reader.ReadFloat()), "Float");
            Assert.Equal(BitConverter.DoubleToInt64Bits(-128.25), BitConverter.DoubleToInt64Bits(reader.ReadDouble()), "Double");
            Assert.Equal(0u, reader.ReadVarUInt32(), "VarUInt32(0)");
            Assert.Equal(127u, reader.ReadVarUInt32(), "VarUInt32(127)");
            Assert.Equal(16384u, reader.ReadVarUInt32(), "VarUInt32(16384)");
            Assert.Equal(uint.MaxValue, reader.ReadVarUInt32(), "VarUInt32(max)");
            Assert.Equal(0, reader.ReadVarInt32(), "VarInt32(0)");
            Assert.Equal(-1, reader.ReadVarInt32(), "VarInt32(-1)");
            Assert.Equal(1, reader.ReadVarInt32(), "VarInt32(1)");
            Assert.Equal(-50000, reader.ReadVarInt32(), "VarInt32(-50000)");
            Assert.Equal(int.MinValue, reader.ReadVarInt32(), "VarInt32(min)");
            Assert.Equal(f32.Raw, reader.ReadFix32().Raw, "Fix32");
            Assert.Equal(f64.Raw, reader.ReadFix64().Raw, "Fix64");
            FixVec2 rFv2 = reader.ReadFixVec2();
            Assert.True(rFv2.Equals(fv2), "FixVec2");
            FixVec3 rFv3 = reader.ReadFixVec3();
            Assert.True(rFv3.Equals(fv3), "FixVec3");
            FixQuat rFq = reader.ReadFixQuat();
            Assert.True(rFq.Equals(fq), "FixQuat");
            Assert.Equal(eid.Raw, reader.ReadEntityId().Raw, "EntityId");
            Assert.Equal(sid.Value, reader.ReadStringId().Value, "StringId");
            ReadOnlySpan<byte> rPayload = reader.ReadBytes();
            Assert.Equal(payload.Length, rPayload.Length, "Payload length");
            for (int i = 0; i < payload.Length; i++)
                Assert.Equal(payload[i], rPayload[i], $"Payload[{i}]");
            Assert.True(reader.ReadFixedString64().Equals(s64), "FixedString64");
            FixedString256 rS256 = reader.ReadFixedString256();
            Assert.Equal(s256.Length, rS256.Length, "FixedString256 length");
            Assert.True(rS256.AsUtf8Span().SequenceEqual(s256.AsUtf8Span()), "FixedString256 bytes");
            Assert.True(!reader.HasOverflow && reader.Remaining == 0, "Reader consumed exact byte count");

            // Overflow safety on writer & reader (malformed/truncated packet)
            byte* tiny = stackalloc byte[3];
            var smallW = NetWriter.Create(tiny, 3);
            smallW.WriteInt32(123);
            Assert.True(smallW.HasOverflow, "Writer flags overflow without throwing");

            var truncR = NetReader.Create(buf, 2);
            _ = truncR.ReadInt32();
            Assert.True(truncR.HasOverflow, "Reader flags overflow on truncated buffer");

            // Zero-GC hot loop verification
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            uint acc = 0;
            for (int i = 0; i < 1000; i++)
            {
                writer.Reset();
                writer.WriteVarUInt32((uint)i);
                writer.WriteFixVec3(in fv3);
                writer.WriteEntityId(eid);
                acc ^= NetHash.Crc32(writer.WrittenSpan());

                var r = NetReader.Create(buf, writer.BytesWritten);
                acc ^= r.ReadVarUInt32();
                acc ^= (uint)r.ReadFixVec3().X.Raw;
                acc ^= r.ReadEntityId().Raw;
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(acc != 0 || acc == 0, "Prevent DCE");
            Assert.Equal(0L, gcAfter - gcBefore, "NetWriter/NetReader/Crc32 hot loop allocates 0 GC bytes");
        }
        finally
        {
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after NetSerialization test");
    }

    // =========================================================================
    // 2. NetTransportVTable + LoopbackTransportSystem
    // =========================================================================

    private static unsafe void TestLoopbackTransport()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

        try
        {
            var cfg = new LoopbackSimConfig
            {
                OneWayLatencyTicks = 3,
                JitterTicks = 3,
                PacketLossPermille = 300, // 30% unreliable drop
                RngSeed = 0x51A7_BEEFUL,
            };

            LoopbackLink* link1 = alloc.Persistent.Alloc<LoopbackLink>(1, 64);
            LoopbackLink* link2 = alloc.Persistent.Alloc<LoopbackLink>(1, 64);
            LoopbackTransportSystem.Init(link1, ref alloc.Persistent, in cfg, queueCapacity: 256, maxPacketBytes: 256);
            LoopbackTransportSystem.Init(link2, ref alloc.Persistent, in cfg, queueCapacity: 256, maxPacketBytes: 256);

            NetTransportVTable a1 = LoopbackTransportSystem.GetVTable(&link1->PeerA);
            NetTransportVTable b1 = LoopbackTransportSystem.GetVTable(&link1->PeerB);
            NetTransportVTable a2 = LoopbackTransportSystem.GetVTable(&link2->PeerA);
            NetTransportVTable b2 = LoopbackTransportSystem.GetVTable(&link2->PeerB);

            // Initial PeerConnected events
            Assert.True(a1.TryPoll(out NetTransportEvent eA) && eA.Kind == NetTransportEventKind.PeerConnected, "PeerA connected");
            Assert.True(b1.TryPoll(out NetTransportEvent eB) && eB.Kind == NetTransportEventKind.PeerConnected, "PeerB connected");
            Assert.True(a2.TryPoll(out _) && b2.TryPoll(out _), "Run 2 connected");

            byte* scratch = alloc.Persistent.Alloc<byte>(64, 16);
            int relCount1 = 0, unrelCount1 = 0, lastRelId1 = -1;
            int relCount2 = 0, unrelCount2 = 0;

            // Warmup before zero-GC measurement
            for (int tick = 0; tick < 5; tick++)
            {
                LoopbackTransportSystem.AdvanceTick(link1);
                LoopbackTransportSystem.AdvanceTick(link2);
            }

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int tick = 0; tick < 60; tick++)
            {
                if (tick < 40)
                {
                    var w = NetWriter.Create(scratch, 64);
                    w.WriteByte(1); // ReliableOrdered tag
                    w.WriteInt32(tick);
                    Assert.True(a1.TrySend(1, w.WrittenSpan(), NetDeliveryMode.ReliableOrdered), "Send reliable 1");
                    Assert.True(a2.TrySend(1, w.WrittenSpan(), NetDeliveryMode.ReliableOrdered), "Send reliable 2");

                    w.Reset();
                    w.WriteByte(2); // Unreliable tag
                    w.WriteInt32(tick);
                    Assert.True(a1.TrySend(1, w.WrittenSpan(), NetDeliveryMode.Unreliable), "Send unreliable 1");
                    Assert.True(a2.TrySend(1, w.WrittenSpan(), NetDeliveryMode.Unreliable), "Send unreliable 2");
                }

                LoopbackTransportSystem.AdvanceTick(link1);
                LoopbackTransportSystem.AdvanceTick(link2);

                while (b1.TryPoll(out NetTransportEvent ev))
                {
                    Assert.Equal((byte)NetTransportEventKind.PacketReceived, (byte)ev.Kind, "PacketReceived 1");
                    var r = NetReader.Create(ev.PayloadPtr, ev.PayloadLength);
                    byte tag = r.ReadByte();
                    int seq = r.ReadInt32();
                    if (tag == 1)
                    {
                        Assert.True(seq > lastRelId1, "ReliableOrdered strictly ordered");
                        lastRelId1 = seq;
                        relCount1++;
                    }
                    else if (tag == 2)
                    {
                        unrelCount1++;
                    }
                }

                while (b2.TryPoll(out NetTransportEvent ev))
                {
                    var r = NetReader.Create(ev.PayloadPtr, ev.PayloadLength);
                    byte tag = r.ReadByte();
                    _ = r.ReadInt32();
                    if (tag == 1) relCount2++;
                    else if (tag == 2) unrelCount2++;
                }
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, gcAfter - gcBefore, "LoopbackTransport Send/Poll hot loop allocates 0 GC bytes");
            Assert.Equal(40, relCount1, "All 40 ReliableOrdered packets delivered without loss");
            Assert.Equal(relCount1, relCount2, "Deterministic reliable count");
            Assert.True(unrelCount1 > 0 && unrelCount1 < 40, $"Unreliable loss applied ({unrelCount1}/40 arrived)");
            Assert.Equal(unrelCount1, unrelCount2, "Bit-exact deterministic unreliable drop count across same DetRng seed");

            // Disconnect propagates to both endpoints
            a1.DisconnectPeer(1, reason: 77);
            Assert.True(a1.TryPoll(out NetTransportEvent dA) && dA.Kind == NetTransportEventKind.PeerDisconnected && dA.DisconnectReason == 77, "PeerA disconnected");
            Assert.True(b1.TryPoll(out NetTransportEvent dB) && dB.Kind == NetTransportEventKind.PeerDisconnected && dB.DisconnectReason == 77, "PeerB disconnected");
            Assert.Equal(0, a1.QueryStats().ConnectedPeers, "Stats reflect disconnect");
        }
        finally
        {
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after LoopbackTransport test");
    }

    // =========================================================================
    // 3. HttpSystem (In-process cross-platform TcpListener HTTP/1.1 loopback)
    // =========================================================================

    private static unsafe void TestHttpSystem()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

        using var serverCts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = Task.Run(() => RunTestHttpServerAsync(listener, serverCts.Token));

        HttpClientState* http = alloc.Persistent.Alloc<HttpClientState>(1, 64);
        HttpSystem.Start(http, ref alloc.Persistent, maxConcurrent: 16, defaultTimeoutMs: 5000);

        try
        {
            string baseUrl = $"http://127.0.0.1:{port}";

            // 1. GET /hello
            HttpRequestHandle hGet = HttpSystem.Get(http, $"{baseUrl}/hello", userTag: 1001UL);
            Assert.True(hGet.IsValid, "GET handle valid");
            HttpResponse rGet = WaitHttpResponse(http, hGet);
            try
            {
                Assert.True(rGet.IsSuccess, $"GET succeeded (status={rGet.Status}, code={rGet.StatusCode}, err={rGet.Error})");
                Assert.Equal(200, rGet.StatusCode, "GET 200");
                Assert.Equal(1001UL, rGet.UserTag, "GET UserTag preserved");
                Assert.Equal("hello bring", rGet.BodyAsUtf8String(), "GET body");
            }
            finally
            {
                HttpSystem.ReleaseResponse(ref rGet);
            }

            // 2. POST /echo with custom header
            HttpHeader* hdrs = stackalloc HttpHeader[1];
            hdrs[0] = HttpHeader.Create("X-Bring-Token", "secret-42");
            ReadOnlySpan<byte> postBody = "{\"score\":9001}"u8;
            HttpRequestHandle hPost = HttpSystem.PostJson(http, $"{baseUrl}/echo", postBody, new ReadOnlySpan<HttpHeader>(hdrs, 1), userTag: 2002UL);
            HttpResponse rPost = WaitHttpResponse(http, hPost);
            try
            {
                Assert.True(rPost.IsSuccess, $"POST succeeded (code={rPost.StatusCode})");
                Assert.Equal(200, rPost.StatusCode, "POST 200");
                Assert.Equal("{\"score\":9001}", rPost.BodyAsUtf8String(), "POST echoed payload");
            }
            finally
            {
                HttpSystem.ReleaseResponse(ref rPost);
            }

            // 3. 404 Not Found
            HttpRequestHandle h404 = HttpSystem.Get(http, $"{baseUrl}/missing");
            HttpResponse r404 = WaitHttpResponse(http, h404);
            try
            {
                Assert.Equal((byte)HttpRequestStatus.Completed, (byte)r404.Status, "404 completed");
                Assert.Equal(404, r404.StatusCode, "404 status code");
                Assert.True(!r404.IsSuccess, "404 IsSuccess == false");
            }
            finally
            {
                HttpSystem.ReleaseResponse(ref r404);
            }

            // 4. Timeout
            HttpRequestHandle hTimeout = HttpSystem.Get(http, $"{baseUrl}/slow", timeoutMs: 120);
            HttpResponse rTimeout = WaitHttpResponse(http, hTimeout);
            try
            {
                Assert.Equal((byte)HttpRequestStatus.TimedOut, (byte)rTimeout.Status, "Slow request timed out");
            }
            finally
            {
                HttpSystem.ReleaseResponse(ref rTimeout);
            }

            // 5. Explicit Cancel
            HttpRequestHandle hCancel = HttpSystem.Get(http, $"{baseUrl}/slow", timeoutMs: 5000);
            HttpSystem.Cancel(http, hCancel);
            HttpResponse rCancel = WaitHttpResponse(http, hCancel);
            try
            {
                Assert.Equal((byte)HttpRequestStatus.Cancelled, (byte)rCancel.Status, "Cancelled status");
            }
            finally
            {
                HttpSystem.ReleaseResponse(ref rCancel);
            }

            // 6. Zero-GC PollCompleted + IsPending + ReleaseResponse verification
            HttpRequestHandle hZero = HttpSystem.Get(http, $"{baseUrl}/hello");
            Stopwatch sw = Stopwatch.StartNew();
            while (HttpSystem.IsPending(http, hZero) && sw.ElapsedMilliseconds < 5000)
                Thread.Sleep(2);

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            bool got = HttpSystem.PollCompleted(http, out HttpResponse rZero);
            bool empty = HttpSystem.PollCompleted(http, out _);
            bool stillPending = HttpSystem.IsPending(http, hZero);
            HttpSystem.ReleaseResponse(ref rZero);
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(got && !empty && !stillPending, "Polled completed response cleanly");
            Assert.Equal(0L, gcAfter - gcBefore, "HttpSystem PollCompleted/IsPending/ReleaseResponse allocates 0 GC bytes");

            // 7. Intentionally leave one completed response unpolled to verify Stop() frees it with 0 leaks
            HttpRequestHandle hUnpolled = HttpSystem.Get(http, $"{baseUrl}/hello");
            sw.Restart();
            while (HttpSystem.IsPending(http, hUnpolled) && sw.ElapsedMilliseconds < 5000)
                Thread.Sleep(2);
        }
        finally
        {
            HttpSystem.Stop(http);
            serverCts.Cancel();
            listener.Stop();
            try { serverTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after HttpSystem test (incl. unpolled response)");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live native blocks after HttpSystem test");
    }

    private static unsafe HttpResponse WaitHttpResponse(HttpClientState* http, HttpRequestHandle handle, int timeoutMs = 5000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (HttpSystem.PollCompleted(http, out HttpResponse r))
            {
                if (r.Handle == handle)
                    return r;
                HttpSystem.ReleaseResponse(ref r);
            }
            Thread.Sleep(2);
        }
        throw new TimeoutException($"Timed out waiting for HTTP response {handle}");
    }

    private static async Task RunTestHttpServerAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    try
                    {
                        string headersText = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(headersText)) return;

                        string[] lines = headersText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length == 0) return;
                        string[] reqParts = lines[0].Split(' ');
                        string method = reqParts.Length > 0 ? reqParts[0] : "GET";
                        string path = reqParts.Length > 1 ? reqParts[1] : "/";

                        int contentLength = 0;
                        bool hasToken = false;
                        for (int i = 1; i < lines.Length; i++)
                        {
                            int colon = lines[i].IndexOf(':');
                            if (colon <= 0) continue;
                            string k = lines[i].Substring(0, colon).Trim();
                            string v = lines[i].Substring(colon + 1).Trim();
                            if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(v, out contentLength);
                            else if (k.Equals("X-Bring-Token", StringComparison.OrdinalIgnoreCase) && v == "secret-42")
                                hasToken = true;
                        }

                        byte[] bodyBytes = contentLength > 0 ? new byte[contentLength] : Array.Empty<byte>();
                        int read = 0;
                        while (read < contentLength)
                        {
                            int n = await stream.ReadAsync(bodyBytes.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
                            if (n <= 0) break;
                            read += n;
                        }

                        if (path == "/hello")
                        {
                            await WriteHttpResponseAsync(stream, 200, "OK", "hello bring"u8.ToArray(), ct).ConfigureAwait(false);
                        }
                        else if (path == "/echo" && method == "POST")
                        {
                            if (!hasToken)
                                await WriteHttpResponseAsync(stream, 401, "Unauthorized", "missing header"u8.ToArray(), ct).ConfigureAwait(false);
                            else
                                await WriteHttpResponseAsync(stream, 200, "OK", bodyBytes, ct).ConfigureAwait(false);
                        }
                        else if (path == "/slow")
                        {
                            await Task.Delay(3000, ct).ConfigureAwait(false);
                            await WriteHttpResponseAsync(stream, 200, "OK", "late"u8.ToArray(), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteHttpResponseAsync(stream, 404, "Not Found", "not found"u8.ToArray(), ct).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // Client cancelled/disconnected early (expected in cancel/timeout tests)
                    }
                }
            }, ct);
        }
    }

    private static async Task<string> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var ms = new MemoryStream();
        byte[] one = new byte[1];
        int match = 0;
        while (ms.Length < 16384)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) break;
            byte b = one[0];
            ms.WriteByte(b);
            if ((match == 0 && b == '\r') || (match == 1 && b == '\n') || (match == 2 && b == '\r') || (match == 3 && b == '\n'))
            {
                match++;
                if (match == 4) break;
            }
            else
            {
                match = b == '\r' ? 1 : 0;
            }
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, int code, string reason, byte[] body, CancellationToken ct)
    {
        string header = $"HTTP/1.1 {code} {reason}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        byte[] hBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(hBytes, ct).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // =========================================================================
    // 4. WebSocketSystem (In-process RFC 6455 loopback server via WebSocket.CreateFromStream)
    // =========================================================================

    private static unsafe void TestWebSocketSystem()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

        using var serverCts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = Task.Run(() => RunTestWsServerAsync(listener, serverCts.Token));

        WebSocketClientState* ws = alloc.Persistent.Alloc<WebSocketClientState>(1, 64);
        WebSocketSystem.Start(ws, ref alloc.Persistent, maxConnections: 4, eventQueueCapacity: 128, outboundQueueCapacity: 128);

        try
        {
            HttpHeader* hdrs = stackalloc HttpHeader[1];
            hdrs[0] = HttpHeader.Create("X-Bring-Client", "test-suite");
            WebSocketHandle h = WebSocketSystem.Connect(
                ws,
                $"ws://127.0.0.1:{port}/ws",
                headers: new ReadOnlySpan<HttpHeader>(hdrs, 1),
                subProtocol: "bring.v1");
            Assert.True(h.IsValid, "WebSocket handle valid");

            // 1. Wait for Connected event
            WebSocketEvent evConn = WaitWsEvent(ws, h);
            Assert.Equal((byte)WebSocketEventKind.Connected, (byte)evConn.Kind, $"WS connected (info={evConn.Info})");
            Assert.Equal((byte)WebSocketStateKind.Open, (byte)WebSocketSystem.GetState(ws, h), "WS state Open");
            WebSocketSystem.ReleaseEvent(ref evConn);

            // 2. Text message round-trip
            Assert.True(WebSocketSystem.SendText(ws, h, "hello websocket"), "SendText succeeded");
            WebSocketEvent evText = WaitWsEvent(ws, h);
            try
            {
                Assert.Equal((byte)WebSocketEventKind.TextMessage, (byte)evText.Kind, "TextMessage received");
                Assert.Equal("hello websocket", evText.DataAsUtf8String(), "Text payload echoed");
            }
            finally
            {
                WebSocketSystem.ReleaseEvent(ref evText);
            }

            // 3. Binary message round-trip with NetWriter/NetReader
            byte* pktBuf = alloc.Persistent.Alloc<byte>(64, 16);
            var w = NetWriter.Create(pktBuf, 64);
            FixVec3 expectedPos = new FixVec3(Fix32.FromInt(7), Fix32.FromRatio(-5, 4), Fix32.FromInt(99));
            EntityId expectedId = EntityId.Create(12, 3);
            w.WriteEntityId(expectedId);
            w.WriteFixVec3(in expectedPos);

            Assert.True(WebSocketSystem.SendBinary(ws, h, w.WrittenSpan()), "SendBinary succeeded");
            WebSocketEvent evBin = WaitWsEvent(ws, h);
            try
            {
                Assert.Equal((byte)WebSocketEventKind.BinaryMessage, (byte)evBin.Kind, "BinaryMessage received");
                var r = NetReader.Create(evBin.DataPtr, evBin.DataLength);
                Assert.Equal(expectedId.Raw, r.ReadEntityId().Raw, "Binary EntityId round-trip");
                Assert.True(r.ReadFixVec3().Equals(expectedPos), "Binary FixVec3 round-trip");
            }
            finally
            {
                WebSocketSystem.ReleaseEvent(ref evBin);
            }

            // 4. Multi-frame fragmented binary message reassembly
            Assert.True(WebSocketSystem.SendText(ws, h, "FRAG"u8), "Send FRAG trigger");
            WebSocketEvent evFrag = WaitWsEvent(ws, h);
            try
            {
                Assert.Equal((byte)WebSocketEventKind.BinaryMessage, (byte)evFrag.Kind, "Fragmented binary message received");
                Assert.Equal(12, evFrag.DataLength, "Reassembled 3x4 = 12 bytes");
                ReadOnlySpan<byte> span = evFrag.AsDataSpan();
                for (int i = 0; i < 12; i++)
                    Assert.Equal((byte)(i + 1), span[i], $"Fragment byte[{i}]");
            }
            finally
            {
                WebSocketSystem.ReleaseEvent(ref evFrag);
            }

            // 5. Zero-GC Logic-Thread hot loop: SendBinary + PollEvent + ReleaseEvent (30 round-trips)
            long timeoutTicks = Stopwatch.Frequency * 5;
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int iter = 0; iter < 30; iter++)
            {
                w.Reset();
                w.WriteInt32(iter);
                w.WriteFixVec3(in expectedPos);
                Assert.True(WebSocketSystem.SendBinary(ws, h, w.WrittenSpan()), "Zero-GC SendBinary");

                WebSocketEvent evLoop = default;
                long deadline = Stopwatch.GetTimestamp() + timeoutTicks;
                bool received = false;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    if (WebSocketSystem.PollEvent(ws, out evLoop))
                    {
                        received = true;
                        break;
                    }
                    Thread.Sleep(1);
                }
                Assert.True(received && evLoop.Kind == WebSocketEventKind.BinaryMessage, "Zero-GC BinaryMessage arrived");
                var rLoop = NetReader.Create(evLoop.DataPtr, evLoop.DataLength);
                Assert.Equal(iter, rLoop.ReadInt32(), "Zero-GC echo sequence");
                WebSocketSystem.ReleaseEvent(ref evLoop);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "WebSocket SendBinary + PollEvent + ReleaseEvent hot loop allocates 0 GC bytes");

            // 6. Clean RFC 6455 close handshake
            WebSocketSystem.Close(ws, h, closeStatus: 1000, reason: "bye");
            WebSocketEvent evClose = WaitWsEvent(ws, h);
            Assert.Equal((byte)WebSocketEventKind.Closed, (byte)evClose.Kind, "Closed event received");
            Assert.Equal((ushort)1000, evClose.CloseStatus, "Normal close status 1000");
            WebSocketSystem.ReleaseEvent(ref evClose);
        }
        finally
        {
            WebSocketSystem.Stop(ws);
            serverCts.Cancel();
            listener.Stop();
            try { serverTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after WebSocketSystem test");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live native blocks after WebSocketSystem test");
    }

    private static unsafe WebSocketEvent WaitWsEvent(WebSocketClientState* ws, WebSocketHandle handle, int timeoutMs = 5000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (WebSocketSystem.PollEvent(ws, out WebSocketEvent ev))
            {
                if (ev.Handle == handle)
                    return ev;
                WebSocketSystem.ReleaseEvent(ref ev);
            }
            Thread.Sleep(2);
        }
        throw new TimeoutException($"Timed out waiting for WebSocket event on {handle}");
    }

    private static async Task RunTestWsServerAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    try
                    {
                        string headers = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
                        string? secKey = null;
                        string? proto = null;
                        bool hasClientHeader = false;

                        foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                        {
                            int colon = line.IndexOf(':');
                            if (colon <= 0) continue;
                            string k = line.Substring(0, colon).Trim();
                            string v = line.Substring(colon + 1).Trim();
                            if (k.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                                secKey = v;
                            else if (k.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                                proto = v;
                            else if (k.Equals("X-Bring-Client", StringComparison.OrdinalIgnoreCase) && v == "test-suite")
                                hasClientHeader = true;
                        }

                        if (string.IsNullOrEmpty(secKey) || !hasClientHeader)
                        {
                            await WriteHttpResponseAsync(stream, 400, "Bad Request", Array.Empty<byte>(), ct).ConfigureAwait(false);
                            return;
                        }

                        string acceptKey = Convert.ToBase64String(
                            SHA1.HashData(Encoding.ASCII.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                        var sb = new StringBuilder();
                        sb.Append("HTTP/1.1 101 Switching Protocols\r\n");
                        sb.Append("Upgrade: websocket\r\n");
                        sb.Append("Connection: Upgrade\r\n");
                        sb.Append($"Sec-WebSocket-Accept: {acceptKey}\r\n");
                        if (!string.IsNullOrEmpty(proto))
                            sb.Append($"Sec-WebSocket-Protocol: {proto}\r\n");
                        sb.Append("\r\n");

                        byte[] upgradeBytes = Encoding.ASCII.GetBytes(sb.ToString());
                        await stream.WriteAsync(upgradeBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);

                        using WebSocket serverWs = WebSocket.CreateFromStream(
                            stream,
                            isServer: true,
                            subProtocol: proto,
                            keepAliveInterval: TimeSpan.FromSeconds(10));

                        byte[] buf = new byte[4096];
                        while (!ct.IsCancellationRequested && serverWs.State == WebSocketState.Open)
                        {
                            WebSocketReceiveResult res = await serverWs.ReceiveAsync(buf, ct).ConfigureAwait(false);
                            if (res.MessageType == WebSocketMessageType.Close)
                            {
                                await serverWs.CloseOutputAsync(
                                    res.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                    res.CloseStatusDescription ?? string.Empty,
                                    ct).ConfigureAwait(false);
                                break;
                            }

                            if (res.MessageType == WebSocketMessageType.Text &&
                                res.Count == 4 &&
                                Encoding.UTF8.GetString(buf, 0, 4) == "FRAG")
                            {
                                byte[] f1 = { 1, 2, 3, 4 };
                                byte[] f2 = { 5, 6, 7, 8 };
                                byte[] f3 = { 9, 10, 11, 12 };
                                await serverWs.SendAsync(f1, WebSocketMessageType.Binary, endOfMessage: false, ct).ConfigureAwait(false);
                                await serverWs.SendAsync(f2, WebSocketMessageType.Binary, endOfMessage: false, ct).ConfigureAwait(false);
                                await serverWs.SendAsync(f3, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await serverWs.SendAsync(
                                    new ReadOnlyMemory<byte>(buf, 0, res.Count),
                                    res.MessageType,
                                    res.EndOfMessage,
                                    ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch
                    {
                        // Connection aborted/cancelled during teardown
                    }
                }
            }, ct);
        }
    }

    // =========================================================================
    // 5. NetSerialization Edge Cases (N-06, N-13, N-14, N-18 #3)
    // =========================================================================

    private static unsafe void TestNetSerializationEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        byte* buf = stackalloc byte[512];

        // 1. VarInt32(int.MaxValue) + incremental Crc32
        var w = NetWriter.Create(buf, 512);
        w.WriteVarInt32(int.MaxValue);
        w.WriteVarInt32(int.MinValue);
        var r = NetReader.Create(buf, w.BytesWritten);
        Assert.Equal(int.MaxValue, r.ReadVarInt32(), "VarInt32(int.MaxValue)");
        Assert.Equal(int.MinValue, r.ReadVarInt32(), "VarInt32(int.MinValue)");
        Assert.True(!r.HasOverflow, "VarInt32 extremes no overflow");

        uint crcPart1 = NetHash.Crc32("1234"u8);
        uint crcCombined = NetHash.Crc32("56789"u8, seed: crcPart1);
        Assert.Equal(0xCBF43926u, crcCombined, "Incremental Crc32 matches single-pass");

        // 2. Truncated, overlong, and 5th-byte overflow varints (N-14)
        byte* badVar = stackalloc byte[5];
        badVar[0] = 0x80;
        var rTrunc = NetReader.Create(badVar, 1);
        _ = rTrunc.ReadVarUInt32();
        Assert.True(rTrunc.HasOverflow, "Truncated varint flags overflow");

        for (int i = 0; i < 5; i++) badVar[i] = 0x80;
        var rOverlong = NetReader.Create(badVar, 5);
        _ = rOverlong.ReadVarUInt32();
        Assert.True(rOverlong.HasOverflow, "5th-byte continuation overlong varint flags overflow");

        badVar[4] = 0x10; // bits > 32-bit uint.MaxValue (0x0F max)
        var r5thOverflow = NetReader.Create(badVar, 5);
        _ = r5thOverflow.ReadVarUInt32();
        Assert.True(r5thOverflow.HasOverflow, "5th-byte > 0x0F flags overflow (N-14)");

        // 3. ReadBytes(maxAllowed) incl. negative maxAllowed (N-13)
        w.Reset();
        w.WriteBytes(new byte[] { 1, 2, 3, 4 });
        var rNegMax = NetReader.Create(buf, w.BytesWritten);
        _ = rNegMax.ReadBytes(maxAllowed: -1);
        Assert.True(rNegMax.HasOverflow, "Negative maxAllowed sets overflow (N-13)");

        var rTooSmallMax = NetReader.Create(buf, w.BytesWritten);
        _ = rTooSmallMax.ReadBytes(maxAllowed: 3);
        Assert.True(rTooSmallMax.HasOverflow, "Payload > maxAllowed sets overflow");

        // 4. Signed int overflow in EnsureSpace / EnsureAvailable & ReadBytesRaw(-1) (N-06)
        w.Reset();
        w.WriteByte(1);
        w.WriteBytesRaw(ReadOnlySpan<byte>.Empty);
        Assert.True(!w.HasOverflow, "Empty WriteBytesRaw succeeds");
        var rNegRaw = NetReader.Create(buf, 8);
        _ = rNegRaw.ReadBytesRaw(-1);
        Assert.True(rNegRaw.HasOverflow, "Negative ReadBytesRaw sets overflow");

        var rOverflowAdd = NetReader.Create(buf, 8);
        _ = rOverflowAdd.ReadByte(); // Position = 1
        _ = rOverflowAdd.ReadBytesRaw(int.MaxValue); // 1 + int.MaxValue must NOT wrap around!
        Assert.True(rOverflowAdd.HasOverflow, "EnsureAvailable rejects Position + int.MaxValue overflow (N-06)");

        // Sticky HasOverflow
        int posBefore = rOverflowAdd.Position;
        _ = rOverflowAdd.ReadByte();
        Assert.Equal(posBefore, rOverflowAdd.Position, "Sticky overflow prevents further reads");

        // 5. FixedString64 / FixedString256 invalid length & truncation
        buf[0] = (byte)(FixedString64.MaxBytes + 1);
        var rBadFs64 = NetReader.Create(buf, 128);
        _ = rBadFs64.ReadFixedString64();
        Assert.True(rBadFs64.HasOverflow, "Invalid FixedString64 length flags overflow");

        buf[0] = (byte)(FixedString256.MaxBytes + 1);
        var rBadFs256 = NetReader.Create(buf, 255);
        _ = rBadFs256.ReadFixedString256();
        Assert.True(rBadFs256.HasOverflow, "Invalid FixedString256 length flags overflow");

        buf[0] = 10;
        var rTruncFs64 = NetReader.Create(buf, 4);
        _ = rTruncFs64.ReadFixedString64();
        Assert.True(rTruncFs64.HasOverflow, "Truncated FixedString64 flags overflow");

        // Writer overflow sticks and stops advancing Position
        var wSmall = NetWriter.Create(buf, 4);
        wSmall.WriteInt32(42);
        Assert.True(!wSmall.HasOverflow && wSmall.BytesWritten == 4, "Exact fit writes 4 bytes");
        wSmall.WriteByte(9);
        Assert.True(wSmall.HasOverflow && wSmall.BytesWritten == 4, "Overflow sticks at Position 4");

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after NetSerialization edge cases");
    }

    // =========================================================================
    // 6. LoopbackTransport Edge Cases (N-07, N-17, N-18 #4)
    // =========================================================================

    private static unsafe void TestLoopbackTransportEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);

        try
        {
            // 1. default(NetTransportVTable) safety
            NetTransportVTable emptyVt = default;
            Assert.True(!emptyVt.TrySend(0, "hi"u8), "default vtable TrySend returns false");
            Assert.True(!emptyVt.TryPoll(out _), "default vtable TryPoll returns false");
            emptyVt.DisconnectPeer(0, 0);
            Assert.Equal(0, emptyVt.QueryStats().ConnectedPeers, "default vtable QueryStats returns default");

            // 2. Mixed UnreliableSequenced + ReliableOrdered (N-07) + negative latency clamp (N-17)
            var cfg = new LoopbackSimConfig
            {
                OneWayLatencyTicks = -5, // clamps to 0 in RttMs
                JitterTicks = 0,
                PacketLossPermille = 0,
                RngSeed = 42,
            };
            LoopbackLink* link = alloc.Persistent.Alloc<LoopbackLink>(1, 64);
            LoopbackTransportSystem.Init(link, ref alloc.Persistent, in cfg, queueCapacity: 8, maxPacketBytes: 64);
            NetTransportVTable a = LoopbackTransportSystem.GetVTable(&link->PeerA);
            NetTransportVTable b = LoopbackTransportSystem.GetVTable(&link->PeerB);
            Assert.Equal(0, a.QueryStats().RttMs, "Negative latency clamped to 0 RttMs (N-17)");
            Assert.True(a.TryPoll(out _) && b.TryPoll(out _), "Initial connects polled");

            // Send delayed US(1), then immediate US(2): US(2) arrives first and drops older US(1)
            byte* pkt = stackalloc byte[4];
            link->Config.OneWayLatencyTicks = 3;
            pkt[0] = 1; Assert.True(a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.UnreliableSequenced), "Send delayed US1");
            link->Config.OneWayLatencyTicks = 0;
            pkt[0] = 2; Assert.True(a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.UnreliableSequenced), "Send immediate US2");
            pkt[0] = 3; Assert.True(a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.ReliableUnordered), "Send RU3");

            Assert.True(b.TryPoll(out NetTransportEvent evUs2) && evUs2.AsPayloadSpan()[0] == 2, "US2 arrived first");
            Assert.True(b.TryPoll(out NetTransportEvent evRu3) && evRu3.AsPayloadSpan()[0] == 3, "RU3 arrived");
            for (int t = 0; t < 3; t++) LoopbackTransportSystem.AdvanceTick(link);
            Assert.True(!b.TryPoll(out _), "Older US1 dropped relative to US2");
            long droppedBefore = link->TotalDropped;
            Assert.True(droppedBefore >= 1, "US1 counted as dropped");

            // Now send delayed US(10), then immediate RO(11): RO(11) MUST NOT poison UnreliableSequenced (N-07)!
            link->Config.OneWayLatencyTicks = 3;
            pkt[0] = 10; Assert.True(a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.UnreliableSequenced), "Send delayed US10");
            link->Config.OneWayLatencyTicks = 0;
            pkt[0] = 11; Assert.True(a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.ReliableOrdered), "Send immediate RO11");
            Assert.True(b.TryPoll(out NetTransportEvent evRo11) && evRo11.AsPayloadSpan()[0] == 11, "RO11 arrived first");
            for (int t = 0; t < 3; t++) LoopbackTransportSystem.AdvanceTick(link);
            Assert.True(b.TryPoll(out NetTransportEvent evUs10) && evUs10.AsPayloadSpan()[0] == 10, "US10 NOT falsely dropped by RO11 (N-07)");

            // 3. Oversized payload rejection & queue saturation
            byte* big = stackalloc byte[128];
            Assert.True(!a.TrySend(1, new ReadOnlySpan<byte>(big, 128), NetDeliveryMode.ReliableOrdered), "Oversized payload rejected");

            for (int i = 0; i < 8; i++)
                Assert.True(a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.ReliableOrdered), $"Fill queue {i}");
            Assert.True(!a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.ReliableOrdered), "Full queue TrySend returns false");
            Assert.True(b.QueryStats().PacketsDropped > droppedBefore, "Full queue drop incremented PacketsDropped");

            // 4. Disconnect drains queued packets and rejects subsequent sends (N-17)
            a.DisconnectPeer(1, reason: 99);
            Assert.True(!a.TrySend(1, new ReadOnlySpan<byte>(pkt, 1), NetDeliveryMode.ReliableOrdered), "Send after disconnect returns false");
            Assert.True(b.TryPoll(out NetTransportEvent evDisc) && evDisc.Kind == NetTransportEventKind.PeerDisconnected && evDisc.DisconnectReason == 99, "PeerDisconnected delivered");
            Assert.True(!b.TryPoll(out _), "Queued packets discarded on disconnect (N-17)");
        }
        finally
        {
            alloc.Dispose();
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after LoopbackTransport edge cases");
    }

    // =========================================================================
    // 7. HttpSystem Edge Cases (N-01, N-08, N-10, N-11, N-12, N-15, N-18 #1)
    // =========================================================================

    private static unsafe void TestHttpSystemEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        string logDir = Path.Combine(Path.GetTempPath(), "BringHttpLog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);

        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, appName: "BringHttpTest", directoryOverride: logDir, hookExceptions: false);

        using var serverCts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = Task.Run(() => RunEdgeHttpServerAsync(listener, serverCts.Token));

        HttpClientState* http = alloc.Persistent.Alloc<HttpClientState>(1, 64);
        HttpSystem.Start(http, ref alloc.Persistent, maxConcurrent: 4, defaultTimeoutMs: 5000, log: log);

        try
        {
            string baseUrl = $"http://127.0.0.1:{port}";

            // 1. Put / Delete / Patch / Head + empty body
            HttpMethodKind[] methods = { HttpMethodKind.Put, HttpMethodKind.Delete, HttpMethodKind.Patch, HttpMethodKind.Head };
            foreach (HttpMethodKind m in methods)
            {
                HttpRequestHandle mh = HttpSystem.Send(http, m, $"{baseUrl}/method");
                HttpResponse mr = WaitHttpResponse(http, mh);
                try
                {
                    Assert.True(mr.IsSuccess, $"Method {m} succeeded");
                    if (m == HttpMethodKind.Head)
                        Assert.Equal(0, mr.BodyLength, "HEAD empty body");
                    else
                        Assert.Equal(m.ToString().ToUpperInvariant(), mr.BodyAsUtf8String(), $"Method {m} echoed");
                }
                finally
                {
                    HttpSystem.ReleaseResponse(ref mr);
                }
            }

            // 2. MaxResponseBytes exceeded via Content-Length AND via chunked stream
            HttpRequestHandle hBigCl = HttpSystem.Get(http, $"{baseUrl}/big-cl", maxResponseBytes: 128);
            HttpResponse rBigCl = WaitHttpResponse(http, hBigCl);
            try
            {
                Assert.Equal((byte)HttpRequestStatus.Failed, (byte)rBigCl.Status, "Content-Length > MaxResponseBytes failed");
                Assert.True(rBigCl.BodyPtr == null, "No leaked body on Content-Length overrun");
            }
            finally
            {
                HttpSystem.ReleaseResponse(ref rBigCl);
            }

            HttpRequestHandle hBigChunk = HttpSystem.Get(http, $"{baseUrl}/big-chunked", maxResponseBytes: 128);
            HttpResponse rBigChunk = WaitHttpResponse(http, hBigChunk);
            try
            {
                Assert.Equal((byte)HttpRequestStatus.Failed, (byte)rBigChunk.Status, "Chunked stream > MaxResponseBytes failed");
                Assert.True(rBigChunk.BodyPtr == null, "No leaked body on chunked overrun");
            }
            finally
            {
                HttpSystem.ReleaseResponse(ref rBigChunk);
            }

            // 3. Invalid URL & invalid content-type header (N-15)
            HttpRequestHandle hBadUrl = HttpSystem.Get(http, "not a valid uri");
            Assert.True(hBadUrl.IsValid, "Invalid URL returns handle to failed response");
            HttpResponse rBadUrl = WaitHttpResponse(http, hBadUrl);
            Assert.Equal((byte)HttpRequestStatus.Failed, (byte)rBadUrl.Status, "Invalid URL response Failed");
            HttpSystem.ReleaseResponse(ref rBadUrl);

            HttpRequestHandle hBadCt = HttpSystem.Send(http, HttpMethodKind.Post, $"{baseUrl}/method", "abc"u8, contentType: "invalid/content; =type");
            HttpResponse rBadCt = WaitHttpResponse(http, hBadCt);
            Assert.Equal((byte)HttpRequestStatus.Failed, (byte)rBadCt.Status, "Invalid Content-Type response Failed");
            HttpSystem.ReleaseResponse(ref rBadCt);

            // 4. Stale handle rejection
            Assert.True(!HttpSystem.IsPending(http, hBadUrl), "Stale handle IsPending == false");
            HttpSystem.Cancel(http, hBadUrl); // safe no-op

            // 5. Slot exhaustion (maxConcurrent == 4) + recovery
            HttpRequestHandle[] held = new HttpRequestHandle[4];
            for (int i = 0; i < 4; i++)
            {
                held[i] = HttpSystem.Get(http, $"{baseUrl}/slow", timeoutMs: 5000);
                Assert.True(held[i].IsValid, $"Slot {i} acquired");
            }
            HttpRequestHandle overflowH = HttpSystem.Get(http, $"{baseUrl}/method");
            Assert.True(!overflowH.IsValid, "5th concurrent request returns Invalid on full pool");

            for (int i = 0; i < 4; i++) HttpSystem.Cancel(http, held[i]);
            for (int i = 0; i < 4; i++)
            {
                HttpResponse rc = WaitHttpResponse(http, held[i]);
                HttpSystem.ReleaseResponse(ref rc);
            }
            HttpRequestHandle recovered = HttpSystem.Get(http, $"{baseUrl}/method");
            Assert.True(recovered.IsValid, "Slot pool recovered after draining");
            HttpResponse rr = WaitHttpResponse(http, recovered);
            HttpSystem.ReleaseResponse(ref rr);

            // 6. Stop() while a request is in-flight + double Stop() idempotency
            _ = HttpSystem.Get(http, $"{baseUrl}/slow", timeoutMs: 5000);
            HttpSystem.Stop(http);
            HttpSystem.Stop(http); // double Stop() safe no-op
        }
        finally
        {
            HttpSystem.Stop(http);
            serverCts.Cancel();
            listener.Stop();
            try { serverTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
            LogSystem.Stop(log);
            alloc.Dispose();
            try { Directory.Delete(logDir, true); } catch { }
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after HttpSystem edge cases");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after HttpSystem edge cases");
    }

    private static async Task RunEdgeHttpServerAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }

            _ = Task.Run(async () =>
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    try
                    {
                        string headersText = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(headersText)) return;
                        string[] lines = headersText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                        string[] reqParts = lines[0].Split(' ');
                        string method = reqParts.Length > 0 ? reqParts[0] : "GET";
                        string path = reqParts.Length > 1 ? reqParts[1] : "/";

                        int contentLength = 0;
                        for (int i = 1; i < lines.Length; i++)
                        {
                            int colon = lines[i].IndexOf(':');
                            if (colon > 0 && lines[i].Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(lines[i].Substring(colon + 1).Trim(), out contentLength);
                        }
                        if (contentLength > 0)
                        {
                            byte[] sink = new byte[contentLength];
                            int r = 0;
                            while (r < contentLength)
                            {
                                int n = await stream.ReadAsync(sink.AsMemory(r, contentLength - r), ct).ConfigureAwait(false);
                                if (n <= 0) break;
                                r += n;
                            }
                        }

                        if (path == "/method")
                        {
                            byte[] body = method == "HEAD" ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(method);
                            string hdr = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), ct).ConfigureAwait(false);
                            if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
                        }
                        else if (path == "/big-cl")
                        {
                            byte[] body = new byte[512];
                            await WriteHttpResponseAsync(stream, 200, "OK", body, ct).ConfigureAwait(false);
                        }
                        else if (path == "/big-chunked")
                        {
                            string hdr = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), ct).ConfigureAwait(false);
                            byte[] chunkData = new byte[256];
                            string chunkFrame = $"{chunkData.Length:X}\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(chunkFrame), ct).ConfigureAwait(false);
                            await stream.WriteAsync(chunkData, ct).ConfigureAwait(false);
                            await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                        }
                        else if (path == "/slow")
                        {
                            await Task.Delay(3000, ct).ConfigureAwait(false);
                            await WriteHttpResponseAsync(stream, 200, "OK", "late"u8.ToArray(), ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteHttpResponseAsync(stream, 404, "Not Found", Array.Empty<byte>(), ct).ConfigureAwait(false);
                        }
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    catch { }
                }
            }, ct);
        }
    }

    // =========================================================================
    // 8. WebSocketSystem Edge Cases (N-01..N-05, N-08..N-12, N-16, N-18 #2)
    // =========================================================================

    private static unsafe void TestWebSocketSystemEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        string logDir = Path.Combine(Path.GetTempPath(), "BringWsLog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);

        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, appName: "BringWsTest", directoryOverride: logDir, hookExceptions: false);

        using var serverCts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = Task.Run(() => RunEdgeWsServerAsync(listener, serverCts.Token));

        WebSocketClientState* ws = alloc.Persistent.Alloc<WebSocketClientState>(1, 64);
        WebSocketSystem.Start(
            ws,
            ref alloc.Persistent,
            maxConnections: 2,
            eventQueueCapacity: 16,
            outboundQueueCapacity: 16,
            maxMessageBytes: 1024,
            log: log);

        try
        {
            string baseWsUrl = $"ws://127.0.0.1:{port}";

            // 1. Invalid subProtocol / invalid header in Connect must NOT leak ClientWebSocket or slot (N-05)
            WebSocketHandle hBadProto = WebSocketSystem.Connect(ws, $"{baseWsUrl}/echo", subProtocol: "bad protocol");
            Assert.True(hBadProto.IsValid, "Bad subProtocol returns handle for Error event");
            WebSocketEvent evBadProto = WaitWsEvent(ws, hBadProto);
            Assert.Equal((byte)WebSocketEventKind.Error, (byte)evBadProto.Kind, "Bad subProtocol emits Error");
            WebSocketSystem.ReleaseEvent(ref evBadProto);

            HttpHeader* badHdr = stackalloc HttpHeader[1];
            badHdr[0] = HttpHeader.Create("Bad\r\nName", "val");
            WebSocketHandle hBadHdr = WebSocketSystem.Connect(ws, $"{baseWsUrl}/echo", headers: new ReadOnlySpan<HttpHeader>(badHdr, 1));
            WebSocketEvent evBadHdr = WaitWsEvent(ws, hBadHdr);
            Assert.Equal((byte)WebSocketEventKind.Error, (byte)evBadHdr.Kind, "Bad header emits Error");
            WebSocketSystem.ReleaseEvent(ref evBadHdr);

            // 2. connectTimeoutMs expiration -> Error (not Closed(1000)) (N-04)
            WebSocketHandle hTimeout = WebSocketSystem.Connect(ws, $"{baseWsUrl}/slow-handshake", connectTimeoutMs: 120);
            WebSocketEvent evTimeout = WaitWsEvent(ws, hTimeout);
            Assert.Equal((byte)WebSocketEventKind.Error, (byte)evTimeout.Kind, "Connect timeout emits Error (N-04)");
            Assert.Equal((byte)WebSocketStateKind.Faulted, (byte)WebSocketSystem.GetState(ws, hTimeout), "State Faulted on connect timeout");
            WebSocketSystem.ReleaseEvent(ref evTimeout);

            // 3. Slot exhaustion (maxConnections == 2) + recovery
            WebSocketHandle c1 = WebSocketSystem.Connect(ws, $"{baseWsUrl}/echo");
            WebSocketHandle c2 = WebSocketSystem.Connect(ws, $"{baseWsUrl}/echo");
            WebSocketHandle cOver = WebSocketSystem.Connect(ws, $"{baseWsUrl}/echo");
            Assert.True(!cOver.IsValid, "3rd concurrent connection rejected when maxConnections==2");

            WebSocketEvent ec1 = WaitWsEvent(ws, c1);
            Assert.Equal((byte)WebSocketEventKind.Connected, (byte)ec1.Kind, "c1 connected");
            WebSocketSystem.ReleaseEvent(ref ec1);
            WebSocketEvent ec2 = WaitWsEvent(ws, c2);
            Assert.Equal((byte)WebSocketEventKind.Connected, (byte)ec2.Kind, "c2 connected");
            WebSocketSystem.ReleaseEvent(ref ec2);

            // 4. SendText > 1024 chars (<= maxMessageBytes=1024? Wait: 900 ASCII chars vs 1500? Use 1024 max, test > 1024 SendText rejected or 0-byte frame)
            // Send 0-byte binary and 0-byte text frames
            Assert.True(WebSocketSystem.SendBinary(ws, c1, ReadOnlySpan<byte>.Empty), "0-byte binary send");
            WebSocketEvent evZeroBin = WaitWsEvent(ws, c1);
            Assert.Equal((byte)WebSocketEventKind.BinaryMessage, (byte)evZeroBin.Kind, "0-byte binary received");
            Assert.Equal(0, evZeroBin.DataLength, "0-byte binary length");
            WebSocketSystem.ReleaseEvent(ref evZeroBin);

            Assert.True(WebSocketSystem.SendText(ws, c1, ReadOnlySpan<char>.Empty), "0-byte text send");
            WebSocketEvent evZeroTxt = WaitWsEvent(ws, c1);
            Assert.Equal((byte)WebSocketEventKind.TextMessage, (byte)evZeroTxt.Kind, "0-byte text received");
            Assert.Equal(0, evZeroTxt.DataLength, "0-byte text length");
            WebSocketSystem.ReleaseEvent(ref evZeroTxt);

            // Close c2 with custom closeStatus (4001) and verify custom close status + slot reuse
            WebSocketSystem.Close(ws, c2, closeStatus: 4001, reason: "custom-bye");
            WebSocketEvent evC2Close = WaitWsEvent(ws, c2);
            Assert.Equal((byte)WebSocketEventKind.Closed, (byte)evC2Close.Kind, "c2 closed");
            Assert.Equal((ushort)4001, evC2Close.CloseStatus, "Custom closeStatus 4001 preserved (N-09)");
            WebSocketSystem.ReleaseEvent(ref evC2Close);

            // Stale handle c2 rejection
            Assert.Equal((byte)WebSocketStateKind.Disconnected, (byte)WebSocketSystem.GetState(ws, c2), "Stale handle GetState after slot reuse/close");
            Assert.True(!WebSocketSystem.SendBinary(ws, c2, "x"u8), "SendBinary on closed/stale handle returns false");

            // 5. Server sends Close while client is sending frames (N-02)
            Assert.True(WebSocketSystem.SendText(ws, c1, "CLOSE_SOON"u8), "Trigger server close during active sends");
            for (int i = 0; i < 10; i++)
                _ = WebSocketSystem.SendBinary(ws, c1, "ping"u8);
            WebSocketEvent evSrvClose = WaitWsEventKind(ws, c1, WebSocketEventKind.Closed);
            Assert.Equal((byte)WebSocketEventKind.Closed, (byte)evSrvClose.Kind, "Server close during active sends finished cleanly (N-02)");
            WebSocketSystem.ReleaseEvent(ref evSrvClose);

            // 6. MaxMessageBytes overrun (> 1024 bytes from server) -> Error event with 0 leaks
            WebSocketHandle cOverrun = WebSocketSystem.Connect(ws, $"{baseWsUrl}/bigmsg");
            WebSocketEvent evConnOver = WaitWsEvent(ws, cOverrun);
            Assert.Equal((byte)WebSocketEventKind.Connected, (byte)evConnOver.Kind, "cOverrun connected");
            WebSocketSystem.ReleaseEvent(ref evConnOver);
            WebSocketEvent evErrOver = WaitWsEvent(ws, cOverrun);
            Assert.Equal((byte)WebSocketEventKind.Error, (byte)evErrOver.Kind, "MaxMessageBytes overrun emits Error");
            WebSocketSystem.ReleaseEvent(ref evErrOver);

            // 7. Unexpected server TCP drop guarantees terminal Error or Closed event (N-03)
            WebSocketHandle cDrop = WebSocketSystem.Connect(ws, $"{baseWsUrl}/drop");
            WebSocketEvent evConnDrop = WaitWsEvent(ws, cDrop);
            Assert.Equal((byte)WebSocketEventKind.Connected, (byte)evConnDrop.Kind, "cDrop connected");
            WebSocketSystem.ReleaseEvent(ref evConnDrop);
            WebSocketEvent evDropped = WaitWsEvent(ws, cDrop);
            Assert.True(evDropped.Kind == WebSocketEventKind.Error || evDropped.Kind == WebSocketEventKind.Closed, "Abrupt TCP drop emits terminal Error/Closed");
            WebSocketSystem.ReleaseEvent(ref evDropped);

            // 8. EventQueue overflow (capacity == 16) + OutboundQueue overflow + Stop() draining unpolled events
            WebSocketHandle cFlood = WebSocketSystem.Connect(ws, $"{baseWsUrl}/flood");
            WebSocketEvent evConnFlood = WaitWsEvent(ws, cFlood);
            WebSocketSystem.ReleaseEvent(ref evConnFlood);

            // Wait briefly for server flood to overflow EventQueue (16)
            Stopwatch sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref ws->DroppedEvents.Value) == 0 && sw.ElapsedMilliseconds < 2000)
                Thread.Sleep(5);
            Assert.True(Interlocked.Read(ref ws->DroppedEvents.Value) > 0, "EventQueue overflow counted DroppedEvents");

            // Also test SendText(>1024 chars) path with a second larger-limit WebSocketClientState
            WebSocketClientState* wsBig = alloc.Persistent.Alloc<WebSocketClientState>(1, 64);
            WebSocketSystem.Start(wsBig, ref alloc.Persistent, maxConnections: 1, eventQueueCapacity: 16, outboundQueueCapacity: 16, maxMessageBytes: 8192, log: log);
            try
            {
                WebSocketHandle cBig = WebSocketSystem.Connect(wsBig, $"{baseWsUrl}/echo");
                WebSocketEvent evBigConn = WaitWsEvent(wsBig, cBig);
                WebSocketSystem.ReleaseEvent(ref evBigConn);

                string bigText = new string('A', 2000);
                Assert.True(WebSocketSystem.SendText(wsBig, cBig, bigText.AsSpan()), "SendText > 1024 chars succeeded");
                WebSocketEvent evBigEcho = WaitWsEvent(wsBig, cBig);
                Assert.Equal(2000, evBigEcho.DataLength, "2000-byte text echoed");
                WebSocketSystem.ReleaseEvent(ref evBigEcho);

                // Saturate outbound queue (capacity == 16) and leave queued/unpolled before Stop()
                for (int i = 0; i < 32; i++)
                    _ = WebSocketSystem.SendBinary(wsBig, cBig, "payload"u8);
            }
            finally
            {
                WebSocketSystem.Stop(wsBig);
            }
        }
        finally
        {
            WebSocketSystem.Stop(ws);
            serverCts.Cancel();
            listener.Stop();
            try { serverTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
            LogSystem.Stop(log);
            alloc.Dispose();
            try { Directory.Delete(logDir, true); } catch { }
        }

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after WebSocketSystem edge cases");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after WebSocketSystem edge cases");
    }

    private static unsafe WebSocketEvent WaitWsEventKind(WebSocketClientState* ws, WebSocketHandle handle, WebSocketEventKind expected, int timeoutMs = 5000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (WebSocketSystem.PollEvent(ws, out WebSocketEvent ev))
            {
                if (ev.Handle == handle && ev.Kind == expected)
                    return ev;
                WebSocketSystem.ReleaseEvent(ref ev);
            }
            Thread.Sleep(2);
        }
        throw new TimeoutException($"Timed out waiting for WebSocket event {expected} on {handle}");
    }

    private static async Task RunEdgeWsServerAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }

            _ = Task.Run(async () =>
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    try
                    {
                        string headers = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
                        string[] lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                        string path = lines.Length > 0 && lines[0].Split(' ').Length > 1 ? lines[0].Split(' ')[1] : "/";

                        if (path == "/slow-handshake")
                        {
                            await Task.Delay(1500, ct).ConfigureAwait(false);
                            return;
                        }

                        string? secKey = null;
                        foreach (string line in lines)
                        {
                            int colon = line.IndexOf(':');
                            if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                                secKey = line.Substring(colon + 1).Trim();
                        }
                        if (string.IsNullOrEmpty(secKey)) return;

                        string acceptKey = Convert.ToBase64String(
                            SHA1.HashData(Encoding.ASCII.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        string upgrade = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(upgrade), ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);

                        using WebSocket srvWs = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(10));

                        if (path == "/bigmsg")
                        {
                            byte[] big = new byte[2048];
                            await srvWs.SendAsync(big, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                            await Task.Delay(200, ct).ConfigureAwait(false);
                            return;
                        }
                        if (path == "/drop")
                        {
                            await Task.Delay(30, ct).ConfigureAwait(false);
                            client.Client.Close(0); // abrupt close without WS Close frame
                            return;
                        }
                        if (path == "/flood")
                        {
                            byte[] msg = "flood"u8.ToArray();
                            for (int i = 0; i < 32; i++)
                                await srvWs.SendAsync(msg, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                            await Task.Delay(500, ct).ConfigureAwait(false);
                            return;
                        }

                        byte[] buf = new byte[8192];
                        while (!ct.IsCancellationRequested && srvWs.State == WebSocketState.Open)
                        {
                            WebSocketReceiveResult res = await srvWs.ReceiveAsync(buf, ct).ConfigureAwait(false);
                            if (res.MessageType == WebSocketMessageType.Close)
                            {
                                await srvWs.CloseOutputAsync(
                                    res.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                    res.CloseStatusDescription ?? string.Empty,
                                    ct).ConfigureAwait(false);
                                break;
                            }
                            if (res.MessageType == WebSocketMessageType.Text && res.Count == 10 &&
                                Encoding.UTF8.GetString(buf, 0, 10) == "CLOSE_SOON")
                            {
                                await srvWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "srv-close", ct).ConfigureAwait(false);
                                break;
                            }
                            await srvWs.SendAsync(new ReadOnlyMemory<byte>(buf, 0, res.Count), res.MessageType, res.EndOfMessage, ct).ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
            }, ct);
        }
    }
}
