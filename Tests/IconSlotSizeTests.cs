using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// IconGenerator.SlotSizeForDpi is the pure DPI→pixel-size mapping behind the LIVE tray icon
// (issue #40 item 2): the icon must be rendered at the size the TASKBAR's monitor needs, not the
// process's DPI context, or the shell rescales the single frame and the thin low-battery arc washes
// out on a secondary-monitor mixed-DPI setup. These pin round(16 * dpi / 96), the 16..64 clamp, and
// the 0/"unknown" fallback so the math stays correct without a live taskbar to observe.
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
        // 110 DPI → 16 * 110 / 96 = 18.33 → 18; 116 DPI → 19.33 → 19; a midpoint 108 DPI →
        // 18.0 exactly. Pick a true .5 case: 16 * 105 / 96 = 17.5 → 18 (away from zero).
        Assert.Equal(18, IconGenerator.SlotSizeForDpi(105));
        Assert.Equal(18, IconGenerator.SlotSizeForDpi(110));
    }
}
