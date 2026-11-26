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
        return 8;
    }

    private static TrafficSample Sample(TrafficSampler sampler, double time, long received, long sent,
        string id = "adapter-a", bool available = true) =>
        sampler.Sample(new TrafficCounterReading(id, available, received, sent), TimeSpan.FromSeconds(time));

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

    private static void Equal(double expected, double actual, string message) =>
        Expect(Math.Abs(expected - actual) < 0.0001, $"{message} Expected {expected}, actual {actual}.");

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
