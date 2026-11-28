using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SaruMon.Core;

public sealed record TcpConnectionInfo(
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    TcpState State)
{
    public int? ProcessId { get; init; }
}

/// <summary>Reads the OS TCP table without packet capture or name resolution.</summary>
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

    public static IReadOnlyList<TcpConnectionInfo> ReadWithOwners(IReadOnlyCollection<IPAddress> selectedLocalAddresses)
    {
        ArgumentNullException.ThrowIfNull(selectedLocalAddresses);
        if (selectedLocalAddresses.Count == 0) return Array.Empty<TcpConnectionInfo>();
        var ipv4 = ReadOwnerTable(AddressFamily.InterNetwork);
        var ipv6 = Socket.OSSupportsIPv6 ? ReadOwnerTable(AddressFamily.InterNetworkV6) : [];
        return Filter(ipv4.Concat(ipv6), selectedLocalAddresses);
    }

    private static IEnumerable<TcpConnectionInfo> ReadOwnerTable(AddressFamily family)
    {
        const uint bufferTooSmall = 122;
        const int ownerPidConnections = 4;
        var length = 0;
        var status = GetExtendedTcpTable(null, ref length, false, (int)family, ownerPidConnections, 0);
        for (var attempt = 0; status == bufferTooSmall && attempt < 3; attempt++)
        {
            if (length is < 4 or > 32 * 1024 * 1024) throw new InvalidDataException("TCP接続表のサイズが不正です。");
            var buffer = new byte[length];
            status = GetExtendedTcpTable(buffer, ref length, false, (int)family, ownerPidConnections, 0);
            if (status == 0)
            {
                if (length < 4 || length > buffer.Length) throw new InvalidDataException("TCP接続表のサイズが不正です。");
                return ReadOwnerRows(buffer.AsSpan(0, length), family);
            }
        }
        if (status != 0) throw new NetworkInformationException((int)status);
        return [];
    }

    private static TcpConnectionInfo[] ReadOwnerRows(ReadOnlySpan<byte> bytes, AddressFamily family)
    {
        var ipv4 = family == AddressFamily.InterNetwork;
        var rowSize = ipv4 ? 24 : 56;
        if (bytes.Length < 4) throw new InvalidDataException("TCP接続表が不完全です。");
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 0 || count > (bytes.Length - 4) / rowSize) throw new InvalidDataException("TCP接続表の行数が不正です。");
        var result = new TcpConnectionInfo[count];
        for (var index = 0; index < count; index++)
        {
            var row = bytes.Slice(4 + index * rowSize, rowSize);
            var local = ipv4 ? new IPAddress(row.Slice(4, 4)) : new IPAddress(row[..16], ReadScope(row[16..]));
            var remote = ipv4 ? new IPAddress(row.Slice(12, 4)) : new IPAddress(row.Slice(24, 16), ReadScope(row[40..]));
            var localPort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 8 : 20)..]);
            var remotePort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 16 : 44)..]);
            var state = (TcpState)BinaryPrimitives.ReadInt32LittleEndian(row[(ipv4 ? 0 : 48)..]);
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[(ipv4 ? 20 : 52)..]);
            result[index] = new TcpConnectionInfo(local, localPort, remote, remotePort, state) { ProcessId = pid > 0 ? pid : null };
        }
        return result;

        static uint ReadScope(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable([Out] byte[]? buffer, ref int length,
        [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);

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
