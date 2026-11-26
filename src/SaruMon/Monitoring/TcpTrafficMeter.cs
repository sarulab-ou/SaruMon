namespace SaruMon.Monitoring;

internal sealed record TcpTrafficDisplay(long Revision, long ReceivedBytes, long SentBytes,
    double DownloadBytesPerSecond, double UploadBytesPerSecond, bool Incomplete);

/// <summary>Uses the helper's clock and reuses the last rate between snapshots.</summary>
internal sealed class TcpTrafficMeter
{
    private TcpTrafficSnapshot? previous;
    private TcpTrafficDisplay? display;

    public TcpTrafficDisplay? Read(TcpTrafficSnapshot? snapshot)
    {
        if (ReferenceEquals(previous, snapshot)) return display;
        if (snapshot is null)
        {
            previous = null;
            return display = null;
        }

        var reading = snapshot.Reading;
        var baseline = previous?.Revision == snapshot.Revision ? previous.Reading : null;
        var elapsedTicks = reading.SampledAtTicks - (baseline?.SampledAtTicks ?? 0);
        var received = reading.ReceivedBytes - (baseline?.ReceivedBytes ?? 0);
        var sent = reading.SentBytes - (baseline?.SentBytes ?? 0);
        var seconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
        var hasRate = elapsedTicks > 0 && received >= 0 && sent >= 0;
        previous = snapshot;
        return display = new TcpTrafficDisplay(snapshot.Revision, reading.ReceivedBytes, reading.SentBytes,
            hasRate ? received / seconds : 0, hasRate ? sent / seconds : 0, reading.Incomplete);
    }
}
