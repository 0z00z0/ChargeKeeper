using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure state-mapping tests for the Home Assistant publisher (TODO #28): which fields are "known",
// plus the off-state semantics — Smart Charge off → stop 100, start unavailable, smart_charge false.
public class HaStateBuilderTests
{
    [Fact]
    public void Build_OnAc_SmartChargeOn_IncludesThresholdsWattsAndFlag()
    {
        var s = HaStateBuilder.Build(soc: 72, chargeRateMw: 45000, onAc: true,
            new ChargeThresholdState(Capable: true, Enabled: true, Start: 60, Stop: 80), adapterWatts: 65);

        Assert.Equal(72, s.Soc);
        Assert.Equal(45000, s.PowerMw);
        Assert.True(s.OnAc);
        Assert.True(s.SmartChargeEnabled);
        Assert.Equal(60, s.ChargeStart);
        Assert.Equal(80, s.ChargeStop);
        Assert.Equal(65, s.AdapterWatts);
    }

    [Fact]
    public void Build_SmartChargeDisabled_Stop100_StartUnavailable_FlagFalse()
    {
        var s = HaStateBuilder.Build(50, -18000, false,
            new ChargeThresholdState(Capable: true, Enabled: false, Start: 60, Stop: 80), null);
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);        // omitted → HA "unknown/unavailable"
        Assert.Equal(100, s.ChargeStop);   // charging allowed all the way to full
    }

    [Fact]
    public void Build_NotCapable_SameAsOff()
    {
        var s = HaStateBuilder.Build(50, 0, true,
            new ChargeThresholdState(Capable: false, Enabled: true, Start: 60, Stop: 80), 65);
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);
        Assert.Equal(100, s.ChargeStop);
    }

    [Fact]
    public void Build_NullThreshold_SameAsOff()
    {
        var s = HaStateBuilder.Build(50, 0, true, threshold: null, adapterWatts: 65);
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);
        Assert.Equal(100, s.ChargeStop);
    }

    [Fact]
    public void Build_TravelOverrideActive_LooksLikeDisabled_Stop100_StartUnavailable()
    {
        // The "charge to 100% once" travel override activates by calling
        // ChargeThresholdService.SetEnabled(false), so the live threshold read (_lastThresholdState)
        // comes back Enabled: false while the override is active — this Enabled:false state is exactly
        // what HaStateBuilder sees. Expected: Smart Charge off, Charge stop 100, Charge start unavailable.
        var s = HaStateBuilder.Build(90, 30000, onAc: true,
            new ChargeThresholdState(Capable: true, Enabled: false, Start: 60, Stop: 80), adapterWatts: 65);
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);
        Assert.Equal(100, s.ChargeStop);
    }

    [Fact]
    public void Build_OnBattery_OmitsWatts_ButKeepsThresholds()
    {
        // A stale adapter reading must never be published while on battery; the Smart Charge
        // thresholds are configuration, not AC-dependent, so they still publish.
        var s = HaStateBuilder.Build(50, -12000, onAc: false,
            new ChargeThresholdState(true, true, 60, 80), adapterWatts: 65);
        Assert.Null(s.AdapterWatts);
        Assert.True(s.SmartChargeEnabled);
        Assert.Equal(60, s.ChargeStart);
        Assert.Equal(80, s.ChargeStop);
    }

    [Fact]
    public void Build_ZeroThresholdValues_TreatedAsOff()
    {
        // Enabled but with 0/0 thresholds is not a valid Smart Charge config → treated as off.
        var s = HaStateBuilder.Build(50, 0, true,
            new ChargeThresholdState(Capable: true, Enabled: true, Start: 0, Stop: 0), null);
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);
        Assert.Equal(100, s.ChargeStop);
    }
}
