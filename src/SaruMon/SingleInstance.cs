using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SaruMon;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource shutdown = new();
    private Task? listener;
    public bool IsPrimary { get; }

    public SingleInstance(string suffix = "")
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + ":" + Process.GetCurrentProcess().SessionId + suffix)))[..24];
        pipeName = "SaruMon-" + key;
        mutex = new Mutex(true, @"Local\" + pipeName, out var created);
        if (created) IsPrimary = true;
        else
        {
            try { IsPrimary = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsPrimary = true; }
        }
    }

    public void StartListening(Action activate)
    {
        if (!IsPrimary || listener != null) throw new InvalidOperationException();
        listener = Task.Run(async () =>
        {
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(shutdown.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(Environment.ProcessId.ToString(CultureInfo.InvariantCulture).AsMemory(), timeout.Token);
                    var command = await reader.ReadLineAsync(timeout.Token);
                    if (command == "SHOW")
                    {
                        activate();
                        await writer.WriteLineAsync("OK".AsMemory(), timeout.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
            }
        });
    }

    public async Task<bool> ActivateExistingAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            if (!int.TryParse(await reader.ReadLineAsync(timeout.Token), out var ownerPid)) return false;
            AllowSetForegroundWindow(ownerPid);
            await writer.WriteLineAsync("SHOW".AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token) == "OK";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        try { listener?.GetAwaiter().GetResult(); }
        finally
        {
            shutdown.Dispose();
            if (IsPrimary) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
