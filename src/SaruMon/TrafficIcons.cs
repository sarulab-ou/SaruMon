using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SaruMon;

internal enum TrafficState { Idle, Receiving, Sending, Both, Offline }

internal sealed class TrafficIcons : IDisposable
{
    public static readonly Color ReceiveColor = Color.FromArgb(16, 151, 107);
    public static readonly Color SendColor = Color.FromArgb(42, 110, 219);
    private readonly Dictionary<TrafficState, Icon> icons = Enum.GetValues<TrafficState>().ToDictionary(x => x, Create);
    public Icon this[TrafficState state] => icons[state];
    public static Color ColorFor(TrafficState state) => state switch
    {
        TrafficState.Receiving => ReceiveColor,
        TrafficState.Sending => SendColor,
        TrafficState.Both => Color.FromArgb(132, 80, 214),
        TrafficState.Offline => Color.FromArgb(204, 122, 32),
        _ => Color.FromArgb(112, 123, 139)
    };

    private static Icon Create(TrafficState state)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var background = new SolidBrush(ColorFor(state));
        g.FillEllipse(background, 1, 1, 30, 30);
        using var pen = new Pen(Color.White, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (state == TrafficState.Offline)
        {
            g.DrawLine(pen, 10, 10, 22, 22);
            g.DrawLine(pen, 22, 10, 10, 22);
        }
        else
        {
            g.DrawLine(pen, 11, 8, 11, 23);
            g.DrawLines(pen, [new(6, 18), new(11, 23), new(16, 18)]);
            g.DrawLine(pen, 22, 24, 22, 9);
            g.DrawLines(pen, [new(17, 14), new(22, 9), new(27, 14)]);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose() { foreach (var icon in icons.Values) icon.Dispose(); }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
