using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using SaruMon.Core;

namespace SaruMon.Udp;

internal sealed class UdpMonitorClient : IAsyncDisposable
{
    private const string StopPendingMessage = "UDP 監視の補助プロセスの終了を確認できませんでした。停止を再試行してください。";
    private readonly SemaphoreSlim gate = new(1);
    private readonly object snapshotGate = new();
    private NamedPipeServerStream? pipe;
    private UdpChannel? channel;
    private CancellationTokenSource? shutdown;
    private Process? helper;
    private Task? readerTask;
    private IReadOnlyList<UdpFlowInfo> snapshot = Array.Empty<UdpFlowInfo>();
    private string? error;
    private bool running;
    private bool disposed;
    private long revision;
    private long droppedEvents;
    private string? beforeStopFailure;

    public bool IsRunning => Volatile.Read(ref running);
    public IReadOnlyList<UdpFlowInfo> Snapshot => Volatile.Read(ref snapshot);
    public string? Error => Volatile.Read(ref error);
    public long DroppedEvents => Interlocked.Read(ref droppedEvents);

    public async Task StartAsync(IReadOnlyCollection<IPAddress> addresses, CancellationToken token)
    {
        var normalized = UdpProtocol.ParseAddresses(addresses.Select(address => address.ToString()).ToArray());
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running) return;
            await StopCoreAsync().ConfigureAwait(false);
            Volatile.Write(ref error, null);
            Volatile.Write(ref snapshot, Array.Empty<UdpFlowInfo>());
            Interlocked.Exchange(ref droppedEvents, 0);
            var pipeName = "SaruMon-UDP-" + Guid.NewGuid().ToString("N");
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            pipe = UdpProcessSecurity.CreateServer(pipeName);
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
            foreach (var argument in new[] { "--udp-helper", pipeName, nonce,
                parent.Id.ToString(CultureInfo.InvariantCulture),
                parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), UdpProcessSecurity.CurrentSid })
                start.ArgumentList.Add(argument);
            // ShellExecute can wait for UAC. Keep the UI thread responsive while the user responds.
            helper = await Task.Run(() => Process.Start(start), CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException("UDP 監視の補助プロセスを起動できませんでした。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            UdpProcessSecurity.VerifyClient(pipe, helper.Id);
            channel = new UdpChannel(pipe);
            var hello = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (hello?.Type != "hello" || hello.Nonce != nonce) throw new InvalidDataException("UDP 監視の認証に失敗しました。");
            long nextRevision;
            lock (snapshotGate) nextRevision = ++revision;
            await channel.WriteAsync(new UdpMessage("configure", nonce,
                normalized.Select(address => address.ToString()).ToArray(), Revision: nextRevision), timeout.Token).ConfigureAwait(false);
            var ready = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (ready?.Type == "error") throw new InvalidOperationException(SafeError(ready.Error));
            if (ready?.Type != "ready" || ready.Revision != nextRevision) throw new InvalidDataException("UDP 監視を開始できませんでした。");
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
        var normalized = UdpProtocol.ParseAddresses(addresses.Select(address => address.ToString()).ToArray());
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long nextRevision;
            lock (snapshotGate)
            {
                nextRevision = ++revision;
                Volatile.Write(ref snapshot, Array.Empty<UdpFlowInfo>());
                Interlocked.Exchange(ref droppedEvents, 0);
            }
            if (!running || channel is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await channel.WriteAsync(new UdpMessage("configure", Addresses:
                normalized.Select(address => address.ToString()).ToArray(), Revision: nextRevision), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref error, FriendlyError(exception));
            await StopCoreAsync().ConfigureAwait(false);
            throw new InvalidOperationException(Error, exception);
        }
        finally { gate.Release(); }
    }

    private async Task ReadSnapshotsAsync(UdpChannel activeChannel, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var message = await activeChannel.ReadAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("UDP 監視の補助プロセスとの接続が終了しました。");
                if (message.Type == "error") throw new InvalidOperationException(SafeError(message.Error));
                UdpProtocol.ValidateSnapshot(message);
                lock (snapshotGate)
                {
                    if (message.Revision != revision) continue;
                    Volatile.Write(ref snapshot, Array.AsReadOnly(message.Flows!));
                    Interlocked.Exchange(ref droppedEvents, message.DroppedEvents);
                    Volatile.Write(ref error, message.DroppedEvents > 0 || message.CaptureLoss
                        ? "記録上限またはイベント欠落のため、一部の通信を記録できませんでした。表示・保存は観測できた範囲です。" : null);
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
            try { await channel.WriteAsync(new UdpMessage("stop"), timeout.Token).ConfigureAwait(false); }
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
                    Volatile.Write(ref error, "UDP 監視の補助プロセスが正常に終了しませんでした。再開できない場合は Windows を再起動してください。");
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

    internal static string SafeError(string? message) => string.IsNullOrWhiteSpace(message) ? "UDP 監視に失敗しました。"
        : new string(message.Take(512).Where(character => !char.IsControl(character)).ToArray());

    internal static string FriendlyError(Exception exception) => exception switch
    {
        Win32Exception { NativeErrorCode: 1223 } => "UDP 監視の管理者承認がキャンセルされました。",
        UnauthorizedAccessException => "UDP 監視を開始できません。現在の Windows ユーザーで管理者承認が必要です。",
        OperationCanceledException => "UDP 監視の応答を待機できませんでした。監視を停止しました。",
        _ => SafeError(exception.Message)
    };
}
