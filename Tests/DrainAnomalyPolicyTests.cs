using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class DrainAnomalyPolicyTests
{
    // A real overnight anomaly: 12% lost over 8h = 1.5%/h, over a 3%/h... no — pick values that
    // clearly clear every floor and the rate. 30% over 8h = 3.75%/h ≥ 3%/h threshold.
    [Fact]
    public void ShouldWarn_GenuineOvernightDrain_True()
    {
        Assert.True(DrainAnomalyPolicy.ShouldWarn(enabled: true, socDropPercent: 30, gapDuration: TimeSpan.FromHours(8), thresholdPercentPerHour: 3));
    }

    [Fact]
    public void ShouldWarn_Disabled_False()
    {
        Assert.False(DrainAnomalyPolicy.ShouldWarn(enabled: false, socDropPercent: 30, gapDuration: TimeSpan.FromHours(8), thresholdPercentPerHour: 3));
    }

    [Fact]
    public void ShouldWarn_SmallDropOverShortGap_False_NoFalsePositive()
    {
        // The bug this policy fixes: a 1-point tick across a ~90s scheduler stall extrapolates to
        // ~40%/h and would otherwise fire. Fails the min-drop floor (and the min-gap floor).
        Assert.False(DrainAnomalyPolicy.ShouldWarn(enabled: true, socDropPercent: 1, gapDuration: TimeSpan.FromSeconds(90), thresholdPercentPerHour: 3));
    }

    [Fact]
    public void ShouldWarn_DropBelowAbsoluteFloor_False()
    {
        // 4% over 30 min = 8%/h (well over threshold), but 4 < MinDropPercent (5) — too small in
        // absolute terms to be worth alarming about.
        Assert.False(DrainAnomalyPolicy.ShouldWarn(enabled: true, socDropPercent: 4, gapDuration: TimeSpan.FromMinutes(30), thresholdPercentPerHour: 3));
    }

    [Fact]
    public void ShouldWarn_GapBelowDurationFloor_False()
    {
        // 10% drop clears the absolute floor, but over only 10 min (< MinGap 15 min) the %/hour
        // extrapolation isn't trustworthy.
        Assert.False(DrainAnomalyPolicy.ShouldWarn(enabled: true, socDropPercent: 10, gapDuration: TimeSpan.FromMinutes(10), thresholdPercentPerHour: 3));
    }

    [Fact]
    public void ShouldWarn_RiseAcrossGap_False()
    {
        // Battery charged while the app was off — negative drop, never an anomaly.
        Assert.False(DrainAnomalyPolicy.ShouldWarn(enabled: true, socDropPercent: -20, gapDuration: TimeSpan.FromHours(8), thresholdPercentPerHour: 3));
    }

    [Fact]
    public void ShouldWarn_RealDropButUnderRateThreshold_False()
    {
        // 6% over 6h = 1%/h, under a 3%/h threshold — a slow, normal overnight self-discharge.
        Assert.False(DrainAnomalyPolicy.ShouldWarn(enabled: true, socDropPercent: 6, gapDuration: TimeSpan.FromHours(6), thresholdPercentPerHour: 3));
    }

    [Fact]
    public void ShouldWarn_ExactlyAtRateThreshold_True()
    {
        // 15% over 5h = exactly 3%/h — the boundary is inclusive (>=).
        Assert.True(DrainAnomalyPolicy.ShouldWarn(enabled: true, socDropPercent: 15, gapDuration: TimeSpan.FromHours(5), thresholdPercentPerHour: 3));
    }
}
