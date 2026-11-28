using System.Net;
using System.Net.Sockets;
using SaruMon.Core;

namespace SaruMon.Tests;

internal static class UdpEndpointTests
{
    internal static int Run()
    {
        var count = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UDP endpoint test failed: " + message);
            count++;
        }

        var local4 = IPAddress.Parse("192.0.2.10");
        var local6 = IPAddress.Parse("fe80::1234%12");
        var v4 = new UdpEndpointInfo(local4, 5355, 10);
        var mapped = new UdpEndpointInfo(local4.MapToIPv6(), 5356, 12);
        var v6 = new UdpEndpointInfo(local6, 546, 10);
        var any4 = new UdpEndpointInfo(IPAddress.Any, 5353, 11);
        var any6 = new UdpEndpointInfo(IPAddress.IPv6Any, 5353, 11);
        UdpEndpointInfo[] rows = [v4, mapped, v6, any4, any6,
            v4 with { LocalAddress = IPAddress.Parse("192.0.2.11") },
            v6 with { LocalAddress = IPAddress.Parse("fe80::1234%13") }, v4 with { LocalPort = 0 }];

        Assert(UdpEndpoints.Filter(rows, [local4]).SequenceEqual([any4, v4, mapped]), "IPv4, mapped IPv6, and IPv4 wildcard");
        Assert(UdpEndpoints.Filter(rows, [local6]).SequenceEqual([v6, any6]), "IPv6 scope and IPv6 wildcard");
        Assert(UdpEndpoints.Filter(rows, [local6, local4]).Count == 5, "dual-stack adapter");
        Assert(UdpEndpoints.Filter(rows, []).Count == 0, "no adapter selection");
        Assert(UdpEndpoints.ReadWithOwners([]).Count == 0, "empty input skips native read");
        Assert(OwnerAppearsInOsTable(IPAddress.Loopback, IPAddress.Loopback), "IPv4 port and owner PID");
        Assert(OwnerAppearsInOsTable(IPAddress.Any, IPAddress.Loopback), "IPv4 wildcard port and owner PID");
        if (Socket.OSSupportsIPv6)
        {
            Assert(OwnerAppearsInOsTable(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback), "IPv6 port and owner PID");
            Assert(OwnerAppearsInOsTable(IPAddress.IPv6Any, IPAddress.IPv6Loopback), "IPv6 wildcard port and owner PID");
        }
        return count;
    }

    private static bool OwnerAppearsInOsTable(IPAddress bind, IPAddress selected)
    {
        using var socket = new UdpClient(new IPEndPoint(bind, 0));
        var endpoint = (IPEndPoint)socket.Client.LocalEndPoint!;
        return UdpEndpoints.ReadWithOwners([selected]).Contains(new UdpEndpointInfo(bind, endpoint.Port, Environment.ProcessId));
    }
}
