using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SaruMon;

internal enum TrafficState { Idle, Receiving, Sending, Both, Offline }

internal sealed class TrafficIcons : IDisposable
{
    public static readonly Color ReceiveColor = Color.FromArgb(0, 140, 62);
    public static readonly Color SendColor = Color.FromArgb(210, 24, 35);
    internal const int MaximumCachedIcons = 5;
    private readonly Icon[] icons;
    private bool disposed;

    // All combinations are generated once. Polling performs only arithmetic and
    // an array lookup, so changing traffic never allocates a native icon handle.
    public TrafficIcons(int size = 32)
    {
        var created = new List<Icon>(MaximumCachedIcons);
        try
        {
            for (var receive = 0; receive <= 1; receive++)
                for (var send = 0; send <= 1; send++)
                    created.Add(Create(size, receive != 0, send != 0, offline: false));
            created.Add(Create(size, false, false, offline: true));
            icons = created.ToArray();
        }
        catch
        {
            foreach (var icon in created) icon.Dispose();
            throw;
        }
    }

    public Icon Idle => GetIcon(true, 0, 0);
    internal int CachedIconCount => icons.Length;

    public Icon GetIcon(bool isAvailable, double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!isAvailable) return icons[^1];
        var receiving = double.IsFinite(downloadBytesPerSecond) && downloadBytesPerSecond > 0;
        var sending = double.IsFinite(uploadBytesPerSecond) && uploadBytesPerSecond > 0;
        return icons[(receiving ? 2 : 0) + (sending ? 1 : 0)];
    }

    public static Color ColorFor(TrafficState state) => state switch
    {
        TrafficState.Receiving => ReceiveColor,
        TrafficState.Sending => SendColor,
        TrafficState.Both => Color.FromArgb(72, 85, 104),
        TrafficState.Offline => Color.FromArgb(204, 122, 32),
        _ => Color.FromArgb(112, 123, 139)
    };

    private static Icon Create(int size, bool receiving, bool sending, bool offline)
    {
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var border = new Pen(Color.FromArgb(169, 179, 190));
        g.DrawRectangle(border, 0, 0, size - 1, size - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 16f, size / 16f);
        if (offline)
        {
            using var pen = CreateStroke(Color.FromArgb(204, 112, 0));
            g.DrawLine(pen, 4, 4, 12, 12);
            g.DrawLine(pen, 12, 4, 4, 12);
        }
        else
        {
            if (receiving) DrawArrow(g, ReceiveColor, 4.5f, 3.5f, 12f, 9f);
            if (sending) DrawArrow(g, SendColor, 11.5f, 12.5f, 4f, 7f);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void DrawArrow(Graphics graphics, Color color, float center, float tail, float tip, float shoulder)
    {
        using var pen = CreateStroke(color);
        graphics.DrawLine(pen, center, tail, center, tip);
        graphics.DrawLines(pen, [new PointF(center - 2f, shoulder), new PointF(center, tip), new PointF(center + 2f, shoulder)]);
    }

    private static Pen CreateStroke(Color color) => new(color, 2f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var icon in icons) icon.Dispose();
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
