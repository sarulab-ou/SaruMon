using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SaruMon.Core;

namespace SaruMon.Tests;

internal static class TcpConnectionTests
{
    public static int Run()
    {
        var count = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("TCP connection test failed: " + message);
            count++;
        }

        var local4 = IPAddress.Parse("192.0.2.10");
        var other4 = IPAddress.Parse("192.0.2.11");
        var remote4 = IPAddress.Parse("198.51.100.20");
        var local6 = IPAddress.Parse("2001:db8:1::10");
        var remote6 = IPAddress.Parse("2001:db8:2::20");
        var established = new TcpConnectionInfo(local4, 51001, remote4, 443, TcpState.Established);
        var otherAdapter = established with { LocalAddress = other4 };
        var ipv6 = new TcpConnectionInfo(local6, 51002, remote6, 8443, TcpState.Established);
        var selected = new[] { local4, local6 };

        var result = TcpConnections.Filter([established, otherAdapter, ipv6], selected);
        Assert(result.Count == 2 && result.Contains(established) && result.Contains(ipv6), "include selected IPv4 and IPv6, exclude other adapters");
        Assert(TcpConnections.Filter([established, ipv6], [local4]).SequenceEqual([established]), "selection switch filters the whole snapshot");
        Assert(TcpConnections.Filter([established], Array.Empty<IPAddress>()).Count == 0, "empty selection does not show system-wide connections");
        Assert(TcpConnections.Read(Array.Empty<IPAddress>()).Count == 0, "empty selection can return without querying the OS");

        var excluded = new[]
        {
            established with { State = TcpState.Listen },
            established with { RemotePort = 0 },
            established with { LocalPort = 0 },
            established with { RemotePort = 65536 },
            established with { RemoteAddress = IPAddress.Any },
            ipv6 with { RemoteAddress = IPAddress.IPv6Any },
            ipv6 with { RemoteAddress = IPAddress.Any.MapToIPv6() },
            established with { LocalAddress = IPAddress.Any },
            ipv6 with { LocalAddress = IPAddress.IPv6Any }
        };
        Assert(TcpConnections.Filter(excluded, [local4, local6, IPAddress.Any, IPAddress.IPv6Any]).Count == 0,
            "exclude listeners, invalid ports, and unspecified endpoints even when selection contains wildcard addresses");

        var mapped = established with { LocalAddress = local4.MapToIPv6(), RemoteAddress = remote4.MapToIPv6() };
        Assert(TcpConnections.Filter([mapped], [local4]).SequenceEqual([mapped]), "IPv4 selection matches mapped IPv6 table entries");
        Assert(TcpConnections.Filter([established], [local4.MapToIPv6()]).SequenceEqual([established]), "mapped IPv6 selection matches IPv4 table entries");
        Assert(TcpConnections.Filter([mapped], [local4]).Single().RemoteAddress.Equals(remote4.MapToIPv6()), "preserve OS endpoint addresses in returned records");

        var scopedLocal = IPAddress.Parse("fe80::1234%12");
        var scopedOther = IPAddress.Parse("fe80::1234%13");
        var scoped = ipv6 with { LocalAddress = scopedLocal };
        Assert(TcpConnections.Filter([scoped, scoped with { LocalAddress = scopedOther }], [scopedLocal]).SequenceEqual([scoped]),
            "IPv6 scope identifies the selected adapter when link-local address bytes match");

        var closing = established with { State = TcpState.TimeWait };
        var connecting = established with { State = TcpState.SynSent, LocalPort = 51003 };
        result = TcpConnections.Filter([closing, connecting], [local4]);
        Assert(result.Count == 2 && result.Contains(closing) && result.Contains(connecting), "preserve connection state instead of filtering to Established only");

        var port80 = established with { RemotePort = 80 };
        var earlierLocalPort = established with { LocalPort = 51000 };
        var snapshot = new[] { established, ipv6, port80, earlierLocalPort, connecting };
        var original = snapshot.ToArray();
        result = TcpConnections.Filter(snapshot, selected);
        Assert(result.SequenceEqual(TcpConnections.Filter(snapshot.Reverse(), selected)), "stable ordering avoids refresh reordering from OS enumeration order");
        Assert(result.IndexOfRecord(port80) < result.IndexOfRecord(established)
            && result.IndexOfRecord(earlierLocalPort) < result.IndexOfRecord(established), "numeric ports break endpoint ordering ties");
        Assert(snapshot.SequenceEqual(original), "filter does not mutate its input");
        Assert(TcpConnections.Filter([established], [local4, local4]).Count == 1, "duplicate selected addresses do not duplicate rows");
        LoopbackConnectionAppearsInOsTable();
        count++;
        return count;
    }

    private static void LoopbackConnectionAppearsInOsTable()
    {
        // Bind only to IPv4 loopback and an OS-assigned port. No external network or DNS is used.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            listener.Start();
            var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            client.ConnectAsync(IPAddress.Loopback, serverPort, timeout.Token).AsTask().GetAwaiter().GetResult();
            using var server = listener.AcceptTcpClientAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            var observation = Stopwatch.StartNew();
            while (observation.Elapsed < TimeSpan.FromSeconds(2))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (TcpConnections.Read([IPAddress.Loopback]).Any(connection =>
                    connection.LocalAddress.Equals(IPAddress.Loopback)
                    && connection.LocalPort == clientPort
                    && connection.RemoteAddress.Equals(IPAddress.Loopback)
                    && connection.RemotePort == serverPort
                    && connection.State == TcpState.Established)) return;
                Thread.Sleep(25);
            }
            throw new Exception("TCP connection test failed: established loopback connection was absent from the OS TCP table.");
        }
        finally { listener.Stop(); }
    }

    private static int IndexOfRecord(this IReadOnlyList<TcpConnectionInfo> values, TcpConnectionInfo value)
    {
        for (var index = 0; index < values.Count; index++) if (values[index] == value) return index;
        return -1;
    }
}
