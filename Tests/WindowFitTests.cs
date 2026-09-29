using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

public class WindowFitTests
{
    // A plain 1920x1080 panel with a 40px taskbar, used wherever the exact screen does not matter.
    private static readonly (int X, int Y, int W, int H) Work = (0, 0, 1920, 1040);

    [Fact]
    public void Fit_SavedRectEntirelyOffScreen_ReCentres()
    {
        // The disconnected-monitor case. Clamping alone would jam it against the right edge; a
        // window the user has not seen for a session should come back somewhere sensible.
        var r = WindowFit.Fit((5000, 3000, 1200, 800), requiredHeight: 0, Work);
        Assert.Equal((1920 - 1200) / 2, r.X);
        Assert.Equal((1040 - 800) / 2, r.Y);
    }
}
