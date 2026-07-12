using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

public class BatteryStatsFormatterTests
{
    [Fact]
    public void FormatPowerSource_OnAC_NoWattageKnown_ShowsPlainLabel()
    {
        Assert.Equal("AC Power", BatteryStatsFormatter.FormatPowerSource(onAC: true, chargeRateMw: 0, adapterWattage: null));
    }

    [Fact]
    public void FormatPowerSource_OnAC_WithWattage_IncludesCharger()
    {
        var text = BatteryStatsFormatter.FormatPowerSource(onAC: true, chargeRateMw: 0, adapterWattage: 60);
        Assert.Equal("AC Power (60W charger)", text);
    }

    [Fact]
    public void FormatPowerSource_OnAC_PositiveRate_AppendsRate()
    {
        // Expected rate text derived from PowerFormat itself (not hand-typed) so this test doesn't
        // depend on getting PowerFormat's real-minus-sign (U+2212) glyph exactly right by hand.
        var text = BatteryStatsFormatter.FormatPowerSource(onAC: true, chargeRateMw: 45000, adapterWattage: 65);
        Assert.Equal($"AC Power (65W charger)  ·  {PowerFormat.SignedRate(45000)}", text);
    }

    [Fact]
    public void FormatPowerSource_OnBattery_NegativeRate_AppendsRate()
    {
        var text = BatteryStatsFormatter.FormatPowerSource(onAC: false, chargeRateMw: -18000, adapterWattage: null);
        Assert.Equal($"Battery  ·  {PowerFormat.SignedRate(-18000)}", text);
    }

    [Fact]
    public void FormatPowerSource_OnBattery_WattageIgnored_NeverShownWhenDischarging()
    {
        // A stale/leftover adapterWattage argument must never leak into the "Battery" label —
        // only onAC controls whether it's shown at all.
        var text = BatteryStatsFormatter.FormatPowerSource(onAC: false, chargeRateMw: 0, adapterWattage: 60);
        Assert.Equal("Battery", text);
    }

    [Theory]
    [InlineData(true, -100)]  // charging (onAC) but rate is negative — wrong direction, omit
    [InlineData(false, 100)]  // discharging (!onAC) but rate is positive — wrong direction, omit
    public void FormatPowerSource_RateInUnexpectedDirection_Omitted(bool onAC, int rateMw)
    {
        var text = BatteryStatsFormatter.FormatPowerSource(onAC, rateMw, adapterWattage: null);
        Assert.DoesNotContain("·", text);
    }

    [Fact]
    public void FormatTimeRemaining_NullRate_ReturnsDash()
    {
        Assert.Equal("—", BatteryStatsFormatter.FormatTimeRemaining(null, 5000, 10000));
    }

    [Fact]
    public void FormatTimeRemaining_RateBelowNoiseFloor_ReturnsDash()
    {
        // |rate| < 100 mW is treated as "not really charging/discharging" — same floor DashboardWindow used before extraction.
        Assert.Equal("—", BatteryStatsFormatter.FormatTimeRemaining(50, 5000, 10000));
    }

    [Fact]
    public void FormatTimeRemaining_NullRemaining_ReturnsDash()
    {
        Assert.Equal("—", BatteryStatsFormatter.FormatTimeRemaining(1000, null, 10000));
    }

    [Fact]
    public void FormatTimeRemaining_Charging_SaysToFull()
    {
        // 5000 mWh left to fill a 10000 mWh battery at 5000 mW ⇒ 1 hour to full.
        var text = BatteryStatsFormatter.FormatTimeRemaining(5000, 5000, 10000);
        Assert.Equal("~1h 0m to full", text);
    }

    [Fact]
    public void FormatTimeRemaining_Charging_NoFullCapacityKnown_ReturnsDash()
    {
        Assert.Equal("—", BatteryStatsFormatter.FormatTimeRemaining(5000, 5000, null));
    }

    [Fact]
    public void FormatTimeRemaining_Discharging_SaysRemaining()
    {
        // 4000 mWh remaining, draining at 2000 mW ⇒ 2 hours remaining. FullChargeMwh is irrelevant
        // on the discharge path and must not be required.
        var text = BatteryStatsFormatter.FormatTimeRemaining(-2000, 4000, null);
        Assert.Equal("~2h 0m remaining", text);
    }

    [Fact]
    public void FormatHours_UnderAnHour_OmitsHourUnit()
    {
        Assert.Equal("~45m remaining", BatteryStatsFormatter.FormatHours(0.75, chargingDirection: false));
    }

    [Fact]
    public void FormatHours_Over99Hours_ShowsCappedLabel()
    {
        Assert.Equal(">99h", BatteryStatsFormatter.FormatHours(150, chargingDirection: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FormatHours_NonPositive_ReturnsDash(double hours)
    {
        Assert.Equal("—", BatteryStatsFormatter.FormatHours(hours, chargingDirection: true));
    }

    [Fact]
    public void FormatHours_NaN_ReturnsDash()
    {
        Assert.Equal("—", BatteryStatsFormatter.FormatHours(double.NaN, chargingDirection: false));
    }
}
