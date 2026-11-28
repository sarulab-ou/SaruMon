using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Win32.SafeHandles;

namespace SaruMon.Monitoring;

internal enum InvestigationProtocol { Tcp, Udp }
internal readonly record struct InvestigationProcess(int Pid, long StartedUtcTicks);
internal readonly record struct InvestigationKey(InvestigationProcess Process, string Address, int Port, InvestigationProtocol Protocol);
internal sealed record InvestigationRow(InvestigationKey Key, string? Path, long Sent, long Received, long Events,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

internal sealed record InvestigationSnapshot(IReadOnlyList<InvestigationRow> Rows, DateTimeOffset SampledAt,
    long DroppedEvents, bool Incomplete);

internal sealed class InvestigationCapture
{
    internal const int MaxPathLength = 1024;
    private readonly HashSet<IPAddress> localAddresses;
    private readonly Dictionary<int, Identity> identities = new();
    private readonly Dictionary<(InvestigationProcess Process, IPAddress Address, int Port, InvestigationProtocol Protocol), Entry> entries = new();
    private readonly HashSet<Entry> dirty = new();
    private readonly object gate = new();
    private readonly int capacity;
    private readonly Func<int, DateTimeOffset, (long Started, string? Path)> resolve;
    private long dropped;

    internal InvestigationCapture(IEnumerable<IPAddress> addresses, int capacity = TrafficWireProtocol.MaxEntries,
        Func<int, DateTimeOffset, (long Started, string? Path)>? resolve = null)
    {
        localAddresses = addresses.Select(Normalize).ToHashSet();
        this.capacity = capacity;
        this.resolve = resolve ?? ResolveProcess;
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        // The ETW event does not carry an IPv6 zone ID.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes()) : address;
    }

    internal void Attach(ETWTraceEventSource source)
    {
        source.Kernel.ProcessStart += data => ProcessChanged(data.ProcessID, TrafficMonitorHelper.CaptureTimestamp(data), data.TimeStamp, true);
        source.Kernel.ProcessStop += data => ProcessChanged(data.ProcessID, TrafficMonitorHelper.CaptureTimestamp(data), data.TimeStamp, false);
        source.Kernel.TcpIpSend += data => OnTraffic(data, data.saddr, data.daddr, data.dport, data.size, false, InvestigationProtocol.Tcp);
        source.Kernel.TcpIpSendIPV6 += data => OnTraffic(data, data.saddr, data.daddr, data.dport, data.size, false, InvestigationProtocol.Tcp);
        source.Kernel.TcpIpRecv += data => OnTraffic(data, data.saddr, data.daddr, data.dport, data.size, true, InvestigationProtocol.Tcp);
        source.Kernel.TcpIpRecvIPV6 += data => OnTraffic(data, data.saddr, data.daddr, data.dport, data.size, true, InvestigationProtocol.Tcp);
        source.Kernel.UdpIpSend += data => OnTraffic(data, data.saddr, data.daddr, data.dport, data.size, false, InvestigationProtocol.Udp);
        source.Kernel.UdpIpSendIPV6 += data => OnTraffic(data, data.saddr, data.daddr, data.dport, data.size, false, InvestigationProtocol.Udp);

        void OnTraffic(TraceEvent data, IPAddress local, IPAddress remote, int port, long bytes,
            bool receiving, InvestigationProtocol protocol) =>
            Record(protocol, data.ProcessID, local, remote, port, bytes, receiving, data.TimeStamp, TrafficMonitorHelper.CaptureTimestamp(data));
    }

    internal void ProcessChanged(int pid, long timestamp, DateTimeOffset time, bool started)
    {
        if (pid <= 0) return;
        lock (gate)
        {
            if (identities.TryGetValue(pid, out var previous) && timestamp < previous.Boundary) return;
            if (identities.Count < TrafficWireProtocol.MaxEntries * 2 || identities.ContainsKey(pid))
                identities[pid] = new Identity(timestamp, started ? time.UtcTicks : 0, !started);
        }
    }

    internal void ForgetProcessIdentities()
    {
        lock (gate) identities.Clear();
    }

    internal void Record(InvestigationProtocol protocol, int pid, IPAddress local, IPAddress remote, int port,
        long bytes, bool receiving, DateTimeOffset time, long timestamp)
    {
        if (!localAddresses.Contains(Normalize(local)) || port is < 1 or > 65535 || bytes < 0
            || receiving && protocol == InvestigationProtocol.Udp) return;
        remote = Normalize(remote);
        if (remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any)) return;
        pid = Math.Max(pid, 0);
        lock (gate)
        {
            var identity = IdentityFor(pid, timestamp, time);
            var process = new InvestigationProcess(pid, identity?.Started ?? 0);
            var key = (process, remote, port, protocol);
            if (!entries.TryGetValue(key, out var entry))
            {
                if (entries.Count >= capacity)
                {
                    dropped = Add(dropped, 1);
                    return;
                }
                entry = new Entry(new InvestigationKey(process, remote.ToString(), port, protocol), identity?.Path, time);
                entries.Add(key, entry);
            }
            if (receiving) entry.Received = Add(entry.Received, bytes);
            else entry.Sent = Add(entry.Sent, bytes);
            entry.Events = Add(entry.Events, 1);
            if (time < entry.FirstSeen) entry.FirstSeen = time;
            if (time > entry.LastSeen) entry.LastSeen = time;
            dirty.Add(entry);
        }
    }

    internal InvestigationRow[] TakeUpdates(out long droppedEvents)
    {
        lock (gate)
        {
            droppedEvents = dropped;
            var rows = dirty.Select(entry => new InvestigationRow(entry.Key, entry.Path, entry.Sent, entry.Received,
                entry.Events, entry.FirstSeen, entry.LastSeen)).ToArray();
            dirty.Clear();
            return rows;
        }
    }

    internal static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private Identity? IdentityFor(int pid, long timestamp, DateTimeOffset time)
    {
        if (pid <= 0) return null;
        if (!identities.TryGetValue(pid, out var identity))
        {
            if (identities.Count >= TrafficWireProtocol.MaxEntries * 2) return null;
            identities[pid] = identity = new Identity(0, 0, false);
        }
        if (identity.Ended || timestamp < identity.Boundary) return null;
        if (!identity.Resolved)
        {
            var (started, path) = resolve(pid, time);
            // A reused live PID cannot supply metadata for an earlier event.
            if (started > 0 && started <= time.UtcTicks)
            {
                identity.Started = started;
                identity.Path = path is { Length: <= MaxPathLength } ? path : null;
            }
            identity.Resolved = true;
        }
        return identity.Started <= time.UtcTicks ? identity : null;
    }

    private static (long Started, string? Path) ResolveProcess(int pid, DateTimeOffset eventTime)
    {
        try
        {
            using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _)) return default;
            var started = DateTime.FromFileTimeUtc(created).Ticks;
            if (started > eventTime.UtcTicks) return default;
            var length = MaxPathLength + 1;
            var path = new StringBuilder(length);
            return (started, QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return default; }
    }

    private sealed class Entry(InvestigationKey key, string? path, DateTimeOffset time)
    {
        internal InvestigationKey Key { get; } = key;
        internal string? Path { get; } = path;
        internal DateTimeOffset FirstSeen = time;
        internal DateTimeOffset LastSeen = time;
        internal long Sent, Received, Events;
    }

    private sealed class Identity(long boundary, long started, bool ended)
    {
        internal long Boundary { get; } = boundary;
        internal bool Ended { get; } = ended;
        internal long Started = started;
        internal bool Resolved;
        internal string? Path;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
