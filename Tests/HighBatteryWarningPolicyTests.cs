using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

public class HighBatteryWarningPolicyTests
{
    // Smart Charge limiting at the given stop percentage. Start is irrelevant to this policy and
    // is 0 on every mode-based vendor by contract.
    private static ChargeThresholdState Limiting(int stop) => new(Capable: true, Enabled: true, Start: 0, Stop: stop);

    private static ChargeThresholdState NotLimiting() => new(Capable: true, Enabled: false, Start: 0, Stop: 0);

    [Fact]
    public void ShouldWarn_BelowThreshold_False()
    {
        Assert.False(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 79, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: null));
    }

    [Fact]
    public void ShouldWarn_CrossingUpward_True()
    {
        // At the threshold, not merely past it: 80 % is already "the battery reached 80".
        Assert.True(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 80, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: null));
    }

    [Fact]
    public void ShouldWarn_StaysHigh_NoRepeat()
    {
        // Latched from the crossing above; the machine sitting on charge must not re-fire it.
        Assert.False(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 92, warnAtPercent: 80, alreadyWarned: true, chargeThreshold: null));
    }

    [Fact]
    public void ClearsLatch_FallsBackBelow_ThenRecrossesAndWarnsAgain()
    {
        Assert.False(HighBatteryWarningPolicy.ClearsLatch(levelPercent: 80, warnAtPercent: 80));
        Assert.True(HighBatteryWarningPolicy.ClearsLatch(levelPercent: 79, warnAtPercent: 80));

        // Re-armed, the next upward crossing warns again.
        Assert.True(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 81, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: null));
    }

    [Fact]
    public void ShouldWarn_Disabled_False()
    {
        Assert.False(HighBatteryWarningPolicy.ShouldWarn(
            enabled: false, levelPercent: 95, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: null));
    }

    [Fact]
    public void ShouldWarn_SmartChargeLimiting_LevelInsideWindow_False()
    {
        // Warn level 80, cap stopping at 90, sitting at 85: above the warn level but within the
        // cap, so the cap is doing its job and there is nothing to report.
        Assert.False(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 85, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: Limiting(90)));
    }

    [Fact]
    public void ShouldWarn_SmartChargeLimiting_LevelAtStop_False()
    {
        // Exactly at the stop threshold is still inside the window — the cap is holding.
        Assert.False(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 90, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: Limiting(90)));
    }

    [Fact]
    public void ShouldWarn_SmartChargeLimiting_LevelAboveStop_True()
    {
        // The whole point of the warning: capping is not holding the battery where it was told to.
        Assert.True(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 91, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: Limiting(90)));
    }

    [Fact]
    public void ShouldWarn_SmartChargeOff_True()
    {
        // Nothing is capping, so a high level is the plain uncapped case the warning exists for.
        Assert.True(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 95, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: NotLimiting()));
    }

    [Fact]
    public void ShouldWarn_NoThresholdInterface_True()
    {
        // A vendor that reports nothing is not a cap either.
        Assert.True(HighBatteryWarningPolicy.ShouldWarn(
            enabled: true, levelPercent: 95, warnAtPercent: 80, alreadyWarned: false, chargeThreshold: null));
    }

    [Fact]
    public void Defaults_OffAt80()
    {
        var s = new AppSettings();
        Assert.False(s.HighBatteryWarningEnabled);
        Assert.Equal(80, s.HighBatteryWarningPct);
    }
}
