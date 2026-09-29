using ChargeKeeper.Vendors.Lenovo;
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
    public void ModeApi_IsInertOnANumericVendor()
    {
        // The mode calls exist because the interface requires them; SetMode in particular must not
        // reach the device.
        var lenovo = new LenovoPowerModule().ChargeThreshold;

        Assert.Null(lenovo.ReadMode());
        Assert.False(lenovo.SetMode("anything"));
    }
}
