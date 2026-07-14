using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Windows.System.Power;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure state-mapping tests for the Home Assistant publisher (TODO #28/#29): HA mobile-app-aligned
// battery state / health / remaining-charge-time derivations, plus the Smart Charge off-state
// semantics (stop 100, start unavailable, smart_charge false).
public class HaStateBuilderTests
{
    // Convenience wrapper so each test only sets the fields it cares about.
    private static HaState Build(int soc = 72, int rateMw = 45000, bool onAc = true,
        BatteryStatus status = BatteryStatus.Charging, ChargeThresholdState? threshold = null,
        int? adapterWatts = 65, int? remainingMwh = 40000, int? fullMwh = 60000, int? designMwh = 60000,
        bool lowPowerMode = false, string? activePreset = null)
        => HaStateBuilder.Build(soc, rateMw, onAc, status, threshold, adapterWatts,
            remainingMwh, fullMwh, designMwh, lowPowerMode, activePreset);

    [Fact]
    public void Build_OnAc_SmartChargeOn_IncludesThresholdsWattsAndFlag()
    {
        var s = Build(threshold: new ChargeThresholdState(Capable: true, Enabled: true, Start: 60, Stop: 80));

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
        var s = Build(soc: 50, rateMw: -18000, onAc: false, status: BatteryStatus.Discharging,
            threshold: new ChargeThresholdState(Capable: true, Enabled: false, Start: 60, Stop: 80),
            adapterWatts: null);
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);        // omitted → HA "unknown/unavailable"
        Assert.Equal(100, s.ChargeStop);   // charging allowed all the way to full
    }

    [Fact]
    public void Build_NotCapable_SameAsOff()
    {
        var s = Build(soc: 50, rateMw: 0, status: BatteryStatus.Idle,
            threshold: new ChargeThresholdState(Capable: false, Enabled: true, Start: 60, Stop: 80));
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);
        Assert.Equal(100, s.ChargeStop);
    }

    [Fact]
    public void Build_TravelOverrideActive_LooksLikeDisabled_Stop100_StartUnavailable()
    {
        // The "charge to 100 % once" override activates by calling SetEnabled(false), so the live
        // threshold read comes back Enabled:false — exactly what the builder sees.
        var s = Build(soc: 90, rateMw: 30000,
            threshold: new ChargeThresholdState(Capable: true, Enabled: false, Start: 60, Stop: 80));
        Assert.False(s.SmartChargeEnabled);
        Assert.Null(s.ChargeStart);
        Assert.Equal(100, s.ChargeStop);
    }

    [Fact]
    public void Build_OnBattery_OmitsWatts_ButKeepsThresholds()
    {
        var s = Build(soc: 50, rateMw: -12000, onAc: false, status: BatteryStatus.Discharging,
            threshold: new ChargeThresholdState(true, true, 60, 80));
        Assert.Null(s.AdapterWatts);
        Assert.True(s.SmartChargeEnabled);
        Assert.Equal(60, s.ChargeStart);
        Assert.Equal(80, s.ChargeStop);
    }

    // ── Issue #29: battery state ─────────────────────────────────────────────────

    [Fact]
    public void Build_Charging_StateIsCharging_IsChargingTrue()
    {
        var s = Build(soc: 70, status: BatteryStatus.Charging);
        Assert.Equal(HaDiscovery.StateCharging, s.BatteryState);
        Assert.True(s.IsCharging);
    }

    [Fact]
    public void Build_Full_StateIsFull_NotCharging()
    {
        var s = Build(soc: 100, status: BatteryStatus.Idle, rateMw: 0);
        Assert.Equal(HaDiscovery.StateFull, s.BatteryState);
        Assert.False(s.IsCharging);
    }

    [Fact]
    public void Build_IdleBelowFull_ThresholdHeld_ReadsNotCharging_NotFull()
    {
        // Held at an 80 % threshold: Idle but NOT full → "Not Charging", not "Full".
        var s = Build(soc: 80, status: BatteryStatus.Idle, rateMw: 0);
        Assert.Equal(HaDiscovery.StateNotCharging, s.BatteryState);
        Assert.False(s.IsCharging);
    }

    [Fact]
    public void Build_Discharging_ReadsNotCharging()
    {
        var s = Build(soc: 55, status: BatteryStatus.Discharging, rateMw: -15000);
        Assert.Equal(HaDiscovery.StateNotCharging, s.BatteryState);
        Assert.False(s.IsCharging);
    }

    [Fact]
    public void Build_PassesThroughLowPowerModeAndActivePreset()
    {
        var s = Build(lowPowerMode: true, activePreset: "Daily");
        Assert.True(s.LowPowerMode);
        Assert.Equal("Daily", s.ActivePreset);
    }

    // ── Issue #29: health ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(60000, 60000, "Good")]     // 100 % of design
    [InlineData(48000, 60000, "Good")]     // 80 %
    [InlineData(42000, 60000, "Degraded")] // 70 %
    [InlineData(30000, 60000, "Poor")]     // 50 %
    public void DeriveHealth_MapsWearRatio(int fullMwh, int designMwh, string expected)
    {
        Assert.Equal(expected, HaStateBuilder.DeriveHealth(fullMwh, designMwh));
    }

    [Theory]
    [InlineData(null, 60000)]
    [InlineData(48000, null)]
    [InlineData(48000, 0)]
    public void DeriveHealth_UnknownWhenCapacityMissing(int? fullMwh, int? designMwh)
    {
        Assert.Null(HaStateBuilder.DeriveHealth(fullMwh, designMwh));
    }

    // ── Issue #29: remaining charge time ─────────────────────────────────────────

    [Fact]
    public void RemainingMinutes_ChargingHalfWay_ComputesMinutesToFull()
    {
        // 30 Wh to add (60000-30000 mWh) at 45000 mW ≈ 0.667 h ≈ 40 min.
        int? min = HaStateBuilder.RemainingMinutesToFull(isCharging: true, chargeRateMw: 45000,
            remainingMwh: 30000, fullMwh: 60000);
        Assert.Equal(40, min);
    }

    [Fact]
    public void RemainingMinutes_NullWhenNotCharging()
    {
        Assert.Null(HaStateBuilder.RemainingMinutesToFull(false, -15000, 30000, 60000));
    }

    [Fact]
    public void RemainingMinutes_NullWhenRateNegligible()
    {
        Assert.Null(HaStateBuilder.RemainingMinutesToFull(true, 50, 30000, 60000));
    }

    [Fact]
    public void Build_Charging_SurfacesRemainingMinutes_Discharging_Omits()
    {
        var charging = Build(status: BatteryStatus.Charging, rateMw: 45000, remainingMwh: 30000, fullMwh: 60000);
        Assert.NotNull(charging.RemainingMinutes);

        var discharging = Build(status: BatteryStatus.Discharging, rateMw: -15000, remainingMwh: 30000, fullMwh: 60000);
        Assert.Null(discharging.RemainingMinutes);
    }
}
