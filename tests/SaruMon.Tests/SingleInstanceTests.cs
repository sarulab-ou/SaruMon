using System.Diagnostics;

namespace SaruMon.Tests;

internal static class SingleInstanceTests
{
    public static int Run()
    {
        var key = Guid.NewGuid().ToString("N");
        using var host = Start("--ipc-host", key);
        try
        {
            var ready = host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (ready != "READY") throw new Exception("IPC host failed to start: " + ready);
            using var client = Start("--ipc-client", key);
            if (!client.WaitForExit(10000) || client.ExitCode != 0) throw new Exception("Second instance did not activate primary.");
            var result = host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (result != "ACTIVATED") throw new Exception("Primary did not receive activation.");
            host.StandardInput.WriteLine("EXIT");
            if (!host.WaitForExit(5000) || host.ExitCode != 0) throw new Exception("IPC host did not exit cleanly.");
            using var replacement = new SingleInstance(key);
            if (!replacement.IsPrimary) throw new Exception("Mutex was not released after primary exit.");
            return 2;
        }
        finally { if (!host.HasExited) host.Kill(); }
    }

    private static Process Start(string mode, string key)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(key);
        return Process.Start(start) ?? throw new Exception("Could not start IPC test process.");
    }
}
