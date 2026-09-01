using System.Drawing;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// The start and stop marks on the tray icon. The gating is the part that matters: a mark drawn when
// the firmware is not capping is a lie about the machine, and a start mark drawn from the 0 that HP
// and Surface report by contract would put a permanent mark at the empty end of the gauge.
public class TrayThresholdMarkTests
{
    [Fact]
    public void NoThresholdStateAtAll_DrawsNothing() =>
        Assert.Equal((null, null), IconGenerator.ThresholdMarksFor(null));

    [Fact]
    public void AMachineWithNoChargeInterface_DrawsNothing() =>
        Assert.Equal((null, null),
                     IconGenerator.ThresholdMarksFor(new ChargeThresholdState(false, true, 60, 80)));

    [Fact]
    public void SmartChargeSwitchedOff_DrawsNothing() =>
        // Capable, but charging to 100 % — there is no cap to mark.
        Assert.Equal((null, null),
                     IconGenerator.ThresholdMarksFor(new ChargeThresholdState(true, false, 60, 80)));

    [Fact]
    public void EnabledWithNoStopValue_DrawsNothing() =>
        // IsLimiting requires Stop > 0; a 0 would otherwise mark the empty end of the gauge.
        Assert.Equal((null, null),
                     IconGenerator.ThresholdMarksFor(new ChargeThresholdState(true, true, 60, 0)));

    [Fact]
    public void AVendorReportingNoStartThreshold_DrawsTheStopMarkOnly()
    {
        // HP and Surface report Start = 0 by contract, so the start mark can never be assumed.
        var (stop, start) = IconGenerator.ThresholdMarksFor(new ChargeThresholdState(true, true, 0, 80));
        Assert.Equal(80, stop);
        Assert.Null(start);
    }

    [Fact]
    public void ARealStartStopRange_DrawsBothMarks()
    {
        var (stop, start) = IconGenerator.ThresholdMarksFor(new ChargeThresholdState(true, true, 60, 80));
        Assert.Equal(80, stop);
        Assert.Equal(60, start);
    }

    // The gating has to reach the pixels, not just the decision — the two renderers each place the
    // marks themselves, so a style that forgets to call the decision would pass everything above.

    [Fact]
    public void TheArcDrawsTheMarks_OnlyWhileLimiting()
    {
        AssertMarksReachThePixels(TrayIconMode.Arc);
    }

    [Fact]
    public void TheBrandMarkDrawsTheMarks_OnlyWhileLimiting()
    {
        AssertMarksReachThePixels(TrayIconMode.BrandMark);
    }

    private static void AssertMarksReachThePixels(TrayIconMode mode)
    {
        var limiting    = new ChargeThresholdState(true, true, 60, 80);
        var notLimiting = new ChargeThresholdState(true, false, 60, 80);

        using var bare      = IconGenerator.RenderStyleBitmap(64, 70, PowerState.Discharging, mode, null);
        using var capped    = IconGenerator.RenderStyleBitmap(64, 70, PowerState.Discharging, mode, limiting);
        using var uncapped  = IconGenerator.RenderStyleBitmap(64, 70, PowerState.Discharging, mode, notLimiting);

        Assert.False(PixelsMatch(bare, capped),   $"{mode} renders identically with and without a cap.");
        Assert.True(PixelsMatch(bare, uncapped),  $"{mode} draws a mark while Smart Charge is off.");
    }

    private static bool PixelsMatch(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;

        return true;
    }
}
