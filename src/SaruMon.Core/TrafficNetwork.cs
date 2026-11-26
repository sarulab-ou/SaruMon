using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SaruMon.Core;

public sealed record TrafficInterface(
    string Id,
    string Name,
    string Description,
    bool IsUp,
    string Type,
    long LinkSpeed,
    IReadOnlyList<string> Addresses)
{
    public override string ToString() => $"{Name} ({(IsUp ? "接続中" : "未接続")})";
}

/// <summary>Reads adapter byte counters without packet capture or administrator privileges.</summary>
public static class TrafficNetwork
{
    public static IReadOnlyList<TrafficInterface> GetInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(ToInterface)
            .OrderByDescending(adapter => adapter.IsUp)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static TrafficCounterReading ReadCounters(string interfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, interfaceId));
            if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
                return Unavailable(interfaceId);

            var counters = adapter.GetIPStatistics();
            if (counters.BytesReceived < 0 || counters.BytesSent < 0)
                return Unavailable(interfaceId);
            return new TrafficCounterReading(interfaceId, true, counters.BytesReceived, counters.BytesSent);
        }
        catch (NetworkInformationException)
        {
            // Adapters may disappear or change state between enumeration and reading.
            return Unavailable(interfaceId);
        }
        catch (InvalidOperationException)
        {
            return Unavailable(interfaceId);
        }
    }

    private static TrafficCounterReading Unavailable(string interfaceId) => new(interfaceId, false, 0, 0);

    private static TrafficInterface ToInterface(NetworkInterface adapter)
    {
        IReadOnlyList<string> addresses;
        try
        {
            addresses = adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(address => address.Address.ToString())
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            addresses = Array.Empty<string>();
        }

        long speed;
        try { speed = Math.Max(0, adapter.Speed); }
        catch (NetworkInformationException) { speed = 0; }

        return new TrafficInterface(adapter.Id, adapter.Name, adapter.Description,
            adapter.OperationalStatus == OperationalStatus.Up,
            adapter.NetworkInterfaceType.ToString(), speed, addresses);
    }
}
