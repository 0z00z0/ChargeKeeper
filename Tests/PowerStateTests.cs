using ChargeKeeper.Helpers;
using Windows.System.Power;
using Xunit;

namespace ChargeKeeper.Tests;

// The third state is not new sensing: Windows already separates "taking charge" from "connected and
// not". These pin the two derivations together, because the tray reads the status while the
// published entity reads the two flags built from it — a disagreement would colour the gauge one way
// and report the other.
public class PowerStateTests
{
    // PowerState is internal, so it cannot appear in a public theory signature; the cases run inside
    // each fact instead.

    [Fact]
    public void EveryBatteryStatus_MapsToOneState()
    {
        Assert.Equal(PowerState.Charging,    PowerStates.From(BatteryStatus.Charging));
        Assert.Equal(PowerState.IdleOnMains, PowerStates.From(BatteryStatus.Idle));
        Assert.Equal(PowerState.Discharging, PowerStates.From(BatteryStatus.Discharging));
        // The pre-first-report seed: no reading is not a reason to claim mains power.
        Assert.Equal(PowerState.Discharging, PowerStates.From(BatteryStatus.NotPresent));
    }

    [Fact]
    public void TheTwoPublishedFlags_GiveTheSameThreeStates()
    {
        Assert.Equal(PowerState.Charging,    PowerStates.From(isCharging: true,  onAc: true));
        Assert.Equal(PowerState.IdleOnMains, PowerStates.From(isCharging: false, onAc: true));
        Assert.Equal(PowerState.Discharging, PowerStates.From(isCharging: false, onAc: false));
    }

    [Fact]
    public void TheStatusAndTheFlags_NeverDisagree()
    {
        // The flags are built from the status, so every status has to reach the same answer by both
        // routes. IsOnAC is the app's own definition of "connected".
        foreach (var status in new[]
        {
            BatteryStatus.Charging, BatteryStatus.Idle, BatteryStatus.Discharging, BatteryStatus.NotPresent,
        })
        {
            var viaFlags = PowerStates.From(
                status == BatteryStatus.Charging, BatteryStatsFormatter.IsOnAC(status));
            Assert.Equal(PowerStates.From(status), viaFlags);
        }
    }

    [Fact]
    public void EveryState_PublishesItsOwnLabel()
    {
        Assert.Equal("Charging",      PowerStates.Label(PowerState.Charging));
        Assert.Equal("Idle on mains", PowerStates.Label(PowerState.IdleOnMains));
        Assert.Equal("Discharging",   PowerStates.Label(PowerState.Discharging));
    }

    [Fact]
    public void OnAcCoversBothMainsStates_AndNeitherIsOnBattery()
    {
        Assert.True(BatteryStatsFormatter.IsOnAC(BatteryStatus.Charging));
        Assert.True(BatteryStatsFormatter.IsOnAC(BatteryStatus.Idle));
        Assert.False(BatteryStatsFormatter.IsOnAC(BatteryStatus.Discharging));
    }
}
