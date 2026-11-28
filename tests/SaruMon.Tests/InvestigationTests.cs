using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using SaruMon.Monitoring;

namespace SaruMon.Tests;

internal static class InvestigationTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 28, 8, 0, 0, TimeSpan.Zero);

    // Explicit opt-in, like the existing TCP and UDP live smoke tests.
    internal static async Task<int> RunLiveAsync()
    {
        var peers = new[] { (Local: IPAddress.Loopback, Remote: IPAddress.Parse("127.0.0.2")),
            (Local: IPAddress.IPv6Loopback, Remote: IPAddress.IPv6Loopback) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var session = new TrafficEtwSession("SaruMon.InvestigationSmoke." + Guid.NewGuid().ToString("N"), includeProcesses: true);
        using var source = new ETWTraceEventSource(session.Name, TraceEventSourceType.Session);
        var capture = new InvestigationCapture(peers.Select(peer => peer.Local));
        capture.Attach(source);
        var consumer = Task.Run(() => source.Process());
        var ports = new List<(IPAddress Remote, int Port)>();
        try
        {
            foreach (var (local, remote) in peers)
            {
                using var listener = new TcpListener(remote, 0);
                listener.Start();
                var destination = (IPEndPoint)listener.LocalEndpoint;
                using var tcp = new TcpClient(new IPEndPoint(local, 0));
                await tcp.ConnectAsync(remote, destination.Port, timeout.Token);
                using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
                var data = new byte[64];
                await tcp.GetStream().WriteAsync(data, timeout.Token);
                await accepted.GetStream().ReadExactlyAsync(data, timeout.Token);
                await accepted.GetStream().WriteAsync(data, timeout.Token);
                await tcp.GetStream().ReadExactlyAsync(data, timeout.Token);
                using var receiver = new UdpClient(destination);
                using var sender = new UdpClient(new IPEndPoint(local, 0));
                await sender.SendAsync(data, destination, timeout.Token);
                await receiver.ReceiveAsync(timeout.Token);
                ports.Add((remote, destination.Port));
            }
        }
        finally
        {
            session.Stop();
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var rows = capture.TakeUpdates(out var dropped);
        foreach (var (remote, port) in ports)
        {
            var flows = rows.Where(row => row.Key.Address == remote.ToString() && row.Key.Port == port
                && row.Key.Process.Pid == Environment.ProcessId && row.Path is not null).ToArray();
            Assert(flows.Any(row => row.Key.Protocol == InvestigationProtocol.Tcp && row.Sent >= 64 && row.Received >= 64)
                && flows.Any(row => row.Key.Protocol == InvestigationProtocol.Udp && row.Sent >= 64)
                && InvestigationReport.Destinations(flows).Length == 1,
                "Expected both attributed protocols and TCP directions for " + remote.AddressFamily);
        }
        Assert(dropped == 0 && session.Loss == default, "ETW smoke data must be complete.");
        Console.WriteLine("PASS: real ETW IPv4/IPv6 TCP and UDP, remote ports, process paths and destination groups.");
        return 0;
    }

    internal static int Run()
    {
        CombinesTcpAndUdpByAddress();
        StopsAddingKeysAtCapacity();
        HandlesPidReuseAndLateEvents();
        SavesBoundedReportsAndAggregateCsv();
        ValidatesWireRowsAndStartupMatches();
        RetainsCumulativeRowsAcrossBatchesAndStop();
        ResetsCaptureAndRejectsBufferedEvents();
        return 7;
    }

    private static void CombinesTcpAndUdpByAddress()
    {
        var localIpv4 = IPAddress.Parse("192.0.2.1");
        var localIpv6 = IPAddress.Parse("fe80::a");
        var peer = IPAddress.Parse("198.51.100.24");
        var ipv6Peer = IPAddress.Parse("2001:db8::b");
        var resolutions = 0;
        var capture = new InvestigationCapture([localIpv4, IPAddress.Parse("fe80::a%12")], resolve: (id, _) =>
        {
            resolutions++;
            return (Started.UtcTicks, id == 1 ? @"C:\Apps\A.exe" : @"C:\Apps\B.exe");
        });
        capture.Record(InvestigationProtocol.Tcp, 1, localIpv4, peer, 443, 40, false, Started.AddSeconds(1), 1);
        capture.Record(InvestigationProtocol.Tcp, 1, localIpv4, peer, 443, 50, true, Started.AddSeconds(2), 2);
        capture.Record(InvestigationProtocol.Udp, 1, localIpv4, peer.MapToIPv6(), 443, 20, false, Started.AddSeconds(3), 3);
        capture.Record(InvestigationProtocol.Tcp, 2, localIpv4, peer, 8443, 10, false, Started.AddSeconds(4), 4);
        capture.Record(InvestigationProtocol.Udp, 1, localIpv6, ipv6Peer, 53, 4, false, Started.AddSeconds(5), 5);
        capture.Record(InvestigationProtocol.Udp, 1, IPAddress.Loopback, ipv6Peer, 53, 999, false, Started.AddSeconds(5), 6);
        var rows = capture.TakeUpdates(out var dropped);
        Assert(rows.Length == 4 && dropped == 0 && resolutions == 2, "Only selected local addresses and one resolution per process should be used.");
        var group = InvestigationReport.Destinations(rows).Single(item => item.Key == peer.ToString());
        Assert(group.Rows.Length == 3 && group.Sent == 70 && group.Received == 50 && group.LastSeen == Started.AddSeconds(4),
            "Both protocols, multiple ports, and multiple apps must merge by normalized IP.");
        var app = InvestigationReport.Applications(rows).Single(item => item.Label == "A.exe");
        var appPeer = InvestigationReport.Destinations(app.Rows).Single(item => item.Key == peer.ToString());
        Assert(appPeer.Rows.Length == 2 && appPeer.Sent == 60 && appPeer.Received == 50, "An app's destination grouping must exclude other apps.");
        var report = InvestigationReport.Markdown(new("Demo", Started, Started.AddSeconds(5), rows, 0, false), null, null);
        Assert(report.Contains("| 198.51.100.24 | 70 | 50 | 2 |") && report.Contains("| 198.51.100.24 | UDP | 443 |"),
            "The report must agree with the grouped view and retain protocol detail.");
        Assert(capture.TakeUpdates(out _).Length == 0, "Unchanged rows must not be resent.");
    }

    private static void StopsAddingKeysAtCapacity()
    {
        var local = IPAddress.Loopback;
        var remote = IPAddress.Parse("203.0.113.2");
        var capture = new InvestigationCapture([local], resolve: (_, _) => default);
        for (var port = 1; port <= TrafficWireProtocol.MaxEntries + 1; port++)
            capture.Record(InvestigationProtocol.Udp, 0, local, remote, port, 1, false, Started, port);
        capture.Record(InvestigationProtocol.Udp, 0, local, remote, 1, 2, false, Started.AddSeconds(1), 3000);
        var rows = capture.TakeUpdates(out var missing);
        Assert(rows.Length == 2048 && missing == 1 && rows.Single(row => row.Key.Port == 1).Sent == 3,
            "Retained rows must keep counting when new rows reach capacity.");
    }

    private static void HandlesPidReuseAndLateEvents()
    {
        var local = IPAddress.Loopback;
        var remote = IPAddress.Parse("192.0.2.1");
        var next = Started.AddSeconds(2);
        var capture = new InvestigationCapture([local], resolve: (_, time) => time < next
            ? (Started.UtcTicks, @"C:\Demo\Old.exe") : (next.UtcTicks, @"C:\Demo\New.exe"));
        capture.Record(InvestigationProtocol.Tcp, 12, local, remote, 443, 8, false, Started.AddSeconds(1), 10);
        capture.ProcessChanged(12, 20, next, false);
        capture.ProcessChanged(12, 21, next, true);
        capture.Record(InvestigationProtocol.Tcp, 12, local, remote, 443, 4, false, next.AddSeconds(1), 30);
        capture.Record(InvestigationProtocol.Tcp, 12, local, remote, 443, 1, false, Started.AddSeconds(1), 19);
        var rows = capture.TakeUpdates(out _);
        Assert(rows.Length == 3 && rows.Single(row => row.Path is null).Sent == 1
            && rows.Single(row => row.Path == @"C:\Demo\New.exe").Key.Process.StartedUtcTicks == next.UtcTicks,
            "A reused PID must not attach its path to older or unverified traffic.");
    }

    private static void SavesBoundedReportsAndAggregateCsv()
    {
        var longPath = @"C:\調査\" + new string('例', 490) + ".exe";
        var rows = Enumerable.Range(1, 180).Select(i => new InvestigationRow(
            new(new(i, Started.UtcTicks), i <= 150 ? "198.51.100.24" : $"203.0.113.{i}", i, InvestigationProtocol.Tcp),
            longPath, 10, 2, 1, Started, Started.AddSeconds(i))).ToArray();
        var record = new InvestigationRecord("Wi-Fi", Started, Started.AddMinutes(4), rows, 9, true);
        var report = InvestigationReport.Markdown(record, null, null);
        Assert(Encoding.UTF8.GetByteCount(report) <= InvestigationReport.MaxMarkdownBytes && report.Contains("省略した内訳:")
            && !report.Contains("省略した内訳: 0 行") && report.Contains("1800 B") && report.Contains("360 B")
            && report.Contains(longPath) && report.Contains("部分記録"), "The byte-limited report must preserve totals, paths and omission metadata.");
        var app = new InvestigationGroup("selected", "selected", [rows[0]]);
        var single = InvestigationReport.Markdown(record, app, "HKCU\\Run · Example");
        Assert(single.Contains("宛先 1 件、内訳 1 行") && single.Contains("HKCU\\Run") && !single.Contains("203.0.113."),
            "Selected app reports must contain only the requested rows and startup entries.");
        var path = Path.Combine(Path.GetTempPath(), $"SaruMon-investigation-{Guid.NewGuid():N}.csv");
        try
        {
            InvestigationReport.SaveCsv(path, record);
            var lines = File.ReadAllLines(path);
            Assert(lines.Length == 181 && lines[1].Contains("部分記録") && lines[1].Contains(longPath),
                "The stopped record's CSV must retain all aggregate rows and their real fields.");
        }
        finally { File.Delete(path); }
    }

    private static void ValidatesWireRowsAndStartupMatches()
    {
        var row = new InvestigationRow(new(new(10, Started.UtcTicks), "2001:db8::1", 443, InvestigationProtocol.Udp),
            @"C:\Apps\B.exe", 3, 0, 1, Started, Started);
        var message = new TrafficMessage("snapshot", Revision: 1, Mode: MonitoringMode.Investigation,
            InvestigationRows: [row], Final: true, SampledAt: Started);
        TrafficWireProtocol.ValidateSnapshot(message, MonitoringMode.Investigation);
        try
        {
            TrafficWireProtocol.ValidateSnapshot(message with { InvestigationRows = [row with { Received = 1 }] }, MonitoringMode.Investigation);
            throw new InvalidOperationException("UDP upload rows must reject inbound bytes.");
        }
        catch (InvalidDataException) { }
        Assert(InvestigationReport.MatchesCommand("\"C:\\Program Files\\Foo\\App.exe\" /quiet", @"c:\program files\Foo\App.exe")
            && !InvestigationReport.MatchesCommand(@"C:\Other.exe C:\Apps\B.exe", row.Path!)
            && !InvestigationReport.MatchesCommand("B.exe /quiet", row.Path!),
            "Startup entries must only match an explicit executable path, not a command argument.");
    }

    private static void RetainsCumulativeRowsAcrossBatchesAndStop()
    {
        var rows = Enumerable.Range(1, TrafficWireProtocol.InvestigationChunkSize + 1).Select(port =>
            new InvestigationRow(new(new(1, Started.UtcTicks), "192.0.2.8", port, InvestigationProtocol.Tcp),
                @"C:AppsApp.exe", 5, 1, 1, Started, Started)).ToArray();
        var batches = new InvestigationBatches();
        var message = new TrafficMessage("snapshot", Revision: 1, Mode: MonitoringMode.Investigation, SampledAt: Started,
            InvestigationRows: rows[..^1], More: true);
        Assert(batches.Add(RoundTrip(message)) is null, "An incomplete batch must not replace the previous visible snapshot.");
        var first = batches.Add(RoundTrip(message with { Chunk = 1, More = false, InvestigationRows = [rows[^1]] }))!;
        Assert(first.Rows.Count == rows.Length && !first.Incomplete, "Chunk assembly must expose one complete snapshot.");
        var idle = batches.Add(RoundTrip(message with { More = false, InvestigationRows = [], SampledAt = Started.AddSeconds(1) }))!;
        Assert(ReferenceEquals(first.Rows, idle.Rows), "An idle update must reuse the existing immutable rows.");
        var final = batches.Add(RoundTrip(message with { More = false, Final = true, DroppedEvents = 2,
            SampledAt = Started.AddSeconds(2), InvestigationRows = [rows[0] with { Sent = 10, LastSeen = Started.AddSeconds(2) }] }))!;
        Assert(final.Rows.Count == rows.Length && final.Rows.Single(row => row.Key.Port == 1).Sent == 10
            && first.Rows.Single(row => row.Key.Port == 1).Sent == 5 && final.Incomplete,
            "A final delta must replace updated rows, preserve other rows, and retain the loss marker.");

        Assert(batches.Add(RoundTrip(message)) is null, "A partial batch from the old revision can be pending at reset.");
        var reset = batches.Add(RoundTrip(message with { Revision = 2, More = false, SampledAt = Started.AddSeconds(3),
            InvestigationRows = [rows[0] with { Sent = 1, Received = 0, FirstSeen = Started.AddSeconds(3), LastSeen = Started.AddSeconds(3) }] }))!;
        Assert(reset.Rows.Count == 1 && reset.Rows[0].Sent == 1 && !reset.Incomplete,
            "The new revision starts at chunk zero and drops all old rows, pending chunks and loss indicators.");

        static TrafficMessage RoundTrip(TrafficMessage message)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(message, TrafficWireProtocol.JsonOptions);
            var read = TrafficWireProtocol.Deserialize(json);
            TrafficWireProtocol.ValidateSnapshot(read, MonitoringMode.Investigation);
            return read;
        }
    }

    private static void ResetsCaptureAndRejectsBufferedEvents()
    {
        var local = IPAddress.Parse("192.0.2.10");
        var remote = IPAddress.Parse("198.51.100.24");
        var capture = new InvestigationCapture([local], capacity: 2,
            resolve: (_, _) => (Started.UtcTicks, @"C:\Apps\Fixture.exe"));
        capture.Record(InvestigationProtocol.Tcp, 10, local, remote, 443, 100, false, Started.AddSeconds(1), 10);
        capture.Record(InvestigationProtocol.Udp, 10, local, remote, 443, 200, false, Started.AddSeconds(1), 11);
        capture.Record(InvestigationProtocol.Udp, 10, local, remote, 444, 400, false, Started.AddSeconds(1), 12);
        Assert(capture.TakeUpdates(out var lost).Length == 2 && lost == 1, "Fixture reaches capacity before reset.");
        capture.Record(InvestigationProtocol.Tcp, 10, local, remote, 443, 20, true, Started.AddSeconds(2), 19);
        capture.ResetLog(20);
        Assert(capture.TakeUpdates(out lost).Length == 0 && lost == 0, "Reset clears rows, unpublished updates and the loss counter.");

        var resumed = Started.AddSeconds(3);
        capture.Record(InvestigationProtocol.Tcp, 10, local, remote, 443, 999, false, Started.AddSeconds(2), 19);
        capture.Record(InvestigationProtocol.Tcp, 10, local, remote, 443, 7, false, resumed, 20);
        capture.Record(InvestigationProtocol.Tcp, 10, local, remote, 443, 8, true, resumed, 21);
        capture.Record(InvestigationProtocol.Udp, 10, local, remote, 444, 9, false, resumed, 22);
        var after = capture.TakeUpdates(out lost);
        Assert(after.Length == 2 && lost == 0 && after.All(row => row.FirstSeen == resumed && row.LastSeen == resumed
            && row.Key.Process.StartedUtcTicks == Started.UtcTicks)
            && after.Single(row => row.Key.Protocol == InvestigationProtocol.Tcp) is { Sent: 7, Received: 8, Events: 2 }
            && after.Single(row => row.Key.Protocol == InvestigationProtocol.Udp) is { Sent: 9, Received: 0, Events: 1 },
            "Only events at or after the reset boundary count, with process identity and capacity still available.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
