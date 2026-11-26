using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SaruMon.Core;

public sealed record UdpFlowInfo(
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    long Packets,
    long Bytes,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// Session-long in-memory summaries of observed UDP send events.
/// Entries never expire or evict earlier history. At capacity, known flows continue
/// accumulating and events for new flows increment DroppedEvents instead.
/// IPv4-mapped IPv6 addresses are normalized to IPv4. ETW supplies no IPv6 scope ID,
/// so both matching and displayed addresses deliberately omit scope IDs. Interfaces
/// sharing the same IPv6 address bytes therefore cannot be distinguished here.
/// </summary>
public sealed class UdpFlowTracker
{
    private readonly int maxFlows;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private readonly Dictionary<FlowKey, FlowEntry> flows = new();
    private readonly LinkedList<FlowEntry> recency = new();
    private HashSet<AddressKey> localAddresses = new();
    private DateTimeOffset lastClock;
    private long droppedEvents;

    public UdpFlowTracker(int maxFlows = 2048, Func<DateTimeOffset>? clock = null)
    {
        if (maxFlows <= 0) throw new ArgumentOutOfRangeException(nameof(maxFlows));
        this.maxFlows = maxFlows;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Valid selected-address events not recorded because their flow was new and the table was full.</summary>
    public long DroppedEvents { get { lock (gate) return droppedEvents; } }

    /// <summary>Changing the normalized address set clears the previous selection's observations.</summary>
    public void SetLocalAddresses(IReadOnlyCollection<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var selected = new HashSet<AddressKey>();
        foreach (var address in addresses)
            if (TryNormalize(address, out var normalized) && IsValidLocal(normalized)) selected.Add(normalized);
        lock (gate)
        {
            if (localAddresses.SetEquals(selected)) return;
            localAddresses = selected;
            flows.Clear();
            recency.Clear();
            lastClock = default;
            droppedEvents = 0;
        }
    }

    /// <summary>
    /// Records one outgoing datagram. Invalid endpoints/counts and unselected local
    /// addresses are ignored so malformed capture events cannot stop observation.
    /// Ports must be 1..65535; a zero-byte datagram still increments its packet counter.
    /// </summary>
    public void Record(IPAddress local, int localPort, IPAddress remote, int remotePort, int bytes)
    {
        if (localPort is < 1 or > 65535 || remotePort is < 1 or > 65535 || bytes < 0 ||
            !TryNormalize(local, out var localKey) || !TryNormalize(remote, out var remoteKey) ||
            !IsValidLocal(localKey) || remoteKey.IsUnspecified || localKey.IsIpv4 != remoteKey.IsIpv4)
            return;

        lock (gate)
        {
            if (!localAddresses.Contains(localKey)) return;
            var key = new FlowKey(localKey, localPort, remoteKey, remotePort);
            if (flows.TryGetValue(key, out var existing))
            {
                existing.Observe(bytes, ReadClock());
                recency.Remove(existing.Node);
                recency.AddLast(existing.Node);
                return;
            }

            // Preserve accumulated history instead of silently evicting old entries.
            // All per-event dictionary/list work remains O(1).
            if (flows.Count >= maxFlows)
            {
                droppedEvents = SaturatingAdd(droppedEvents, 1);
                return;
            }
            var entry = new FlowEntry(key, bytes, ReadClock());
            flows.Add(key, entry);
            recency.AddLast(entry.Node);
        }
    }

    /// <summary>Returns immutable session totals, most recently observed first. Time alone never removes rows.</summary>
    public IReadOnlyList<UdpFlowInfo> Snapshot()
    {
        lock (gate)
        {
            var snapshot = new List<UdpFlowInfo>(flows.Count);
            for (var node = recency.Last; node is not null; node = node.Previous)
            {
                var entry = node.Value;
                snapshot.Add(new UdpFlowInfo(entry.LocalText, entry.Key.LocalPort, entry.RemoteText,
                    entry.Key.RemotePort, entry.Packets, entry.Bytes,
                    entry.FirstSeen, entry.LastSeen));
            }
            return snapshot;
        }
    }

    private DateTimeOffset ReadClock()
    {
        var now = clock();
        // Preserve FirstSeen <= LastSeen even if the wall clock steps backward.
        if (now < lastClock) return lastClock;
        lastClock = now;
        return now;
    }

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsValidLocal(AddressKey address) =>
        !address.IsUnspecified && !address.IsMulticast && !address.IsBroadcast;

    private static bool TryNormalize(IPAddress? address, out AddressKey result)
    {
        result = default;
        if (address is null || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written)) return false;
        if (written == 4)
            result = new AddressKey(true, 0, BinaryPrimitives.ReadUInt32BigEndian(bytes));
        else if (address.IsIPv4MappedToIPv6)
            result = new AddressKey(true, 0, BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]));
        else
            result = new AddressKey(false, BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
        return true;
    }

    // Value keys avoid allocating address strings/arrays on the frequent Record path.
    // IPv6 ScopeId intentionally does not participate: it is absent in ETW events.
    private readonly record struct AddressKey(bool IsIpv4, ulong High, ulong Low)
    {
        public bool IsUnspecified => High == 0 && Low == 0;
        public bool IsBroadcast => IsIpv4 && Low == uint.MaxValue;
        public bool IsMulticast => IsIpv4 ? (Low & 0xF0000000) == 0xE0000000 : (High >> 56) == 0xFF;

        public override string ToString()
        {
            Span<byte> bytes = stackalloc byte[16];
            if (IsIpv4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Low);
                return new IPAddress(bytes[..4]).ToString();
            }
            BinaryPrimitives.WriteUInt64BigEndian(bytes, High);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Low);
            return new IPAddress(bytes).ToString();
        }
    }

    private readonly record struct FlowKey(AddressKey Local, int LocalPort, AddressKey Remote, int RemotePort);

    private sealed class FlowEntry
    {
        public FlowEntry(FlowKey key, int bytes, DateTimeOffset firstSeen)
        {
            Key = key;
            LocalText = key.Local.ToString();
            RemoteText = key.Remote.ToString();
            FirstSeen = firstSeen;
            Observe(bytes, firstSeen);
            Node = new LinkedListNode<FlowEntry>(this);
        }

        public FlowKey Key { get; }
        public string LocalText { get; }
        public string RemoteText { get; }
        public long Packets { get; set; }
        public long Bytes { get; set; }
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; set; }
        public LinkedListNode<FlowEntry> Node { get; }

        public void Observe(int bytes, DateTimeOffset now)
        {
            Packets = SaturatingAdd(Packets, 1);
            Bytes = SaturatingAdd(Bytes, bytes);
            LastSeen = now;
        }
    }
}
