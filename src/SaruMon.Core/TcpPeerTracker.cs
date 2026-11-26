using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SaruMon.Core;

public sealed record TcpPeerInfo(
    string RemoteAddress,
    long SentBytes,
    long ReceivedBytes,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// Session totals for each remote TCP address across all connections and ports.
/// Only events for selected local addresses are included. IPv4-mapped IPv6 is
/// normalized and IPv6 scope IDs are omitted because ETW does not supply them.
/// At capacity, existing totals survive and events for new peers are counted as dropped.
/// </summary>
public sealed class TcpPeerTracker
{
    private readonly int maxPeers;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private readonly Dictionary<AddressKey, PeerEntry> peers = new();
    private readonly LinkedList<PeerEntry> recency = new();
    private HashSet<AddressKey> localAddresses = new();
    private DateTimeOffset lastClock;
    private long droppedEvents;

    public TcpPeerTracker(int maxPeers = 2048, Func<DateTimeOffset>? clock = null)
    {
        if (maxPeers <= 0) throw new ArgumentOutOfRangeException(nameof(maxPeers));
        this.maxPeers = maxPeers;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public long DroppedEvents { get { lock (gate) return droppedEvents; } }

    /// <summary>Changing the normalized address set clears the previous selection's totals.</summary>
    public void SetLocalAddresses(IReadOnlyCollection<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var selected = new HashSet<AddressKey>();
        foreach (var address in addresses)
            if (TryNormalize(address, out var key) && key.IsUnicast) selected.Add(key);
        lock (gate)
        {
            if (localAddresses.SetEquals(selected)) return;
            localAddresses = selected;
            peers.Clear();
            recency.Clear();
            lastClock = default;
            droppedEvents = 0;
        }
    }

    /// <summary>
    /// Records the ETW byte count in the requested direction. Endpoints retain
    /// local/remote orientation for both send and receive; ports intentionally do
    /// not form part of the key. Invalid input is ignored without affecting totals.
    /// </summary>
    public void Record(bool isSend, IPAddress local, IPAddress remote, int bytes)
    {
        if (bytes < 0 || !TryNormalize(local, out var localKey) || !TryNormalize(remote, out var remoteKey)
            || !localKey.IsUnicast || !remoteKey.IsUnicast || localKey.IsIpv4 != remoteKey.IsIpv4)
            return;
        lock (gate)
        {
            if (!localAddresses.Contains(localKey)) return;
            if (peers.TryGetValue(remoteKey, out var existing))
            {
                existing.Observe(isSend, bytes, ReadClock());
                recency.Remove(existing.Node);
                recency.AddLast(existing.Node);
                return;
            }
            if (peers.Count >= maxPeers)
            {
                droppedEvents = SaturatingAdd(droppedEvents, 1);
                return;
            }
            var entry = new PeerEntry(remoteKey.ToString(), isSend, bytes, ReadClock());
            peers.Add(remoteKey, entry);
            recency.AddLast(entry.Node);
        }
    }

    /// <summary>Immutable, most recently observed first. Inactive peers never expire.</summary>
    public IReadOnlyList<TcpPeerInfo> Snapshot()
    {
        lock (gate)
        {
            var snapshot = new List<TcpPeerInfo>(peers.Count);
            for (var node = recency.Last; node is not null; node = node.Previous)
            {
                var entry = node.Value;
                snapshot.Add(new TcpPeerInfo(entry.RemoteAddress, entry.SentBytes, entry.ReceivedBytes,
                    entry.FirstSeen, entry.LastSeen));
            }
            return snapshot;
        }
    }

    private DateTimeOffset ReadClock()
    {
        var now = clock();
        if (now < lastClock) return lastClock;
        lastClock = now;
        return now;
    }

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool TryNormalize(IPAddress? address, out AddressKey result)
    {
        result = default;
        if (address is null || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
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

    private readonly record struct AddressKey(bool IsIpv4, ulong High, ulong Low)
    {
        public bool IsUnicast => (High != 0 || Low != 0) && !(IsIpv4 && Low == uint.MaxValue)
            && !(IsIpv4 ? (Low & 0xF0000000) == 0xE0000000 : (High >> 56) == 0xFF);

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

    private sealed class PeerEntry
    {
        public PeerEntry(string remoteAddress, bool isSend, int bytes, DateTimeOffset firstSeen)
        {
            RemoteAddress = remoteAddress;
            FirstSeen = firstSeen;
            Observe(isSend, bytes, firstSeen);
            Node = new LinkedListNode<PeerEntry>(this);
        }

        public string RemoteAddress { get; }
        public long SentBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; private set; }
        public LinkedListNode<PeerEntry> Node { get; }

        public void Observe(bool isSend, int bytes, DateTimeOffset now)
        {
            if (isSend) SentBytes = SaturatingAdd(SentBytes, bytes);
            else ReceivedBytes = SaturatingAdd(ReceivedBytes, bytes);
            LastSeen = now;
        }
    }
}
