using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SaruMon.Core;

public sealed record UdpEndpointInfo(IPAddress LocalAddress, int LocalPort, int? ProcessId);

/// <summary>Reads local UDP bindings and their owners from the Windows socket table.</summary>
public static class UdpEndpoints
{
    public static IReadOnlyList<UdpEndpointInfo> ReadWithOwners(IReadOnlyCollection<IPAddress> selectedLocalAddresses)
    {
        ArgumentNullException.ThrowIfNull(selectedLocalAddresses);
        if (selectedLocalAddresses.Count == 0) return [];
        var ipv4 = ReadFamily(AddressFamily.InterNetwork);
        var ipv6 = Socket.OSSupportsIPv6 ? ReadFamily(AddressFamily.InterNetworkV6) : [];
        return Filter(ipv4.Concat(ipv6), selectedLocalAddresses);
    }

    public static IReadOnlyList<UdpEndpointInfo> Filter(IEnumerable<UdpEndpointInfo> bindings,
        IReadOnlyCollection<IPAddress> selectedLocalAddresses)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(selectedLocalAddresses);
        var selected = selectedLocalAddresses.Select(SocketOwnerTable.NormalizeAddress)
            .Where(address => !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)).ToHashSet();
        if (selected.Count == 0) return [];
        var allIpv4 = selected.Any(address => address.AddressFamily == AddressFamily.InterNetwork);
        var allIpv6 = selected.Any(address => address.AddressFamily == AddressFamily.InterNetworkV6);
        return bindings.Where(row =>
        {
            var local = SocketOwnerTable.NormalizeAddress(row.LocalAddress);
            return row.LocalPort is > 0 and <= 65535 && (selected.Contains(local)
                || allIpv4 && local.Equals(IPAddress.Any) || allIpv6 && local.Equals(IPAddress.IPv6Any));
        }).OrderBy(row => row.LocalPort).ThenBy(row => row.LocalAddress.ToString(), StringComparer.Ordinal)
            .ThenBy(row => row.ProcessId).ToArray();
    }

    private static UdpEndpointInfo[] ReadFamily(AddressFamily family)
    {
        const int ownerPid = 1;
        var data = SocketOwnerTable.Read(family, ownerPid, GetExtendedUdpTable).Span;
        var ipv4 = family == AddressFamily.InterNetwork;
        var rowSize = ipv4 ? 12 : 28;
        var rows = new UdpEndpointInfo[SocketOwnerTable.ReadCount(data, rowSize)];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = data.Slice(4 + index * rowSize, rowSize);
            var address = ipv4 ? new IPAddress(row[..4])
                : new IPAddress(row[..16], BinaryPrimitives.ReadUInt32BigEndian(row[16..]));
            var port = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 4 : 20)..]);
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[(ipv4 ? 8 : 24)..]);
            rows[index] = new UdpEndpointInfo(address, port, pid > 0 ? pid : null);
        }
        return rows;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable([Out] byte[]? buffer, ref int length,
        [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);
}
