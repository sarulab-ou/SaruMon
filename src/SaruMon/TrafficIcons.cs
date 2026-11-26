using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SaruMon;

internal enum TrafficState { Idle, Receiving, Sending, Both, Offline }

internal sealed class TrafficIcons : IDisposable
{
    public static readonly Color ReceiveColor = Color.FromArgb(0, 140, 62);
    public static readonly Color SendColor = Color.FromArgb(210, 24, 35);
    internal const int MaximumCachedIcons = 32;
    private const double MinimumRate = 1024;
    private const double FullScaleRate = 16 * 1024 * 1024;
    private readonly int size;
    private readonly LinkedList<BarIcon> icons = new();
    private bool disposed;

    public TrafficIcons(int size = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 5);
        this.size = size;
    }

    public Icon Idle => GetIcon(true, 0, 0);
    internal int CachedIconCount => icons.Count;

    public Icon GetIcon(bool isAvailable, double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var receive = isAvailable ? BarHeight(downloadBytesPerSecond) : 0;
        var send = isAvailable ? BarHeight(uploadBytesPerSecond) : 0;
        var offline = !isAvailable;
        for (var node = icons.First; node is not null; node = node.Next)
        {
            var cached = node.Value;
            if (cached.Receive != receive || cached.Send != send || cached.Offline != offline) continue;
            icons.Remove(node);
            icons.AddFirst(node);
            return cached.Icon;
        }

        var icon = Create(size, receive, send, offline);
        icons.AddFirst(new BarIcon(receive, send, offline, icon));
        // Evict the least recent icon. The icon currently assigned to the form
        // and tray was just used at the last refresh, so it stays alive.
        if (icons.Count > MaximumCachedIcons)
        {
            icons.Last!.Value.Icon.Dispose();
            icons.RemoveLast();
        }
        return icon;
    }

    private int BarHeight(double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0) return 0;
        if (rate <= MinimumRate) return 1;
        if (rate >= FullScaleRate) return size - 2;
        var fraction = Math.Log2(rate / MinimumRate) / Math.Log2(FullScaleRate / MinimumRate);
        return 1 + (int)(fraction * (size - 3));
    }

    public static Color ColorFor(TrafficState state) => state switch
    {
        TrafficState.Receiving => ReceiveColor,
        TrafficState.Sending => SendColor,
        TrafficState.Both => Color.FromArgb(72, 85, 104),
        TrafficState.Offline => Color.FromArgb(204, 122, 32),
        _ => Color.FromArgb(112, 123, 139)
    };

    private static Icon Create(int size, int receive, int send, bool offline)
    {
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var border = new Pen(Color.FromArgb(169, 179, 190));
        g.DrawRectangle(border, 0, 0, size - 1, size - 1);
        if (offline)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 16f, size / 16f);
            using var pen = CreateStroke(Color.FromArgb(204, 112, 0));
            g.DrawLine(pen, 4, 4, 12, 12);
            g.DrawLine(pen, 12, 4, 4, 12);
        }
        else
        {
            var laneWidth = (size - 3) / 2;
            if (receive > 0)
            {
                using var green = new SolidBrush(ReceiveColor);
                g.FillRectangle(green, 1, 1, laneWidth, receive);
            }
            if (send > 0)
            {
                using var red = new SolidBrush(SendColor);
                g.FillRectangle(red, size - 1 - laneWidth, size - 1 - send, laneWidth, send);
            }
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
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
        foreach (var entry in icons) entry.Icon.Dispose();
        icons.Clear();
    }

    private sealed record BarIcon(int Receive, int Send, bool Offline, Icon Icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
