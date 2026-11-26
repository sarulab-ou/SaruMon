using System.Net;
using System.Net.Sockets;
using SaruMon.Core;
using SaruMon.Monitoring;

namespace SaruMon.Tests;

/// <summary>Explicit opt-in integration test. Starts UAC/ETW; never called by the ordinary test suite.</summary>
internal static class UdpLiveSmoke
{
    internal static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var monitor = new TrafficMonitorClient(MonitoringMode.Udp);
        var endpoints = new List<DatagramPair>();
        try
        {
            endpoints.Add(new DatagramPair(IPAddress.Loopback));
            if (Socket.OSSupportsIPv6) endpoints.Add(new DatagramPair(IPAddress.IPv6Loopback));
            Console.WriteLine("UDP live smoke: starting the same-user elevated helper (UAC approval required).");
            await monitor.StartAsync(endpoints.Select(pair => pair.Address).ToArray(), timeout.Token);
            if (!monitor.IsRunning) throw new InvalidOperationException("The helper did not report ready.");

            foreach (var pair in endpoints) await pair.SendAsync(3, timeout.Token);
            await WaitForPacketsAsync(3);
            var before = endpoints.Select(pair => Find(monitor.UdpSnapshot, pair)
                ?? throw new InvalidOperationException("Missing observed flow.")).ToArray();
            foreach (var flow in before)
                if (flow.FirstSeen == default || flow.LastSeen < flow.FirstSeen || flow.Bytes < 3 * DatagramPair.PayloadSize)
                    throw new InvalidOperationException("Invalid observed count or first/last timestamp.");

            // Stop immediately after another datagram. The final snapshot must include the flushed ETW event.
            foreach (var pair in endpoints) await pair.SendAsync(1, timeout.Token);
            await monitor.StopAsync();
            if (monitor.IsRunning) throw new InvalidOperationException("Monitoring is still running after StopAsync.");
            for (var index = 0; index < endpoints.Count; index++)
            {
                var retained = Find(monitor.UdpSnapshot, endpoints[index])
                    ?? throw new InvalidOperationException("Stopping erased the observed rows.");
                if (retained.Packets < 4 || retained.Bytes < 4 * DatagramPair.PayloadSize || retained.FirstSeen != before[index].FirstSeen)
                    throw new InvalidOperationException("The final snapshot lost counts or changed FirstSeen.");
                Console.WriteLine($"UDP {endpoints[index].Address.AddressFamily}: packets={retained.Packets}, bytes={retained.Bytes}, first={retained.FirstSeen:O}, last={retained.LastSeen:O}");
            }
            if (monitor.Error is not null || monitor.DroppedEvents != 0)
                throw new InvalidOperationException("Capture was partial: " + monitor.Error);
            Console.WriteLine("PASS: UDP loopback observation, cumulative counts, first-seen time, final snapshot, and helper exit.");
            return 0;

            async Task WaitForPacketsAsync(long minimum)
            {
                using var observed = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                observed.CancelAfter(TimeSpan.FromSeconds(10));
                while (endpoints.Any(pair =>
                    {
                        var flow = Find(monitor.UdpSnapshot, pair);
                        return flow is null || flow.Packets < minimum;
                    }))
                {
                    if (!monitor.IsRunning) throw new InvalidOperationException(monitor.Error ?? "Monitoring ended before observation.");
                    await Task.Delay(100, observed.Token);
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("UDP live smoke failed: " + exception);
            return 1;
        }
        finally
        {
            foreach (var pair in endpoints) pair.Dispose();
            // Awaited disposal is deliberately outside the normal test suite and confirms the helper has exited.
            await monitor.StopAsync();
        }
    }

    private static UdpFlowInfo? Find(IReadOnlyList<UdpFlowInfo> snapshot, DatagramPair pair) => snapshot.FirstOrDefault(flow =>
        flow.LocalAddress == pair.Address.ToString() && flow.LocalPort == pair.LocalPort
        && flow.RemoteAddress == pair.Address.ToString() && flow.RemotePort == pair.RemotePort);

    private sealed class DatagramPair : IDisposable
    {
        internal const int PayloadSize = 96;
        private readonly UdpClient receiver;
        private readonly UdpClient sender;
        internal IPAddress Address { get; }
        internal int LocalPort => ((IPEndPoint)sender.Client.LocalEndPoint!).Port;
        internal int RemotePort => ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        internal DatagramPair(IPAddress address)
        {
            Address = address;
            receiver = new UdpClient(new IPEndPoint(address, 0));
            try { sender = new UdpClient(new IPEndPoint(address, 0)); }
            catch { receiver.Dispose(); throw; }
        }

        internal async Task SendAsync(int count, CancellationToken token)
        {
            var payload = new byte[PayloadSize];
            for (var index = 0; index < count; index++)
            {
                await sender.SendAsync(payload, new IPEndPoint(Address, RemotePort), token);
                var received = await receiver.ReceiveAsync(token);
                if (received.Buffer.Length != PayloadSize) throw new IOException("Loopback datagram was truncated.");
            }
        }

        public void Dispose()
        {
            sender.Dispose();
            receiver.Dispose();
        }
    }
}
