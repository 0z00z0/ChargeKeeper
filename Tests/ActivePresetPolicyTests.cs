using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// The active preset is derived from the thresholds the device reports, never from a stored name,
// so these run over a plain preset list and a ChargeThresholdState — no settings, no vendor.
public class ActivePresetPolicyTests
{
    private static List<ThresholdPreset> TwoPresets() =>
    [
        new ThresholdPreset("Daily",  60, 80),
        new ThresholdPreset("Travel", 80, 100),
    ];

    private static ChargeThresholdState Limiting(int start, int stop) =>
        new(Capable: true, Enabled: true, Start: start, Stop: stop);

    [Fact]
    public void Match_ThresholdsEqualASecondPreset_ReturnsThatPreset()
    {
        // Deliberately the second entry: returning the head of the list would also pass on the first.
        var match = ActivePresetPolicy.Match(TwoPresets(), Limiting(80, 100));

        Assert.NotNull(match);
        Assert.Equal("Travel", match.Name);
    }

    [Fact]
    public void Match_ThresholdsMatchNoPreset_ReturnsNull()
    {
        Assert.Null(ActivePresetPolicy.Match(TwoPresets(), Limiting(50, 70)));
    }

    [Fact]
    public void Match_StopEqualButStartDiffers_ReturnsNull()
    {
        // Both ends are part of a preset. A comparison on Stop alone would call 70-80 "Daily".
        Assert.Null(ActivePresetPolicy.Match(TwoPresets(), Limiting(70, 80)));
    }

    [Fact]
    public void Match_StartEqualButStopDiffers_ReturnsNull()
    {
        Assert.Null(ActivePresetPolicy.Match(TwoPresets(), Limiting(60, 90)));
    }

    [Fact]
    public void Match_DuplicateValues_ReturnsTheFirstInListOrder()
    {
        List<ThresholdPreset> presets =
        [
            new ThresholdPreset("Desk", 60, 80),
            new ThresholdPreset("Home", 60, 80),
        ];

        var match = ActivePresetPolicy.Match(presets, Limiting(60, 80));

        Assert.NotNull(match);
        Assert.Equal("Desk", match.Name);
    }

    [Fact]
    public void Match_EmptyPresetList_ReturnsNull()
    {
        Assert.Null(ActivePresetPolicy.Match([], Limiting(60, 80)));
    }

    [Fact]
    public void Match_NullPresetList_ReturnsNull()
    {
        Assert.Null(ActivePresetPolicy.Match(null, Limiting(60, 80)));
    }

    [Fact]
    public void Match_NoVendorAnswer_ReturnsNull()
    {
        Assert.Null(ActivePresetPolicy.Match(TwoPresets(), null));
    }

    [Fact]
    public void Match_TravelOverrideActive_ReturnsNull_EvenWhenValuesStillEqualAPreset()
    {
        // The override disables Smart Charge and leaves the saved pair readable, so the values can
        // still equal a preset while the battery is deliberately charging past it.
        var overridden = new ChargeThresholdState(Capable: true, Enabled: false, Start: 60, Stop: 80);

        Assert.Null(ActivePresetPolicy.Match(TwoPresets(), overridden));
    }

    [Fact]
    public void Match_ModeBasedVendor_ReturnsNull()
    {
        // HP and Surface report Start as 0 by contract, so no percentage pair can be meant by it.
        var fixedMode = new ChargeThresholdState(Capable: true, Enabled: true, Start: 0, Stop: 80);

        Assert.Null(ActivePresetPolicy.Match(TwoPresets(), fixedMode));
    }
}
