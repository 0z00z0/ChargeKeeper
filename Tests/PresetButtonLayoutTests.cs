using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

public class PresetButtonLayoutTests
{
    // The width PresetButtonPanel gets in the dashboard popup: 340 less RootGrid's padding either
    // side and the Smart Charge badge's own.
    private const double PanelWidth = 280;

    [Fact]
    public void Choose_ThreePresets_FitOnOneRow()
    {
        Assert.Equal((3, 1), PresetButtonLayout.Choose(3, PanelWidth));
    }

    [Fact]
    public void Choose_MorePresetsThanFit_Wraps()
    {
        Assert.Equal((3, 2), PresetButtonLayout.Choose(5, PanelWidth));
        Assert.Equal((3, 3), PresetButtonLayout.Choose(7, PanelWidth));
    }

    [Fact]
    public void Choose_FourPresets_WrapsRatherThanShrinkBelowTheMinimum()
    {
        // Four columns here would be 67 px each, under MinButtonWidth — the case that decides
        // whether the minimum is honoured at all.
        Assert.Equal((3, 2), PresetButtonLayout.Choose(4, PanelWidth));
    }

    [Fact]
    public void Choose_AtTheMinimumWidthBoundary_TakesTheLastColumnThatStillFits()
    {
        // Three buttons at exactly MinButtonWidth, with the two gaps between them.
        double exact = 3 * PresetButtonLayout.MinButtonWidth + 2 * PresetButtonLayout.Spacing;

        Assert.Equal((3, 1), PresetButtonLayout.Choose(3, exact));
        Assert.Equal((2, 2), PresetButtonLayout.Choose(3, exact - 1));
    }

    [Fact]
    public void Choose_NarrowerThanOneButton_StillGivesOneColumn()
    {
        // Never zero columns: a division by the column count follows.
        Assert.Equal((1, 2), PresetButtonLayout.Choose(2, 10));
    }

    [Fact]
    public void Choose_NoPresets_IsEmpty()
    {
        Assert.Equal((0, 0), PresetButtonLayout.Choose(0, PanelWidth));
    }
}
