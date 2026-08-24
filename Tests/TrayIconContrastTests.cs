using System.Drawing;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
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
        // measurement.
        using var bmp = IconGenerator.RenderStyleBitmap(64, 80, charging: false, TrayIconMode.BrandMark);
        var (top, bottom) = InkRows(bmp);

        Assert.True(top >= 0, "the mark renders no ink at all.");
        float used = (bottom - top + 1) / 64f;
        Assert.True(used >= 0.70f, $"ink spans rows {top}..{bottom} of 64 — {used:P0} of the frame.");
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
