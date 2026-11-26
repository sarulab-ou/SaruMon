using SaruMon.Core;

namespace SaruMon.Tests;

internal static class TrafficIntensityTests
{
    public static int Run()
    {
        ZeroAndInvalidRatesStayIdle();
        ExactThresholds();
        MonotonicAcrossRates();
        FiniteHighRatesSaturate();
        DirectionsRemainIndependent();
        UnavailableOverridesRates();
        return 6;
    }

    private static void ZeroAndInvalidRatesStayIdle()
    {
        foreach (var rate in new[] { 0d, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert(TrafficIntensity.GetLevel(rate) == 0, $"Invalid/idle rate {rate} must not light an arrow.");
        Assert(TrafficIntensity.GetLevel(double.Epsilon) == 1, "Any positive traffic must be visible at the minimum level.");
    }

    private static void ExactThresholds()
    {
        Assert(TrafficIntensity.GetLevel(1024) == 1, "1 KiB/s must use the minimum active level.");
        double[] thresholds = [4096, 16384, 65536, 262144, 1048576, 4194304, 16777216];
        for (var index = 0; index < thresholds.Length; index++)
        {
            Assert(TrafficIntensity.GetLevel(thresholds[index] - 0.5) == index + 1, "A rate below a threshold advanced too soon.");
            Assert(TrafficIntensity.GetLevel(thresholds[index]) == index + 2, "An exact threshold did not advance.");
        }
    }

    private static void MonotonicAcrossRates()
    {
        var previous = 0;
        for (var step = 0; step <= 1200; step++)
        {
            var rate = Math.Pow(2, step / 40d) - 1;
            var level = TrafficIntensity.GetLevel(rate);
            Assert(level >= previous && level <= TrafficIntensity.MaximumLevel, "Increasing traffic must not dim or escape the bounded scale.");
            previous = level;
        }
    }

    private static void FiniteHighRatesSaturate()
    {
        foreach (var rate in new[] { TrafficIntensity.SaturationBytesPerSecond, 1e12, double.MaxValue })
            Assert(TrafficIntensity.GetLevel(rate) == TrafficIntensity.MaximumLevel, "Finite high traffic must saturate at the maximum level.");
    }

    private static void DirectionsRemainIndependent()
    {
        var receiveOnly = TrafficIntensity.GetLevels(true, 65536, 0);
        var both = TrafficIntensity.GetLevels(true, 65536, 16777216);
        Assert(receiveOnly.Receive == 4 && receiveOnly.Send == 0, "Receiving must not light the send arrow.");
        Assert(both.Receive == receiveOnly.Receive && both.Send == 8, "Send traffic must not change receive intensity.");
        var invalidSend = TrafficIntensity.GetLevels(true, 65536, double.NaN);
        Assert(invalidSend.Receive == 4 && invalidSend.Send == 0, "An invalid direction must not suppress valid traffic in the other direction.");
    }

    private static void UnavailableOverridesRates()
    {
        var levels = TrafficIntensity.GetLevels(false, 16777216, 16777216);
        Assert(!levels.IsAvailable && levels.Receive == 0 && levels.Send == 0, "Disconnected interfaces must not show cached activity.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
