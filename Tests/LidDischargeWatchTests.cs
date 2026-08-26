using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The lid-close discharge target: state and rules only, no timer and no power scheme, so the
// behaviour is exercised here without the OS or the Settings window.
public class LidDischargeWatchTests
{
    private static LidDischargeWatch Armed(int target)
    {
        var watch = new LidDischargeWatch();
        watch.Arm(target);
        return watch;
    }

    [Fact]
    public void NoTargetArmed_AReadingDecidesNothing()
    {
        Assert.Equal(LidDischargeDecision.NotWatching,
            new LidDischargeWatch().OnReading(percent: 80, isCharging: false));
    }

    [Fact]
    public void AboveTheTarget_HoldsTheMachineAwake()
    {
        Assert.Equal(LidDischargeDecision.Hold, Armed(50).OnReading(percent: 51, isCharging: false));
    }

    [Fact]
    public void AtTheTarget_ReleasesTheMachineToSleep()
    {
        // The target is a level to reach, not one to pass: 50 % with a target of 50 is done.
        Assert.Equal(LidDischargeDecision.TargetReached, Armed(50).OnReading(percent: 50, isCharging: false));
    }

    [Fact]
    public void BelowTheTarget_ReleasesTheMachineToSleep()
    {
        Assert.Equal(LidDischargeDecision.TargetReached, Armed(50).OnReading(percent: 42, isCharging: false));
    }

    [Fact]
    public void DrainingTowardsTheTarget_HoldsUntilItArrives()
    {
        var watch = Armed(50);
        Assert.Equal(LidDischargeDecision.Hold, watch.OnReading(70, isCharging: false));
        Assert.Equal(LidDischargeDecision.Hold, watch.OnReading(60, isCharging: false));
        Assert.Equal(LidDischargeDecision.Hold, watch.OnReading(51, isCharging: false));
        Assert.Equal(LidDischargeDecision.TargetReached, watch.OnReading(50, isCharging: false));
    }

    [Fact]
    public void Charging_GivesTheTargetUpRatherThanHoldingForALevelThatWillNeverArrive()
    {
        // A pack gaining charge cannot reach a target below it, and a hold waiting for one would
        // never end.
        Assert.Equal(LidDischargeDecision.Charging, Armed(50).OnReading(percent: 80, isCharging: true));
    }

    [Fact]
    public void ChargingBelowTheTarget_StillReadsAsTargetReached()
    {
        // The level is the stop condition, so a battery already at or under the target is done
        // whichever way it happens to be moving.
        Assert.Equal(LidDischargeDecision.TargetReached, Armed(50).OnReading(percent: 30, isCharging: true));
    }

    [Fact]
    public void UnderpoweredCharger_KeepsWaitingBecauseTheBatteryIsStillDraining()
    {
        // The case a "power is connected" test gets wrong: connected power delivering less than the
        // machine draws leaves the battery discharging, and that machine must keep waiting.
        Assert.Equal(LidDischargeDecision.Hold, Armed(50).OnReading(percent: 70, isCharging: false));
    }

    [Fact]
    public void AReleasedWatch_DecidesNothingFurther()
    {
        var watch = Armed(50);
        Assert.Equal(LidDischargeDecision.TargetReached, watch.OnReading(50, isCharging: false));
        Assert.Equal(LidDischargeDecision.NotWatching, watch.OnReading(40, isCharging: false));
        Assert.False(watch.IsWatching);
    }

    [Fact]
    public void AGivenUpWatch_DecidesNothingFurther()
    {
        var watch = Armed(50);
        Assert.Equal(LidDischargeDecision.Charging, watch.OnReading(80, isCharging: true));
        Assert.Equal(LidDischargeDecision.NotWatching, watch.OnReading(70, isCharging: false));
    }

    [Fact]
    public void DisarmedMidWatch_StopsDecidingImmediately()
    {
        // What a lid reopening does: the wait is abandoned, and the readings that follow belong to a
        // machine the user is sitting in front of.
        var watch = Armed(50);
        Assert.Equal(LidDischargeDecision.Hold, watch.OnReading(70, isCharging: false));
        watch.Disarm();
        Assert.False(watch.IsWatching);
        Assert.Equal(LidDischargeDecision.NotWatching, watch.OnReading(50, isCharging: false));
    }

    [Fact]
    public void ReClosingTheLidAfterAReopen_StartsAFreshWatch()
    {
        var watch = Armed(50);
        watch.Disarm();
        watch.Arm(50);
        Assert.True(watch.IsWatching);
        Assert.Equal(LidDischargeDecision.Hold, watch.OnReading(70, isCharging: false));
    }

    [Fact]
    public void ReArming_ReplacesTheOutstandingTargetRatherThanStackingASecond()
    {
        var watch = Armed(50);
        watch.Arm(70);
        Assert.Equal(70, watch.Target);
        Assert.Equal(LidDischargeDecision.TargetReached, watch.OnReading(65, isCharging: false));
    }

    [Fact]
    public void IsWatching_IsFalseUntilArmed()
    {
        var watch = new LidDischargeWatch();
        Assert.False(watch.IsWatching);
        Assert.Null(watch.Target);
    }

    [Fact]
    public void ATargetAboveTheAllowedRange_IsClampedToWhatWillActuallyApply()
    {
        // Only a hand-edited settings.json gets here; 100 % would be met the instant the watch armed.
        var watch = Armed(120);
        Assert.Equal(LidDischargeWatch.MaxPercent, watch.Target);
    }

    [Fact]
    public void ATargetBelowTheAllowedRange_IsClampedToWhatWillActuallyApply()
    {
        var watch = Armed(0);
        Assert.Equal(LidDischargeWatch.MinPercent, watch.Target);
        // A flat battery is not a target; the clamp is what stops the hold running the pack down.
        Assert.Equal(LidDischargeDecision.Hold, watch.OnReading(LidDischargeWatch.MinPercent + 1, isCharging: false));
    }

    [Fact]
    public void Clamp_LeavesAnInRangeTargetAlone()
    {
        Assert.Equal(50, LidDischargeWatch.Clamp(50));
    }
}
