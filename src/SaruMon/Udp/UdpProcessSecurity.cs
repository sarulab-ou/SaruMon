using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SaruMon.Udp;

internal static class UdpProcessSecurity
{
    internal static string CurrentSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new UnauthorizedAccessException("Windows ユーザーを確認できません。");
        }
    }

    internal static NamedPipeServerStream CreateServer(string name)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(CurrentSid),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // CurrentUserOnly also compares elevation levels, so it cannot be used for this UAC boundary.
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, security);
    }

    internal static void VerifyClient(PipeStream pipe, int expectedProcessId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (processId != expectedProcessId) throw new UnauthorizedAccessException("UDP 監視の補助プロセスの接続元が一致しません。");
    }

    internal static void VerifyServer(PipeStream pipe, Process parent, long expectedStartTicks)
    {
        using var current = Process.GetCurrentProcess();
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (processId != parent.Id || parent.StartTime.ToUniversalTime().Ticks != expectedStartTicks
            || parent.SessionId != current.SessionId || parent.HasExited
            || !string.Equals(Path.GetFullPath(parent.MainModule?.FileName ?? ""),
                Path.GetFullPath(Environment.ProcessPath ?? ""), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("UDP 監視を要求したアプリを確認できません。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
