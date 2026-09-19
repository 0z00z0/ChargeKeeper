using ZeroZero.Tray;
using Xunit;

namespace ChargeKeeper.Tests;

// The scale-to-pixel-size mapping behind the live tray icon, which is the shared tray slot's. The
// icon has to be rendered at the size the taskbar's monitor needs rather than the process's DPI
// context, or the shell rescales the one frame and the thin low-battery arc washes out on a
// mixed-DPI setup.
public class IconSlotSizeTests
{
    [Theory]
    [InlineData(1.00, 16)]  // 100 % → 16 px (logical small-icon size)
    [InlineData(1.25, 20)]  // 125 % → 20 px
    [InlineData(1.50, 24)]  // 150 % → 24 px
    [InlineData(1.75, 28)]  // 175 % → 28 px
    [InlineData(2.00, 32)]  // 200 % → 32 px — full arc detail preserved
    [InlineData(2.50, 40)]  // 250 % → 40 px
    [InlineData(3.00, 48)]  // 300 % → 48 px
    public void ScalesLogicalSmallIconSizeByTheTaskbarScale(double scale, int expected) =>
        Assert.Equal(expected, TrayIconSlot.PixelsFor(scale));

    [Fact]
    public void Rounds_AwayFromMidpoint() =>
        // 105 DPI is a true .5 case: 16 * 105 / 96 = 17.5, which must round away from zero.
        Assert.Equal(18, TrayIconSlot.PixelsFor(105 / 96.0));
}
