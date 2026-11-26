using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SaruMon.Core;

namespace SaruMon.Tests;

internal static class TrafficIconsTests
{
    public static int Run()
    {
        DirectionColorsAndFixedBackground();
        IncreasingRateBrightensOnlyItsArrow();
        CacheRemainsBounded();
        DisposedCacheCannotReturnReleasedIcons();
        return 4;
    }

    private static void DirectionColorsAndFixedBackground()
    {
        using var icons = new TrafficIcons();
        using var idle = icons.Idle.ToBitmap();
        using var receive = icons.GetIcon(true, TrafficIntensity.SaturationBytesPerSecond, 0).ToBitmap();
        using var send = icons.GetIcon(true, 0, TrafficIntensity.SaturationBytesPerSecond).ToBitmap();
        using var offline = icons.GetIcon(false, 0, 0).ToBitmap();
        var green = receive.GetPixel(11, 11);
        var red = send.GetPixel(22, 22);
        Assert(green.G > green.R && green.G > green.B, "Receive arrow must be green.");
        Assert(red.R > red.G && red.R > red.B, "Send arrow must be red.");
        Assert(receive.GetPixel(22, 22).ToArgb() == idle.GetPixel(22, 22).ToArgb(), "Receive traffic changed the idle send arrow.");
        Assert(send.GetPixel(11, 11).ToArgb() == idle.GetPixel(11, 11).ToArgb(), "Send traffic changed the idle receive arrow.");
        foreach (var bitmap in new[] { receive, send, offline })
            Assert(bitmap.GetPixel(16, 3).ToArgb() == idle.GetPixel(16, 3).ToArgb(), "Background must remain neutral across traffic states.");
        var orange = offline.GetPixel(16, 16);
        Assert(orange.R > orange.G && orange.G > orange.B, "Offline cross must remain orange.");
    }

    private static void IncreasingRateBrightensOnlyItsArrow()
    {
        using var icons = new TrafficIcons();
        var lastReceive = 0;
        var lastSend = 0;
        for (var level = 1; level <= TrafficIntensity.MaximumLevel; level++)
        {
            using var bitmap = icons.GetIcon(true, RateForLevel(level), RateForLevel(level)).ToBitmap();
            var green = bitmap.GetPixel(11, 11).G;
            var red = bitmap.GetPixel(22, 22).R;
            Assert(green > lastReceive && red > lastSend, "Each active level must visibly increase its arrow's channel brightness.");
            lastReceive = green;
            lastSend = red;
        }
    }

    private static void CacheRemainsBounded()
    {
        using var icons = new TrafficIcons();
        var all = new HashSet<Icon>(ReferenceEqualityComparer.Instance);
        for (var receive = 0; receive < TrafficIntensity.LevelCount; receive++)
            for (var send = 0; send < TrafficIntensity.LevelCount; send++)
                all.Add(icons.GetIcon(true, RateForLevel(receive), RateForLevel(send)));
        all.Add(icons.GetIcon(false, 0, 0));
        Assert(all.Count == TrafficIcons.MaximumCachedIcons && icons.CachedIconCount == all.Count,
            "The cache must contain exactly the finite direction combinations and offline icon.");
        Assert(ReferenceEquals(icons.Idle, icons.GetIcon(true, double.NaN, -1)), "Invalid rates must reuse the cached idle icon.");
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
        using var bitmap = new Bitmap(1120, 380);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(245, 247, 250));
        using var font = new Font("Segoe UI", 9);
        using var titleFont = new Font("Segoe UI", 12, FontStyle.Bold);
        using var ink = new SolidBrush(Color.FromArgb(35, 45, 60));
        graphics.DrawString("SaruMon: fixed background, independent arrow intensity (native 16 / 32 / 64 px)", titleFont, ink, 20, 14);
        (string Label, bool Available, double Receive, double Send)[] samples =
        [
            ("Idle", true, 0, 0),
            ("1 KiB/s", true, 1024, 1024),
            ("4 KiB/s", true, 4096, 4096),
            ("16 KiB/s", true, 16384, 16384),
            ("64 KiB/s", true, 65536, 65536),
            ("256 KiB/s", true, 262144, 262144),
            ("1 MiB/s", true, 1048576, 1048576),
            ("4 MiB/s", true, 4194304, 4194304),
            ("16 MiB/s", true, 16777216, 16777216),
            ("Down only", true, 16777216, 0),
            ("Up only", true, 0, 16777216),
            ("Offline", false, 0, 0)
        ];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var x = 20 + index * 91;
            graphics.DrawString(sample.Label, font, ink, x, 50);
            using var source = icons.GetIcon(sample.Available, sample.Receive, sample.Send).ToBitmap();
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
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

    private static double RateForLevel(int level) => level == 0 ? 0 : 1024 * Math.Pow(4, level - 1);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
