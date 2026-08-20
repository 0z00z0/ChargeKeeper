using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

public class ThresholdCapabilityPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Classify_NoVendorAnswer_Hidden(bool supportsNumeric)
    {
        // Read() returning null is the ONE "is this working" signal the provider contract gives:
        // driver missing, unsupported hardware, transport error. Nothing to configure, nothing to
        // read back — so neither surface, and no permanent "Unavailable" badge either.
        Assert.Equal(
            SmartChargeSurface.Hidden,
            ThresholdCapabilityPolicy.Classify(null, supportsNumeric));
    }

    [Fact]
    public void Classify_CapableNumericVendor_Numeric()
    {
        // Lenovo: a real start/stop pair, so presets and network profiles mean something.
        var state = new ChargeThresholdState(Capable: true, Enabled: true, Start: 75, Stop: 80);

        Assert.Equal(
            SmartChargeSurface.Numeric,
            ThresholdCapabilityPolicy.Classify(state, supportsNumeric: true));
    }

    [Fact]
    public void Classify_CapableModeVendor_FixedModes()
    {
        // HP: three coarse BIOS modes and no percentage at all.
        var state = new ChargeThresholdState(Capable: true, Enabled: true, Start: 0, Stop: 80);

        Assert.Equal(
            SmartChargeSurface.FixedModes,
            ThresholdCapabilityPolicy.Classify(state, supportsNumeric: false));
    }

    [Fact]
    public void Classify_ReadOnlyModeVendor_StillFixedModes_NotHidden()
    {
        // HP's read-only BIOS case: the setting is readable but refuses writes. The hardware HAS
        // the feature, so hiding the surface would read as a detection bug rather than a locked
        // setting; the surface stays and the caller adds the read-only note.
        var state = new ChargeThresholdState(Capable: false, Enabled: true, Start: 0, Stop: 80);

        Assert.Equal(
            SmartChargeSurface.FixedModes,
            ThresholdCapabilityPolicy.Classify(state, supportsNumeric: false));
    }

    [Fact]
    public void Classify_ReadOnlyNumericVendor_StillNumeric_NotHidden()
    {
        // Same rule on the numeric side: a readable state from a vendor that takes percentages
        // keeps the percentage surface even when the firmware will not accept a write.
        var state = new ChargeThresholdState(Capable: false, Enabled: false, Start: 0, Stop: 100);

        Assert.Equal(
            SmartChargeSurface.Numeric,
            ThresholdCapabilityPolicy.Classify(state, supportsNumeric: true));
    }
}
