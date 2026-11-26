using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaruMon.Core;

namespace SaruMon.Monitoring;

internal enum MonitoringMode { Udp, Tcp }

internal sealed record TrafficMessage(string Type, string? Nonce = null, string[]? Addresses = null,
    UdpFlowInfo[]? UdpFlows = null, TcpPeerInfo[]? TcpPeers = null, string? Error = null,
    long Revision = 0, long DroppedEvents = 0, bool CaptureLoss = false, bool Final = false,
    MonitoringMode? Mode = null, TcpTrafficReading? TcpTraffic = null);

internal sealed record TcpTrafficReading(long ReceivedBytes, long SentBytes, long SampledAtTicks, bool Incomplete);
internal sealed record TcpTrafficSnapshot(long Revision, TcpTrafficReading Reading);

internal static class TrafficWireProtocol
{
    internal const int MaxAddresses = 128;
    internal const int MaxEntries = 2048;
    internal const int MaxLineChars = 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static bool IsNonce(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool IsPipeName(string value) => value.StartsWith("SaruMon-Monitor-", StringComparison.Ordinal)
        && value.Length == 48 && value.AsSpan(16).ToString().All(Uri.IsHexDigit);

    internal static bool IsMode(MonitoringMode mode) => mode is MonitoringMode.Udp or MonitoringMode.Tcp;

    internal static IPAddress[] ParseAddresses(string[]? addresses)
    {
        if (addresses is null || addresses.Length > MaxAddresses) throw new InvalidDataException("監視アドレスが不正です。");
        return addresses.Select(address => ParseAddress(address)).Distinct().ToArray();
    }

    internal static IPAddress ParseAddress(string? address)
    {
        if (address is null || address.Length > 80 || !IPAddress.TryParse(address, out var result)
            || result.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || result.Equals(IPAddress.Any) || result.Equals(IPAddress.IPv6Any))
            throw new InvalidDataException("IP アドレスが不正です。");
        return result;
    }

    internal static void ValidateSnapshot(TrafficMessage message, MonitoringMode expectedMode)
    {
        if (!IsMode(expectedMode) || message.Mode != expectedMode || message.Type != "snapshot"
            || message.Revision <= 0 || message.DroppedEvents < 0)
            throw new InvalidDataException("通信監視データが不正です。");
        if (expectedMode == MonitoringMode.Tcp)
        {
            if (message.UdpFlows is not null || message.TcpPeers is null || message.TcpPeers.Length > MaxEntries
                || message.TcpTraffic is not { ReceivedBytes: >= 0, SentBytes: >= 0, SampledAtTicks: >= 0 })
                throw new InvalidDataException("TCP 監視データが不正です。");
            foreach (var peer in message.TcpPeers)
            {
                if (peer is null || peer.SentBytes < 0 || peer.ReceivedBytes < 0 || peer.ConnectionCount < 0
                    || peer.FirstSeen == default || peer.LastSeen < peer.FirstSeen)
                    throw new InvalidDataException("TCP の集計が不正です。");
                ParseAddress(peer.RemoteAddress);
            }
            return;
        }
        if (message.TcpPeers is not null || message.TcpTraffic is not null || message.UdpFlows is null || message.UdpFlows.Length > MaxEntries)
            throw new InvalidDataException("UDP 監視データが不正です。");
        foreach (var flow in message.UdpFlows)
        {
            if (flow is null || flow.LocalPort is < 1 or > 65535 || flow.RemotePort is < 1 or > 65535
                || flow.Packets < 1 || flow.Bytes < 0 || flow.FirstSeen == default
                || flow.LastSeen < flow.FirstSeen)
                throw new InvalidDataException("通信エンドポイントが不正です。");
            var local = ParseAddress(flow.LocalAddress);
            var remote = ParseAddress(flow.RemoteAddress);
            if (local.AddressFamily != remote.AddressFamily) throw new InvalidDataException("IP アドレスの種類が一致しません。");
        }
    }

    internal static TrafficMessage Deserialize(string value)
    {
        if (value.Length > MaxLineChars) throw new InvalidDataException("監視メッセージが大きすぎます。");
        return JsonSerializer.Deserialize<TrafficMessage>(value, JsonOptions)
            ?? throw new InvalidDataException("監視メッセージがありません。");
    }
}

/// <summary>Bounded, newline-delimited JSON. Only one reader and one writer use each channel.</summary>
internal sealed class TrafficChannel : IDisposable
{
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim writeGate = new(1);
    private readonly char[] buffer = new char[8192];
    private int position;
    private int length;

    internal TrafficChannel(Stream stream)
    {
        reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 8192, leaveOpen: true);
        writer = new StreamWriter(stream, new UTF8Encoding(false, true), 8192, leaveOpen: true) { AutoFlush = true };
    }

    internal async Task<TrafficMessage?> ReadAsync(CancellationToken token)
    {
        var text = new StringBuilder();
        while (true)
        {
            if (position == length)
            {
                length = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                position = 0;
                if (length == 0)
                {
                    if (text.Length != 0) throw new InvalidDataException("監視メッセージが途中で終了しました。");
                    return null;
                }
            }
            var end = Array.IndexOf(buffer, '\n', position, length - position);
            var count = (end < 0 ? length : end) - position;
            if (text.Length + count > TrafficWireProtocol.MaxLineChars) throw new InvalidDataException("監視メッセージが大きすぎます。");
            text.Append(buffer, position, count);
            position += count;
            if (end < 0) continue;
            position++;
            return TrafficWireProtocol.Deserialize(text.ToString());
        }
    }

    internal async Task WriteAsync(TrafficMessage message, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(message, TrafficWireProtocol.JsonOptions);
        if (json.Length > TrafficWireProtocol.MaxLineChars) throw new InvalidDataException("監視メッセージが大きすぎます。");
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try { await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    public void Dispose()
    {
        reader.Dispose();
        // Shutdown closes the pipe first to cancel a pending read. Flushing that closed stream is harmless.
        try { writer.Dispose(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        writeGate.Dispose();
    }
}
