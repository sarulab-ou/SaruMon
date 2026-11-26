using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using SaruMon.Core;

namespace SaruMon.Monitoring;

internal sealed class TrafficMonitorClient : IAsyncDisposable
{
    private const string StopPendingMessage = "通信監視の補助プロセスの終了を確認できませんでした。停止を再試行してください。";
    private readonly SemaphoreSlim gate = new(1);
    private readonly object snapshotGate = new();
    private readonly MonitoringMode mode;
    private NamedPipeServerStream? pipe;
    private TrafficChannel? channel;
    private CancellationTokenSource? shutdown;
    private Process? helper;
    private Task? readerTask;
    private IReadOnlyList<UdpFlowInfo> udpSnapshot = Array.Empty<UdpFlowInfo>();
    private IReadOnlyList<TcpPeerInfo> tcpSnapshot = Array.Empty<TcpPeerInfo>();
    private TcpTrafficSnapshot? tcpTraffic;
    private string? error;
    private bool running;
    private bool disposed;
    private long revision;
    private long tcpTrafficRevision;
    private TaskCompletionSource? peerResetCompletion;
    private long droppedEvents;
    private string? beforeStopFailure;

    public TrafficMonitorClient(MonitoringMode mode)
    {
        if (!TrafficWireProtocol.IsMode(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        this.mode = mode;
    }

    public bool IsRunning => Volatile.Read(ref running);
    public IReadOnlyList<UdpFlowInfo> UdpSnapshot => Volatile.Read(ref udpSnapshot);
    public IReadOnlyList<TcpPeerInfo> TcpSnapshot => Volatile.Read(ref tcpSnapshot);
    public TcpTrafficSnapshot? TcpTraffic => Volatile.Read(ref tcpTraffic);
    public string? Error => Volatile.Read(ref error);
    public long DroppedEvents => Interlocked.Read(ref droppedEvents);

    public async Task StartAsync(IReadOnlyCollection<IPAddress> addresses, CancellationToken token)
    {
        var normalized = TrafficWireProtocol.ParseAddresses(addresses.Select(address => address.ToString()).ToArray());
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running) return;
            await StopCoreAsync().ConfigureAwait(false);
            Volatile.Write(ref error, null);
            ClearSnapshots();
            Interlocked.Exchange(ref droppedEvents, 0);
            var pipeName = "SaruMon-Monitor-" + Guid.NewGuid().ToString("N");
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            pipe = TrafficProcessSecurity.CreateServer(pipeName);
            shutdown = new CancellationTokenSource();
            using var parent = Process.GetCurrentProcess();
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所が不明です。");
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(path)!
            };
            foreach (var argument in new[] { "--traffic-helper", mode == MonitoringMode.Udp ? "udp" : "tcp", pipeName, nonce,
                parent.Id.ToString(CultureInfo.InvariantCulture),
                parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), TrafficProcessSecurity.CurrentSid })
                start.ArgumentList.Add(argument);
            // ShellExecute can wait for UAC. Keep the UI thread responsive while the user responds.
            helper = await Task.Run(() => Process.Start(start), CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException("通信監視の補助プロセスを起動できませんでした。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            TrafficProcessSecurity.VerifyClient(pipe, helper.Id);
            channel = new TrafficChannel(pipe);
            var hello = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (hello?.Type != "hello" || hello.Nonce != nonce || hello.Mode != mode) throw new InvalidDataException("通信監視の認証に失敗しました。");
            long nextRevision;
            lock (snapshotGate) nextRevision = tcpTrafficRevision = ++revision;
            await channel.WriteAsync(new TrafficMessage("configure", nonce,
                normalized.Select(address => address.ToString()).ToArray(), Revision: nextRevision, Mode: mode), timeout.Token).ConfigureAwait(false);
            var ready = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (ready?.Type == "error") throw new InvalidOperationException(SafeError(ready.Error));
            if (ready?.Type != "ready" || ready.Revision != nextRevision || ready.Mode != mode) throw new InvalidDataException("通信監視を開始できませんでした。");
            Volatile.Write(ref running, true);
            readerTask = ReadSnapshotsAsync(channel, shutdown.Token);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref error, FriendlyError(exception));
            await StopCoreAsync().ConfigureAwait(false);
            throw new InvalidOperationException(Error, exception);
        }
        finally { gate.Release(); }
    }

    public async Task SetLocalAddressesAsync(IReadOnlyCollection<IPAddress> addresses, CancellationToken token)
    {
        var normalized = TrafficWireProtocol.ParseAddresses(addresses.Select(address => address.ToString()).ToArray());
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long nextRevision;
            lock (snapshotGate)
            {
                nextRevision = tcpTrafficRevision = ++revision;
                ClearSnapshots();
                Interlocked.Exchange(ref droppedEvents, 0);
            }
            if (!running || channel is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await channel.WriteAsync(new TrafficMessage("configure", Addresses:
                normalized.Select(address => address.ToString()).ToArray(), Revision: nextRevision, Mode: mode), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref error, FriendlyError(exception));
            await StopCoreAsync().ConfigureAwait(false);
            throw new InvalidOperationException(Error, exception);
        }
        finally { gate.Release(); }
    }

    public async Task ResetTcpPeersAsync(CancellationToken token)
    {
        if (mode != MonitoringMode.Tcp) throw new InvalidOperationException("TCP の相手別ログだけをリセットできます。");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var activeChannel = channel;
            TaskCompletionSource? completion;
            long nextRevision;
            lock (snapshotGate)
            {
                nextRevision = ++revision;
                Volatile.Write(ref tcpSnapshot, Array.Empty<TcpPeerInfo>());
                Interlocked.Exchange(ref droppedEvents, 0);
                Volatile.Write(ref error, null);
                // The overview keeps its revision and baseline. Only the log is reset.
                completion = running && activeChannel is not null
                    ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                peerResetCompletion = completion;
            }
            if (completion is null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await activeChannel!.WriteAsync(new TrafficMessage("reset-tcp-peers", Revision: nextRevision, Mode: mode), timeout.Token).ConfigureAwait(false);
            await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (snapshotGate) peerResetCompletion = null;
            gate.Release();
        }
    }

    private async Task ReadSnapshotsAsync(TrafficChannel activeChannel, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var message = await activeChannel.ReadAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("通信監視の補助プロセスとの接続が終了しました。");
                if (message.Type == "error") throw new InvalidOperationException(SafeError(message.Error));
                TrafficWireProtocol.ValidateSnapshot(message, mode);
                lock (snapshotGate)
                {
                    if (message.Revision != revision) continue;
                    if (mode == MonitoringMode.Udp) Volatile.Write(ref udpSnapshot, Array.AsReadOnly(message.UdpFlows!));
                    else
                    {
                        Volatile.Write(ref tcpSnapshot, Array.AsReadOnly(message.TcpPeers!));
                        var reading = message.TcpTraffic!;
                        if (message.CaptureLoss) reading = reading with { Incomplete = true };
                        Volatile.Write(ref tcpTraffic, new TcpTrafficSnapshot(tcpTrafficRevision, reading));
                    }
                    Interlocked.Exchange(ref droppedEvents, message.DroppedEvents);
                    Volatile.Write(ref error, message.DroppedEvents > 0 || message.CaptureLoss
                        ? "記録上限またはイベント欠落のため、一部の通信を記録できませんでした。表示・保存は観測できた範囲です。" : null);
                    peerResetCompletion?.TrySetResult();
                }
                if (message.Final) break;
            }
        }
        catch (Exception exception) when (token.IsCancellationRequested || exception is IOException or InvalidOperationException
            or System.Text.Json.JsonException or UnauthorizedAccessException or OperationCanceledException or System.Text.DecoderFallbackException)
        {
            if (!token.IsCancellationRequested) Volatile.Write(ref error, FriendlyError(exception));
        }
        finally
        {
            Volatile.Write(ref running, false);
            lock (snapshotGate) peerResetCompletion?.TrySetCanceled();
            // Closing the pipe is also the helper's independent stop signal.
            pipe?.Dispose();
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref running, false);
        Exception? stopFailure = null;
        if (channel is not null && pipe?.IsConnected == true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await channel.WriteAsync(new TrafficMessage("stop", Mode: mode), timeout.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        if (readerTask is not null && !readerTask.IsCompleted)
        {
            // A normal stop ends with a final snapshot, after ETW's pending buffers have drained.
            try { await readerTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
            catch (TimeoutException) { Volatile.Write(ref error, "最後の集計を取得できませんでした。直前の記録を保持しています。"); }
            catch (Exception exception) { Volatile.Write(ref error, FriendlyError(exception)); }
        }
        shutdown?.Cancel();
        pipe?.Dispose();
        if (readerTask is not null)
        {
            try { await readerTask.ConfigureAwait(false); }
            catch (Exception exception) { Volatile.Write(ref error, FriendlyError(exception)); }
        }
        if (helper is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (Error == StopPendingMessage)
                {
                    Volatile.Write(ref error, beforeStopFailure);
                    beforeStopFailure = null;
                }
                if (helper.ExitCode != 0 && Error is null)
                    Volatile.Write(ref error, "通信監視の補助プロセスが正常に終了しませんでした。再開できない場合は Windows を再起動してください。");
            }
            catch (OperationCanceledException)
            {
                if (Error != StopPendingMessage) beforeStopFailure = Error;
                Volatile.Write(ref error, StopPendingMessage);
                stopFailure = new InvalidOperationException(StopPendingMessage);
            }
            catch (InvalidOperationException) { }
            if (stopFailure is null) helper.Dispose();
        }
        try { channel?.Dispose(); } catch (IOException) { }
        shutdown?.Dispose();
        channel = null;
        pipe = null;
        shutdown = null;
        if (stopFailure is null) helper = null;
        readerTask = null;
        if (stopFailure is not null) throw stopFailure;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            await StopCoreAsync().ConfigureAwait(false);
            disposed = true;
        }
        finally { gate.Release(); }
    }

    private void ClearSnapshots()
    {
        Volatile.Write(ref udpSnapshot, Array.Empty<UdpFlowInfo>());
        Volatile.Write(ref tcpSnapshot, Array.Empty<TcpPeerInfo>());
        Volatile.Write(ref tcpTraffic, null);
    }

    internal static string SafeError(string? message) => string.IsNullOrWhiteSpace(message) ? "通信監視に失敗しました。"
        : new string(message.Take(512).Where(character => !char.IsControl(character)).ToArray());

    internal static string FriendlyError(Exception exception) => exception switch
    {
        Win32Exception { NativeErrorCode: 1223 } => "通信監視の管理者承認がキャンセルされました。",
        UnauthorizedAccessException => "通信監視を開始できません。現在の Windows ユーザーで管理者承認が必要です。",
        OperationCanceledException => "通信監視の応答を待機できませんでした。監視を停止しました。",
        _ => SafeError(exception.Message)
    };
}
