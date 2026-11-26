using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SaruMon.Tests;

internal static class TrafficIconsTests
{
    public static int Run()
    {
        BarsGrowFromOppositeEdges();
        RatesUseIntegerPixelHeights();
        CacheRemainsBounded();
        DisposedCacheCannotReturnReleasedIcons();
        CpuBarFillsByPercentage();
        return 5;
    }

    private static void BarsGrowFromOppositeEdges()
    {
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var icons = new TrafficIcons(size);
            using var idle = icons.Idle.ToBitmap();
            using var receive = icons.GetIcon(true, 128 * 1024, 0).ToBitmap();
            using var send = icons.GetIcon(true, 0, 128 * 1024).ToBitmap();
            using var offline = icons.GetIcon(false, 0, 0).ToBitmap();
            var left = size / 4;
            var right = size * 3 / 4;
            var height = 1 + (size - 3) / 2;
            var green = TrafficIcons.ReceiveColor.ToArgb();
            var red = TrafficIcons.SendColor.ToArgb();
            var white = Color.White.ToArgb();
            for (var row = 1; row < size - 1; row++)
            {
                Assert(receive.GetPixel(left, row).ToArgb() == (row <= height ? green : white), "Receive must grow from the top, one complete row at a time.");
                Assert(send.GetPixel(right, row).ToArgb() == (row >= size - 1 - height ? red : white), "Send must grow from the bottom, one complete row at a time.");
                Assert(receive.GetPixel(right, row).ToArgb() == white && send.GetPixel(left, row).ToArgb() == white,
                    "A direction must leave the other half blank.");
                Assert(idle.GetPixel(left, row).ToArgb() == white && idle.GetPixel(right, row).ToArgb() == white,
                    "Both halves must be blank while idle.");
            }
            foreach (var bitmap in new[] { idle, receive, send, offline })
            {
                Assert(bitmap.Size == new Size(size, size), "An icon must be rendered at the requested size.");
                Assert(bitmap.GetPixel(size / 2, 1).ToArgb() == Color.White.ToArgb(), "Background must remain white across traffic states.");
            }
            var orange = offline.GetPixel(size / 2, size / 2);
            Assert(orange.R > orange.G && orange.G > orange.B, "Offline cross must remain orange.");
        }
    }

    private static void RatesUseIntegerPixelHeights()
    {
        const int size = 16;
        using var icons = new TrafficIcons(size);
        var previous = 0;
        for (var height = 1; height <= size - 2; height++)
        {
            var rate = height == 1 ? double.Epsilon : 1024 * Math.Pow(2, 14 * (height - 0.5) / (size - 3));
            var reverseHeight = size - 1 - height;
            var upload = 1024 * Math.Pow(2, 14 * (reverseHeight - 0.5) / (size - 3));
            using var bitmap = icons.GetIcon(true, rate, upload).ToBitmap();
            var greenRows = Enumerable.Range(1, size - 2).Count(y => bitmap.GetPixel(4, y).ToArgb() == TrafficIcons.ReceiveColor.ToArgb());
            var redRows = Enumerable.Range(1, size - 2).Count(y => bitmap.GetPixel(12, y).ToArgb() == TrafficIcons.SendColor.ToArgb());
            Assert(greenRows == height && greenRows == previous + 1 && redRows == reverseHeight,
                "Changing byte rates must update each bar independently in single-pixel steps.");
            previous = greenRows;
        }
        Assert(ReferenceEquals(icons.GetIcon(true, 16 * 1024 * 1024, 0), icons.GetIcon(true, double.MaxValue, 0)),
            "Rates above full scale must saturate at the top of the lane.");
    }

    private static void CacheRemainsBounded()
    {
        using var icons = new TrafficIcons();
        Assert(ReferenceEquals(icons.GetIcon(true, 1, 2), icons.GetIcon(true, 4, 8)),
            "Rates that produce the same pixel heights must reuse the same icon.");
        Assert(ReferenceEquals(icons.Idle, icons.GetIcon(true, double.NaN, -1)), "Invalid rates must reuse the cached idle icon.");
        Assert(ReferenceEquals(icons.Idle, icons.GetIcon(true, double.PositiveInfinity, double.NegativeInfinity)), "Non-finite rates must not create activity.");
        Assert(ReferenceEquals(icons.GetIcon(false, 0, 0), icons.GetIcon(false, double.MaxValue, double.MaxValue)), "Offline must reuse one cached icon.");
        for (var i = 0; i <= 14; i++)
            for (var j = 0; j <= 14; j++)
                icons.GetIcon(true, 1024 * Math.Pow(2, i), 1024 * Math.Pow(2, j));
        Assert(icons.CachedIconCount == TrafficIcons.MaximumCachedIcons, "Many combinations must reuse bounded cache storage.");
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

    private static void CpuBarFillsByPercentage()
    {
        foreach (var (size, border, top) in new[] { (16, 1, 3), (20, 1, 4), (24, 2, 5), (32, 2, 6) })
        {
            using var icons = new CpuUsageIcons(size);
            using var unavailable = icons.GetIcon(null).ToBitmap();
            using var idle = icons.GetIcon(0).ToBitmap();
            using var half = icons.GetIcon(50).ToBitmap();
            using var full = icons.GetIcon(100).ToBitmap();
            var center = size / 2;
            var blue = Color.FromArgb(87, 192, 255).ToArgb();
            var track = Color.FromArgb(150, 150, 160).ToArgb();
            var markerRow = (size - border) / 2;
            Assert(idle.GetPixel(center, markerRow) != unavailable.GetPixel(center, markerRow),
                "Idle and unavailable states must be visually distinguishable.");
            Assert(half.GetPixel(center, 0).A == 0 && half.GetPixel(center, size - 1).A == 0,
                "The horizontal meter must have transparent space above and below it.");
            for (var y = top; y < size - top; y++)
            {
                for (var x = border; x < size - border; x++)
                {
                    Assert(idle.GetPixel(x, y).ToArgb() == track, "The empty CPU meter must have a plain track.");
                    Assert(half.GetPixel(x, y).ToArgb() == (x < center ? blue : track),
                        "Half CPU load must fill the left half of the full-height track.");
                    Assert(full.GetPixel(x, y).ToArgb() == blue, "Full CPU load must fill the entire track.");
                }
            }
            Assert(ReferenceEquals(icons.GetIcon(50), icons.GetIcon(51)), "Equal bar widths must reuse the cached CPU icon.");
        }
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
        graphics.DrawString("SaruMon · Receive ↓ / Send ↑", titleFont, ink, 20, 14);
        (string Label, bool Available, double Receive, double Send)[] samples =
        [
            ("Idle", true, 0, 0),
            ("Receive · 1 pixel", true, 1024, 0),
            ($"Send · {MainForm.FormatBitRate(128 * 1024)}", true, 0, 128 * 1024),
            ("Receive + send", true, 2 * 1024 * 1024, 64 * 1024),
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
