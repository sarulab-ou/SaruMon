using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using SaruMon.Monitoring;

namespace SaruMon.Tests;

/// <summary>Explicit opt-in: a fixed, anonymous HTTPS HEAD to this public repository.</summary>
internal static class TcpNetworkSmoke
{
    internal static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var connection = new TcpClient();
        await using var monitor = new TrafficMonitorClient(MonitoringMode.Tcp);
        IPAddress? remote = null;
        try
        {
            await connection.ConnectAsync("github.com", 443, timeout.Token);
            var local = ((IPEndPoint)connection.Client.LocalEndPoint!).Address;
            remote = ((IPEndPoint)connection.Client.RemoteEndPoint!).Address;
            Console.WriteLine("TCP network smoke: starting the elevated helper for an anonymous public-repository HEAD request.");
            await monitor.StartAsync([local], timeout.Token);
            using var tls = new SslStream(connection.GetStream(), leaveInnerStreamOpen: true);
            // Keep the platform's normal certificate validation and TLS defaults.
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "github.com" }, timeout.Token);
            var request = Encoding.ASCII.GetBytes("HEAD /sarulab-ou/SaruMon HTTP/1.1\r\nHost: github.com\r\nUser-Agent: SaruMon-Network-Smoke\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(request, timeout.Token);
            await tls.FlushAsync(timeout.Token);

            var response = new byte[16 * 1024];
            var length = 0;
            var complete = false;
            while (length < response.Length)
            {
                var read = await tls.ReadAsync(response.AsMemory(length), timeout.Token);
                if (read == 0) break;
                length += read;
                if (Encoding.ASCII.GetString(response, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) { complete = true; break; }
            }
            var headers = Encoding.ASCII.GetString(response, 0, length);
            if (!complete || !(headers.StartsWith("HTTP/1.1 2", StringComparison.Ordinal) || headers.StartsWith("HTTP/1.1 3", StringComparison.Ordinal)))
                throw new IOException("The public HEAD response was incomplete or unsuccessful.");

            using (var observed = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                observed.CancelAfter(TimeSpan.FromSeconds(12));
                while (true)
                {
                    var peer = monitor.TcpSnapshot.SingleOrDefault(item => item.RemoteAddress == Normalize(remote));
                    if (peer is { SentBytes: > 0, ReceivedBytes: > 0 }) break;
                    if (!monitor.IsRunning) throw new InvalidOperationException(monitor.Error ?? "TCP monitoring ended.");
                    await Task.Delay(100, observed.Token);
                }
            }
            await monitor.StopAsync();
            var retained = monitor.TcpSnapshot.SingleOrDefault(item => item.RemoteAddress == Normalize(remote));
            if (retained is not { SentBytes: > 0, ReceivedBytes: > 0 } || monitor.IsRunning)
                throw new InvalidOperationException("The remote peer's bidirectional totals were not retained.");
            var overview = monitor.TcpTraffic?.Reading;
            if (overview is null || overview.SentBytes < retained.SentBytes || overview.ReceivedBytes < retained.ReceivedBytes || overview.Incomplete)
                throw new InvalidOperationException("The separate interface overview is missing observed TCP traffic.");
            if (monitor.Error is not null || monitor.DroppedEvents != 0)
                throw new InvalidOperationException("Capture was partial: " + monitor.Error);
            Console.WriteLine($"TCP public peer {remote}: upload={retained.SentBytes}, download={retained.ReceivedBytes}");
            Console.WriteLine("PASS: ordinary-network TCP send/receive orientation and retained totals for an anonymous HTTPS HEAD.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("TCP network smoke failed: " + exception);
            PrintTargetOnly(monitor, remote);
            return 1;
        }
        finally { await monitor.StopAsync(); }
    }

    private static string Normalize(IPAddress address) => address.IsIPv4MappedToIPv6
        ? address.MapToIPv4().ToString() : address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(address.GetAddressBytes()).ToString() : address.ToString();

    private static void PrintTargetOnly(TrafficMonitorClient monitor, IPAddress? remote)
    {
        Console.Error.WriteLine($"TCP capture: running={monitor.IsRunning}, dropped={monitor.DroppedEvents}, error={monitor.Error ?? "(none)"}");
        if (remote is null) return;
        var peer = monitor.TcpSnapshot.SingleOrDefault(item => item.RemoteAddress == Normalize(remote));
        Console.Error.WriteLine($"TCP public peer {remote}: upload={peer?.SentBytes ?? 0}, download={peer?.ReceivedBytes ?? 0}");
    }
}
