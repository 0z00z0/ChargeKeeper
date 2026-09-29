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
    public void Charging_PausesTheTargetWithoutGivingItUp()
    {
        // A pack gaining charge cannot come down to a target below it while the charger stays in,
        // and the target is still wanted once the charger comes out.
        var watch = Armed(50);
        Assert.Equal(LidDischargeDecision.Charging, watch.OnReading(percent: 80, isCharging: true));
        Assert.True(watch.IsWatching);
    }
}
