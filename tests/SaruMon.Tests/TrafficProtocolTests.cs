using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using SaruMon.Core;
using SaruMon.Monitoring;

namespace SaruMon.Tests;

internal static class TrafficProtocolTests
{
    internal static int Run()
    {
        var count = 0;
        void Validate(TrafficMessage value) => TrafficWireProtocol.ValidateSnapshot(value, MonitoringMode.Udp);
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Traffic protocol: " + description);
            count++;
        }
        void Reject(Action action, string description)
        {
            try { action(); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                count++;
                return;
            }
            throw new InvalidOperationException("Traffic protocol accepted: " + description);
        }
        var nonce = new string('a', 64);
        var pipeName = "SaruMon-Monitor-" + new string('b', 32);
        Check(TrafficWireProtocol.IsNonce(nonce) && !TrafficWireProtocol.IsNonce(nonce + "0") && !TrafficWireProtocol.IsNonce(new string('g', 64)), "nonce length/alphabet");
        Check(TrafficWireProtocol.IsPipeName(pipeName) && !TrafficWireProtocol.IsPipeName("../" + pipeName), "pipe name whitelist");
        Check(TrafficWireProtocol.ParseAddresses(["127.0.0.1", "::1", "127.0.0.1"]).Length == 2, "bounded IPv4/IPv6 addresses");
        Reject(() => TrafficWireProtocol.ParseAddresses(["0.0.0.0"]), "unspecified address");
        Reject(() => TrafficWireProtocol.ParseAddresses(["example.com"]), "hostname instead of literal address");
        Reject(() => TrafficWireProtocol.ParseAddresses(Enumerable.Repeat("127.0.0.1", TrafficWireProtocol.MaxAddresses + 1).ToArray()), "too many addresses");
        Reject(() => TrafficWireProtocol.Deserialize("{\"Type\":\"stop\",\"Command\":\"ignored\"}"), "unknown command property");
        Reject(() => TrafficWireProtocol.Deserialize("null"), "null message");

        var now = DateTimeOffset.UtcNow;
        var flow = new UdpFlowInfo("127.0.0.1", 1234, "127.0.0.1", 5678, 1, 0, now, now);
        var valid = new TrafficMessage("snapshot", UdpFlows: [flow], Revision: 1, Mode: MonitoringMode.Udp);
        Validate(valid);
        Check(true, "zero-length datagram is valid");
        Reject(() => Validate(valid with { UdpFlows = [flow with { RemotePort = 65536 }] }), "invalid port");
        Reject(() => Validate(valid with { UdpFlows = [flow with { RemoteAddress = "::1" }] }), "mixed families");
        Reject(() => Validate(valid with { UdpFlows = [flow with { Packets = -1 }] }), "negative packet count");
        Reject(() => Validate(valid with { UdpFlows = [flow with { FirstSeen = now.AddSeconds(1) }] }), "observation timestamp order");
        Reject(() => Validate(valid with { DroppedEvents = -1 }), "negative dropped-event count");
        Reject(() => Validate(valid with { UdpFlows = Enumerable.Repeat(flow, TrafficWireProtocol.MaxEntries + 1).ToArray() }), "too many flow rows");
        var largest = JsonSerializer.Serialize(valid with { UdpFlows = Enumerable.Repeat(flow, TrafficWireProtocol.MaxEntries).ToArray() }, TrafficWireProtocol.JsonOptions);
        Check(largest.Length < TrafficWireProtocol.MaxLineChars, "maximum legitimate snapshot fits wire bound");
        Reject(() => Validate(valid with { Mode = null }), "missing monitoring mode");
        Reject(() => Validate(valid with { Mode = MonitoringMode.Tcp }), "wrong monitoring mode");
        var peer = new TcpPeerInfo("198.51.100.1", 10, 20, now, now);
        var tcp = new TrafficMessage("snapshot", TcpPeers: [peer], Revision: 1, Mode: MonitoringMode.Tcp);
        TrafficWireProtocol.ValidateSnapshot(tcp, MonitoringMode.Tcp);
        Check(true, "TCP bidirectional peer snapshot");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp, MonitoringMode.Udp), "TCP snapshot for UDP client");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp with { UdpFlows = [flow] }, MonitoringMode.Tcp), "mixed protocol payload");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp with { TcpPeers = null }, MonitoringMode.Tcp), "missing peer array");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp with { TcpPeers = [peer with { SentBytes = -1 }] }, MonitoringMode.Tcp), "negative sent count");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp with { TcpPeers = [peer with { ReceivedBytes = -1 }] }, MonitoringMode.Tcp), "negative received count");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp with { TcpPeers = [peer with { RemoteAddress = "host.example" }] }, MonitoringMode.Tcp), "peer hostname is not an address");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp with { TcpPeers = [peer with { FirstSeen = now.AddSeconds(1) }] }, MonitoringMode.Tcp), "TCP observation time order");
        Reject(() => TrafficWireProtocol.ValidateSnapshot(tcp with { TcpPeers = Enumerable.Repeat(peer, TrafficWireProtocol.MaxEntries + 1).ToArray() }, MonitoringMode.Tcp), "oversize peer array");
        Check(JsonSerializer.Serialize(tcp with { TcpPeers = Enumerable.Repeat(peer, TrafficWireProtocol.MaxEntries).ToArray() }, TrafficWireProtocol.JsonOptions).Length
            < TrafficWireProtocol.MaxLineChars, "maximum TCP peer snapshot fits wire bound");

        var args = new[] { "--traffic-helper", "udp", pipeName, nonce, "1234", DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), "S-1-5-21-1-2-3-1001" };
        Check(TrafficMonitorHelper.TryParseArguments(args, out _), "valid helper arguments");
        Check(TrafficMonitorHelper.TryParseArguments(args.Select((argument, index) => index == 1 ? "tcp" : argument).ToArray(), out var tcpArguments)
            && tcpArguments.Mode == MonitoringMode.Tcp, "valid TCP helper mode");
        Check(!TrafficMonitorHelper.TryParseArguments(args.Select((argument, index) => index == 1 ? "exec" : argument).ToArray(), out _), "helper mode whitelist");
        Check(!TrafficMonitorHelper.TryParseArguments(args.Concat(["arbitrary.exe"]).ToArray(), out _), "no extra executable/path argument");
        Check(!TrafficMonitorHelper.TryParseArguments(args.Select((argument, index) => index == 6 ? "Everyone" : argument).ToArray(), out _), "SID syntax");
        Check(TrafficMonitorClient.FriendlyError(new System.ComponentModel.Win32Exception(1223)).Contains("キャンセル"), "UAC cancellation explanation");
        Check(TrafficMonitorClient.SafeError(new string('a', 600) + "\n").Length == 512, "error display bound");

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Type\":\"stop\"}\n{\"Type\":\"hello\"}\n")))
        using (var channel = new TrafficChannel(stream))
        {
            Check(channel.ReadAsync(default).GetAwaiter().GetResult()?.Type == "stop"
                && channel.ReadAsync(default).GetAwaiter().GetResult()?.Type == "hello"
                && channel.ReadAsync(default).GetAwaiter().GetResult() is null, "multiple framed messages and clean EOF");
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', TrafficWireProtocol.MaxLineChars + 1))))
        using (var channel = new TrafficChannel(stream))
            Reject(() => channel.ReadAsync(default).GetAwaiter().GetResult(), "oversize unterminated message");
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Type\":\"stop\"}")))
        using (var channel = new TrafficChannel(stream))
            Reject(() => channel.ReadAsync(default).GetAwaiter().GetResult(), "truncated message");

        // A local, unelevated pipe validates the explicit ACL and OS peer PID checks; no ETW is started.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var process = Process.GetCurrentProcess())
        {
            var name = "SaruMon-Monitor-" + Guid.NewGuid().ToString("N");
            using var namedServer = TrafficProcessSecurity.CreateServer(name);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connected = namedServer.WaitForConnectionAsync(timeout.Token);
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            connected.GetAwaiter().GetResult();
            TrafficProcessSecurity.VerifyClient(namedServer, process.Id);
            TrafficProcessSecurity.VerifyServer(client, process, process.StartTime.ToUniversalTime().Ticks);
            Check(true, "explicit current-user ACL and bidirectional peer PID validation");
            Reject(() => TrafficProcessSecurity.VerifyClient(namedServer, int.MaxValue), "unexpected helper PID");
        }
        return count;
    }
}
