using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using SaruMon.Core;

namespace SaruMon;

internal enum TrafficState { Idle, Receiving, Sending, Both, Offline }

internal sealed class TrafficIcons : IDisposable
{
    public static readonly Color ReceiveColor = Color.FromArgb(16, 151, 107);
    public static readonly Color SendColor = Color.FromArgb(211, 47, 47);
    private static readonly Color BackgroundColor = Color.FromArgb(27, 32, 39);
    private static readonly Color InactiveArrowColor = Color.FromArgb(94, 102, 112);
    private static readonly Color ReceiveGlowColor = Color.FromArgb(77, 255, 153);
    private static readonly Color SendGlowColor = Color.FromArgb(255, 98, 107);
    private static readonly PointF[] ReceiveArrow = [new(9, 7), new(13, 7), new(13, 18), new(17, 18), new(11, 24), new(5, 18), new(9, 18)];
    private static readonly PointF[] SendArrow = [new(20, 25), new(24, 25), new(24, 14), new(28, 14), new(22, 8), new(16, 14), new(20, 14)];
    internal const int MaximumCachedIcons = TrafficIntensity.LevelCount * TrafficIntensity.LevelCount + 1;
    private readonly Icon[] icons;
    private bool disposed;

    // All combinations are generated once. Polling performs only arithmetic and
    // an array lookup, so changing traffic never allocates a native icon handle.
    public TrafficIcons()
    {
        var created = new List<Icon>(MaximumCachedIcons);
        try
        {
            for (var receive = 0; receive < TrafficIntensity.LevelCount; receive++)
                for (var send = 0; send < TrafficIntensity.LevelCount; send++)
                    created.Add(Create(receive, send, offline: false));
            created.Add(Create(0, 0, offline: true));
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
        var levels = TrafficIntensity.GetLevels(isAvailable, downloadBytesPerSecond, uploadBytesPerSecond);
        return levels.IsAvailable
            ? icons[levels.Receive * TrafficIntensity.LevelCount + levels.Send]
            : icons[^1];
    }

    public static Color ColorFor(TrafficState state) => state switch
    {
        TrafficState.Receiving => ReceiveColor,
        TrafficState.Sending => SendColor,
        TrafficState.Both => Color.FromArgb(72, 85, 104),
        TrafficState.Offline => Color.FromArgb(204, 122, 32),
        _ => Color.FromArgb(112, 123, 139)
    };

    private static Icon Create(int receiveLevel, int sendLevel, bool offline)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var backgroundPath = new GraphicsPath();
        backgroundPath.AddArc(1, 1, 14, 14, 180, 90);
        backgroundPath.AddArc(17, 1, 14, 14, 270, 90);
        backgroundPath.AddArc(17, 17, 14, 14, 0, 90);
        backgroundPath.AddArc(1, 17, 14, 14, 90, 90);
        backgroundPath.CloseFigure();
        using var background = new SolidBrush(BackgroundColor);
        using var border = new Pen(Color.FromArgb(74, 83, 96), 1);
        g.FillPath(background, backgroundPath);
        g.DrawPath(border, backgroundPath);
        if (offline)
        {
            using var pen = new Pen(Color.FromArgb(240, 150, 55), 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 10, 10, 22, 22);
            g.DrawLine(pen, 22, 10, 10, 22);
        }
        else
        {
            DrawArrow(g, ReceiveArrow, receiveLevel, Color.FromArgb(24, 105, 60), ReceiveGlowColor);
            DrawArrow(g, SendArrow, sendLevel, Color.FromArgb(114, 37, 43), SendGlowColor);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void DrawArrow(Graphics graphics, PointF[] points, int level, Color dimColor, Color brightColor)
    {
        var color = level == 0 ? InactiveArrowColor : Blend(dimColor, brightColor, (level - 1d) / (TrafficIntensity.MaximumLevel - 1));
        if (level > 0)
        {
            var strength = level / (float)TrafficIntensity.MaximumLevel;
            using var glow = new Pen(Color.FromArgb(20 + (int)(90 * strength), color), 1 + 1.4f * strength) { LineJoin = LineJoin.Round };
            graphics.DrawPolygon(glow, points);
        }
        using var fill = new SolidBrush(color);
        graphics.FillPolygon(fill, points);
    }

    private static Color Blend(Color start, Color end, double fraction) => Color.FromArgb(
        (int)Math.Round(start.R + (end.R - start.R) * fraction),
        (int)Math.Round(start.G + (end.G - start.G) * fraction),
        (int)Math.Round(start.B + (end.B - start.B) * fraction));

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
