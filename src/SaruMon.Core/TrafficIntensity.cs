namespace SaruMon.Core;

public readonly record struct TrafficIntensityLevels(bool IsAvailable, int Receive, int Send);

/// <summary>Fixed logarithmic rate buckets, shared by both directions and independent of adapter speed.</summary>
public static class TrafficIntensity
{
    public const int MaximumLevel = 8;
    public const int LevelCount = MaximumLevel + 1;
    public const double ReferenceBytesPerSecond = 1024;
    public const double SaturationBytesPerSecond = 16 * 1024 * 1024;

    // Zero is idle. Any positive rate lights the first level, then each fourfold
    // increase from the 1 KiB/s reference advances a level, up to 16 MiB/s.
    public static int GetLevel(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return 0;
        var level = 1;
        var threshold = 4 * ReferenceBytesPerSecond;
        while (level < MaximumLevel && bytesPerSecond >= threshold)
        {
            level++;
            threshold *= 4;
        }
        return level;
    }

    public static TrafficIntensityLevels GetLevels(bool isAvailable, double downloadBytesPerSecond, double uploadBytesPerSecond) =>
        isAvailable
            ? new(true, GetLevel(downloadBytesPerSecond), GetLevel(uploadBytesPerSecond))
            : new(false, 0, 0);
}
