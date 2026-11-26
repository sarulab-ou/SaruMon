using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using SaruMon.Core;
using SaruMon.Udp;

namespace SaruMon.Tests;

internal static class UdpProtocolTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("UDP protocol: " + description);
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
            throw new InvalidOperationException("UDP protocol accepted: " + description);
        }
        var nonce = new string('a', 64);
        var pipeName = "SaruMon-UDP-" + new string('b', 32);
        Check(UdpProtocol.IsNonce(nonce) && !UdpProtocol.IsNonce(nonce + "0") && !UdpProtocol.IsNonce(new string('g', 64)), "nonce length/alphabet");
        Check(UdpProtocol.IsPipeName(pipeName) && !UdpProtocol.IsPipeName("../" + pipeName), "pipe name whitelist");
        Check(UdpProtocol.ParseAddresses(["127.0.0.1", "::1", "127.0.0.1"]).Length == 2, "bounded IPv4/IPv6 addresses");
        Reject(() => UdpProtocol.ParseAddresses(["0.0.0.0"]), "unspecified address");
        Reject(() => UdpProtocol.ParseAddresses(["example.com"]), "hostname instead of literal address");
        Reject(() => UdpProtocol.ParseAddresses(Enumerable.Repeat("127.0.0.1", UdpProtocol.MaxAddresses + 1).ToArray()), "too many addresses");
        Reject(() => UdpProtocol.Deserialize("{\"Type\":\"stop\",\"Command\":\"ignored\"}"), "unknown command property");
        Reject(() => UdpProtocol.Deserialize("null"), "null message");

        var now = DateTimeOffset.UtcNow;
        var flow = new UdpFlowInfo("127.0.0.1", 1234, "127.0.0.1", 5678, 1, 0, now, now);
        var valid = new UdpMessage("snapshot", Flows: [flow], Revision: 1);
        UdpProtocol.ValidateSnapshot(valid);
        Check(true, "zero-length datagram is valid");
        Reject(() => UdpProtocol.ValidateSnapshot(valid with { Flows = [flow with { RemotePort = 65536 }] }), "invalid port");
        Reject(() => UdpProtocol.ValidateSnapshot(valid with { Flows = [flow with { RemoteAddress = "::1" }] }), "mixed families");
        Reject(() => UdpProtocol.ValidateSnapshot(valid with { Flows = [flow with { Packets = -1 }] }), "negative packet count");
        Reject(() => UdpProtocol.ValidateSnapshot(valid with { Flows = [flow with { FirstSeen = now.AddSeconds(1) }] }), "observation timestamp order");
        Reject(() => UdpProtocol.ValidateSnapshot(valid with { DroppedEvents = -1 }), "negative dropped-event count");
        Reject(() => UdpProtocol.ValidateSnapshot(valid with { Flows = Enumerable.Repeat(flow, UdpProtocol.MaxFlows + 1).ToArray() }), "too many flow rows");
        var largest = JsonSerializer.Serialize(valid with { Flows = Enumerable.Repeat(flow, UdpProtocol.MaxFlows).ToArray() }, UdpProtocol.JsonOptions);
        Check(largest.Length < UdpProtocol.MaxLineChars, "maximum legitimate snapshot fits wire bound");

        var args = new[] { "--udp-helper", pipeName, nonce, "1234", DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), "S-1-5-21-1-2-3-1001" };
        Check(UdpMonitorHelper.TryParseArguments(args, out _), "valid helper arguments");
        Check(!UdpMonitorHelper.TryParseArguments(args.Concat(["arbitrary.exe"]).ToArray(), out _), "no extra executable/path argument");
        Check(!UdpMonitorHelper.TryParseArguments(args.Select((argument, index) => index == 5 ? "Everyone" : argument).ToArray(), out _), "SID syntax");
        Check(UdpMonitorClient.FriendlyError(new System.ComponentModel.Win32Exception(1223)).Contains("キャンセル"), "UAC cancellation explanation");
        Check(UdpMonitorClient.SafeError(new string('a', 600) + "\n").Length == 512, "error display bound");

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Type\":\"stop\"}\n{\"Type\":\"hello\"}\n")))
        using (var channel = new UdpChannel(stream))
        {
            Check(channel.ReadAsync(default).GetAwaiter().GetResult()?.Type == "stop"
                && channel.ReadAsync(default).GetAwaiter().GetResult()?.Type == "hello"
                && channel.ReadAsync(default).GetAwaiter().GetResult() is null, "multiple framed messages and clean EOF");
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', UdpProtocol.MaxLineChars + 1))))
        using (var channel = new UdpChannel(stream))
            Reject(() => channel.ReadAsync(default).GetAwaiter().GetResult(), "oversize unterminated message");
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Type\":\"stop\"}")))
        using (var channel = new UdpChannel(stream))
            Reject(() => channel.ReadAsync(default).GetAwaiter().GetResult(), "truncated message");

        // A local, unelevated pipe validates the explicit ACL and OS peer PID checks; no ETW is started.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var process = Process.GetCurrentProcess())
        {
            var name = "SaruMon-UDP-" + Guid.NewGuid().ToString("N");
            using var namedServer = UdpProcessSecurity.CreateServer(name);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connected = namedServer.WaitForConnectionAsync(timeout.Token);
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            connected.GetAwaiter().GetResult();
            UdpProcessSecurity.VerifyClient(namedServer, process.Id);
            UdpProcessSecurity.VerifyServer(client, process, process.StartTime.ToUniversalTime().Ticks);
            Check(true, "explicit current-user ACL and bidirectional peer PID validation");
            Reject(() => UdpProcessSecurity.VerifyClient(namedServer, int.MaxValue), "unexpected helper PID");
        }
        return count;
    }
}
