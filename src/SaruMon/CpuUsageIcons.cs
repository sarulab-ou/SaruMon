namespace SaruMon;

internal sealed class CpuUsageIcons : IDisposable
{
    private static readonly Color BarColor = Color.FromArgb(28, 104, 210);
    private static readonly string[] Label =
    [
        "### ### # #",
        "#   # # # #",
        "#   ### # #",
        "#   #   # #",
        "### #   ###"
    ];

    private readonly int size;
    private readonly int inset;
    private readonly int trackWidth;
    private readonly Icon?[] byWidth;
    private bool disposed;

    public CpuUsageIcons(int size)
    {
        this.size = size;
        inset = Scale(2);
        trackWidth = size - 2 * inset;
        byWidth = new Icon?[trackWidth + 2];
    }

    public Icon GetIcon(int? percentage)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var width = percentage switch
        {
            null => -1,
            <= 0 => 0,
            _ => Math.Max(1, Math.Min(percentage.Value, 100) * trackWidth / 100)
        };
        return byWidth[width + 1] ??= Draw(width);
    }

    private Icon Draw(int width)
    {
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var border = new Pen(Color.FromArgb(169, 179, 190));
        graphics.DrawRectangle(border, 0, 0, size - 1, size - 1);
        using var ink = new SolidBrush(Color.FromArgb(31, 44, 63));
        for (var row = 0; row < Label.Length; row++)
        {
            for (var column = 0; column < Label[row].Length; column++)
            {
                if (Label[row][column] != '#') continue;
                graphics.FillRectangle(ink, Rectangle.FromLTRB(Scale(2 + column), Scale(3 + row),
                    Scale(3 + column), Scale(4 + row)));
            }
        }

        using var track = new SolidBrush(Color.FromArgb(218, 227, 240));
        var top = Scale(10);
        var height = Scale(14) - top;
        if (width < 0)
        {
            graphics.FillRectangle(track, Scale(6), Scale(11), Scale(10) - Scale(6), Math.Max(1, Scale(1)));
        }
        else
        {
            graphics.FillRectangle(track, inset, top, trackWidth, height);
            using var bar = new SolidBrush(BarColor);
            graphics.FillRectangle(bar, inset, top, width, height);
        }

        return TrayIconImage.FromBitmap(bitmap);
    }

    private int Scale(int pixel) => (pixel * size + 8) / 16;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var icon in byWidth) icon?.Dispose();
    }
}
