using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using SaruMon.Core;

namespace SaruMon.Udp;

internal static class UdpMonitorHelper
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
        UdpChannel? channel = null;
        UdpEtwSession? session = null;
        ETWTraceEventSource? source = null;
        Task? consumer = null;
        Task? commands = null;
        Task? publisher = null;
        Task? parentExit = null;
        var exitCode = 0;
        UdpFlowTracker? tracker = null;
        var stateGate = new object();
        long revision = 0;
        try
        {
            using var parent = Process.GetProcessById(arguments.ParentId);
            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(handshake.Token).ConfigureAwait(false);
            UdpProcessSecurity.VerifyServer(pipe, parent, arguments.ParentStartTicks);
            channel = new UdpChannel(pipe);
            await channel.WriteAsync(new UdpMessage("hello", arguments.Nonce), handshake.Token).ConfigureAwait(false);
            var initial = await channel.ReadAsync(handshake.Token).ConfigureAwait(false);
            if (initial?.Type != "configure" || initial.Nonce != arguments.Nonce || initial.Revision <= 0)
                throw new InvalidDataException("UDP 監視の初期設定が不正です。");
            var addresses = UdpProtocol.ParseAddresses(initial.Addresses);
            tracker = new UdpFlowTracker(maxFlows: UdpProtocol.MaxFlows);
            tracker.SetLocalAddresses(addresses);
            revision = initial.Revision;
            // Descriptive and stable per user/logon session: never fill the system logger slots with random sessions.
            var sessionName = $"SaruMon.UDP.{arguments.UserSid}.{parent.SessionId}";
            session = new UdpEtwSession(sessionName);
            source = new ETWTraceEventSource(sessionName, TraceEventSourceType.Session);
            source.Kernel.UdpIpSend += data => tracker.Record(data.saddr, data.sport, data.daddr, data.dport, data.size);
            source.Kernel.UdpIpSendIPV6 += data => tracker.Record(data.saddr, data.sport, data.daddr, data.dport, data.size);
            consumer = Task.Run(() => source.Process(), CancellationToken.None);
            await channel.WriteAsync(new UdpMessage("ready", Revision: revision), handshake.Token).ConfigureAwait(false);

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
                    if (message is null || message.Type == "stop") return;
                    if (message.Type != "configure") throw new InvalidDataException("監視コマンドが不正です。");
                    var updated = UdpProtocol.ParseAddresses(message.Addresses);
                    lock (stateGate)
                    {
                        if (message.Revision <= revision) throw new InvalidDataException("監視設定の順序が不正です。");
                        // Even adapters sharing an address set must start a fresh observation window.
                        tracker.SetLocalAddresses(Array.Empty<IPAddress>());
                        tracker.SetLocalAddresses(updated);
                        revision = message.Revision;
                    }
                }
            }

            async Task PublishAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                {
                    UdpMessage message;
                    lock (stateGate) message = CreateSnapshot();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await channel.WriteAsync(message, timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            if (channel is not null && pipe.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await channel.WriteAsync(new UdpMessage("error", Error: UdpMonitorClient.FriendlyError(exception)), timeout.Token).ConfigureAwait(false); }
                catch (Exception) { }
            }
            exitCode = 1;
        }
        finally
        {
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
            if (tracker is not null && session is not null && channel is not null && pipe.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    UdpMessage final;
                    lock (stateGate) final = CreateSnapshot() with { Final = true, CaptureLoss = exitCode != 0 || session.Loss.Buffers > 0 };
                    await channel.WriteAsync(final, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
            pipe.Dispose();
            await ObserveAsync(commands).ConfigureAwait(false);
            await ObserveAsync(parentExit).ConfigureAwait(false);
            try { channel?.Dispose(); } catch (IOException) { }
        }
        return exitCode;

        UdpMessage CreateSnapshot()
        {
            var loss = session!.Loss;
            var dropped = tracker!.DroppedEvents;
            var totalDropped = dropped > long.MaxValue - loss.Events ? long.MaxValue : dropped + loss.Events;
            return new UdpMessage("snapshot", Flows: tracker.Snapshot().ToArray(), Revision: revision,
                DroppedEvents: totalDropped, CaptureLoss: loss.Buffers > 0);
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch (Exception) { }
    }

    internal static bool TryParseArguments(string[] args, out HelperArguments arguments)
    {
        arguments = default;
        if (args.Length != 6 || args[0] != "--udp-helper" || !UdpProtocol.IsPipeName(args[1])
            || !UdpProtocol.IsNonce(args[2])
            || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0
            || !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks) || startTicks <= 0
            || startTicks > DateTime.MaxValue.Ticks || args[5].Length is < 5 or > 184)
            return false;
        try { _ = new SecurityIdentifier(args[5]); }
        catch (ArgumentException) { return false; }
        arguments = new HelperArguments(args[1], args[2], parentId, startTicks, args[5]);
        return true;
    }

    internal readonly record struct HelperArguments(string PipeName, string Nonce, int ParentId, long ParentStartTicks, string UserSid);
}
