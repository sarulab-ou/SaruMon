using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using SaruMon.Core;

namespace SaruMon.Monitoring;

internal static class TrafficMonitorHelper
{
    internal static async Task<int> RunAsync(string[] args)
    {
        // No file paths or commands are accepted across this privilege boundary.
        if (!TryParseArguments(args, out var arguments)) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || identity.User?.Value != arguments.UserSid) return 3;

        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", arguments.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        TrafficChannel? channel = null;
        TrafficEtwSession? session = null;
        ETWTraceEventSource? source = null;
        Task? consumer = null;
        Task? commands = null;
        Task? publisher = null;
        Task? parentExit = null;
        var exitCode = 0;
        UdpFlowTracker? udpTracker = null;
        TcpPeerTracker? tcpTracker = null;
        InvestigationCapture? investigation = null;
        NetworkAddressChangedEventHandler? hostAddressesChanged = null;
        var trafficClock = new Stopwatch();
        var stateGate = new object();
        long revision = 0;
        (uint Events, uint Buffers) logLossBaseline = default;
        (uint Events, uint Buffers) lastInvestigationLoss = default;
        try
        {
            using var parent = Process.GetProcessById(arguments.ParentId);
            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(handshake.Token).ConfigureAwait(false);
            TrafficProcessSecurity.VerifyServer(pipe, parent, arguments.ParentStartTicks);
            channel = new TrafficChannel(pipe);
            await channel.WriteAsync(new TrafficMessage("hello", arguments.Nonce, Mode: arguments.Mode), handshake.Token).ConfigureAwait(false);
            var initial = await channel.ReadAsync(handshake.Token).ConfigureAwait(false);
            if (initial?.Type != "configure" || initial.Nonce != arguments.Nonce || initial.Revision <= 0 || initial.Mode != arguments.Mode)
                throw new InvalidDataException("通信監視の初期設定が不正です。");
            var addresses = TrafficWireProtocol.ParseAddresses(initial.Addresses);
            if (arguments.Mode == MonitoringMode.Udp) udpTracker = new UdpFlowTracker(maxFlows: TrafficWireProtocol.MaxEntries);
            else if (arguments.Mode == MonitoringMode.Investigation) investigation = new InvestigationCapture(addresses);
            else
            {
                tcpTracker = new TcpPeerTracker(maxPeers: TrafficWireProtocol.MaxEntries);
                RefreshHostAddresses();
                hostAddressesChanged = (_, _) => RefreshHostAddresses();
                NetworkChange.NetworkAddressChanged += hostAddressesChanged;
            }
            SetAddresses(addresses);
            revision = initial.Revision;
            // Descriptive and stable per user/logon session: never fill the system logger slots with random sessions.
            var sessionName = $"SaruMon.Traffic.{arguments.Mode}.{arguments.UserSid}.{parent.SessionId}";
            session = new TrafficEtwSession(sessionName, includeProcesses: investigation is not null);
            source = new ETWTraceEventSource(sessionName, TraceEventSourceType.Session);
            investigation?.Attach(source);
            if (udpTracker is not null)
            {
                source.Kernel.UdpIpSend += data => udpTracker.Record(data.saddr, data.sport, data.daddr, data.dport, data.size, CaptureTimestamp(data));
                source.Kernel.UdpIpSendIPV6 += data => udpTracker.Record(data.saddr, data.sport, data.daddr, data.dport, data.size, CaptureTimestamp(data));
            }
            if (tcpTracker is not null)
            {
                source.Kernel.TcpIpSend += data => tcpTracker.Record(true, data.saddr, data.daddr, data.size, CaptureTimestamp(data));
                source.Kernel.TcpIpSendIPV6 += data => tcpTracker.Record(true, data.saddr, data.daddr, data.size, CaptureTimestamp(data));
                // Kernel TCP events use socket orientation in BOTH directions:
                // saddr is local, daddr is remote, including Recv. See PerfView's
                // kerneltraceeventparser.mof TcpIp_TypeGroup1/3 descriptions.
                source.Kernel.TcpIpRecv += data => tcpTracker.Record(false, data.saddr, data.daddr, data.size, CaptureTimestamp(data));
                source.Kernel.TcpIpRecvIPV6 += data => tcpTracker.Record(false, data.saddr, data.daddr, data.size, CaptureTimestamp(data));
                // Count observed Connect/Accept events, never data packets or retries.
                // TypeGroup2/4 use the same local/remote socket orientation.
                source.Kernel.TcpIpConnect += data => tcpTracker.RecordConnection(data.saddr, data.daddr, CaptureTimestamp(data));
                source.Kernel.TcpIpConnectIPV6 += data => tcpTracker.RecordConnection(data.saddr, data.daddr, CaptureTimestamp(data));
                source.Kernel.TcpIpAccept += data => tcpTracker.RecordConnection(data.saddr, data.daddr, CaptureTimestamp(data));
                source.Kernel.TcpIpAcceptIPV6 += data => tcpTracker.RecordConnection(data.saddr, data.daddr, CaptureTimestamp(data));
            }
            consumer = Task.Run(() => source.Process(), CancellationToken.None);
            await channel.WriteAsync(new TrafficMessage("ready", Revision: revision, Mode: arguments.Mode), handshake.Token).ConfigureAwait(false);

            commands = ReadCommandsAsync();
            publisher = PublishAsync();
            parentExit = parent.WaitForExitAsync(lifetime.Token);
            var completed = await Task.WhenAny(commands, publisher, parentExit, consumer).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (completed == consumer && !lifetime.IsCancellationRequested)
                throw new IOException("通信イベントの取得が終了しました。");

            async Task ReadCommandsAsync()
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var message = await channel.ReadAsync(lifetime.Token).ConfigureAwait(false);
                    if (message is null) return;
                    if (message.Mode != arguments.Mode) throw new InvalidDataException("監視対象の種類が一致しません。");
                    if (message.Type == "stop") return;
                    if (investigation is not null) throw new InvalidDataException("調査中の対象は固定されています。");
                    lock (stateGate)
                    {
                        if (message.Revision <= revision) throw new InvalidDataException("監視設定の順序が不正です。");
                        if (message.Type == "configure")
                        {
                            var updated = TrafficWireProtocol.ParseAddresses(message.Addresses);
                            // Even adapters sharing an address set must start a fresh observation window.
                            SetAddresses(Array.Empty<IPAddress>());
                            SetAddresses(updated);
                        }
                        else if (message.Type == "reset-log")
                        {
                            logLossBaseline = session.Loss;
                            // The ETW session and Stopwatch both use the Windows performance counter.
                            var cutoff = Stopwatch.GetTimestamp();
                            if (tcpTracker is not null) tcpTracker.ResetPeers(cutoff);
                            else udpTracker!.ResetFlows(cutoff);
                        }
                        else throw new InvalidDataException("監視コマンドが不正です。");
                        revision = message.Revision;
                    }
                }
            }

            async Task PublishAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                {
                    TrafficMessage message;
                    lock (stateGate) message = CreateSnapshot();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await WriteSnapshotAsync(message, timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            if (channel is not null && pipe.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await channel.WriteAsync(new TrafficMessage("error", Error: TrafficMonitorClient.FriendlyError(exception)), timeout.Token).ConfigureAwait(false); }
                catch (Exception) { }
            }
            exitCode = 1;
        }
        finally
        {
            if (hostAddressesChanged is not null) NetworkChange.NetworkAddressChanged -= hostAddressesChanged;
            lifetime.Cancel();
            await ObserveAsync(publisher).ConfigureAwait(false);
            // Stop through the owned trace handle only. This also unblocks ProcessTrace.
            try { session?.Stop(); } catch (Exception) { exitCode = 4; }
            if (consumer is not null)
            {
                // Let the consumer drain buffers flushed by Stop before taking the final snapshot.
                try { await consumer.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception) { exitCode = 4; }
            }
            try { source?.Dispose(); } catch (Exception) { exitCode = 4; }
            await ObserveAsync(consumer).ConfigureAwait(false);
            try { session?.Dispose(); } catch (Exception) { exitCode = 4; }
            if ((udpTracker is not null || tcpTracker is not null || investigation is not null)
                && session is not null && channel is not null && pipe.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    TrafficMessage final;
                    lock (stateGate) final = CreateSnapshot(failed: exitCode != 0) with { Final = true };
                    await WriteSnapshotAsync(final, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
            pipe.Dispose();
            await ObserveAsync(commands).ConfigureAwait(false);
            await ObserveAsync(parentExit).ConfigureAwait(false);
            try { channel?.Dispose(); } catch (IOException) { }
        }
        return exitCode;

        TrafficMessage CreateSnapshot(bool failed = false)
        {
            var loss = session!.Loss;
            if (investigation is not null)
            {
                if (loss != lastInvestigationLoss)
                {
                    investigation.ForgetProcessIdentities();
                    lastInvestigationLoss = loss;
                }
                var updates = investigation.TakeUpdates(out var omitted);
                return new TrafficMessage("snapshot", Revision: revision, Mode: arguments.Mode,
                    DroppedEvents: InvestigationCapture.Add(omitted, loss.Events), CaptureLoss: failed || loss.Buffers > 0,
                    InvestigationRows: updates, SampledAt: DateTimeOffset.UtcNow);
            }
            var dropped = udpTracker?.DroppedEvents ?? tcpTracker!.DroppedEvents;
            var lostEvents = unchecked(loss.Events - logLossBaseline.Events);
            var totalDropped = dropped > long.MaxValue - lostEvents ? long.MaxValue : dropped + lostEvents;
            TcpPeerInfo[]? peers = null;
            TcpTrafficReading? traffic = null;
            if (tcpTracker is not null)
            {
                peers = tcpTracker.Snapshot(out var received, out var sent).ToArray();
                traffic = new TcpTrafficReading(received, sent, trafficClock.Elapsed.Ticks, failed || loss.Events > 0 || loss.Buffers > 0);
            }
            return new TrafficMessage("snapshot", UdpFlows: udpTracker?.Snapshot().ToArray(),
                TcpPeers: peers, Revision: revision, Mode: arguments.Mode,
                DroppedEvents: totalDropped, CaptureLoss: failed || loss.Buffers != logLossBaseline.Buffers, TcpTraffic: traffic);
        }

        async Task WriteSnapshotAsync(TrafficMessage message, CancellationToken token)
        {
            if (message.InvestigationRows is not { Length: > TrafficWireProtocol.InvestigationChunkSize } rows)
            {
                await channel!.WriteAsync(message, token).ConfigureAwait(false);
                return;
            }
            var chunk = 0;
            for (var offset = 0; offset < rows.Length; offset += TrafficWireProtocol.InvestigationChunkSize)
            {
                var end = Math.Min(rows.Length, offset + TrafficWireProtocol.InvestigationChunkSize);
                var more = end < rows.Length;
                await channel!.WriteAsync(message with
                {
                    InvestigationRows = rows[offset..end], Chunk = chunk++, More = more, Final = message.Final && !more
                }, token).ConfigureAwait(false);
            }
        }

        void SetAddresses(IReadOnlyCollection<IPAddress> addresses)
        {
            udpTracker?.SetLocalAddresses(addresses);
            tcpTracker?.SetLocalAddresses(addresses);
            if (tcpTracker is not null) trafficClock.Restart();
        }

        void RefreshHostAddresses()
        {
            try
            {
                var hostAddresses = new List<IPAddress>();
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try { hostAddresses.AddRange(adapter.GetIPProperties().UnicastAddresses.Select(entry => entry.Address)); }
                    catch (NetworkInformationException) { /* The adapter may have disappeared. */ }
                }
                tcpTracker!.SetHostAddresses(hostAddresses);
            }
            catch (NetworkInformationException) { /* Retain the last available host address set. */ }
        }
    }

    internal static long CaptureTimestamp(TraceEvent data)
    {
        // The reset boundary is a raw QPC value. A session-relative time cannot be compared to it.
#pragma warning disable CS0618
        return data.TimeStampQPC;
#pragma warning restore CS0618
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch (Exception) { }
    }

    internal static bool TryParseArguments(string[] args, out HelperArguments arguments)
    {
        arguments = default;
        if (args.Length != 7 || args[0] != "--traffic-helper" || args[1] is not ("udp" or "tcp" or "investigation")
            || !TrafficWireProtocol.IsPipeName(args[2]) || !TrafficWireProtocol.IsNonce(args[3])
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0
            || !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks) || startTicks <= 0
            || startTicks > DateTime.MaxValue.Ticks || args[6].Length is < 5 or > 184)
            return false;
        try { _ = new SecurityIdentifier(args[6]); }
        catch (ArgumentException) { return false; }
        var mode = args[1] == "udp" ? MonitoringMode.Udp : args[1] == "tcp" ? MonitoringMode.Tcp : MonitoringMode.Investigation;
        arguments = new HelperArguments(mode,
            args[2], args[3], parentId, startTicks, args[6]);
        return true;
    }

    internal readonly record struct HelperArguments(MonitoringMode Mode, string PipeName, string Nonce,
        int ParentId, long ParentStartTicks, string UserSid);
}
