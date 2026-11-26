using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SaruMon.Tests;

internal static class TrafficIconsTests
{
    public static int Run()
    {
        DirectionColorsAndFixedBackground();
        AnyTrafficHasSameFixedColor();
        CacheRemainsBounded();
        DisposedCacheCannotReturnReleasedIcons();
        return 4;
    }

    private static void DirectionColorsAndFixedBackground()
    {
        using var icons = new TrafficIcons();
        using var idle = icons.Idle.ToBitmap();
        using var receive = icons.GetIcon(true, 1, 0).ToBitmap();
        using var send = icons.GetIcon(true, 0, 1).ToBitmap();
        using var offline = icons.GetIcon(false, 0, 0).ToBitmap();
        var green = receive.GetPixel(8, 8);
        var red = send.GetPixel(24, 24);
        Assert(green.G > green.R && green.G > green.B, "Receive arrow must be green.");
        Assert(red.R > red.G && red.R > red.B, "Send arrow must be red.");
        Assert(idle.GetPixel(8, 8).ToArgb() == Color.White.ToArgb() && idle.GetPixel(24, 24).ToArgb() == Color.White.ToArgb(), "Idle arrows must be absent on a white background.");
        Assert(receive.GetPixel(24, 24).ToArgb() == Color.White.ToArgb(), "Receive traffic must not display the send arrow.");
        Assert(send.GetPixel(8, 8).ToArgb() == Color.White.ToArgb(), "Send traffic must not display the receive arrow.");
        foreach (var bitmap in new[] { receive, send, offline })
            Assert(bitmap.GetPixel(16, 3).ToArgb() == Color.White.ToArgb(), "Background must remain white across traffic states.");
        var orange = offline.GetPixel(16, 16);
        Assert(orange.R > orange.G && orange.G > orange.B, "Offline cross must remain orange.");
    }

    private static void AnyTrafficHasSameFixedColor()
    {
        using var icons = new TrafficIcons();
        foreach (var rate in new[] { double.Epsilon, 1, 4096, 1048576, double.MaxValue })
        {
            Assert(ReferenceEquals(icons.GetIcon(true, rate, 0), icons.GetIcon(true, 1, 0)), "Any receive traffic must use the same visible arrow.");
            Assert(ReferenceEquals(icons.GetIcon(true, 0, rate), icons.GetIcon(true, 0, 1)), "Any send traffic must use the same visible arrow.");
            using var bitmap = icons.GetIcon(true, rate, rate).ToBitmap();
            Assert(bitmap.GetPixel(8, 8).ToArgb() == TrafficIcons.ReceiveColor.ToArgb()
                && bitmap.GetPixel(24, 24).ToArgb() == TrafficIcons.SendColor.ToArgb(), "Traffic volume must not alter direction colors.");
        }
    }

    private static void CacheRemainsBounded()
    {
        using var icons = new TrafficIcons();
        var all = new HashSet<Icon>(ReferenceEqualityComparer.Instance);
        foreach (var receive in new[] { 0d, double.Epsilon, 1, double.MaxValue })
            foreach (var send in new[] { 0d, double.Epsilon, 1, double.MaxValue })
                all.Add(icons.GetIcon(true, receive, send));
        all.Add(icons.GetIcon(false, 0, 0));
        Assert(all.Count == TrafficIcons.MaximumCachedIcons && icons.CachedIconCount == all.Count,
            "The cache must contain exactly the finite direction combinations and offline icon.");
        Assert(ReferenceEquals(icons.Idle, icons.GetIcon(true, double.NaN, -1)), "Invalid rates must reuse the cached idle icon.");
        Assert(ReferenceEquals(icons.Idle, icons.GetIcon(true, double.PositiveInfinity, double.NegativeInfinity)), "Non-finite rates must not create activity.");
        Assert(ReferenceEquals(icons.GetIcon(false, 0, 0), icons.GetIcon(false, double.MaxValue, double.MaxValue)), "Offline must reuse one cached icon.");
        for (var index = 0; index < 4000; index++)
            Assert(all.Contains(icons.GetIcon(true, index * 17323.5, index * 117.75)), "Changing rates allocated an uncached native icon.");
        Assert(icons.CachedIconCount == TrafficIcons.MaximumCachedIcons, "Polling expanded the icon cache.");
    }

    private static void DisposedCacheCannotReturnReleasedIcons()
    {
        var icons = new TrafficIcons();
        icons.Dispose();
        icons.Dispose();
        try { _ = icons.GetIcon(true, 1, 1); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("A disposed cache returned an invalid native icon.");
    }

    internal static void WritePreview(string outputPath)
    {
        using var icons = new TrafficIcons();
        using var bitmap = new Bitmap(650, 380);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(245, 247, 250));
        using var font = new Font("Segoe UI", 9);
        using var titleFont = new Font("Segoe UI", 12, FontStyle.Bold);
        using var ink = new SolidBrush(Color.FromArgb(35, 45, 60));
        graphics.DrawString("SaruMon: white background, fixed colors (16 / 32 / 64 px)", titleFont, ink, 20, 14);
        (string Label, bool Available, double Receive, double Send)[] samples =
        [
            ("Idle", true, 0, 0),
            ("Down only", true, 1, 0),
            ("Up only", true, 0, 1),
            ("Down + Up", true, 1, 1),
            ("Offline", false, 0, 0)
        ];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var x = 25 + index * 126;
            graphics.DrawString(sample.Label, font, ink, x, 50);
            using var source = icons.GetIcon(sample.Available, sample.Receive, sample.Send).ToBitmap();
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(x + 24, 80, 16, 16));
            graphics.DrawImage(source, new Rectangle(x + 16, 112, 32, 32));
            graphics.DrawImage(source, new Rectangle(x, 160, 64, 64));
            using var dark = new SolidBrush(Color.FromArgb(32, 34, 39));
            graphics.FillRectangle(dark, x - 4, 245, 76, 105);
            graphics.DrawImage(source, new Rectangle(x + 24, 257, 16, 16));
            graphics.DrawImage(source, new Rectangle(x + 16, 293, 32, 32));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        bitmap.Save(outputPath, ImageFormat.Png);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
