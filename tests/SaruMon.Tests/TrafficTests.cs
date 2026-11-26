using SaruMon.Core;

namespace SaruMon.Tests;

internal static class TrafficTests
{
    public static int Run()
    {
        FirstSampleAndRates();
        CounterResetDoesNotSpike();
        DisconnectAndReconnect();
        SwitchingInterfaceResetsTotals();
        InvalidIntervalRebaselines();
        ResetClearsTotals();
        LargeTotalsDoNotOverflow();
        RejectNegativeCounters();
        UnicastFirstSampleAndNonUnicastTraffic();
        UnicastDirectionsAreIndependent();
        UnicastCounterResetsAreIndependent();
        UnicastMissingCountersRecoverWithoutSpikes();
        UnicastDisconnectAndReconnect();
        UnicastInterfaceAndExplicitReset();
        UnicastByteResetAndInvalidInterval();
        LargeUnicastCountersDoNotOverflow();
        TcpActivityUsesBothFamiliesAndResends();
        TcpActivityRebaselinesAfterResetAndMissingRead();
        return 18;
    }

    private static TrafficSample Sample(TrafficSampler sampler, double time, long received, long sent,
        string id = "adapter-a", bool available = true, long? unicastReceived = null, long? unicastSent = null) =>
        sampler.Sample(new TrafficCounterReading(id, available, received, sent, unicastReceived, unicastSent), TimeSpan.FromSeconds(time));

    private static void FirstSampleAndRates()
    {
        var sampler = new TrafficSampler();
        var baseline = Sample(sampler, 10, 900_000, 100_000);
        Expect(baseline.IsAvailable && !baseline.HasRate && baseline.TotalReceivedBytes == 0,
            "First sample must not count bytes from before monitoring started.");
        var active = Sample(sampler, 10.5, 901_024, 100_256);
        Expect(active.HasRate && active.IsReceiving && active.IsSending, "Traffic must mark both directions active.");
        Equal(2048, active.DownloadBytesPerSecond, "Download rate must use real elapsed time.");
        Equal(512, active.UploadBytesPerSecond, "Upload rate must use real elapsed time.");
        Expect(active.TotalReceivedBytes == 1024 && active.TotalSentBytes == 256, "Session totals must use deltas.");
        var later = Sample(sampler, 12.5, 903_072, 100_256);
        Equal(1024, later.DownloadBytesPerSecond, "Delayed polls must use their actual interval.");
        Expect(later.IsReceiving && !later.IsSending, "Direction flags must be independent.");
        var idle = Sample(sampler, 13, 903_072, 100_256);
        Expect(idle.HasRate && !idle.IsReceiving && !idle.IsSending && idle.TotalReceivedBytes == 3072,
            "An idle sample must have zero rate without resetting totals.");
    }

    private static void CounterResetDoesNotSpike()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 1, 1000, 2000);
        Sample(sampler, 2, 1500, 2200);
        var reset = Sample(sampler, 3, 5, 7);
        Expect(!reset.HasRate && reset.TotalReceivedBytes == 500 && reset.TotalSentBytes == 200,
            "Counter reset must preserve totals and rebaseline.");
        var resumed = Sample(sampler, 3.5, 105, 57);
        Equal(200, resumed.DownloadBytesPerSecond, "Sampling must resume after counter reset.");
        Expect(resumed.TotalReceivedBytes == 600 && resumed.TotalSentBytes == 250, "Reset must not add historical traffic.");
        var oneDirectionReset = Sample(sampler, 4, 10, 1000);
        Expect(!oneDirectionReset.HasRate && oneDirectionReset.TotalSentBytes == 250,
            "A driver restart affecting one counter must rebaseline the whole observation.");
    }

    private static void DisconnectAndReconnect()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 1000, 2000);
        Sample(sampler, 1, 1100, 2050);
        var disconnected = Sample(sampler, 2, 0, 0, available: false);
        Expect(!disconnected.IsAvailable && !disconnected.HasRate && disconnected.TotalReceivedBytes == 100,
            "Disconnect must preserve totals while clearing the baseline.");
        var reconnected = Sample(sampler, 3, 5000, 6000);
        Expect(reconnected.IsAvailable && !reconnected.HasRate && reconnected.TotalReceivedBytes == 100,
            "Reconnect must not count the unobserved interval as a burst.");
        var next = Sample(sampler, 3.5, 5050, 6025);
        Equal(100, next.DownloadBytesPerSecond, "Reconnect must resume rate calculations.");
        Expect(next.TotalReceivedBytes == 150 && next.TotalSentBytes == 75, "Reconnect must retain session totals.");
    }

    private static void SwitchingInterfaceResetsTotals()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 100);
        Sample(sampler, 1, 200, 250);
        var switched = Sample(sampler, 2, 100_000, 300_000, "adapter-b");
        Expect(switched.InterfaceId == "adapter-b" && !switched.HasRate && switched.TotalReceivedBytes == 0,
            "Changing adapter must reset totals and establish a new baseline.");
        var second = Sample(sampler, 3, 100_010, 300_020, "adapter-b");
        Expect(second.TotalReceivedBytes == 10 && second.TotalSentBytes == 20, "Only the selected adapter may affect totals.");
        var missing = Sample(sampler, 4, 0, 0, "missing", available: false);
        Expect(!missing.IsAvailable && missing.TotalReceivedBytes == 0, "Selecting a missing adapter must reset old totals.");
    }

    private static void InvalidIntervalRebaselines()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 2, 100, 100);
        var repeated = Sample(sampler, 2, 200, 200);
        Expect(!repeated.HasRate && repeated.TotalReceivedBytes == 0, "Zero elapsed time must not divide by zero.");
        var backwards = Sample(sampler, 1, 300, 300);
        Expect(!backwards.HasRate, "Backwards timestamps must not produce negative rates.");
        var resumed = Sample(sampler, 1.5, 350, 325);
        Equal(100, resumed.DownloadBytesPerSecond, "A valid timestamp must resume after rebaselining.");
    }

    private static void ResetClearsTotals()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 100);
        Sample(sampler, 1, 200, 200);
        sampler.Reset();
        var reset = Sample(sampler, 2, 300, 300);
        Expect(!reset.HasRate && reset.TotalReceivedBytes == 0 && reset.TotalSentBytes == 0,
            "Explicit reset must clear totals and baseline.");
    }

    private static void LargeTotalsDoNotOverflow()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 0, 0);
        Sample(sampler, 1, long.MaxValue - 10, 0);
        Sample(sampler, 2, 0, 0);
        var sample = Sample(sampler, 3, 20, 0);
        Expect(sample.TotalReceivedBytes == long.MaxValue, "Totals must saturate instead of overflowing negative.");
    }

    private static void RejectNegativeCounters()
    {
        try { Sample(new TrafficSampler(), 0, -1, 0); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Negative available counters must be rejected.");
    }

    private static void UnicastFirstSampleAndNonUnicastTraffic()
    {
        var sampler = new TrafficSampler();
        var baseline = Sample(sampler, 0, 1000, 2000, unicastReceived: 100, unicastSent: 200);
        Expect(baseline.ReceivedUnicastDelta == 0 && baseline.SentUnicastDelta == 0
            && !baseline.IsUnicastReceiving && !baseline.IsUnicastSending,
            "First unicast counters must be supported but must not flash for historical packets.");
        var nonUnicast = Sample(sampler, .5, 1200, 2050, unicastReceived: 100, unicastSent: 200);
        Expect(nonUnicast.IsReceiving && nonUnicast.IsSending && !nonUnicast.IsUnicastReceiving && !nonUnicast.IsUnicastSending,
            "Non-unicast byte growth must not light unicast arrows.");
        Equal(400, nonUnicast.DownloadBytesPerSecond, "Unicast filtering must preserve all-traffic byte rates.");
        Expect(nonUnicast.TotalReceivedBytes == 200 && nonUnicast.TotalSentBytes == 50,
            "Unicast filtering must preserve all-traffic totals.");
        var legacy = Sample(new TrafficSampler(), 0, 100, 100);
        Expect(legacy.ReceivedUnicastDelta is null && legacy.SentUnicastDelta is null,
            "Readings without optional counters must report unicast as unavailable.");
    }

    private static void UnicastDirectionsAreIndependent()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 100, unicastReceived: 10, unicastSent: 20);
        var received = Sample(sampler, .5, 110, 100, unicastReceived: 11, unicastSent: 20);
        Expect(received.ReceivedUnicastDelta == 1 && received.SentUnicastDelta == 0
            && received.IsUnicastReceiving && !received.IsUnicastSending,
            "Even one received unicast packet must light only the receive arrow.");
        var sent = Sample(sampler, 1, 110, 120, unicastReceived: 11, unicastSent: 23);
        Expect(sent.ReceivedUnicastDelta == 0 && sent.SentUnicastDelta == 3
            && !sent.IsUnicastReceiving && sent.IsUnicastSending,
            "Sent unicast packets must light only the send arrow.");
        var both = Sample(sampler, 1.5, 120, 140, unicastReceived: 12, unicastSent: 24);
        Expect(both.IsUnicastReceiving && both.IsUnicastSending, "Bidirectional unicast must light both arrows.");
        var idle = Sample(sampler, 2, 120, 140, unicastReceived: 12, unicastSent: 24);
        Expect(idle.ReceivedUnicastDelta == 0 && idle.SentUnicastDelta == 0
            && !idle.IsUnicastReceiving && !idle.IsUnicastSending, "Idle unicast arrows must turn off.");
    }

    private static void UnicastCounterResetsAreIndependent()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 200, unicastReceived: 10, unicastSent: 20);
        Sample(sampler, .5, 200, 300, unicastReceived: 11, unicastSent: 22);
        var receiveReset = Sample(sampler, 1, 300, 400, unicastReceived: 0, unicastSent: 25);
        Expect(receiveReset.ReceivedUnicastDelta == 0 && receiveReset.SentUnicastDelta == 3 && receiveReset.HasRate,
            "A receive unicast reset must not hide valid send activity or byte rates.");
        var resumed = Sample(sampler, 1.5, 400, 500, unicastReceived: 1, unicastSent: 26);
        Expect(resumed.ReceivedUnicastDelta == 1 && resumed.SentUnicastDelta == 1,
            "Unicast observation must resume immediately after its counter reset.");
        var sendReset = Sample(sampler, 2, 500, 600, unicastReceived: 2, unicastSent: 0);
        Expect(sendReset.ReceivedUnicastDelta == 1 && sendReset.SentUnicastDelta == 0
            && sendReset.TotalReceivedBytes == 400 && sendReset.TotalSentBytes == 400,
            "A send unicast reset must preserve receive activity and the byte totals.");
    }

    private static void UnicastMissingCountersRecoverWithoutSpikes()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 100, unicastSent: 50);
        var acquired = Sample(sampler, .5, 200, 200, unicastReceived: 100, unicastSent: 51);
        Expect(acquired.ReceivedUnicastDelta == 0 && acquired.SentUnicastDelta == 1,
            "A newly available counter needs a baseline without suppressing the other direction.");
        var missing = Sample(sampler, 1, 300, 300, unicastReceived: 102);
        Expect(missing.ReceivedUnicastDelta == 2 && missing.SentUnicastDelta is null && !missing.IsUnicastSending,
            "A missing unicast counter must not fall back to all-traffic activity.");
        var invalid = Sample(sampler, 1.5, 400, 400, unicastReceived: -1, unicastSent: 60);
        Expect(invalid.ReceivedUnicastDelta is null && invalid.SentUnicastDelta == 0 && invalid.HasRate,
            "An invalid optional counter must not break byte sampling or count an unobserved interval.");
        var recovered = Sample(sampler, 2, 500, 500, unicastReceived: 500, unicastSent: 62);
        Expect(recovered.ReceivedUnicastDelta == 0 && recovered.SentUnicastDelta == 2
            && recovered.TotalReceivedBytes == 400 && recovered.TotalSentBytes == 400,
            "Recovered counters must rebaseline independently and preserve all byte totals.");
        var next = Sample(sampler, 2.5, 600, 600, unicastReceived: 501, unicastSent: 63);
        Expect(next.ReceivedUnicastDelta == 1 && next.SentUnicastDelta == 1, "Both counters must resume after recovery.");
    }

    private static void UnicastDisconnectAndReconnect()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 200, unicastReceived: 10, unicastSent: 20);
        Sample(sampler, .5, 200, 300, unicastReceived: 11, unicastSent: 21);
        var disconnected = Sample(sampler, 1, 0, 0, available: false, unicastReceived: 100, unicastSent: 200);
        Expect(disconnected.ReceivedUnicastDelta is null && disconnected.SentUnicastDelta is null
            && !disconnected.IsUnicastReceiving && !disconnected.IsUnicastSending,
            "Disconnected adapters must not report optional packet activity.");
        var reconnected = Sample(sampler, 1.5, 1000, 2000, unicastReceived: 100, unicastSent: 200);
        Expect(reconnected.ReceivedUnicastDelta == 0 && reconnected.SentUnicastDelta == 0,
            "Reconnect must not flash for packets accumulated while disconnected.");
        var resumed = Sample(sampler, 2, 1100, 2100, unicastReceived: 101, unicastSent: 201);
        Expect(resumed.ReceivedUnicastDelta == 1 && resumed.SentUnicastDelta == 1
            && resumed.TotalReceivedBytes == 200 && resumed.TotalSentBytes == 200,
            "Reconnect must resume packet observation without resetting observed byte totals.");
    }

    private static void UnicastInterfaceAndExplicitReset()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 100, unicastReceived: 10, unicastSent: 10);
        Sample(sampler, .5, 200, 200, unicastReceived: 20, unicastSent: 20);
        var switched = Sample(sampler, 1, 5000, 5000, "adapter-b", unicastReceived: 500, unicastSent: 500);
        Expect(switched.ReceivedUnicastDelta == 0 && switched.SentUnicastDelta == 0 && switched.TotalReceivedBytes == 0,
            "An interface change must rebaseline unicast without borrowing the previous adapter's counters.");
        var next = Sample(sampler, 1.5, 5100, 5200, "adapter-b", unicastReceived: 501, unicastSent: 502);
        Expect(next.ReceivedUnicastDelta == 1 && next.SentUnicastDelta == 2, "The selected interface's deltas must be used.");
        sampler.Reset();
        var reset = Sample(sampler, 2, 6000, 6000, "adapter-b", unicastReceived: 600, unicastSent: 600);
        Expect(reset.ReceivedUnicastDelta == 0 && reset.SentUnicastDelta == 0 && reset.TotalReceivedBytes == 0,
            "An explicit reset must clear the packet baseline as well as byte totals.");
    }

    private static void UnicastByteResetAndInvalidInterval()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 100, 100, unicastReceived: 10, unicastSent: 10);
        Sample(sampler, .5, 200, 200, unicastReceived: 20, unicastSent: 20);
        var reset = Sample(sampler, 1, 0, 300, unicastReceived: 500, unicastSent: 500);
        Expect(!reset.HasRate && reset.ReceivedUnicastDelta == 0 && reset.SentUnicastDelta == 0,
            "A byte-counter restart must rebaseline packet observation too.");
        var repeated = Sample(sampler, 1, 100, 400, unicastReceived: 600, unicastSent: 600);
        Expect(repeated.ReceivedUnicastDelta == 0 && repeated.SentUnicastDelta == 0,
            "Repeated timestamps must not produce packet activity.");
        var backwards = Sample(sampler, .5, 200, 500, unicastReceived: 700, unicastSent: 700);
        Expect(backwards.ReceivedUnicastDelta == 0 && backwards.SentUnicastDelta == 0,
            "Backward timestamps must rebaseline packet observation.");
        var resumed = Sample(sampler, 1, 300, 600, unicastReceived: 701, unicastSent: 702);
        Expect(resumed.ReceivedUnicastDelta == 1 && resumed.SentUnicastDelta == 2 && resumed.TotalReceivedBytes == 200,
            "Packet and byte sampling must recover after invalid intervals.");
    }

    private static void LargeUnicastCountersDoNotOverflow()
    {
        var sampler = new TrafficSampler();
        Sample(sampler, 0, 0, 0, unicastReceived: 0, unicastSent: 0);
        var maximum = Sample(sampler, .5, 10, 10, unicastReceived: long.MaxValue, unicastSent: long.MaxValue);
        Expect(maximum.ReceivedUnicastDelta == long.MaxValue && maximum.SentUnicastDelta == long.MaxValue,
            "Large unicast counters must preserve a nonnegative delta.");
        var reset = Sample(sampler, 1, 20, 20, unicastReceived: 0, unicastSent: 0);
        Expect(reset.ReceivedUnicastDelta == 0 && reset.SentUnicastDelta == 0, "Counter wrap must rebaseline without a negative spike.");
        var next = Sample(sampler, 1.5, 30, 30, unicastReceived: 1, unicastSent: 1);
        Expect(next.ReceivedUnicastDelta == 1 && next.SentUnicastDelta == 1, "Packet sampling must resume after wrap.");
    }

    private static void TcpActivityUsesBothFamiliesAndResends()
    {
        var sampler = new TcpIndicatorSampler();
        var first = sampler.Sample(new(10, 20, 3), new(30, 40, 4), hasInterfaceInterval: true);
        Expect(first == new TcpActivity(true, false, false), "TCP counters need an initial baseline even when the interface is active.");
        var receive = sampler.Sample(new(11, 20, 3), new(30, 40, 4), hasInterfaceInterval: true);
        Expect(receive == new TcpActivity(true, true, false), "IPv4 TCP receive activity should affect receive only.");
        var resend = sampler.Sample(new(11, 20, 3), new(30, 40, 5), hasInterfaceInterval: true);
        Expect(resend == new TcpActivity(true, false, true), "An IPv6 retransmission should count as send activity.");
        var both = sampler.Sample(new(11, 21, 3), new(31, 40, 5), hasInterfaceInterval: true);
        Expect(both == new TcpActivity(true, true, true), "TCP send and receive may be active in different address families.");
    }

    private static void TcpActivityRebaselinesAfterResetAndMissingRead()
    {
        var sampler = new TcpIndicatorSampler();
        sampler.Sample(new(10, 20, 3), new(30, 40, 4), hasInterfaceInterval: false);
        var reset = sampler.Sample(new(0, 0, 0), new(31, 40, 4), hasInterfaceInterval: true);
        Expect(reset == new TcpActivity(true, true, false), "A counter reset must preserve activity from the other address family.");
        var missing = sampler.Sample(null, null, hasInterfaceInterval: true);
        Expect(missing == default, "A failed TCP statistics read should show no activity and clear the baseline.");
        var recovered = sampler.Sample(new(100, 100, 100), null, hasInterfaceInterval: true);
        Expect(recovered == new TcpActivity(true, false, false), "A recovered statistic must not count traffic from the missing interval.");
        var switched = sampler.Sample(new(101, 101, 101), null, hasInterfaceInterval: false);
        Expect(switched == new TcpActivity(true, false, false), "An interface change invalidates the interval used for TCP indication.");
        var next = sampler.Sample(new(101, 102, 101), null, hasInterfaceInterval: true);
        Expect(next == new TcpActivity(true, false, true), "The first complete interval after switching must reflect current TCP activity.");
        sampler.Reset();
        var enabled = sampler.Sample(new(105, 110, 110), null, hasInterfaceInterval: true);
        Expect(enabled == new TcpActivity(true, false, false), "Re-enabling the indicator must establish a fresh baseline.");
    }

    private static void Equal(double expected, double actual, string message) =>
        Expect(Math.Abs(expected - actual) < 0.0001, $"{message} Expected {expected}, actual {actual}.");

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
