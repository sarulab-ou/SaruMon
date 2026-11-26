using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SaruMon.Core;

public sealed record TcpConnectionInfo(
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    TcpState State);

/// <summary>Reads the OS TCP table; it does not capture packets, resolve names, or identify processes.</summary>
public static class TcpConnections
{
    /// <summary>
    /// Returns connections whose local endpoint matches one of the selected adapter's unicast addresses.
    /// Network-information failures are propagated so callers can distinguish an error from an empty table.
    /// </summary>
    public static IReadOnlyList<TcpConnectionInfo> Read(IReadOnlyCollection<IPAddress> selectedLocalAddresses)
    {
        ArgumentNullException.ThrowIfNull(selectedLocalAddresses);
        if (selectedLocalAddresses.Count == 0) return Array.Empty<TcpConnectionInfo>();

        var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
        return Filter(connections.Select(connection => new TcpConnectionInfo(
            connection.LocalEndPoint.Address,
            connection.LocalEndPoint.Port,
            connection.RemoteEndPoint.Address,
            connection.RemoteEndPoint.Port,
            connection.State)), selectedLocalAddresses);
    }

    /// <summary>Applies adapter selection to an existing snapshot with stable endpoint ordering.</summary>
    public static IReadOnlyList<TcpConnectionInfo> Filter(IEnumerable<TcpConnectionInfo> connections,
        IReadOnlyCollection<IPAddress> selectedLocalAddresses)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(selectedLocalAddresses);
        var selected = selectedLocalAddresses
            .Where(IsSpecifiedAddress)
            .Select(NormalizeAddress)
            .ToHashSet();
        if (selected.Count == 0) return Array.Empty<TcpConnectionInfo>();

        return connections
            .Where(connection => connection.State != TcpState.Listen
                && connection.LocalPort is > 0 and <= 65535
                && connection.RemotePort is > 0 and <= 65535
                && IsSpecifiedAddress(connection.LocalAddress)
                && IsSpecifiedAddress(connection.RemoteAddress)
                && selected.Contains(NormalizeAddress(connection.LocalAddress)))
            .OrderBy(connection => connection.RemoteAddress.ToString(), StringComparer.Ordinal)
            .ThenBy(connection => connection.RemotePort)
            .ThenBy(connection => connection.LocalAddress.ToString(), StringComparer.Ordinal)
            .ThenBy(connection => connection.LocalPort)
            .ThenBy(connection => connection.State)
            .ToArray();
    }

    private static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6
        ? address.MapToIPv4() : address;

    private static bool IsSpecifiedAddress(IPAddress address)
    {
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        var normalized = NormalizeAddress(address);
        return !normalized.Equals(IPAddress.Any) && !normalized.Equals(IPAddress.IPv6Any);
    }
}
