using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// The DPI-to-pixel-size mapping behind the live tray icon. The icon has to be rendered at the size
// the taskbar's monitor needs rather than the process's DPI context, or the shell rescales the one
// frame and the thin low-battery arc washes out on a mixed-DPI setup.
public class IconSlotSizeTests
{
    [Theory]
    [InlineData(96u, 16)]   // 100 %  → 16 px (logical small-icon size)
    [InlineData(120u, 20)]  // 125 %  → 20 px
    [InlineData(144u, 24)]  // 150 %  → 24 px
    [InlineData(168u, 28)]  // 175 %  → 28 px
    [InlineData(192u, 32)]  // 200 %  → 32 px — full arc detail preserved
    [InlineData(240u, 40)]  // 250 %  → 40 px
    [InlineData(288u, 48)]  // 300 %  → 48 px
    public void ScalesLogicalSmallIconSizeByDpi(uint dpi, int expected) =>
        Assert.Equal(expected, IconGenerator.SlotSizeForDpi(dpi));

    [Fact]
    public void UnknownDpi_FallsBackTo100Percent() =>
        // 0 is what the Win32 query returns when the DPI is unavailable; treat as 96 (16 px),
        // not the clamp floor by accident.
        Assert.Equal(16, IconGenerator.SlotSizeForDpi(0));

    [Theory]
    [InlineData(48u)]   // 50 % — below the small-icon floor
    [InlineData(72u)]   // 75 %
    public void BelowRange_ClampsToFloor(uint dpi) =>
        Assert.Equal(16, IconGenerator.SlotSizeForDpi(dpi));

    [Theory]
    [InlineData(384u)]  // 400 % → 64 px exactly (top of range)
    [InlineData(480u)]  // 500 % — beyond supported range
    [InlineData(96000u)] // absurd/bogus value must never yield a giant bitmap
    public void AboveRange_ClampsToCeiling(uint dpi) =>
        Assert.Equal(64, IconGenerator.SlotSizeForDpi(dpi));

    [Fact]
    public void Rounds_AwayFromMidpoint()
    {
        // 105 DPI is a true .5 case: 16 * 105 / 96 = 17.5, which must round away from zero.
        Assert.Equal(18, IconGenerator.SlotSizeForDpi(105));
        Assert.Equal(18, IconGenerator.SlotSizeForDpi(110));
    }
}
