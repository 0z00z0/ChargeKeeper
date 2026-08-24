using System.Drawing;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// The tray icon is drawn on a transparent background against a taskbar the app does not control, so
// contrast is the whole of its legibility. These cover the two halves of that: how much of the slot
// the brand mark occupies, and the outline strength chosen from the taskbar's light/dark setting.
public class TrayIconContrastTests
{
    // Contrast selection

    [Fact]
    public void ALightTaskbarGetsAHarderOutline()
    {
        // On a dark taskbar the background separates the pastel tier colours by itself and the halo
        // is only a soft shadow; on a light one it is the only edge the glyph has.
        var light = IconGenerator.IconContrast.For(lightTaskbar: true);
        var dark  = IconGenerator.IconContrast.For(lightTaskbar: false);

        Assert.True(light.Outline.A > dark.Outline.A,
                    $"light halo alpha {light.Outline.A} is not above dark's {dark.Outline.A}.");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(64)]
    public void ALightTaskbarGetsAWiderHalo_AtEverySlotSize(int size)
    {
        var light = IconGenerator.IconContrast.For(lightTaskbar: true);
        var dark  = IconGenerator.IconContrast.For(lightTaskbar: false);

        Assert.True(light.ExtraWidth(size) > dark.ExtraWidth(size),
                    $"at {size} px the light halo adds {light.ExtraWidth(size)} px, the dark one {dark.ExtraWidth(size)}.");
    }

    [Fact]
    public void TheHaloWidthHasAFloor_SoItSurvivesThe16PxFrame()
    {
        // 16 * 0.09 = 1.44 px would round away at the smallest slot; the floor keeps it drawable.
        Assert.Equal(2.0f, IconGenerator.IconContrast.For(lightTaskbar: true).ExtraWidth(16));
        Assert.Equal(1.5f, IconGenerator.IconContrast.For(lightTaskbar: false).ExtraWidth(16));
    }

    [Fact]
    public void ALightTaskbarGetsADarkerEmptyTrack()
    {
        // The arc's unfilled track is mid-grey against a dark taskbar; the same grey on a light one
        // is barely there, so the light variant is darker.
        var light = IconGenerator.IconContrast.For(lightTaskbar: true).Track;
        var dark  = IconGenerator.IconContrast.For(lightTaskbar: false).Track;

        Assert.True(light.R < dark.R && light.G < dark.G && light.B < dark.B,
                    $"light track {light} is not darker than the dark track {dark}.");
    }

    // Taskbar theme, as the tray icon learns about it

    [Theory]
    [InlineData(Microsoft.Win32.UserPreferenceCategory.General)]     // WM_SETTINGCHANGE "ImmersiveColorSet"
    [InlineData(Microsoft.Win32.UserPreferenceCategory.Color)]       // WM_SYSCOLORCHANGE
    [InlineData(Microsoft.Win32.UserPreferenceCategory.VisualStyle)] // WM_THEMECHANGED
    public void TheCategoriesAThemeFlipArrivesInAreAccepted(Microsoft.Win32.UserPreferenceCategory category)
    {
        Assert.True(IconGenerator.CategoryCanCarryThemeChange(category),
                    $"{category} is dropped, so a light/dark flip arriving in it never repaints.");
    }

    [Theory]
    [InlineData(Microsoft.Win32.UserPreferenceCategory.Mouse)]
    [InlineData(Microsoft.Win32.UserPreferenceCategory.Keyboard)]
    [InlineData(Microsoft.Win32.UserPreferenceCategory.Locale)]
    [InlineData(Microsoft.Win32.UserPreferenceCategory.Power)]
    public void CategoriesThatCannotCarryAThemeFlipAreDropped(Microsoft.Win32.UserPreferenceCategory category)
    {
        Assert.False(IconGenerator.CategoryCanCarryThemeChange(category),
                     $"{category} is accepted, so unrelated settings churn the icon.");
    }

    [Fact]
    public void AnUnmovedThemeReportsNoChange()
    {
        // General is also the catch-all for every unmapped setting, so the event alone means
        // nothing — without this gate a mouse-speed change would force a full GDI repaint.
        IconGenerator.RefreshThemeCacheIfChanged();   // seed from the live setting

        Assert.False(IconGenerator.RefreshThemeCacheIfChanged(),
                     "a second read of an unchanged setting reported a change.");
    }

    [Fact]
    public void ARefreshLeavesTheCacheAgreeingWithTheLiveSetting()
    {
        // The refresh both detects and adopts; a detect that failed to adopt would repaint with the
        // old contrast and then report "unchanged" for ever after. Cleared first, so the adopt
        // branch is the one that runs rather than the unchanged-value early return.
        IconGenerator.InvalidateThemeCache();
        IconGenerator.RefreshThemeCacheIfChanged();

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool live = key?.GetValue("SystemUsesLightTheme") is int light && light != 0;

        Assert.Equal(live, IconGenerator.TaskbarUsesLightTheme());
    }

    // Frame usage

    [Fact]
    public void TheMarksInkStaysInsideTheFrame_AndOnItsCentreLine()
    {
        Assert.True(IconGenerator.MarkInkTop >= 0f);
        Assert.True(IconGenerator.MarkInkBottom <= IconGenerator.MarkCanvas);
        Assert.Equal(IconGenerator.MarkCanvas / 2f,
                     (IconGenerator.MarkInkTop + IconGenerator.MarkInkBottom) / 2f, 1.0);
    }

    [Fact]
    public void TheMarkUsesMostOfTheFrameHeight()
    {
        // It used 48 % — y 66..190 of 256 — which left roughly 66 units dead at each end.
        float used = (IconGenerator.MarkInkBottom - IconGenerator.MarkInkTop) / IconGenerator.MarkCanvas;
        Assert.True(used >= 0.70f, $"the mark occupies {used:P0} of the frame height.");
    }

    [Fact]
    public void TheRenderedMarkFillsTheFrameItDeclares()
    {
        // The narrow pixel assertion: the declared extent is worth nothing if the render clips it or
        // falls short of it. 64 px is the largest slot, where a stroke floor cannot distort the
        // measurement. The threshold line is included because the 48 % this replaces was measured
        // with the mark's fixed guard line in place.
        AssertFillsFrame(new ChargeThresholdState(true, true, 60, 80), atLeast: 0.75f);
    }

    [Fact]
    public void TheRenderedMarkStillFillsTheFrameWithNoChargeCap()
    {
        // With Smart Charge off there is no threshold line, so the battery body carries the height
        // on its own. Less than the capped mark, still well past the 48 % the letterbox managed.
        AssertFillsFrame(threshold: null, atLeast: 0.62f);
    }

    private static void AssertFillsFrame(ChargeThresholdState? threshold, float atLeast)
    {
        using var bmp = IconGenerator.RenderStyleBitmap(64, 80, false, TrayIconMode.BrandMark, threshold);
        var (top, bottom) = InkRows(bmp);

        Assert.True(top >= 0, "the mark renders no ink at all.");
        float used = (bottom - top + 1) / 64f;
        Assert.True(used >= atLeast, $"ink spans rows {top}..{bottom} of 64 — {used:P0} of the frame.");
        Assert.True(top > 0 && bottom < 63, $"ink runs to the frame edge (rows {top}..{bottom}) — clipped.");
    }

    /// <summary>First and last rows carrying any non-transparent pixel, or (-1, -1) for an empty
    /// bitmap.</summary>
    private static (int Top, int Bottom) InkRows(Bitmap bmp)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A > 0)
                {
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }

        return (top, bottom);
    }
}
