using System.Drawing;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Geometry and pixels for the tray's flow mark. The decision that picks the mark is covered by
/// <see cref="PowerFlowTests"/>; these pin that the chosen mark is the shape actually drawn, that
/// it fits the 16 px ring, and that only the arc style carries it.
/// </summary>
public class TrayFlowMarkTests
{
    // The arc's own geometry, taken from the renderer rather than restated, so the fit below is
    // measured against what is actually drawn.
    private static (float Cx, float Cy, float R, float Stroke) ArcGeometry(int size) =>
        (size / 2f, size / 2f, IconGenerator.ArcRingRadius(size), IconGenerator.ArcStroke(size));

    /// <summary>Which side of centre the apex sits on IS the message. A sign slip here draws
    /// charging as a down arrow, which is worse than drawing nothing.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheApexPointsUpForChargeGoingIn_AndDownForChargeGoingOut(bool goingIn)
    {
        var flow = goingIn ? PowerFlow.In : PowerFlow.Out;
        using var path = IconGenerator.FlowMarkPath(cx: 50f, cy: 50f, box: 20f, flow);

        var points = path.PathPoints;
        Assert.Equal(3, points.Length);

        // Checked first and on its own, so a flipped sign fails on the direction rather than on
        // some later consequence of it.
        var above = points.Where(p => p.Y < 50f).ToArray();
        var below = points.Where(p => p.Y > 50f).ToArray();
        var (apexSide, baseSide) = goingIn ? (above, below) : (below, above);
        Assert.True(apexSide.Length == 1 && baseSide.Length == 2,
            goingIn ? "charge going in must put one apex above the centre and the base below it"
                    : "charge going out must put one apex below the centre and the base above it");

        // The base is level and the apex sits on the vertical centre line, so the mark is a
        // symmetrical triangle rather than a lopsided wedge.
        Assert.Equal(baseSide[0].Y, baseSide[1].Y, 3);
        Assert.Equal(50f, apexSide[0].X, 3);
    }

    [Fact]
    public void AtRestTheMarkIsARoundDot_NotATriangle()
    {
        using var dot = IconGenerator.FlowMarkPath(50f, 50f, 20f, PowerFlow.Rest);
        var bounds = dot.GetBounds();

        Assert.True(dot.PathPoints.Length > 3, "the rest mark must be a curve, not a polygon");
        Assert.Equal(bounds.Width, bounds.Height, 2);
        Assert.Equal(50f, bounds.X + bounds.Width / 2f, 2);
        Assert.Equal(50f, bounds.Y + bounds.Height / 2f, 2);
    }

    /// <summary>
    /// The size constraint the mark was designed against. At the smallest tray slot the ring leaves
    /// under eight pixels of clear centre, and the mark plus the transparent gap punched around it
    /// has to sit inside that — otherwise the gap bites a notch out of the gauge and the icon reads
    /// as a broken ring.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(64)]
    public void TheMarkAndItsMoatFitInsideTheRing(int size)
    {
        var (_, _, r, stroke) = ArcGeometry(size);
        float hole = 2f * (r - stroke / 2f);
        float occupied = IconGenerator.FlowMarkBox(r, stroke) + IconGenerator.FlowMarkMoatWidth(size);

        Assert.True(occupied <= hole,
            $"at {size} px the mark plus its moat is {occupied:F2} px across but the ring's centre is only {hole:F2} px");
    }

    /// <summary>Small is allowed; absent is not. A mark that shrank to nothing would pass the fit
    /// test above and show a viewer nothing at all.</summary>
    [Fact]
    public void TheMarkStillHasRealWidthAtTheSmallestSlot()
    {
        var (_, _, r, stroke) = ArcGeometry(16);
        Assert.True(IconGenerator.FlowMarkBox(r, stroke) >= 4f,
            "the mark must keep at least four pixels across at 16 px or it is a smudge");
    }

    /// <summary>Each flow has to reach the pixels, and reach them differently. Equality here would
    /// mean three readings painting one icon.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void EachFlowPaintsADistinctArcIcon(int size)
    {
        using var none = IconGenerator.RenderStyleBitmap(size, 62, PowerState.Charging, TrayIconMode.Arc, null, null);
        using var into = IconGenerator.RenderStyleBitmap(size, 62, PowerState.Charging, TrayIconMode.Arc, null, PowerFlow.In);
        using var outOf = IconGenerator.RenderStyleBitmap(size, 62, PowerState.Charging, TrayIconMode.Arc, null, PowerFlow.Out);
        using var rest = IconGenerator.RenderStyleBitmap(size, 62, PowerState.Charging, TrayIconMode.Arc, null, PowerFlow.Rest);

        Assert.False(PixelsMatch(none, into),  "a flow reading must change the icon");
        Assert.False(PixelsMatch(none, rest),  "at rest is a reading and must show a mark");
        Assert.False(PixelsMatch(into, outOf), "in and out must not paint the same icon");
        Assert.False(PixelsMatch(into, rest),  "in and at rest must not paint the same icon");
        Assert.False(PixelsMatch(outOf, rest), "out and at rest must not paint the same icon");
    }

    /// <summary>
    /// The case the mark exists for, all the way to the pixels: the reported state says mains and
    /// the level does not move, yet the icon has to differ because the pack is losing charge.
    /// </summary>
    [Fact]
    public void PluggedInButDraining_PaintsADifferentIconFromCharging()
    {
        const int Pct = 70;
        var drainingOnMains = PowerFlows.From(-22739);
        var chargingOnMains = PowerFlows.From(+22739);

        using var draining = IconGenerator.RenderStyleBitmap(16, Pct, PowerState.Charging, TrayIconMode.Arc, null, drainingOnMains);
        using var charging = IconGenerator.RenderStyleBitmap(16, Pct, PowerState.Charging, TrayIconMode.Arc, null, chargingOnMains);

        Assert.False(PixelsMatch(draining, charging),
            "a pack falling on mains must not paint the same icon as one filling on mains");
    }

    /// <summary>An absent reading paints today's icon exactly, so a machine with no battery gains
    /// nothing new rather than a mark meaning "at rest".</summary>
    [Fact]
    public void AnUnavailableReadingPaintsTheIconWithNoMark()
    {
        using var noReading = IconGenerator.RenderStyleBitmap(32, 62, PowerState.Discharging, TrayIconMode.Arc, null,
                                                              PowerFlows.From(null));
        using var noFlowArg = IconGenerator.RenderStyleBitmap(32, 62, PowerState.Discharging, TrayIconMode.Arc, null);

        Assert.True(PixelsMatch(noReading, noFlowArg));
    }

    /// <summary>The two styles that deliberately do not carry the mark. Numeric's frame is spent on
    /// its digits; the brand mark's payload is the interior fill band the moat would erase.</summary>
    // TrayIconMode is internal, so it cannot appear in a public theory signature; the cases run
    // inside the test instead.
    [Fact]
    public void TheOtherStylesAreUnchangedByTheFlow()
    {
        foreach (var mode in new[] { TrayIconMode.Numeric, TrayIconMode.BrandMark })
            foreach (var flow in new PowerFlow?[] { PowerFlow.In, PowerFlow.Out, PowerFlow.Rest })
            {
                using var without = IconGenerator.RenderStyleBitmap(32, 62, PowerState.Charging, mode, null, null);
                using var with    = IconGenerator.RenderStyleBitmap(32, 62, PowerState.Charging, mode, null, flow);
                Assert.True(PixelsMatch(without, with), $"{mode} must not draw a flow mark ({flow}).");
            }
    }

    /// <summary>The mark is drawn last, so the moat it punches survives. Anything painted after it
    /// would fill the gap back in and refuse the mark from the ring it has to stand clear of.</summary>
    [Fact]
    public void TheMarkClearsAGapEvenWhereTheRingRunsBehindIt()
    {
        // 96 % brings the arc almost the whole way round, so the fill passes close to the mark.
        using var withMark = IconGenerator.RenderStyleBitmap(64, 96, PowerState.Charging, TrayIconMode.Arc, null, PowerFlow.In);

        // Just outside the mark's apex there must be cleared pixels, not ring or fill.
        var (cx, cy, r, stroke) = ArcGeometry(64);
        float box = IconGenerator.FlowMarkBox(r, stroke);
        int probeX = (int)Math.Round(cx);
        int probeY = (int)Math.Round(cy - box * 0.86f / 2f - IconGenerator.FlowMarkMoatWidth(64) / 2f);

        Assert.Equal(0, withMark.GetPixel(probeX, probeY).A);
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
