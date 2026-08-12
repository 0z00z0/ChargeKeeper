using ChargeKeeper.Vendors.Lenovo;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Contract-shape tests for the Lenovo module.
///
/// Hardware-free by design: these assert only what the module *claims* about itself, never
/// calling <c>Read</c>/<c>SetEnabled</c>/<c>SetThresholds</c>, which P/Invoke the native
/// <c>LenPower.dll</c> bridge and would write to real firmware.
/// </summary>
public class LenovoChargeThresholdTests
{
    [Fact]
    public void Module_ReportsLenovoAsVendorName()
    {
        Assert.Equal("Lenovo", new LenovoPowerModule().VendorName);
    }

    [Fact]
    public void Module_SupportsNumericThresholds()
    {
        // Lenovo firmware takes a real start/stop pair. If this ever flips, the dashboard hides
        // the percentage picker on hardware that can actually use it.
        Assert.True(new LenovoPowerModule().ChargeThreshold.SupportsNumericThresholds);
    }

    [Fact]
    public void ModesAndNumericThresholds_AreMutuallyExclusive()
    {
        // Numeric vendor => no discrete modes. Same invariant asserted from the HP side.
        var lenovo = new LenovoPowerModule().ChargeThreshold;

        Assert.True(lenovo.SupportsNumericThresholds);
        Assert.Empty(lenovo.AvailableModes);
    }

    [Fact]
    public void ModeApi_IsInertOnANumericVendor()
    {
        // The mode calls exist because the interface requires them, but must do nothing here —
        // and SetMode in particular must not reach the device.
        var lenovo = new LenovoPowerModule().ChargeThreshold;

        Assert.Null(lenovo.ReadMode());
        Assert.False(lenovo.SetMode("anything"));
    }

    [Fact]
    public void Module_ClaimsStandbySupport()
    {
        // Lenovo is the vendor that has LenovoSmartStandby; this is what keeps the Smart Standby
        // toggle visible on ThinkPads now that availability is vendor-driven.
        Assert.True(new LenovoPowerModule().Standby.IsSupported);
    }

    [Fact]
    public void Module_ProvidersAreNonNull()
    {
        var lenovo = new LenovoPowerModule();

        Assert.NotNull(lenovo.ChargeThreshold);
        Assert.NotNull(lenovo.Standby);
        Assert.NotNull(lenovo.ChargerInfo);
    }
}
