using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SaruMon;

internal readonly record struct TcpSegments(long Received, long Sent, long Resent);
internal readonly record struct TcpActivity(bool IsAvailable, bool IsReceiving, bool IsSending);

internal sealed class TcpIndicatorSampler
{
    private IPGlobalProperties? properties;
    private TcpSegments? previousIpv4;
    private TcpSegments? previousIpv6;

    public TcpActivity Read(bool hasInterfaceInterval)
    {
        properties ??= IPGlobalProperties.GetIPGlobalProperties();
        var ipv4 = Socket.OSSupportsIPv4 ? ReadSegments(properties, ipv6: false) : null;
        var ipv6 = Socket.OSSupportsIPv6 ? ReadSegments(properties, ipv6: true) : null;
        return Sample(ipv4, ipv6, hasInterfaceInterval);
    }

    internal TcpActivity Sample(TcpSegments? ipv4, TcpSegments? ipv6, bool hasInterfaceInterval)
    {
        // Compare each address family separately so a reset or failed read in one
        // family cannot hide genuine activity in the other or cause a false burst.
        var receiving = hasInterfaceInterval &&
            (Increased(ipv4?.Received, previousIpv4?.Received) || Increased(ipv6?.Received, previousIpv6?.Received));
        var sending = hasInterfaceInterval &&
            (Increased(ipv4?.Sent, previousIpv4?.Sent) || Increased(ipv6?.Sent, previousIpv6?.Sent) ||
             Increased(ipv4?.Resent, previousIpv4?.Resent) || Increased(ipv6?.Resent, previousIpv6?.Resent));
        previousIpv4 = ipv4;
        previousIpv6 = ipv6;
        return new TcpActivity(ipv4.HasValue || ipv6.HasValue, receiving, sending);
    }

    public void Reset()
    {
        previousIpv4 = null;
        previousIpv6 = null;
    }

    private static bool Increased(long? current, long? previous) => previous is >= 0 && current > previous;

    private static TcpSegments? ReadSegments(IPGlobalProperties properties, bool ipv6)
    {
        try
        {
            var statistics = ipv6 ? properties.GetTcpIPv6Statistics() : properties.GetTcpIPv4Statistics();
            return new TcpSegments(statistics.SegmentsReceived, statistics.SegmentsSent, statistics.SegmentsResent);
        }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException)
        {
            return null;
        }
    }
}
