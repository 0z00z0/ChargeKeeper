using ChargeKeeper.Vendors.Lenovo;
using Microsoft.Win32;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Contract-shape tests for the Lenovo module: only what the module claims about itself, never
/// <c>Read</c>/<c>SetEnabled</c>/<c>SetThresholds</c>, which P/Invoke the native
/// <c>LenPower.dll</c> bridge and would write real firmware.
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
        // The mode calls exist because the interface requires them; SetMode in particular must not
        // reach the device.
        var lenovo = new LenovoPowerModule().ChargeThreshold;

        Assert.Null(lenovo.ReadMode());
        Assert.False(lenovo.SetMode("anything"));
    }

    [Fact]
    public void Module_ReportsStandbySupportOnlyWhenTheServiceIsInstalled()
    {
        // VendorCatalog falls back to the Lenovo module when no vendor answers, so IsSupported runs
        // on non-Lenovo hardware too and must follow the machine rather than return a constant.
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\LenovoSmartStandby");

        Assert.Equal(key is not null, new LenovoPowerModule().Standby.IsSupported);
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
