using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

public class WindowFitTests
{
    // A plain 1920x1080 panel with a 40px taskbar, used wherever the exact screen does not matter.
    private static readonly (int X, int Y, int W, int H) Work = (0, 0, 1920, 1040);

    [Fact]
    public void Fit_ContentTallerThanSavedRect_GrowsToFitIt()
    {
        // The whole point: a page longer than the window opened scrolled.
        var r = WindowFit.Fit((100, 100, 1200, 750), requiredHeight: 900, Work);
        Assert.Equal(900, r.H);
    }

    [Fact]
    public void Fit_ContentTallerThanWorkArea_StopsAtWorkArea_ScrollbarAccepted()
    {
        // Explicitly accepted: growing past the work area would push the window's own controls
        // behind the taskbar, which is worse than the scrollbar it would remove.
        var r = WindowFit.Fit((0, 0, 1200, 750), requiredHeight: 5000, Work);
        Assert.Equal(1040, r.H);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void Fit_SavedRectWiderThanScreen_ShrinksToWorkAreaWidth()
    {
        var r = WindowFit.Fit((0, 0, 4000, 700), requiredHeight: 0, Work);
        Assert.Equal(1920, r.W);
        Assert.Equal(0, r.X);
    }

    [Fact]
    public void Fit_SavedRectEntirelyOffScreen_ReCentres()
    {
        // The disconnected-monitor case. Clamping alone would jam it against the right edge; a
        // window the user has not seen for a session should come back somewhere sensible.
        var r = WindowFit.Fit((5000, 3000, 1200, 800), requiredHeight: 0, Work);
        Assert.Equal((1920 - 1200) / 2, r.X);
        Assert.Equal((1040 - 800) / 2, r.Y);
    }

    [Fact]
    public void Fit_SavedRectPartlyOffScreen_SlidesBackIn_KeepsSize()
    {
        // Still overlapping, so the user's chosen size and rough position are respected — it is
        // only pulled far enough to be fully visible.
        var r = WindowFit.Fit((1800, 900, 1200, 800), requiredHeight: 0, Work);
        Assert.Equal((720, 240, 1200, 800), r);
    }

    [Fact]
    public void Fit_SavedRectAlreadyFits_LeftAlone()
    {
        // A valid saved rect survives untouched, so the window does not creep back to centre every
        // time it opens.
        var rect = (200, 150, 1200, 800);
        Assert.Equal(rect, WindowFit.Fit(rect, requiredHeight: 700, Work));
    }

    [Fact]
    public void Fit_ZeroRequiredHeight_NeverGrows_ForTheOnCloseSavePath()
    {
        var r = WindowFit.Fit((100, 100, 1200, 400), requiredHeight: 0, Work);
        Assert.Equal(400, r.H);
    }

    [Fact]
    public void Fit_UndockedLaptopPanel_RegressionFromTheSavedDockedRect()
    {
        // A measured case: a rect saved while docked to a 5634x1440 virtual desktop, reopened on a
        // 2194x1323 work area, so it is 174 px too tall and its right edge sits a thousand pixels
        // past the screen.
        var work = (0, 0, 2194, 1323);
        var r = WindowFit.Fit((1150, 297, 2150, 1497), requiredHeight: 1497, work);

        Assert.Equal(1323, r.H);          // capped to the work area, not the saved 1497
        Assert.Equal(2150, r.W);          // still fits width-wise, so the width is kept
        Assert.Equal(44, r.X);            // pulled back so the right edge lands exactly on 2194
        Assert.Equal(0, r.Y);
        Assert.True(r.X + r.W <= work.Item3 && r.Y + r.H <= work.Item4);
    }

    // HeightForContent: the About window's measured sizing

    [Fact]
    public void HeightForContent_ContentShorterThanViewport_ShrinksTheWindow()
    {
        // 660 DIP of window around ~330 DIP of content leaves the bottom half empty; chrome is 40.
        Assert.Equal(370, WindowFit.HeightForContent(660, contentHeight: 330, viewportHeight: 620, minHeight: 320));
    }

    [Fact]
    public void HeightForContent_ContentTallerThanViewport_GrowsTheWindow()
    {
        // Crediting another library must widen the window's height on its own, without the constant
        // being re-tuned by hand.
        Assert.Equal(500, WindowFit.HeightForContent(400, contentHeight: 460, viewportHeight: 360, minHeight: 320));
    }

    [Fact]
    public void HeightForContent_ChromeIsCarriedByTheDifference_NotAddedUp()
    {
        // Content exactly filling the viewport must leave the window alone, whatever the chrome is —
        // that is what makes measuring the difference safe when the title bar changes.
        Assert.Equal(500, WindowFit.HeightForContent(500, contentHeight: 400, viewportHeight: 400, minHeight: 100));
    }

    [Fact]
    public void HeightForContent_TinyPayload_StopsAtTheFloor_NotASliver()
    {
        Assert.Equal(320, WindowFit.HeightForContent(660, contentHeight: 20, viewportHeight: 620, minHeight: 320));
    }

    [Fact]
    public void HeightForContent_FractionalDips_RoundUp_SoNothingIsClipped()
    {
        // DIPs are doubles and the window height is an int; rounding down would clip the last line.
        Assert.Equal(371, WindowFit.HeightForContent(660, contentHeight: 330.2, viewportHeight: 620, minHeight: 320));
    }

    // The resize floor. The live values are the Settings window's own: a 320 DIP nav pane, seven nav
    // items, and the ScrollViewer's 20,8,20,12 padding.

    private const double NavPane      = 320;
    private const int    NavItems     = 7;
    private const double PaddingH     = 40;
    private const double PaddingV     = 20;

    [Fact]
    public void MinimumWidthDip_IsTheNavPanePlusTheContentColumnsFixedParts()
    {
        // 320 pane + 40 scroller padding + 16 scrollbar + 32 card padding + 220 widest control.
        Assert.Equal(628, WindowFit.MinimumWidthDip(NavPane, PaddingH));
    }

    [Fact]
    public void MinimumSizeDip_LeavesRoomBelowTheDefaultOpeningSize()
    {
        // A minimum at or above the opening size would make the window unresizable rather than
        // merely bounded. DefaultWidth/DefaultHeight in SettingsWindow are 1200x750 DIPs.
        Assert.True(WindowFit.MinimumWidthDip(NavPane, PaddingH) < 1200);
        Assert.True(WindowFit.MinimumHeightDip(NavItems, PaddingV) < 750);
    }

    [Fact]
    public void MinimumHeightDip_IsGovernedByTheNavPane_NotTheScrollingContent()
    {
        // 32 title bar + 48 pane header + 7x40 items + 69 footer. The content side is far shorter,
        // and it scrolls anyway.
        Assert.Equal(429, WindowFit.MinimumHeightDip(NavItems, PaddingV));
        Assert.Equal(429, WindowFit.MinimumHeightDip(NavItems, scrollerPadding: 200));
    }

    [Fact]
    public void MinimumHeightDip_GrowsWithTheNavItems_SoANewPageIsNotCutOff()
    {
        Assert.Equal(469, WindowFit.MinimumHeightDip(NavItems + 1, PaddingV));
    }

    [Fact]
    public void ToPhysicalPixels_ScalesByTheRasterizationScale()
    {
        // The 175 % laptop panel. Passing the DIP figure straight through would let the window be
        // dragged to 57 % of its intended floor there.
        Assert.Equal(628,  WindowFit.ToPhysicalPixels(628, 1.0));
        Assert.Equal(1099, WindowFit.ToPhysicalPixels(628, 1.75));
    }

    [Fact]
    public void ToPhysicalPixels_UnreadableScale_FallsBackTo100Percent()
    {
        // XamlRoot is null before the first layout; 0 must not collapse the minimum to nothing.
        Assert.Equal(628, WindowFit.ToPhysicalPixels(628, 0));
    }
}
