using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

public class ChargeThresholdViewTests
{
    [Fact]
    public void UnderALift_TheSwitchReadsOnAndTheParkedPresetIsMarked_WhileTheEditorsStayHidden()
    {
        var presets = new[] { new ThresholdPreset("Daily", 60, 80), new ThresholdPreset("Docking", 45, 55) };
        var live    = new ChargeThresholdState(Capable: true, Enabled: false, Start: 45, Stop: 55);

        var display = ChargeThresholdView.ForDisplay(live, parked: (45, 55));

        Assert.True(display.Shown!.Enabled);
        Assert.Equal("Docking", ActivePresetPolicy.Match(presets, display.Shown)?.Name);
        Assert.False(display.OffersThresholdEdit);
    }
}
