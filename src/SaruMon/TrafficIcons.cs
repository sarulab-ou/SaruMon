using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SaruMon;

internal enum TrafficState { Idle, Receiving, Sending, Both, Offline }

internal sealed class TrafficIcons : IDisposable
{
    public static readonly Color ReceiveColor = Color.FromArgb(0, 140, 62);
    public static readonly Color SendColor = Color.FromArgb(210, 24, 35);
    private static readonly PointF[] ReceiveArrow = [new(4, 4), new(12, 4), new(12, 18), new(14, 18), new(8, 28), new(2, 18), new(4, 18)];
    private static readonly PointF[] SendArrow = [new(20, 28), new(28, 28), new(28, 14), new(30, 14), new(24, 4), new(18, 14), new(20, 14)];
    internal const int MaximumCachedIcons = 5;
    private readonly Icon[] icons;
    private bool disposed;

    // All combinations are generated once. Polling performs only arithmetic and
    // an array lookup, so changing traffic never allocates a native icon handle.
    public TrafficIcons()
    {
        var created = new List<Icon>(MaximumCachedIcons);
        try
        {
            for (var receive = 0; receive <= 1; receive++)
                for (var send = 0; send <= 1; send++)
                    created.Add(Create(receive != 0, send != 0, offline: false));
            created.Add(Create(false, false, offline: true));
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

    private static Icon Create(bool receiving, bool sending, bool offline)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.White);
        using var border = new Pen(Color.FromArgb(150, 150, 150), 1);
        g.DrawRectangle(border, 0.5f, 0.5f, 31, 31);
        if (offline)
        {
            using var pen = new Pen(Color.FromArgb(204, 112, 0), 4);
            g.DrawLine(pen, 8, 8, 24, 24);
            g.DrawLine(pen, 24, 8, 8, 24);
        }
        else
        {
            if (receiving) DrawArrow(g, ReceiveArrow, ReceiveColor);
            if (sending) DrawArrow(g, SendArrow, SendColor);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void DrawArrow(Graphics graphics, PointF[] points, Color color)
    {
        using var fill = new SolidBrush(color);
        graphics.FillPolygon(fill, points);
    }

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
