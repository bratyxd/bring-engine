using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace BringEngine.Networking;

public enum NetDeliveryMode : byte
{
    Unreliable = 0,
    UnreliableSequenced = 1,
    ReliableUnordered = 2,
    ReliableOrdered = 3,
}

public enum NetTransportEventKind : byte
{
    None = 0,
    PeerConnected = 1,
    PeerDisconnected = 2,
    PacketReceived = 3,
}

/// <summary>
/// Transport-agnostic peer event returned by NetTransportVTable.Poll.
/// PayloadPtr is valid until the next Poll/Step or Release call as defined by the transport.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NetTransportEvent
{
    public NetTransportEventKind Kind;
    public NetDeliveryMode Delivery;
    public byte Channel;
    public uint PeerId;
    public byte* PayloadPtr;
    public int PayloadLength;
    public int DisconnectReason;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> AsPayloadSpan()
        => PayloadPtr == null || PayloadLength <= 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(PayloadPtr, PayloadLength);
}

[StructLayout(LayoutKind.Sequential)]
public struct NetTransportStats
{
    public int ConnectedPeers;
    public int RttMs;
    public float PacketLossRatio;
    public long PacketsSent;
    public long PacketsReceived;
    public long PacketsDropped;
    public long BytesSent;
    public long BytesReceived;
}

/// <summary>
/// Zero-OOP, unmanaged function-pointer transport contract so games can plug in LiteNetLib,
/// Backdash, Facepunch.Steamworks, WebSockets, raw sockets, or LoopbackTransport without C# interfaces.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NetTransportVTable
{
    public void* Context;
    public delegate*<void*, uint, byte*, int, NetDeliveryMode, byte, bool> Send;
    public delegate*<void*, out NetTransportEvent, bool> Poll;
    public delegate*<void*, uint, int, void> Disconnect;
    public delegate*<void*, NetTransportStats> GetStats;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TrySend(uint peerId, ReadOnlySpan<byte> payload, NetDeliveryMode mode = NetDeliveryMode.ReliableOrdered, byte channel = 0)
    {
        if (Send == null) return false;
        fixed (byte* ptr = payload)
            return Send(Context, peerId, ptr, payload.Length, mode, channel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryPoll(out NetTransportEvent ev)
    {
        if (Poll == null)
        {
            ev = default;
            return false;
        }
        return Poll(Context, out ev);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void DisconnectPeer(uint peerId, int reason = 0)
    {
        if (Disconnect != null)
            Disconnect(Context, peerId, reason);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly NetTransportStats QueryStats() => GetStats != null ? GetStats(Context) : default;
}

// =============================================================================
// DETERMINISTIC IN-MEMORY LOOPBACK TRANSPORT (FOR HEADLESS CI & SIM TESTING)
// =============================================================================

[StructLayout(LayoutKind.Sequential)]
public struct LoopbackSimConfig
{
    public int OneWayLatencyTicks;
    public int JitterTicks;
    public int PacketLossPermille; // 0..1000 (e.g. 50 = 5% drop on unreliable packets)
    public ulong RngSeed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LoopbackQueuedPacket
{
    public byte Active;
    public byte DestEndpoint; // 0 or 1
    public byte Channel;
    public NetDeliveryMode Delivery;
    public int DeliverAtTick;
    public int DataOffset;
    public int Length;
    public uint Sequence;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LoopbackEndpoint
{
    internal LoopbackLink* Link;
    internal byte EndpointIndex; // 0 or 1
    internal byte Connected;
    internal byte PendingConnectEvent;
    internal byte PendingDisconnectEvent;
    internal int DisconnectReason;
    internal int MaxPacketBytes;
    internal byte* PollScratch;
    internal uint LastSeqReceived;
    internal int LastOrderedDeliverTick;
    internal long PacketsSent;
    internal long PacketsReceived;
    internal long BytesSent;
    internal long BytesReceived;
}

/// <summary>
/// Deterministic two-endpoint in-memory network link (Client &lt;-&gt; Server or Peer A &lt;-&gt; Peer B)
/// with configurable fixed-tick latency, jitter, and packet loss.
/// 100% unmanaged, 0 GC allocations, ideal for headless multiplayer &amp; rollback verification tests.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct LoopbackLink
{
    public LoopbackEndpoint PeerA; // Endpoint 0 (remote peerId = 1)
    public LoopbackEndpoint PeerB; // Endpoint 1 (remote peerId = 0)

    internal LoopbackQueuedPacket* Queue;
    internal byte* PayloadPool;
    internal int QueueCapacity;
    internal int MaxPacketBytes;
    internal uint NextSequence;

    public LoopbackSimConfig Config;
    public DetRng Rng;
    public int CurrentTick;
    public long TotalDropped;
}

public static unsafe class LoopbackTransportSystem
{
    /// <summary>
    /// Allocates a deterministic two-endpoint loopback link inside a LinearArena (0 leaks on arena dispose).
    /// </summary>
    public static void Init(
        LoopbackLink* link,
        ref LinearArena arena,
        in LoopbackSimConfig config,
        int queueCapacity = 256,
        int maxPacketBytes = 1400)
    {
        if (link == null) throw new ArgumentNullException(nameof(link));
        if (queueCapacity < 8) queueCapacity = 8;
        if (maxPacketBytes < 64) maxPacketBytes = 64;

        *link = default;
        link->Config = config;
        link->Rng = DetRng.Create(config.RngSeed == 0 ? 0xC0FFEE123456789UL : config.RngSeed);
        link->QueueCapacity = queueCapacity;
        link->MaxPacketBytes = maxPacketBytes;
        link->Queue = arena.Alloc<LoopbackQueuedPacket>(queueCapacity, 64);
        link->PayloadPool = arena.AllocUninitialized<byte>(queueCapacity * maxPacketBytes, 64);

        InitEndpoint(ref link->PeerA, link, 0, ref arena, maxPacketBytes);
        InitEndpoint(ref link->PeerB, link, 1, ref arena, maxPacketBytes);
    }

    private static void InitEndpoint(ref LoopbackEndpoint ep, LoopbackLink* link, byte idx, ref LinearArena arena, int maxPacketBytes)
    {
        ep = new LoopbackEndpoint
        {
            Link = link,
            EndpointIndex = idx,
            Connected = 1,
            PendingConnectEvent = 1,
            MaxPacketBytes = maxPacketBytes,
            PollScratch = arena.AllocUninitialized<byte>(maxPacketBytes, 16),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AdvanceTick(LoopbackLink* link)
    {
        if (link != null) link->CurrentTick++;
    }

    public static NetTransportVTable GetVTable(LoopbackEndpoint* endpoint) => new NetTransportVTable
    {
        Context = endpoint,
        Send = &SendImpl,
        Poll = &PollImpl,
        Disconnect = &DisconnectImpl,
        GetStats = &GetStatsImpl,
    };

    private static bool SendImpl(void* ctx, uint peerId, byte* payload, int length, NetDeliveryMode mode, byte channel)
    {
        _ = peerId;
        LoopbackEndpoint* ep = (LoopbackEndpoint*)ctx;
        if (ep == null || ep->Link == null || ep->Connected == 0) return false;
        LoopbackLink* link = ep->Link;
        if (length < 0 || length > link->MaxPacketBytes) return false;

        ep->PacketsSent++;
        ep->BytesSent += length;

        // Apply deterministic packet loss ONLY to unreliable packets (reliable packets retransmit in real transports)
        bool isUnreliable = mode == NetDeliveryMode.Unreliable || mode == NetDeliveryMode.UnreliableSequenced;
        if (isUnreliable && link->Config.PacketLossPermille > 0)
        {
            int roll = link->Rng.NextInt(0, 1000);
            if (roll < link->Config.PacketLossPermille)
            {
                link->TotalDropped++;
                return true;
            }
        }

        // Find free queue slot
        int freeIdx = -1;
        for (int i = 0; i < link->QueueCapacity; i++)
        {
            if (link->Queue[i].Active == 0)
            {
                freeIdx = i;
                break;
            }
        }
        if (freeIdx < 0)
        {
            link->TotalDropped++;
            return false;
        }

        int jitter = link->Config.JitterTicks > 0 ? link->Rng.NextInt(0, link->Config.JitterTicks + 1) : 0;
        int delay = link->Config.OneWayLatencyTicks + jitter;
        if (delay < 0) delay = 0;
        int deliverAt = link->CurrentTick + delay;
        if (mode == NetDeliveryMode.ReliableOrdered)
        {
            if (deliverAt < ep->LastOrderedDeliverTick)
                deliverAt = ep->LastOrderedDeliverTick;
            ep->LastOrderedDeliverTick = deliverAt;
        }

        byte dest = (byte)(ep->EndpointIndex ^ 1);
        int offset = freeIdx * link->MaxPacketBytes;
        if (length > 0 && payload != null)
            Buffer.MemoryCopy(payload, link->PayloadPool + offset, link->MaxPacketBytes, length);

        link->Queue[freeIdx] = new LoopbackQueuedPacket
        {
            Active = 1,
            DestEndpoint = dest,
            Channel = channel,
            Delivery = mode,
            DeliverAtTick = deliverAt,
            DataOffset = offset,
            Length = length,
            Sequence = ++link->NextSequence,
        };
        return true;
    }

    private static bool PollImpl(void* ctx, out NetTransportEvent ev)
    {
        LoopbackEndpoint* ep = (LoopbackEndpoint*)ctx;
        if (ep == null || ep->Link == null)
        {
            ev = default;
            return false;
        }

        uint remoteId = (uint)(ep->EndpointIndex ^ 1);
        if (ep->PendingConnectEvent != 0)
        {
            ep->PendingConnectEvent = 0;
            ev = new NetTransportEvent
            {
                Kind = NetTransportEventKind.PeerConnected,
                PeerId = remoteId,
            };
            return true;
        }

        if (ep->PendingDisconnectEvent != 0)
        {
            ep->PendingDisconnectEvent = 0;
            ev = new NetTransportEvent
            {
                Kind = NetTransportEventKind.PeerDisconnected,
                PeerId = remoteId,
                DisconnectReason = ep->DisconnectReason,
            };
            return true;
        }

        LoopbackLink* link = ep->Link;
        int bestIdx = -1;
        uint bestSeq = uint.MaxValue;

        for (int i = 0; i < link->QueueCapacity; i++)
        {
            ref LoopbackQueuedPacket q = ref link->Queue[i];
            if (q.Active == 0 || q.DestEndpoint != ep->EndpointIndex) continue;
            if (q.DeliverAtTick > link->CurrentTick) continue;

            // ReliableOrdered preserves send sequence order; pick earliest eligible sequence
            if (bestIdx < 0 || q.Sequence < bestSeq)
            {
                bestIdx = i;
                bestSeq = q.Sequence;
            }
        }

        if (bestIdx < 0)
        {
            ev = default;
            return false;
        }

        LoopbackQueuedPacket pkt = link->Queue[bestIdx];
        link->Queue[bestIdx].Active = 0;

        // Drop stale out-of-order packets for UnreliableSequenced
        if (pkt.Delivery == NetDeliveryMode.UnreliableSequenced && pkt.Sequence < ep->LastSeqReceived)
        {
            link->TotalDropped++;
            return PollImpl(ctx, out ev);
        }
        if (pkt.Sequence > ep->LastSeqReceived)
            ep->LastSeqReceived = pkt.Sequence;

        if (pkt.Length > 0)
            Buffer.MemoryCopy(link->PayloadPool + pkt.DataOffset, ep->PollScratch, ep->MaxPacketBytes, pkt.Length);

        ep->PacketsReceived++;
        ep->BytesReceived += pkt.Length;

        ev = new NetTransportEvent
        {
            Kind = NetTransportEventKind.PacketReceived,
            Delivery = pkt.Delivery,
            Channel = pkt.Channel,
            PeerId = remoteId,
            PayloadPtr = pkt.Length > 0 ? ep->PollScratch : null,
            PayloadLength = pkt.Length,
        };
        return true;
    }

    private static void DisconnectImpl(void* ctx, uint peerId, int reason)
    {
        _ = peerId;
        LoopbackEndpoint* ep = (LoopbackEndpoint*)ctx;
        if (ep == null || ep->Link == null || ep->Connected == 0) return;
        LoopbackLink* link = ep->Link;

        ep->Connected = 0;
        ep->PendingDisconnectEvent = 1;
        ep->DisconnectReason = reason;

        LoopbackEndpoint* other = ep->EndpointIndex == 0 ? &link->PeerB : &link->PeerA;
        if (other->Connected != 0)
        {
            other->Connected = 0;
            other->PendingDisconnectEvent = 1;
            other->DisconnectReason = reason;
        }
    }

    private static NetTransportStats GetStatsImpl(void* ctx)
    {
        LoopbackEndpoint* ep = (LoopbackEndpoint*)ctx;
        if (ep == null || ep->Link == null) return default;
        LoopbackLink* link = ep->Link;
        return new NetTransportStats
        {
            ConnectedPeers = ep->Connected,
            RttMs = (link->Config.OneWayLatencyTicks * 2) * 16,
            PacketLossRatio = link->Config.PacketLossPermille / 1000f,
            PacketsSent = ep->PacketsSent,
            PacketsReceived = ep->PacketsReceived,
            PacketsDropped = link->TotalDropped,
            BytesSent = ep->BytesSent,
            BytesReceived = ep->BytesReceived,
        };
    }
}
