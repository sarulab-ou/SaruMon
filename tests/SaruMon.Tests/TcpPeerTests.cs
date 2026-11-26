using System.Collections;
using System.Net;
using System.Reflection;
using SaruMon.Core;

namespace SaruMon.Tests;

internal static class TcpPeerTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress Remote = IPAddress.Parse("198.51.100.20");
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    public static int Run()
    {
        AggregatesDirectionsAcrossSelectedLocalAddresses();
        DifferentPeersKeepIndependentTotals();
        EquivalentMappedAddressesSharePeer();
        Ipv6ScopeIsOmittedForEtwMatching();
        SelectionChangesClearHistoryAndDrops();
        FirstSeenSurvivesIdleAndClockChanges();
        CapacityKeepsExistingTotals();
        DefaultCapacityAndInvalidLimits();
        InvalidEventsDoNotAffectTotals();
        CountersAndDropsSaturate();
        ConcurrentUpdatesKeepBothDirections();
        SnapshotsRemainIndependent();
        ObservedConnectionsAreIndependentOfBytes();
        ConnectionsUpdateFirstAndLastTimes();
        ConnectionsUseSelectionNormalizationAndCapacity();
        return 15;
    }

    private static void AggregatesDirectionsAcrossSelectedLocalAddresses()
    {
        var local2 = IPAddress.Parse("192.0.2.11");
        var tracker = new TcpPeerTracker(clock: () => Epoch);
        tracker.Record(true, Local, Remote, 9999);
        Assert(tracker.Snapshot().Count == 0, "No selection must produce no observations.");
        tracker.SetLocalAddresses([Local, local2]);
        tracker.Record(true, Local, Remote, 100);
        tracker.Record(false, local2, Remote, 250);
        tracker.Record(true, local2, Remote, 40);
        tracker.Record(false, Local, Remote, 70);
        tracker.Record(true, IPAddress.Parse("192.0.2.12"), Remote, 9999);
        var peer = tracker.Snapshot().Single();
        Assert(peer.RemoteAddress == Remote.ToString() && peer.SentBytes == 140 && peer.ReceivedBytes == 320 && peer.ConnectionCount == 0,
            "A remote peer must aggregate selected connections while preserving independent send/receive totals.");
    }

    private static void DifferentPeersKeepIndependentTotals()
    {
        var now = Epoch;
        var tracker = new TcpPeerTracker(clock: () => now);
        tracker.SetLocalAddresses([Local]);
        var remote2 = IPAddress.Parse("198.51.100.21");
        tracker.Record(false, Remote, Local, 9999); // Reversed endpoints must fail the local selection filter.
        tracker.Record(true, Local, Remote, 30);
        now = now.AddSeconds(10);
        tracker.Record(false, Local, remote2, 90);
        now = now.AddSeconds(10);
        tracker.Record(false, Local, Remote, 50);
        var peers = tracker.Snapshot();
        Assert(peers.Count == 2 && peers[0].RemoteAddress == Remote.ToString()
            && peers[0].SentBytes == 30 && peers[0].ReceivedBytes == 50 && peers[0].FirstSeen == Epoch && peers[0].LastSeen == now,
            "Peer totals, first time, or activity order were mixed.");
        Assert(peers[1].SentBytes == 0 && peers[1].ReceivedBytes == 90 && peers[1].FirstSeen == Epoch.AddSeconds(10),
            "Receiving first must not be counted as uploading.");
    }

    private static void EquivalentMappedAddressesSharePeer()
    {
        var tracker = new TcpPeerTracker();
        tracker.SetLocalAddresses([Local.MapToIPv6()]);
        tracker.Record(true, Local, Remote.MapToIPv6(), 10);
        tracker.Record(false, Local.MapToIPv6(), Remote, 20);
        var peer = tracker.Snapshot().Single();
        Assert(peer.RemoteAddress == Remote.ToString() && peer.SentBytes == 10 && peer.ReceivedBytes == 20,
            "IPv4-mapped addresses must not split one peer into separate rows.");
    }

    private static void Ipv6ScopeIsOmittedForEtwMatching()
    {
        var tracker = new TcpPeerTracker();
        tracker.SetLocalAddresses([IPAddress.Parse("fe80::10%5")]);
        tracker.Record(true, IPAddress.Parse("fe80::10"), IPAddress.Parse("fe80::20%9"), 10);
        tracker.Record(false, IPAddress.Parse("fe80::10%99"), IPAddress.Parse("fe80::20"), 20);
        var peer = tracker.Snapshot().Single();
        Assert(peer.RemoteAddress == "fe80::20" && peer.SentBytes == 10 && peer.ReceivedBytes == 20,
            "ETW's missing IPv6 scope ID prevented matching or merged the directions incorrectly.");
    }

    private static void SelectionChangesClearHistoryAndDrops()
    {
        var selected = new List<IPAddress> { Local };
        var tracker = new TcpPeerTracker(maxPeers: 1);
        tracker.SetLocalAddresses(selected);
        selected.Clear();
        tracker.Record(true, Local, Remote, 10);
        tracker.Record(true, Local, IPAddress.Parse("198.51.100.21"), 5);
        tracker.SetLocalAddresses([Local.MapToIPv6(), Local]);
        Assert(tracker.Snapshot().Single().SentBytes == 10 && tracker.DroppedEvents == 1,
            "Equivalent selection or caller mutation changed the recorded history.");
        var local2 = IPAddress.Parse("192.0.2.11");
        tracker.SetLocalAddresses([local2]);
        tracker.Record(true, Local, Remote, 9999);
        tracker.Record(false, local2, Remote, 20);
        var peer = tracker.Snapshot().Single();
        Assert(peer.SentBytes == 0 && peer.ReceivedBytes == 20 && tracker.DroppedEvents == 0,
            "Changing selection retained old totals or drops.");
        tracker.SetLocalAddresses([]);
        Assert(tracker.Snapshot().Count == 0 && tracker.DroppedEvents == 0, "An empty selection retained observations.");
    }

    private static void FirstSeenSurvivesIdleAndClockChanges()
    {
        var now = Epoch;
        var tracker = new TcpPeerTracker(clock: () => now);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(true, Local, Remote, 10);
        now = now.AddYears(1);
        var idle = tracker.Snapshot().Single();
        Assert(idle.FirstSeen == Epoch && idle.LastSeen == Epoch, "Idle time expired a row or modified its timestamps.");
        tracker.Record(false, Local, Remote, 20);
        var last = now;
        now = Epoch.AddDays(-1);
        tracker.Record(true, Local, Remote, 5);
        var peer = tracker.Snapshot().Single();
        Assert(peer.FirstSeen == Epoch && peer.LastSeen == last && peer.SentBytes == 15 && peer.ReceivedBytes == 20,
            "Clock reversal or resumed traffic damaged first-seen time or totals.");
    }

    private static void CapacityKeepsExistingTotals()
    {
        var tracker = new TcpPeerTracker(maxPeers: 1);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(true, Local, Remote, 10);
        var extra = IPAddress.Parse("198.51.100.21");
        tracker.Record(true, Local, extra, 200);
        tracker.Record(false, Local, extra, 300);
        tracker.Record(false, Local, Remote, 30);
        var peer = tracker.Snapshot().Single();
        Assert(peer.RemoteAddress == Remote.ToString() && peer.SentBytes == 10 && peer.ReceivedBytes == 30 && tracker.DroppedEvents == 2,
            "Overflow must retain old totals and count each rejected new-peer event.");
    }

    private static void DefaultCapacityAndInvalidLimits()
    {
        foreach (var capacity in new[] { 0, -1 })
        {
            var rejected = false;
            try { _ = new TcpPeerTracker(capacity); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Nonpositive capacity was accepted.");
        }
        var tracker = new TcpPeerTracker();
        tracker.SetLocalAddresses([Local]);
        for (var index = 0; index < 2049; index++)
            tracker.Record(true, Local, new IPAddress(new byte[] { 198, 51, (byte)(index >> 8), (byte)index }), 1);
        Assert(tracker.Snapshot().Count == 2048 && tracker.DroppedEvents == 1, "Default peer capacity changed or evicted history.");
    }

    private static void InvalidEventsDoNotAffectTotals()
    {
        var tracker = new TcpPeerTracker(maxPeers: 1);
        tracker.SetLocalAddresses([Local, IPAddress.Any, IPAddress.Broadcast, IPAddress.Parse("224.0.0.1")]);
        tracker.Record(true, null!, Remote, 1);
        tracker.Record(false, Local, null!, 1);
        tracker.Record(true, Local, Remote, -1);
        tracker.Record(false, Local, IPAddress.Any, 1);
        tracker.Record(true, IPAddress.Any, Remote, 1);
        tracker.Record(false, Local, IPAddress.IPv6Loopback, 1);
        tracker.Record(true, Local, IPAddress.Broadcast, 1);
        tracker.Record(true, Local, IPAddress.Parse("224.0.0.1"), 1);
        tracker.Record(true, IPAddress.Broadcast, Remote, 1);
        tracker.Record(true, IPAddress.Parse("224.0.0.1"), Remote, 1);
        Assert(tracker.Snapshot().Count == 0 && tracker.DroppedEvents == 0, "Invalid inputs altered counters or capacity loss.");
        tracker.Record(false, Local, Remote, 0);
        var peer = tracker.Snapshot().Single();
        Assert(peer.SentBytes == 0 && peer.ReceivedBytes == 0, "A zero-byte observation must not add bytes.");
    }

    private static void CountersAndDropsSaturate()
    {
        var tracker = new TcpPeerTracker(maxPeers: 1);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(true, Local, Remote, 1);
        // Seed near Int64's boundary, then exercise the public event path.
        var storage = (IDictionary)typeof(TcpPeerTracker).GetField("peers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker)!;
        var entry = storage.Values.Cast<object>().Single();
        entry.GetType().GetProperty("SentBytes")!.SetValue(entry, long.MaxValue - 1);
        entry.GetType().GetProperty("ReceivedBytes")!.SetValue(entry, long.MaxValue - 1);
        entry.GetType().GetProperty("ConnectionCount")!.SetValue(entry, long.MaxValue - 1);
        tracker.Record(true, Local, Remote, int.MaxValue);
        tracker.Record(false, Local, Remote, int.MaxValue);
        tracker.RecordConnection(Local, Remote);
        tracker.RecordConnection(Local, Remote);
        typeof(TcpPeerTracker).GetField("droppedEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tracker, long.MaxValue - 1);
        tracker.Record(true, Local, IPAddress.Parse("198.51.100.21"), 1);
        tracker.Record(false, Local, IPAddress.Parse("198.51.100.21"), 1);
        var peer = tracker.Snapshot().Single();
        Assert(peer.SentBytes == long.MaxValue && peer.ReceivedBytes == long.MaxValue && peer.ConnectionCount == long.MaxValue
            && tracker.DroppedEvents == long.MaxValue,
            "Counters wrapped instead of saturating.");
    }

    private static void ConcurrentUpdatesKeepBothDirections()
    {
        var tracker = new TcpPeerTracker(clock: () => Epoch);
        tracker.SetLocalAddresses([Local]);
        Parallel.For(0, 10000, index =>
        {
            tracker.Record(index % 2 == 0, Local, Remote, index % 2 == 0 ? 2 : 3);
            tracker.RecordConnection(Local, Remote);
        });
        var peer = tracker.Snapshot().Single();
        Assert(peer.SentBytes == 10000 && peer.ReceivedBytes == 15000 && peer.ConnectionCount == 10000,
            "Concurrent events lost directional bytes or connection increments.");
    }

    private static void SnapshotsRemainIndependent()
    {
        var tracker = new TcpPeerTracker(clock: () => Epoch);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(true, Local, Remote, 10);
        var earlier = tracker.Snapshot();
        tracker.Record(false, Local, Remote, 20);
        tracker.RecordConnection(Local, Remote);
        tracker.SetLocalAddresses([]);
        Assert(earlier.Single().SentBytes == 10 && earlier.Single().ReceivedBytes == 0 && earlier.Single().FirstSeen == Epoch
            && earlier.Single().ConnectionCount == 0,
            "A previous immutable snapshot changed after updates or a reset.");
    }

    private static void ObservedConnectionsAreIndependentOfBytes()
    {
        var tracker = new TcpPeerTracker();
        tracker.SetLocalAddresses([Local]);
        tracker.Record(true, Local, Remote, 100);
        tracker.Record(false, Local, Remote, 200);
        Assert(tracker.Snapshot().Single().ConnectionCount == 0,
            "Traffic on a preexisting connection must not invent an observed connection establishment.");
        tracker.RecordConnection(Local, Remote);
        tracker.RecordConnection(Local, Remote);
        for (var index = 0; index < 10; index++)
        {
            tracker.Record(true, Local, Remote, 1);
            tracker.Record(false, Local, Remote, 2);
        }
        var peer = tracker.Snapshot().Single();
        Assert(peer.ConnectionCount == 2 && peer.SentBytes == 110 && peer.ReceivedBytes == 220,
            "Data events inflated the connection count, or connect events added bytes.");
    }

    private static void ConnectionsUpdateFirstAndLastTimes()
    {
        var now = Epoch;
        var tracker = new TcpPeerTracker(clock: () => now);
        tracker.SetLocalAddresses([Local]);
        tracker.RecordConnection(Local, Remote);
        var first = tracker.Snapshot().Single();
        Assert(first.ConnectionCount == 1 && first.SentBytes == 0 && first.ReceivedBytes == 0
            && first.FirstSeen == Epoch && first.LastSeen == Epoch,
            "A connection without data must still create a timed observation.");
        now = now.AddSeconds(10);
        tracker.Record(true, Local, Remote, 50);
        now = now.AddSeconds(10);
        tracker.RecordConnection(Local, Remote);
        var peer = tracker.Snapshot().Single();
        Assert(peer.ConnectionCount == 2 && peer.SentBytes == 50 && peer.FirstSeen == Epoch && peer.LastSeen == now,
            "Connection activity failed to update last time or moved the first observation time.");
        now = Epoch.AddDays(-1);
        tracker.RecordConnection(Local, Remote);
        Assert(tracker.Snapshot().Single().LastSeen == Epoch.AddSeconds(20), "A backward wall clock moved the last connection time backwards.");
    }

    private static void ConnectionsUseSelectionNormalizationAndCapacity()
    {
        var tracker = new TcpPeerTracker(maxPeers: 1);
        tracker.RecordConnection(Local, Remote);
        tracker.SetLocalAddresses([Local.MapToIPv6()]);
        tracker.RecordConnection(Local, Remote.MapToIPv6());
        tracker.RecordConnection(Local.MapToIPv6(), Remote);
        tracker.RecordConnection(IPAddress.Parse("192.0.2.99"), Remote);
        tracker.RecordConnection(Local, IPAddress.Any);
        tracker.RecordConnection(null!, Remote);
        tracker.RecordConnection(Local, null!);
        tracker.RecordConnection(Local, IPAddress.Parse("224.0.0.1"));
        tracker.RecordConnection(Local, IPAddress.IPv6Loopback);
        tracker.RecordConnection(Local, IPAddress.Parse("198.51.100.21"));
        var peer = tracker.Snapshot().Single();
        Assert(peer.RemoteAddress == Remote.ToString() && peer.ConnectionCount == 2 && tracker.DroppedEvents == 1,
            "Connection filtering, mapped addresses, or capacity behavior differed from data observations.");
        tracker.SetLocalAddresses([IPAddress.Parse("fe80::10%5")]);
        tracker.RecordConnection(IPAddress.Parse("fe80::10"), IPAddress.Parse("fe80::20%9"));
        tracker.RecordConnection(IPAddress.Parse("fe80::10%99"), IPAddress.Parse("fe80::20"));
        Assert(tracker.Snapshot().Single().RemoteAddress == "fe80::20" && tracker.Snapshot().Single().ConnectionCount == 2
            && tracker.DroppedEvents == 0, "Connection reset or IPv6 scope normalization failed.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TCP peer test failed: " + message);
    }
}
