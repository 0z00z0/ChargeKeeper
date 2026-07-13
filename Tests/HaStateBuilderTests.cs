using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure state-mapping tests for the Home Assistant publisher (TODO #28): which fields are "known".
public class HaStateBuilderTests
{
    [Fact]
    public void Build_OnAc_SmartChargeOn_IncludesThresholdsAndWatts()
    {
        var s = HaStateBuilder.Build(soc: 72, chargeRateMw: 45000, onAc: true,
            new ChargeThresholdState(Capable: true, Enabled: true, Start: 60, Stop: 80), adapterWatts: 65);

        Assert.Equal(72, s.Soc);
        Assert.Equal(45000, s.PowerMw);
        Assert.True(s.OnAc);
        Assert.Equal(60, s.ChargeStart);
        Assert.Equal(80, s.ChargeStop);
        Assert.Equal(65, s.AdapterWatts);
    }

    [Fact]
    public void Build_SmartChargeDisabled_OmitsThresholds()
    {
        var s = HaStateBuilder.Build(50, -18000, false,
            new ChargeThresholdState(Capable: true, Enabled: false, Start: 60, Stop: 80), null);
        Assert.Null(s.ChargeStart);
        Assert.Null(s.ChargeStop);
    }

    [Fact]
    public void Build_NotCapable_OmitsThresholds()
    {
        var s = HaStateBuilder.Build(50, 0, true,
            new ChargeThresholdState(Capable: false, Enabled: true, Start: 60, Stop: 80), 65);
        Assert.Null(s.ChargeStart);
        Assert.Null(s.ChargeStop);
    }

    [Fact]
    public void Build_NullThreshold_OmitsThresholds()
    {
        var s = HaStateBuilder.Build(50, 0, true, threshold: null, adapterWatts: 65);
        Assert.Null(s.ChargeStart);
        Assert.Null(s.ChargeStop);
    }

    [Fact]
    public void Build_OnBattery_OmitsWatts_ButKeepsThresholds()
    {
        // A stale adapter reading must never be published while on battery; the Smart Charge
        // thresholds are configuration, not AC-dependent, so they still publish.
        var s = HaStateBuilder.Build(50, -12000, onAc: false,
            new ChargeThresholdState(true, true, 60, 80), adapterWatts: 65);
        Assert.Null(s.AdapterWatts);
        Assert.Equal(60, s.ChargeStart);
        Assert.Equal(80, s.ChargeStop);
    }

    [Fact]
    public void Build_ZeroThresholdValues_Omitted()
    {
        var s = HaStateBuilder.Build(50, 0, true,
            new ChargeThresholdState(Capable: true, Enabled: true, Start: 0, Stop: 0), null);
        Assert.Null(s.ChargeStart);
        Assert.Null(s.ChargeStop);
    }
}
