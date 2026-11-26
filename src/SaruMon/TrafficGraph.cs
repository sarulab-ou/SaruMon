using System.Drawing.Drawing2D;

namespace SaruMon;

internal sealed class TrafficGraph : Control
{
    private const int HistorySeconds = 60;
    private readonly Queue<(double Down, double Up)> samples = new();
    public TrafficGraph()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(102, 116, 134);
        AccessibleName = "直近60秒の通信速度グラフ";
    }

    public void Add(double down, double up)
    {
        samples.Enqueue((down, up));
        while (samples.Count > HistorySeconds + 1) samples.Dequeue();
        Invalidate();
    }

    public void Reset() { samples.Clear(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new RectangleF(12, 30, Math.Max(1, Width - 24), Math.Max(1, Height - 56));
        var ceiling = Math.Max(1024, samples.Count == 0 ? 0 : samples.Max(x => Math.Max(x.Down, x.Up)) * 1.15);
        using var grid = new Pen(Color.FromArgb(231, 236, 242));
        for (var i = 0; i < 4; i++)
        {
            var y = area.Top + area.Height * i / 3;
            g.DrawLine(grid, area.Left, y, area.Right, y);
        }
        TextRenderer.DrawText(g, "通信速度  /  " + MainForm.FormatBytes(ceiling) + "/s", Font, new Point(10, 5), ForeColor);
        TextRenderer.DrawText(g, "60秒前", Font, new Point(10, Height - 23), ForeColor);
        TextRenderer.DrawText(g, "現在", Font, new Rectangle(0, Height - 23, Width - 10, 23), ForeColor, TextFormatFlags.Right);
        var data = samples.ToArray();
        if (data.Length < 2) return;
        Draw(TrafficIcons.ReceiveColor, x => x.Down);
        Draw(TrafficIcons.SendColor, x => x.Up);

        void Draw(Color color, Func<(double Down, double Up), double> select)
        {
            using var pen = new Pen(color, 2);
            var points = data.Select((x, i) => new PointF(area.Right - (data.Length - 1 - i) * area.Width / HistorySeconds,
                area.Bottom - (float)(select(x) / ceiling) * area.Height)).ToArray();
            g.DrawLines(pen, points);
        }
    }
}
