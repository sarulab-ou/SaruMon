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
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var icons = new TrafficIcons(size);
            using var idle = icons.Idle.ToBitmap();
            using var receive = icons.GetIcon(true, 1, 0).ToBitmap();
            using var send = icons.GetIcon(true, 0, 1).ToBitmap();
            using var offline = icons.GetIcon(false, 0, 0).ToBitmap();
            var left = size / 4;
            var right = size * 3 / 4;
            var green = receive.GetPixel(left, left);
            var red = send.GetPixel(right, right);
            Assert(green.G > green.R && green.G > green.B, "Receive arrow must be green.");
            Assert(red.R > red.G && red.R > red.B, "Send arrow must be red.");
            Assert(idle.GetPixel(left, left).ToArgb() == Color.White.ToArgb() && idle.GetPixel(right, right).ToArgb() == Color.White.ToArgb(), "Idle arrows must be absent on a white background.");
            Assert(receive.GetPixel(right, right).ToArgb() == Color.White.ToArgb(), "Receive traffic must not display the send arrow.");
            Assert(send.GetPixel(left, left).ToArgb() == Color.White.ToArgb(), "Send traffic must not display the receive arrow.");
            foreach (var bitmap in new[] { idle, receive, send, offline })
            {
                Assert(bitmap.Size == new Size(size, size), "An icon must be rendered at the requested size.");
                Assert(bitmap.GetPixel(size / 2, 1).ToArgb() == Color.White.ToArgb(), "Background must remain white across traffic states.");
            }
            var orange = offline.GetPixel(size / 2, size / 2);
            Assert(orange.R > orange.G && orange.G > orange.B, "Offline cross must remain orange.");
        }
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
        using var bitmap = new Bitmap(730, 440);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(245, 247, 250));
        using var font = new Font("Segoe UI", 9);
        using var titleFont = new Font("Segoe UI", 12, FontStyle.Bold);
        using var ink = new SolidBrush(Color.FromArgb(35, 45, 60));
        using var dark = new SolidBrush(Color.FromArgb(32, 34, 39));
        graphics.DrawString("SaruMon notification icons", titleFont, ink, 20, 14);
        (string Label, bool Available, double Receive, double Send)[] samples =
        [
            ("Idle", true, 0, 0),
            ("Down only", true, 1, 0),
            ("Up only", true, 0, 1),
            ("Down + Up", true, 1, 1),
            ("Offline", false, 0, 0)
        ];
        for (var index = 0; index < samples.Length; index++)
            graphics.DrawString(samples[index].Label, font, ink, 116 + index * 120, 50);

        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        var y = 80;
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            graphics.DrawString($"{size} px / 2×", font, ink, 20, y + 16);
            using var icons = new TrafficIcons(size);
            for (var index = 0; index < samples.Length; index++)
            {
                var sample = samples[index];
                var x = 108 + index * 120;
                using var source = icons.GetIcon(sample.Available, sample.Receive, sample.Send).ToBitmap();
                graphics.DrawImageUnscaled(source, x, y + 4);
                graphics.FillRectangle(dark, x + 35, y, size * 2 + 8, size * 2 + 8);
                graphics.DrawImage(source, new Rectangle(x + 39, y + 4, size * 2, size * 2));
            }
            y += 85;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        bitmap.Save(outputPath, ImageFormat.Png);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
