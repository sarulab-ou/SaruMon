namespace SaruMon.Core;

/// <summary>Cumulative operating-system counters for one adapter.</summary>
public sealed record TrafficCounterReading(
    string InterfaceId,
    bool IsAvailable,
    long BytesReceived,
    long BytesSent,
    long? UnicastPacketsReceived = null,
    long? UnicastPacketsSent = null);

public sealed record TrafficSample(
    string InterfaceId,
    bool IsAvailable,
    bool HasRate,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    long ReceivedDelta,
    long SentDelta,
    long TotalReceivedBytes,
    long TotalSentBytes,
    long? ReceivedUnicastDelta = null,
    long? SentUnicastDelta = null)
{
    public bool IsReceiving => ReceivedDelta > 0;
    public bool IsSending => SentDelta > 0;
    public bool IsUnicastReceiving => ReceivedUnicastDelta > 0;
    public bool IsUnicastSending => SentUnicastDelta > 0;
}

/// <summary>
/// Converts cumulative counters to rates using a monotonic timestamp supplied by the caller.
/// A new selection starts new totals; disconnects and resets never produce traffic spikes.
/// Instances are used on a single polling thread.
/// </summary>
public sealed class TrafficSampler
{
    private string? _interfaceId;
    private TrafficCounterReading? _previous;
    private TimeSpan _previousTimestamp;
    private long _totalReceived;
    private long _totalSent;

    public void Reset()
    {
        _interfaceId = null;
        _previous = null;
        _previousTimestamp = default;
        _totalReceived = 0;
        _totalSent = 0;
    }

    public TrafficSample Sample(TrafficCounterReading reading, TimeSpan monotonicTimestamp)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.InterfaceId);
        if (reading.IsAvailable && (reading.BytesReceived < 0 || reading.BytesSent < 0))
            throw new ArgumentOutOfRangeException(nameof(reading), "Counters must be nonnegative.");

        if (!StringComparer.Ordinal.Equals(_interfaceId, reading.InterfaceId))
        {
            Reset();
            _interfaceId = reading.InterfaceId;
        }

        if (!reading.IsAvailable)
        {
            _previous = null;
            return Empty(reading, hasRate: false);
        }

        var previous = _previous;
        var elapsed = monotonicTimestamp - _previousTimestamp;
        _previous = reading;
        _previousTimestamp = monotonicTimestamp;

        // Rebaseline after first observation, counter reset, or an invalid time interval.
        // A reset of either direction may indicate a driver/adapter restart.
        if (previous is null || elapsed <= TimeSpan.Zero ||
            reading.BytesReceived < previous.BytesReceived || reading.BytesSent < previous.BytesSent)
            return Empty(reading, hasRate: false);

        var received = reading.BytesReceived - previous.BytesReceived;
        var sent = reading.BytesSent - previous.BytesSent;
        _totalReceived = SaturatingAdd(_totalReceived, received);
        _totalSent = SaturatingAdd(_totalSent, sent);

        return new TrafficSample(reading.InterfaceId, true, true,
            received / elapsed.TotalSeconds, sent / elapsed.TotalSeconds,
            received, sent, _totalReceived, _totalSent,
            UnicastDelta(reading.UnicastPacketsReceived, previous.UnicastPacketsReceived),
            UnicastDelta(reading.UnicastPacketsSent, previous.UnicastPacketsSent));
    }

    private TrafficSample Empty(TrafficCounterReading reading, bool hasRate) =>
        new(reading.InterfaceId, reading.IsAvailable, hasRate, 0, 0, 0, 0,
            _totalReceived, _totalSent,
            reading.IsAvailable ? UnicastDelta(reading.UnicastPacketsReceived, null) : null,
            reading.IsAvailable ? UnicastDelta(reading.UnicastPacketsSent, null) : null);

    // Null means this direction's counter is unavailable. A valid counter without
    // a baseline (or after a reset) is supported but silent until the next sample.
    // Each direction recovers independently, without changing the byte totals.
    private static long? UnicastDelta(long? current, long? previous)
    {
        if (current is null or < 0) return null;
        if (previous is null or < 0 || current < previous) return 0;
        return current.Value - previous.Value;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
