using System.Collections;
using System.Net;
using System.Reflection;
using SaruMon.Core;

namespace SaruMon.Tests;

internal static class UdpFlowTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress Remote = IPAddress.Parse("198.51.100.20");
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    public static int Run()
    {
        FiltersAndAccumulatesSelectedAddress();
        SeparatesEndpointPairs();
        ChangingSelectionClearsObservationsAndDrops();
        MappedIpv4UsesOneFlow();
        Ipv6MatchingOmitsEtwUnavailableScope();
        HistoryDoesNotExpireAndFirstSeenIsStable();
        FirstAndLastSeenBelongToEachFlow();
        CapacityPreservesExistingHistory();
        DefaultCapacityAndInvalidLimits();
        InvalidEventsDoNotPolluteFlows();
        CountersSaturate();
        DroppedEventsSaturate();
        ConcurrentEventsDoNotLoseCounts();
        SnapshotsAreIndependent();
        BackwardClockKeepsTimestampOrder();
        return 15;
    }

    private static void FiltersAndAccumulatesSelectedAddress()
    {
        var tracker = new UdpFlowTracker(clock: () => Epoch);
        tracker.Record(Local, 5000, Remote, 443, 50);
        Assert(tracker.Snapshot().Count == 0 && tracker.DroppedEvents == 0, "No selected address must mean no observed flows or capacity drops.");
        tracker.SetLocalAddresses([Local]);
        tracker.Record(IPAddress.Parse("192.0.2.11"), 5000, Remote, 443, 500);
        tracker.Record(Local, 5000, Remote, 443, 50);
        tracker.Record(Local, 5000, Remote, 443, 0);
        tracker.Record(Local, 5000, Remote, 443, 100);
        var flow = tracker.Snapshot().Single();
        Assert(flow.LocalAddress == Local.ToString() && flow.RemoteAddress == Remote.ToString(), "Flow endpoints changed.");
        Assert(flow.Packets == 3 && flow.Bytes == 150, "Send totals or zero-byte datagram counting were incorrect.");
        Assert(flow.FirstSeen == Epoch && flow.LastSeen == Epoch, "First observation timestamps were not initialized together.");
    }

    private static void SeparatesEndpointPairs()
    {
        var secondLocal = IPAddress.Parse("192.0.2.11");
        var tracker = new UdpFlowTracker();
        tracker.SetLocalAddresses([Local, secondLocal]);
        tracker.Record(Local, 5000, Remote, 53, 10);
        tracker.Record(Local, 5001, Remote, 53, 20);
        tracker.Record(Local, 5000, Remote, 443, 30);
        tracker.Record(secondLocal, 5000, Remote, 53, 40);
        tracker.Record(Local, 5000, IPAddress.Parse("198.51.100.21"), 53, 50);
        var flows = tracker.Snapshot();
        Assert(flows.Count == 5, "Distinct source/destination address and port pairs were merged.");
    }

    private static void ChangingSelectionClearsObservationsAndDrops()
    {
        var tracker = new UdpFlowTracker(maxFlows: 1);
        var selected = new List<IPAddress> { Local };
        tracker.SetLocalAddresses(selected);
        tracker.Record(Local, 5000, Remote, 443, 10);
        tracker.Record(Local, 5001, Remote, 443, 10);
        tracker.SetLocalAddresses([Local.MapToIPv6(), Local]);
        Assert(tracker.Snapshot().Single().Packets == 1 && tracker.DroppedEvents == 1, "Equivalent selection reset counters or drop metadata.");
        selected.Clear();
        tracker.Record(Local, 5000, Remote, 443, 10);
        Assert(tracker.Snapshot().Single().Packets == 2, "External mutation changed the copied address selection.");
        var replacement = IPAddress.Parse("192.0.2.12");
        tracker.SetLocalAddresses([replacement]);
        Assert(tracker.Snapshot().Count == 0 && tracker.DroppedEvents == 0, "Changing selection retained previous observations or drops.");
        tracker.Record(Local, 5000, Remote, 443, 100);
        tracker.Record(replacement, 5000, Remote, 443, 20);
        Assert(tracker.Snapshot().Single().Bytes == 20, "Old selection continued affecting counters.");
        tracker.SetLocalAddresses(Array.Empty<IPAddress>());
        Assert(tracker.Snapshot().Count == 0 && tracker.DroppedEvents == 0, "Clearing selection did not clear session state.");
    }

    private static void MappedIpv4UsesOneFlow()
    {
        var tracker = new UdpFlowTracker();
        tracker.SetLocalAddresses([Local.MapToIPv6()]);
        tracker.Record(Local, 5000, Remote.MapToIPv6(), 443, 10);
        tracker.Record(Local.MapToIPv6(), 5000, Remote, 443, 20);
        var flow = tracker.Snapshot().Single();
        Assert(flow.Packets == 2 && flow.Bytes == 30 && flow.LocalAddress == Local.ToString() && flow.RemoteAddress == Remote.ToString(),
            "Mapped IPv4 endpoints did not normalize to a shared IPv4 flow.");
    }

    private static void Ipv6MatchingOmitsEtwUnavailableScope()
    {
        var tracker = new UdpFlowTracker();
        tracker.SetLocalAddresses([IPAddress.Parse("fe80::1234%5")]);
        tracker.Record(IPAddress.Parse("fe80::1234"), 5000, IPAddress.Parse("fe80::5678%8"), 443, 10);
        tracker.Record(IPAddress.Parse("fe80::1234%99"), 5000, IPAddress.Parse("fe80::5678"), 443, 20);
        var flow = tracker.Snapshot().Single();
        Assert(flow.Bytes == 30 && flow.LocalAddress == "fe80::1234" && flow.RemoteAddress == "fe80::5678",
            "IPv6 matching must use address bytes without scope, which ETW does not supply.");
    }

    private static void HistoryDoesNotExpireAndFirstSeenIsStable()
    {
        var now = Epoch;
        var tracker = new UdpFlowTracker(clock: () => now);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(Local, 5000, Remote, 443, 100);
        now = Epoch.AddYears(1);
        var retained = tracker.Snapshot().Single();
        Assert(retained.Bytes == 100 && retained.FirstSeen == Epoch && retained.LastSeen == Epoch,
            "Time alone removed history or changed the original observation times.");
        tracker.Record(Local, 5000, Remote, 443, 25);
        var resumed = tracker.Snapshot().Single();
        Assert(resumed.Packets == 2 && resumed.Bytes == 125 && resumed.FirstSeen == Epoch && resumed.LastSeen == now,
            "Resumed traffic reset session totals or moved FirstSeen.");
    }

    private static void FirstAndLastSeenBelongToEachFlow()
    {
        var now = Epoch;
        var tracker = new UdpFlowTracker(clock: () => now);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(Local, 5000, Remote, 53, 10);
        now = Epoch.AddSeconds(10);
        tracker.Record(Local, 5000, Remote, 53, 20);
        now = Epoch.AddSeconds(20);
        tracker.Record(Local, 5001, Remote, 443, 30);
        now = Epoch.AddSeconds(30);
        tracker.Record(Local, 5000, Remote, 53, 40);
        var snapshot = tracker.Snapshot();
        Assert(snapshot[0].LocalPort == 5000 && snapshot[0].FirstSeen == Epoch && snapshot[0].LastSeen == now,
            "Updated rows must retain their first time and move to latest-observed order.");
        Assert(snapshot[1].FirstSeen == Epoch.AddSeconds(20) && snapshot[1].LastSeen == Epoch.AddSeconds(20),
            "One flow's timing affected another flow.");
    }

    private static void CapacityPreservesExistingHistory()
    {
        var tracker = new UdpFlowTracker(maxFlows: 2, clock: () => Epoch);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(Local, 5000, Remote, 53, 10);
        tracker.Record(Local, 5001, Remote, 53, 20);
        tracker.Record(Local, 5000, Remote, 53, 30);
        tracker.Record(Local, 5002, Remote, 53, 40);
        tracker.Record(Local, 5002, Remote, 53, 50);
        var snapshot = tracker.Snapshot();
        Assert(snapshot.Select(row => row.LocalPort).SequenceEqual(new[] { 5000, 5001 }), "Capacity overflow evicted accumulated history.");
        Assert(snapshot[0].Bytes == 40 && snapshot[1].Bytes == 20 && tracker.DroppedEvents == 2,
            "Overflow must count each unrecorded event without altering retained totals.");
        for (var port = 6000; port < 9000; port++) tracker.Record(Local, port, Remote, 53, 1);
        tracker.Record(Local, 5001, Remote, 53, 15);
        Assert(tracker.Snapshot().Count == 2 && tracker.DroppedEvents == 3002 && tracker.Snapshot()[0].Bytes == 35,
            "At capacity, known flows must keep accumulating while new events are explicitly counted as dropped.");
    }

    private static void DefaultCapacityAndInvalidLimits()
    {
        foreach (var capacity in new[] { 0, -1 })
        {
            var rejected = false;
            try { _ = new UdpFlowTracker(maxFlows: capacity); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Nonpositive capacity must be rejected.");
        }
        var tracker = new UdpFlowTracker(clock: () => Epoch);
        tracker.SetLocalAddresses([Local]);
        for (var port = 1; port <= 2049; port++) tracker.Record(Local, port, Remote, 53, 1);
        Assert(tracker.Snapshot().Count == 2048 && tracker.DroppedEvents == 1, "The default bound must retain 2048 flows and count the excess event.");
    }

    private static void InvalidEventsDoNotPolluteFlows()
    {
        var tracker = new UdpFlowTracker();
        tracker.SetLocalAddresses([Local, IPAddress.Any, IPAddress.Broadcast, IPAddress.Parse("224.0.0.1")]);
        foreach (var port in new[] { -1, 0, 65536 })
        {
            tracker.Record(Local, port, Remote, 53, 10);
            tracker.Record(Local, 5000, Remote, port, 10);
        }
        tracker.Record(Local, 5000, Remote, 53, -1);
        tracker.Record(null!, 5000, Remote, 53, 1);
        tracker.Record(Local, 5000, null!, 53, 1);
        tracker.Record(IPAddress.Any, 5000, Remote, 53, 1);
        tracker.Record(Local, 5000, IPAddress.Any, 53, 1);
        tracker.Record(IPAddress.Broadcast, 5000, Remote, 53, 1);
        tracker.Record(IPAddress.Parse("224.0.0.1"), 5000, Remote, 53, 1);
        tracker.Record(Local, 5000, IPAddress.IPv6Loopback, 53, 1);
        Assert(tracker.Snapshot().Count == 0 && tracker.DroppedEvents == 0, "Invalid inputs were accepted or mislabeled as capacity drops.");
        tracker.Record(Local, 65535, IPAddress.Broadcast, 65535, 0);
        tracker.Record(Local, 5000, IPAddress.Parse("239.0.0.1"), 5353, 10);
        Assert(tracker.Snapshot().Count == 2, "Valid UDP broadcast/multicast destinations were rejected.");
    }

    private static void CountersSaturate()
    {
        var tracker = new UdpFlowTracker();
        tracker.SetLocalAddresses([Local]);
        tracker.Record(Local, 5000, Remote, 53, 1);
        // Emitting 9e18 events is impractical. Seed storage at the boundary,
        // then exercise the public Record path to detect integer-wrap regressions.
        var storage = (IDictionary)typeof(UdpFlowTracker).GetField("flows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker)!;
        var entry = storage.Values.Cast<object>().Single();
        foreach (var counter in new[] { "Packets", "Bytes" })
            entry.GetType().GetProperty(counter)!.SetValue(entry, long.MaxValue - 1);
        tracker.Record(Local, 5000, Remote, 53, int.MaxValue);
        tracker.Record(Local, 5000, Remote, 53, 1);
        var flow = tracker.Snapshot().Single();
        Assert(flow.Packets == long.MaxValue && flow.Bytes == long.MaxValue, "Send counters wrapped instead of saturating.");
    }

    private static void DroppedEventsSaturate()
    {
        var tracker = new UdpFlowTracker(maxFlows: 1);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(Local, 5000, Remote, 53, 1);
        typeof(UdpFlowTracker).GetField("droppedEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tracker, long.MaxValue - 1);
        tracker.Record(Local, 5001, Remote, 53, 1);
        tracker.Record(Local, 5001, Remote, 53, 1);
        Assert(tracker.DroppedEvents == long.MaxValue && tracker.Snapshot().Single().Bytes == 1,
            "Capacity drop count wrapped or modified retained history.");
    }

    private static void ConcurrentEventsDoNotLoseCounts()
    {
        var tracker = new UdpFlowTracker(clock: () => Epoch);
        tracker.SetLocalAddresses([Local]);
        Parallel.For(0, 10000, _ => tracker.Record(Local, 5000, Remote, 443, 2));
        var flow = tracker.Snapshot().Single();
        Assert(flow.Packets == 10000 && flow.Bytes == 20000, "Concurrent send updates lost increments.");
    }

    private static void SnapshotsAreIndependent()
    {
        var tracker = new UdpFlowTracker(clock: () => Epoch);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(Local, 5000, Remote, 53, 10);
        var first = tracker.Snapshot();
        tracker.Record(Local, 5000, Remote, 53, 20);
        tracker.SetLocalAddresses(Array.Empty<IPAddress>());
        Assert(first.Single().Packets == 1 && first.Single().Bytes == 10 && first.Single().FirstSeen == Epoch,
            "A returned snapshot changed after later updates or selection changes.");
    }

    private static void BackwardClockKeepsTimestampOrder()
    {
        var now = Epoch.AddSeconds(100);
        var tracker = new UdpFlowTracker(clock: () => now);
        tracker.SetLocalAddresses([Local]);
        tracker.Record(Local, 5000, Remote, 53, 10);
        now = Epoch.AddSeconds(90);
        tracker.Record(Local, 5000, Remote, 53, 10);
        Assert(tracker.Snapshot().Single().FirstSeen == Epoch.AddSeconds(100) && tracker.Snapshot().Single().LastSeen == Epoch.AddSeconds(100),
            "Backward wall clock made LastSeen precede FirstSeen.");
        now = Epoch.AddSeconds(160);
        tracker.Record(Local, 5000, Remote, 53, 10);
        Assert(tracker.Snapshot().Single().FirstSeen == Epoch.AddSeconds(100) && tracker.Snapshot().Single().LastSeen == now,
            "Clock recovery moved the original FirstSeen timestamp.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("UDP flow test failed: " + message);
    }
}
