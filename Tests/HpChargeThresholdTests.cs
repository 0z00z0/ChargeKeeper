using ChargeKeeper.Vendors.Hp;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// HP vendor module decision logic, exercised through the pure mapping helpers rather than
/// <c>Read</c>/<c>SetThresholds</c>, which talk to <c>root\HP\InstrumentedBIOS</c>. The split lets
/// these run anywhere, and keeps a test from changing real firmware settings on an HP machine.
/// </summary>
public class HpChargeThresholdTests
{
    // TryMapToLimiting: numeric request → coarse mode

    [Theory]
    [InlineData(90, 80)]    // inverted
    public void TryMapToLimiting_InvalidRange_Rejected(int start, int stop)
    {
        // Rejection has to happen before any firmware contact, which is why this is a pure function.
        Assert.False(HpChargeThreshold.TryMapToLimiting(start, stop, out _));
    }

    [Theory]
    [InlineData(0, 80)]
    public void TryMapToLimiting_BelowNearFull_SnapsToLimiting(int start, int stop)
    {
        Assert.True(HpChargeThreshold.TryMapToLimiting(start, stop, out bool limiting));
        Assert.True(limiting);
    }

    // Discrete charge modes

    [Theory]
    [InlineData("Maximize Battery Health")]   // close but not the firmware's exact spelling
    public void SetMode_UnknownId_RejectedWithoutFirmwareContact(string id)
    {
        // Must return false on the id check alone: reaching HpBios.SetSetting would write real
        // firmware on an HP machine.
        Assert.False(new HpPowerModule().ChargeThreshold.SetMode(id));
    }
}
