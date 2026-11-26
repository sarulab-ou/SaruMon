namespace SaruMon;

internal sealed class CpuUsageIcons : IDisposable
{
    private static readonly Color BarColor = Color.FromArgb(87, 192, 255);
    private static readonly Color TrackColor = Color.FromArgb(150, 150, 160);

    private readonly int size;
    private readonly int borderWidth;
    private readonly int trackWidth;
    private readonly Icon?[] byWidth;
    private bool disposed;

    public CpuUsageIcons(int size)
    {
        this.size = size;
        borderWidth = Math.Max(1, Scale(1));
        trackWidth = size - 2 * borderWidth;
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
        graphics.Clear(Color.Transparent);

        var top = Scale(3);
        var height = size - 2 * top;
        using var frame = new SolidBrush(Color.FromArgb(76, 94, 113));
        graphics.FillRectangle(frame, 0, top - borderWidth, size, height + 2 * borderWidth);
        using var track = new SolidBrush(TrackColor);
        graphics.FillRectangle(track, borderWidth, top, trackWidth, height);
        if (width < 0)
        {
            graphics.FillRectangle(frame, Scale(6), (size - borderWidth) / 2, Scale(10) - Scale(6), borderWidth);
        }
        else if (width > 0)
        {
            using var bar = new SolidBrush(BarColor);
            graphics.FillRectangle(bar, borderWidth, top, width, height);
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
