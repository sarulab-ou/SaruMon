using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SaruMon.Monitoring;

internal readonly record struct ProcessIdentity(int Pid, long StartedUtcTicks)
{
    internal bool CanTarget => Pid > 4 && Pid != Environment.ProcessId && StartedUtcTicks > 0;
}

internal sealed record ProcessTarget(ProcessIdentity Identity, string Name)
{
    internal string Label => $"{Name} (PID {Identity.Pid})";
}

internal sealed record ProcessSample(ProcessTarget Target, int ParentPid, int Threads, long? CpuTicks, long? WorkingSet);

internal static class WindowsProcesses
{
    private const uint QueryLimited = 0x1000;
    private const uint Terminate = 0x0001;
    internal static int LogicalProcessors { get; } = Math.Max(1, (int)GetActiveProcessorCount(0xffff));

    internal static ProcessSample[] Snapshot()
    {
        const int noMoreFiles = 18;
        var sampledAt = DateTime.UtcNow.Ticks;
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var rows = new List<ProcessSample>();
        var hasEntry = Process32First(snapshot, ref entry);
        while (hasEntry)
        {
            var info = ReadSample(entry.Pid, sampledAt);
            rows.Add(new ProcessSample(new ProcessTarget(new ProcessIdentity(entry.Pid, info.Started), entry.Name),
                entry.ParentPid, entry.Threads, info.CpuTicks, info.WorkingSet));
            hasEntry = Process32Next(snapshot, ref entry);
        }
        var error = Marshal.GetLastWin32Error();
        if (error != noMoreFiles) throw new Win32Exception(error);
        return rows.ToArray();
    }

    private static (long Started, long? CpuTicks, long? WorkingSet) ReadSample(int pid, long sampledAt)
    {
        if (pid <= 0) return default;
        using var process = OpenProcess(QueryLimited, false, pid);
        if (!TryGetTimes(process, out var started, out var cpu) || started > sampledAt) return default;

        var counters = new ProcessMemory { Size = (uint)Marshal.SizeOf<ProcessMemory>() };
        long? workingSet = GetProcessMemoryInfo(process, ref counters, counters.Size) ? (long)counters.WorkingSet : null;
        return (started, cpu, workingSet);
    }

    internal static (long Started, string? Path) ReadImage(int pid, long latestStart, int maxPathLength)
    {
        if (pid <= 0) return default;
        using var process = OpenProcess(QueryLimited, false, pid);
        if (!TryGetTimes(process, out var started, out _) || started > latestStart) return default;
        return (started, ReadPath(process, maxPathLength));
    }

    internal static string? ReadPath(ProcessIdentity identity)
    {
        if (identity.StartedUtcTicks <= 0) return null;
        using var process = OpenProcess(QueryLimited, false, identity.Pid);
        return TryGetTimes(process, out var started, out _) && started == identity.StartedUtcTicks
            ? ReadPath(process, 32767) : null;
    }

    private static string? ReadPath(SafeProcessHandle process, int maxLength)
    {
        var size = maxLength + 1;
        var path = new StringBuilder(size);
        return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
    }

    private static bool TryGetTimes(SafeProcessHandle process, out long started, out long cpu)
    {
        started = cpu = 0;
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out var kernel, out var user)) return false;
        started = DateTime.FromFileTimeUtc(created).Ticks;
        cpu = kernel + user;
        return true;
    }

    internal static void RequestExit(ProcessIdentity identity, bool force)
    {
        if (!identity.CanTarget) throw new InvalidOperationException("終了対象のプロセスを確認できません。");

        // Validate creation time and terminate using the same handle, even if the PID is recycled.
        using var handle = OpenProcess(QueryLimited | (force ? Terminate : 0), false, identity.Pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!TryGetTimes(handle, out var started, out _) || started != identity.StartedUtcTicks)
            throw new InvalidOperationException("取得時と同じプロセスを確認できませんでした。");
        if (!IsProcessCritical(handle, out var critical))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (critical) throw new InvalidOperationException("Windows の重要なシステムプロセスです。");

        if (force)
        {
            if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }

        using var process = Process.GetProcessById(identity.Pid);
        if (process.StartTime.ToUniversalTime().Ticks != started)
            throw new InvalidOperationException("取得時と同じプロセスを確認できませんでした。");
        if (!process.CloseMainWindow())
            throw new InvalidOperationException("通常の終了要求を送れるウィンドウがありません。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public int Pid;
        public nuint DefaultHeap;
        public uint ModuleId;
        public int Threads;
        public int ParentPid;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemory
    {
        public uint Size;
        public uint PageFaults;
        public nuint PeakWorkingSet;
        public nuint WorkingSet;
        public nuint PeakPagedPool;
        public nuint PagedPool;
        public nuint PeakNonPagedPool;
        public nuint NonPagedPool;
        public nuint Pagefile;
        public nuint PeakPagefile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemory counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
