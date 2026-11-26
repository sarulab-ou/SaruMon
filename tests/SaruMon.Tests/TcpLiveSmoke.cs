using System.Net;
using System.Net.Sockets;
using SaruMon.Core;
using SaruMon.Monitoring;

namespace SaruMon.Tests;

/// <summary>Explicit opt-in integration test. Starts UAC/ETW only when requested.</summary>
internal static class TcpLiveSmoke
{
    internal static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var monitor = new TrafficMonitorClient(MonitoringMode.Tcp);
        var groups = new List<PeerGroup>();
        var failed = false;
        try
        {
            // Distinct 127/8 addresses make upload/download direction independently testable.
            groups.Add(new PeerGroup(IPAddress.Loopback, IPAddress.Parse("127.0.0.2")));
            if (Socket.OSSupportsIPv6) groups.Add(new PeerGroup(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback));
            foreach (var group in groups) await group.ConnectAsync(timeout.Token);
            Console.WriteLine("TCP live smoke: starting the same-user elevated helper (UAC approval required).");
            await monitor.StartAsync(groups.Select(group => group.LocalAddress).ToArray(), timeout.Token);
            if (!monitor.IsRunning) throw new InvalidOperationException("The TCP helper did not report ready.");

            // Different local/remote ports on two connections must merge into a single peer row.
            foreach (var group in groups) await group.ExchangeAsync(96, 160, timeout.Token);
            await WaitForTotalsAsync(192, 320, 0);
            var baseline = groups.Select(group => Find(group)).ToArray();
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var peer = baseline[index];
                if (monitor.TcpSnapshot.Count(item => item.RemoteAddress == group.RemoteAddress.ToString()) != 1)
                    throw new InvalidOperationException("Ports were not aggregated into one peer.");
                if (peer.FirstSeen == default || peer.LastSeen < peer.FirstSeen)
                    throw new InvalidOperationException("Invalid TCP first/last timestamp.");
                if (!group.BothEndsSelected && (peer.SentBytes != 192 || peer.ReceivedBytes != 320))
                    throw new InvalidOperationException($"TCP upload/download direction mismatch: {peer.SentBytes}/{peer.ReceivedBytes}.");
                if (peer.ConnectionCount != 0)
                    throw new InvalidOperationException("A connection established before monitoring was counted.");
            }
            if (monitor.UdpSnapshot.Count != 0) throw new InvalidOperationException("TCP monitoring exposed UDP rows.");

            // A refused attempt must not be mistaken for an observed Connect/Accept.
            foreach (var group in groups) await group.AttemptRefusedConnectionAsync(timeout.Token);
            // Two new connections per peer: IPv4 selects the client side only, while IPv6
            // selects both ::1 endpoints and therefore observes two Connect + two Accept events.
            foreach (var group in groups) await group.ConnectAsync(timeout.Token);
            // Exercise all four connections, preserving bytes already seen on the initial two.
            foreach (var group in groups) await group.ExchangeAsync(96, 160, timeout.Token);
            await WaitForTotalsAsync(576, 960, 2);
            var before = groups.Select(group => Find(group)).ToArray();
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var peer = before[index];
                var expectedConnections = group.BothEndsSelected ? 4 : 2;
                if (peer.ConnectionCount != expectedConnections)
                    throw new InvalidOperationException($"TCP connection count mismatch: {peer.ConnectionCount}, expected {expectedConnections}.");
                if (peer.FirstSeen != baseline[index].FirstSeen || peer.LastSeen <= baseline[index].LastSeen)
                    throw new InvalidOperationException("TCP connection/data events did not preserve first time and advance last time.");
            }

            var overviewBefore = monitor.TcpTraffic ?? throw new InvalidOperationException("Missing TCP overview.");
            foreach (var group in groups) await group.SendTailAsync(40, timeout.Token);
            await monitor.ResetLogAsync(timeout.Token);
            if (!monitor.IsRunning || groups.Any(group => monitor.TcpSnapshot.Any(item => item.RemoteAddress == group.RemoteAddress.ToString()))
                || monitor.TcpTraffic?.Revision != overviewBefore.Revision)
                throw new InvalidOperationException("Active reset stopped monitoring, restored old rows, or changed the overview baseline.");
            foreach (var group in groups) await group.ExchangeAsync(24, 32, timeout.Token);
            await WaitForTotalsAsync(96, 128, 0);
            before = groups.Select(group => Find(group)).ToArray();
            for (var index = 0; index < groups.Count; index++)
            {
                var peer = before[index];
                var both = groups[index].BothEndsSelected;
                if (peer.SentBytes != (both ? 224 : 96) || peer.ReceivedBytes != (both ? 224 : 128)
                    || peer.ConnectionCount != 0 || peer.FirstSeen <= baseline[index].FirstSeen)
                    throw new InvalidOperationException("The reset log included pre-reset data, connections, or timestamps.");
            }

            // Receive application data first, then stop immediately, requiring the final ETW buffer drain.
            foreach (var group in groups) await group.SendTailAsync(80, timeout.Token);
            await monitor.StopAsync();
            if (monitor.IsRunning) throw new InvalidOperationException("TCP monitoring did not stop.");
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var peer = Find(group);
                var expectedReceive = before[index].ReceivedBytes + (group.BothEndsSelected ? 80 : 0);
                if (peer.FirstSeen != before[index].FirstSeen || peer.SentBytes < before[index].SentBytes + 80
                    || peer.ReceivedBytes < expectedReceive || peer.LastSeen < before[index].LastSeen
                    || peer.ConnectionCount != before[index].ConnectionCount)
                    throw new InvalidOperationException("TCP final snapshot lost traffic, connection counts, or timestamps.");
                Console.WriteLine($"TCP {group.RemoteAddress}: upload={peer.SentBytes}, download={peer.ReceivedBytes}, connections={peer.ConnectionCount}, first={peer.FirstSeen:O}, last={peer.LastSeen:O}");
            }
            if (monitor.Error is not null || monitor.DroppedEvents != 0)
                throw new InvalidOperationException("TCP capture was partial: " + monitor.Error);
            if (monitor.TcpTraffic?.Reading is not { SentBytes: 0, ReceivedBytes: 0, Incomplete: false })
                throw new InvalidOperationException("Loopback TCP must stay out of the interface overview.");
            await monitor.ResetLogAsync(timeout.Token);
            if (monitor.IsRunning || monitor.TcpSnapshot.Count != 0 || monitor.DroppedEvents != 0)
                throw new InvalidOperationException("Resetting a stopped log must clear its retained rows without starting capture.");
            Console.WriteLine("PASS: live and stopped peer log reset, pre-reset events excluded, overview revision retained.");
            Console.WriteLine("PASS: TCP peer aggregation, bidirectional bytes, observed connection counts, refused/previous connections excluded, first/last times, final snapshot, and helper exit.");
            return 0;

            TcpPeerInfo Find(PeerGroup group) => monitor.TcpSnapshot.SingleOrDefault(peer => peer.RemoteAddress == group.RemoteAddress.ToString())
                ?? throw new InvalidOperationException("Missing TCP peer: " + group.RemoteAddress);

            async Task WaitForTotalsAsync(long sent, long received, long connections)
            {
                using var observed = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                observed.CancelAfter(TimeSpan.FromSeconds(12));
                while (groups.Any(group =>
                    {
                        var peer = monitor.TcpSnapshot.FirstOrDefault(item => item.RemoteAddress == group.RemoteAddress.ToString());
                        return peer is null || peer.SentBytes < (group.BothEndsSelected ? sent + received : sent)
                            || peer.ReceivedBytes < (group.BothEndsSelected ? sent + received : received)
                            || peer.ConnectionCount < (group.BothEndsSelected ? 2 * connections : connections);
                    }))
                {
                    if (!monitor.IsRunning) throw new InvalidOperationException(monitor.Error ?? "TCP monitoring ended before observation.");
                    await Task.Delay(100, observed.Token);
                }
            }
        }
        catch (Exception exception)
        {
            failed = true;
            Console.Error.WriteLine("TCP live smoke failed: " + exception);
            DumpSnapshot("failure", monitor);
            return 1;
        }
        finally
        {
            try
            {
                await monitor.StopAsync();
                if (failed) DumpSnapshot("after stop", monitor);
            }
            finally { foreach (var group in groups) group.Dispose(); }
        }
    }

    private static void DumpSnapshot(string stage, TrafficMonitorClient monitor)
    {
        Console.Error.WriteLine($"TCP {stage}: running={monitor.IsRunning}, peers={monitor.TcpSnapshot.Count}, dropped={monitor.DroppedEvents}, error={monitor.Error ?? "(none)"}");
        foreach (var peer in monitor.TcpSnapshot)
            Console.Error.WriteLine($"TCP peer {peer.RemoteAddress}: upload={peer.SentBytes}, download={peer.ReceivedBytes}, connections={peer.ConnectionCount}, first={peer.FirstSeen:O}, last={peer.LastSeen:O}");
    }

    private sealed class PeerGroup : IDisposable
    {
        private readonly List<(TcpClient Sender, TcpClient Receiver)> pairs = [];
        internal IPAddress LocalAddress { get; }
        internal IPAddress RemoteAddress { get; }
        internal bool BothEndsSelected => LocalAddress.Equals(RemoteAddress);

        internal PeerGroup(IPAddress localAddress, IPAddress remoteAddress)
        {
            LocalAddress = localAddress;
            RemoteAddress = remoteAddress;
        }

        internal async Task ConnectAsync(CancellationToken token)
        {
            using var listener = new TcpListener(RemoteAddress, 0);
            listener.Start();
            for (var index = 0; index < 2; index++)
            {
                var sender = new TcpClient(new IPEndPoint(LocalAddress, 0));
                try
                {
                    var accepted = listener.AcceptTcpClientAsync(token);
                    await sender.ConnectAsync(RemoteAddress, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                    var receiver = await accepted;
                    sender.NoDelay = true;
                    receiver.NoDelay = true;
                    pairs.Add((sender, receiver));
                }
                catch { sender.Dispose(); throw; }
            }
        }

        internal async Task ExchangeAsync(int sent, int received, CancellationToken token)
        {
            foreach (var pair in pairs)
            {
                await TransferAsync(pair.Sender, pair.Receiver, sent, token);
                await TransferAsync(pair.Receiver, pair.Sender, received, token);
            }
        }

        internal async Task AttemptRefusedConnectionAsync(CancellationToken token)
        {
            // Reserving a local port without listening avoids accidentally reaching another service.
            using var reserved = new Socket(RemoteAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            reserved.Bind(new IPEndPoint(RemoteAddress, 0));
            using var sender = new TcpClient(new IPEndPoint(LocalAddress, 0));
            try { await sender.ConnectAsync(RemoteAddress, ((IPEndPoint)reserved.LocalEndPoint!).Port, token); }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused) { return; }
            throw new InvalidOperationException("A non-listening TCP port accepted a connection.");
        }

        internal Task SendTailAsync(int size, CancellationToken token) => TransferAsync(pairs[0].Sender, pairs[0].Receiver, size, token);

        private static async Task TransferAsync(TcpClient sender, TcpClient receiver, int size, CancellationToken token)
        {
            await sender.GetStream().WriteAsync(new byte[size], token);
            await receiver.GetStream().ReadExactlyAsync(new byte[size], token);
        }

        public void Dispose()
        {
            foreach (var pair in pairs) { pair.Sender.Dispose(); pair.Receiver.Dispose(); }
        }
    }
}
