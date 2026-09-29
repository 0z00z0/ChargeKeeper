using System.Collections.Generic;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Windows.System.Power;
using Xunit;

namespace ChargeKeeper.Tests;

// What the Home Assistant publisher states about Smart Charge while a charge-to-full lift is in force.
public class LiveStateBuilderTests
{
    // Convenience wrapper so each test only sets the fields it cares about.
    private static LiveState Build(int soc = 72, int rateMw = 45000, bool onAc = true,
        BatteryStatus status = BatteryStatus.Charging, ChargeThresholdState? threshold = null,
        int? adapterWatts = 65, int? remainingMwh = 40000, int? fullMwh = 60000, int? designMwh = 60000,
        bool lowPowerMode = false, IReadOnlyList<ThresholdPreset>? presets = null)
        => LiveStateBuilder.Build(soc, rateMw, onAc, status, threshold, adapterWatts,
            remainingMwh, fullMwh, designMwh, lowPowerMode, presets);

    [Fact]
    public void Build_UnderAChargeToFullLift_PublishesTheParkedThresholds_NotSmartChargeOff()
    {
        // Lifting the cap disables it at the firmware, so the device reads Enabled:false for the
        // length of one charge. Published as it stands, a one-charge lift reads as Smart Charge
        // switched off for good — which is what the parked pair exists to prevent.
        var live   = new ChargeThresholdState(Capable: true, Enabled: false, Start: 0, Stop: 0);
        var shown  = ChargeThresholdView.Shown(live, parked: (60, 80));

        var s = Build(soc: 90, rateMw: 30000, threshold: shown);

        Assert.True(s.SmartChargeEnabled);
        Assert.Equal(60,  s.ChargeStart);
        Assert.Equal(80,  s.ChargeStop);
    }
}
