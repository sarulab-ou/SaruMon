using System.Runtime.InteropServices;

namespace SaruMon.Monitoring;

internal sealed class CpuUsageMeter : IDisposable
{
    private const string ProcessorTime = @"\Processor Information(_Total)\% Processor Time";
    private const uint FormatDouble = 0x00000200;
    private nint query;
    private readonly nint counter;

    public CpuUsageMeter()
    {
        if (PdhOpenQueryW(0, 0, out var openedQuery) != 0) return;
        if (PdhAddEnglishCounterW(openedQuery, ProcessorTime, 0, out counter) != 0)
        {
            PdhCloseQuery(openedQuery);
            return;
        }

        query = openedQuery;
        // The rate counter needs a baseline before the first timer tick.
        PdhCollectQueryData(query);
    }

    public int? ReadPercentage()
    {
        if (query == 0 || PdhCollectQueryData(query) != 0
            || PdhGetFormattedCounterValue(counter, FormatDouble, 0, out var reading) != 0
            || reading.Status is not (0 or 1) || !double.IsFinite(reading.Value))
            return null;

        return (int)Math.Round(Math.Clamp(reading.Value, 0, 100));
    }

    public void Dispose()
    {
        if (query == 0) return;
        PdhCloseQuery(query);
        query = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(nint dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint counterType, out CounterValue value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
