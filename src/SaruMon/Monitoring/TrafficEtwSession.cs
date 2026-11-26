using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SaruMon.Monitoring;

/// <summary>
/// Owns only the handle returned by StartTrace. Never adopts or stops an existing session.
/// TraceEventSession.Stop is deliberately avoided: it also resets the global CPU sampling interval.
/// </summary>
internal sealed class TrafficEtwSession : IDisposable
{
    private readonly object gate = new();
    private readonly Guid sessionGuid = Guid.NewGuid();
    private ulong handle;
    private (uint Events, uint Buffers) lastLoss;
    internal string Name { get; }

    internal TrafficEtwSession(string name)
    {
        Name = name;
        using var properties = new PropertiesBuffer(sessionGuid);
        var status = StartTrace(out var createdHandle, name, properties.Pointer);
        if (status == 183)
            throw new InvalidOperationException("SaruMon の通信監視セッションが既に存在します。別の監視を終了してください。異常終了後の場合は Windows を再起動してからお試しください。");
        if (status == 5) throw new UnauthorizedAccessException("通信監視には管理者の承認が必要です。");
        if (status != 0) throw new Win32Exception((int)status, "通信監視セッションを開始できませんでした。");
        handle = createdHandle;
    }

    internal (uint Events, uint Buffers) Loss
    {
        get
        {
            lock (gate)
            {
                if (handle == 0) return lastLoss;
                using var properties = new PropertiesBuffer(sessionGuid);
                var status = ControlTrace(handle, null, properties.Pointer, 0);
                if (status != 0) throw new Win32Exception((int)status);
                var value = Marshal.PtrToStructure<TraceProperties>(properties.Pointer);
                lastLoss = (value.EventsLost, value.RealTimeBuffersLost);
                return lastLoss;
            }
        }
    }

    internal void Stop()
    {
        lock (gate)
        {
            if (handle == 0) return;
            using var properties = new PropertiesBuffer(sessionGuid);
            var status = ControlTrace(handle, null, properties.Pointer, 1);
            if (status != 0 && status != 4201) throw new Win32Exception((int)status, "通信監視セッションを停止できませんでした。");
            if (status == 0)
            {
                var value = Marshal.PtrToStructure<TraceProperties>(properties.Pointer);
                lastLoss = (value.EventsLost, value.RealTimeBuffersLost);
            }
            handle = 0;
        }
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        internal uint BufferSize, ProviderId;
        internal ulong HistoricalContext;
        internal long TimeStamp;
        internal Guid Guid;
        internal uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        internal WnodeHeader Wnode;
        internal uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer,
            EnableFlags, AgeLimit, NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten,
            LogBuffersLost, RealTimeBuffersLost;
        internal IntPtr LoggerThreadId;
        internal uint LogFileNameOffset, LoggerNameOffset;
    }

    private sealed class PropertiesBuffer : IDisposable
    {
        internal IntPtr Pointer { get; }
        internal PropertiesBuffer(Guid sessionGuid)
        {
            var structureSize = Marshal.SizeOf<TraceProperties>();
            var totalSize = structureSize + 2048;
            Pointer = Marshal.AllocHGlobal(totalSize);
            Marshal.Copy(new byte[totalSize], 0, Pointer, totalSize);
            var properties = new TraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)totalSize, Guid = sessionGuid, ClientContext = 1, Flags = 0x00020000 },
                BufferSize = 64,
                MinimumBuffers = 8,
                MaximumBuffers = 128,
                LogFileMode = 0x02000000 | 0x00000100, // SYSTEM_LOGGER_MODE | REAL_TIME_MODE
                FlushTimer = 1,
                EnableFlags = 0x00010000 | 0x10000000, // NETWORK_TCPIP | NO_SYSCONFIG
                LoggerNameOffset = (uint)structureSize,
                LogFileNameOffset = 0 // No ETL file and no packet payload provider.
            };
            Marshal.StructureToPtr(properties, Pointer, false);
        }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)]
    private static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);
}
