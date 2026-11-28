using System.Diagnostics;
using SaruMon.Monitoring;

namespace SaruMon.Tests;

internal static class ProcessTests
{
    internal static int Run()
    {
        var count = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Process test failed: " + message);
            count++;
        }

        var parent = new ProcessSample(new ProcessTarget(new ProcessIdentity(100, 20), "Parent.exe"), 10, 1, 0, 0);
        var child = new ProcessSample(new ProcessTarget(new ProcessIdentity(200, 40), "Child.exe"), 100, 2, 6, 1000);
        var previous = new Dictionary<ProcessIdentity, long> { [child.Target.Identity] = 2 };
        Assert(TasksView.CpuPercent(child, previous, 10, 2) == 20, "CPU time normalized to all logical processors");
        var reused = child with { Target = child.Target with { Identity = new ProcessIdentity(200, 60) } };
        Assert(TasksView.CpuPercent(reused, previous, 10, 2) is null, "new process with the same PID needs a new baseline");
        Assert(TasksView.CpuPercent(child with { CpuTicks = 1 }, previous, 10, 2) is null, "counter rollback has no CPU reading");
        var processes = new Dictionary<int, ProcessSample> { [100] = parent };
        Assert(TasksView.ParentName(child, processes) == "Parent.exe", "display the verified parent name");
        processes[100] = parent with { Target = parent.Target with { Identity = new ProcessIdentity(100, 80) } };
        Assert(TasksView.ParentName(child, processes) is null, "a reused parent PID does not supply the name");
        Assert(!new ProcessIdentity(Environment.ProcessId, 1).CanTarget && !new ProcessIdentity(4, 1).CanTarget,
            "SaruMon and system PID are excluded from process termination");

        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--process-test-child");
        using var fixture = Process.Start(start) ?? throw new InvalidOperationException("Could not start process test fixture.");
        try
        {
            var ready = fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Assert(ready == "READY", "only a known test child can be used for termination");
            var row = WindowsProcesses.Snapshot().Single(sample => sample.Target.Identity.Pid == fixture.Id);
            var identity = row.Target.Identity;
            Assert(identity.StartedUtcTicks == fixture.StartTime.ToUniversalTime().Ticks && row.ParentPid == Environment.ProcessId,
                "Windows snapshot records the child's exact identity and parent");
            Assert(row.Threads > 0 && row.CpuTicks.HasValue && row.WorkingSet > 0,
                "Windows snapshot reports threads, CPU time and working set");
            Assert(string.Equals(WindowsProcesses.ReadPath(identity), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                "read the image path of the verified process");
            Assert(Refuses(identity with { StartedUtcTicks = identity.StartedUtcTicks - 1 }, force: true) && !fixture.HasExited,
                "force action refuses a stale identity before calling Windows");
            Assert(Refuses(identity, force: false) && !fixture.HasExited, "ordinary exit does not terminate a process without a window");
            WindowsProcesses.RequestExit(identity, force: true);
            Assert(fixture.WaitForExit(5000) && fixture.ExitCode == 1, "force action terminates only the owned child process");
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.StandardInput.Close();
                if (!fixture.WaitForExit(3000)) fixture.Kill();
            }
        }
        return count;
    }

    private static bool Refuses(ProcessIdentity identity, bool force)
    {
        try { WindowsProcesses.RequestExit(identity, force); }
        catch (InvalidOperationException) { return true; }
        return false;
    }
}
