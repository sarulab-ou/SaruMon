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
            await WaitForTotalsAsync();
            var before = groups.Select(group => Find(group)).ToArray();
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var peer = before[index];
                if (monitor.TcpSnapshot.Count(item => item.RemoteAddress == group.RemoteAddress.ToString()) != 1)
                    throw new InvalidOperationException("Ports were not aggregated into one peer.");
                if (peer.FirstSeen == default || peer.LastSeen < peer.FirstSeen)
                    throw new InvalidOperationException("Invalid TCP first/last timestamp.");
                if (!group.BothEndsSelected && (peer.SentBytes != 192 || peer.ReceivedBytes != 320))
                    throw new InvalidOperationException($"TCP upload/download direction mismatch: {peer.SentBytes}/{peer.ReceivedBytes}.");
            }
            if (monitor.UdpSnapshot.Count != 0) throw new InvalidOperationException("TCP monitoring exposed UDP rows.");

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
                    || peer.ReceivedBytes < expectedReceive)
                    throw new InvalidOperationException("TCP final snapshot lost traffic or changed FirstSeen.");
                Console.WriteLine($"TCP {group.RemoteAddress}: upload={peer.SentBytes}, download={peer.ReceivedBytes}, first={peer.FirstSeen:O}, last={peer.LastSeen:O}");
            }
            if (monitor.Error is not null || monitor.DroppedEvents != 0)
                throw new InvalidOperationException("TCP capture was partial: " + monitor.Error);
            Console.WriteLine("PASS: TCP peer aggregation across ports, upload/download direction, first-seen time, final snapshot, and helper exit.");
            return 0;

            TcpPeerInfo Find(PeerGroup group) => monitor.TcpSnapshot.SingleOrDefault(peer => peer.RemoteAddress == group.RemoteAddress.ToString())
                ?? throw new InvalidOperationException("Missing TCP peer: " + group.RemoteAddress);

            async Task WaitForTotalsAsync()
            {
                using var observed = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                observed.CancelAfter(TimeSpan.FromSeconds(12));
                while (groups.Any(group =>
                    {
                        var peer = monitor.TcpSnapshot.FirstOrDefault(item => item.RemoteAddress == group.RemoteAddress.ToString());
                        return peer is null || peer.SentBytes < (group.BothEndsSelected ? 512 : 192)
                            || peer.ReceivedBytes < (group.BothEndsSelected ? 512 : 320);
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
            Console.Error.WriteLine($"TCP peer {peer.RemoteAddress}: upload={peer.SentBytes}, download={peer.ReceivedBytes}, first={peer.FirstSeen:O}, last={peer.LastSeen:O}");
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
