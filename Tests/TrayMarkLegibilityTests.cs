using System.Drawing;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// What the threshold marks are worth at the size the notification area actually asks for. The tests
// above this one prove a mark reaches the pixels at 64 px; these measure whether it survives 16 px,
// which is where it was illegible.
public class TrayMarkLegibilityTests
{
    private const int Slot = 16;

    /// <summary>Pixels the cap adds to a frame, counted where the mark's own ink is solid enough to
    /// read: alpha at least <paramref name="minAlpha"/> and redder than it is blue, which is what
    /// separates terracotta from the grey track and the dark halo.</summary>
    private static (int Ink, int Outside) MeasureMark(int stopPercent, float ringOuterRadius)
    {
        var limiting = new ChargeThresholdState(true, true, 0, stopPercent);

        using var bare   = IconGenerator.RenderStyleBitmap(Slot, 70, PowerState.Discharging, TrayIconMode.Arc, null);
        using var capped = IconGenerator.RenderStyleBitmap(Slot, 70, PowerState.Discharging, TrayIconMode.Arc, limiting);

        int ink = 0, outside = 0;
        float c = Slot / 2f;

        for (int y = 0; y < Slot; y++)
            for (int x = 0; x < Slot; x++)
            {
                Color a = bare.GetPixel(x, y), b = capped.GetPixel(x, y);
                if (a == b) continue;
                if (b.A < 128 || b.R <= b.B) continue;

                ink++;
                double dx = x + 0.5 - c, dy = y + 0.5 - c;
                if (Math.Sqrt(dx * dx + dy * dy) > ringOuterRadius) outside++;
            }

        return (ink, outside);
    }

    /// <summary>The ring's outer edge in the same units, so "outside the ring" is measured rather
    /// than assumed. Mirrors <see cref="IconGenerator.ArcRingOuterRadius"/>.</summary>
    private static float RingOuter => IconGenerator.ArcRingOuterRadius(Slot);

    [Fact]
    public void TheStopMark_ReadsAtTheSmallestTraySlot()
    {
        var (ink, outside) = MeasureMark(80, RingOuter);

        // Nine solid pixels is a mark a display resolves; the geometry it replaced left four, all of
        // them inside the ring stroke and sharing its colour family.
        Assert.True(ink >= 9, $"The stop mark covers only {ink} solid pixels at {Slot} px.");

        // The part that does the reading: ink beyond the ring's outer edge sits against empty space
        // rather than competing with the stroke.
        Assert.True(outside >= 3,
                    $"Only {outside} of the stop mark's {ink} solid pixels fall outside the ring.");
    }

    [Fact]
    public void TheStartMark_StaysDistinguishableFromTheStop()
    {
        var limiting = new ChargeThresholdState(true, true, 60, 80);

        using var stopOnly = IconGenerator.RenderStyleBitmap(
            Slot, 70, PowerState.Discharging, TrayIconMode.Arc, new ChargeThresholdState(true, true, 0, 80));
        using var both = IconGenerator.RenderStyleBitmap(
            Slot, 70, PowerState.Discharging, TrayIconMode.Arc, limiting);

        int start = 0;
        for (int y = 0; y < Slot; y++)
            for (int x = 0; x < Slot; x++)
                if (stopOnly.GetPixel(x, y) != both.GetPixel(x, y)) start++;

        // The start mark is deliberately the lighter of the two, so it is held to a lower floor than
        // the stop — but it still has to exist on screen.
        Assert.True(start >= 6, $"The start mark changes only {start} pixels at {Slot} px.");
    }

    [Fact]
    public void TheMarkTips_StayInsideTheFrame()
    {
        // A tip drawn past the bitmap edge is clipped, and a clipped mark reads shorter on one side
        // of the sweep than the other.
        Assert.True(IconGenerator.ArcMarkOuterRadius(Slot) <= Slot / 2f,
                    "The mark's outer tip reaches past the icon bounds.");
    }
}
