using System.Collections.Generic;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// After an MQTT threshold or smart-charge command, HomeAssistantService reads ChargeThresholdService
// fresh and republishes through HaStateBuilder.ApplyChargeControl. These pin that the mapping
// reflects the post-command truth rather than an optimistic value; the MQTT I/O is not covered here.
public class HomeAssistantServiceTests
{
    private static List<ThresholdPreset> Presets() =>
    [
        new ThresholdPreset("Daily",  60, 80),
        new ThresholdPreset("Travel", 55, 75),
    ];

    private static HaState BaseState() => new(
        Soc: 50, BatteryState: HaDiscovery.StateCharging, LowPowerMode: false, PowerMw: 12000,
        IsCharging: true, OnAc: true, Health: "Good", RemainingMinutes: 30,
        SmartChargeEnabled: false, ChargeStart: null, ChargeStop: 100, AdapterWatts: 65,
        ActivePreset: null);

    [Fact]
    public void Reflect_AfterThresholdWrite_ShowsSmartChargeOnWithAppliedValues()
    {
        // A freshly-applied 55–75 threshold read back Enabled from the device — Travel's range.
        var fresh = new ChargeThresholdState(Capable: true, Enabled: true, Start: 55, Stop: 75);
        var reflected = HaStateBuilder.ApplyChargeControl(BaseState(), fresh, Presets());

        Assert.True(reflected.SmartChargeEnabled);
        Assert.Equal(55, reflected.ChargeStart);
        Assert.Equal(75, reflected.ChargeStop);
        Assert.Equal("Travel", reflected.ActivePreset);
        // Battery fields untouched by the charge-control overlay.
        Assert.Equal(50, reflected.Soc);
        Assert.Equal(65, reflected.AdapterWatts);
    }

    [Fact]
    public void Reflect_AfterSmartChargeOff_ShowsOff_Stop100_StartOmitted()
    {
        var fresh = new ChargeThresholdState(Capable: true, Enabled: false, Start: 0, Stop: 0);
        var reflected = HaStateBuilder.ApplyChargeControl(BaseState(), fresh, Presets());

        Assert.False(reflected.SmartChargeEnabled);
        Assert.Null(reflected.ChargeStart);
        Assert.Equal(100, reflected.ChargeStop);
        Assert.Null(reflected.ActivePreset);
    }
}
