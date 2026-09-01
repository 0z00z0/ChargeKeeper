using System;
using System.Linq;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// The gauge's colour maths, away from every renderer: no bitmap, no brush, no window. What the tray
// icon and the dashboard arc both read is exactly what is asserted here, so a drift between the two
// surfaces is impossible without one of these failing first.
public class GaugePaletteTests
{
    private static string Hex(uint argb) => $"{argb:X8}";

    // PowerState is internal, so it cannot appear in a public theory signature; every case runs
    // inside the fact instead, and each assertion names the state it failed on.
    private static PowerState[] EveryState() => Enum.GetValues<PowerState>();

    // ── Oklab, the space the anchors are blended in ───────────────────────────

    [Fact]
    public void EveryPaletteColour_SurvivesTheTripThroughOklab()
    {
        // A conversion that loses a channel byte would move every anchor as well as every midpoint,
        // and the anchors would still look right next to each other.
        foreach (uint colour in new[]
        {
            GaugePalette.Ember, GaugePalette.Terracotta, GaugePalette.SageGreen,
            GaugePalette.Lavender, GaugePalette.SteelBlue, GaugePalette.Orchid, GaugePalette.Amber,
        })
            Assert.Equal(Hex(colour), Hex(Oklab.ToArgb(Oklab.FromArgb(colour), 0xFF)));
    }

    [Fact]
    public void TheChannelExtremesAndGreys_SurviveTheTripThroughOklab()
    {
        // Black exercises the transfer function's linear segment, white its exponent, and the
        // primaries the full 3x3 both ways.
        foreach (uint colour in new uint[]
        {
            0xFF000000, 0xFFFFFFFF, 0xFF808080, 0xFFFF0000, 0xFF00FF00, 0xFF0000FF,
            0xFF010203, 0xFFFEFDFC,
        })
            Assert.Equal(Hex(colour), Hex(Oklab.ToArgb(Oklab.FromArgb(colour), 0xFF)));
    }

    [Fact]
    public void EveryGreyLevel_SurvivesTheTripThroughOklab()
    {
        // 256 round trips along the neutral axis: the transfer function is where a rounding slip
        // hides, and a single off-by-one there moves the whole ramp.
        for (uint level = 0; level <= 0xFF; level++)
        {
            uint grey = 0xFF000000 | (level << 16) | (level << 8) | level;
            Assert.Equal(Hex(grey), Hex(Oklab.ToArgb(Oklab.FromArgb(grey), 0xFF)));
        }
    }

    [Fact]
    public void MixAtOrBeyondTheEnds_ReturnsEachEndExactly()
    {
        // The second guard behind the flat rule: Sample never asks for a position outside a pair, but
        // an unclamped blend would extrapolate rather than hold, and out-of-gamut Oklab extrapolation
        // produces a colour that belongs to neither anchor.
        Assert.Equal(Hex(GaugePalette.Ember),    Hex(Oklab.Mix(GaugePalette.Ember, GaugePalette.Lavender,  0.0)));
        Assert.Equal(Hex(GaugePalette.Lavender), Hex(Oklab.Mix(GaugePalette.Ember, GaugePalette.Lavender,  1.0)));
        Assert.Equal(Hex(GaugePalette.Ember),    Hex(Oklab.Mix(GaugePalette.Ember, GaugePalette.Lavender, -0.8)));
        Assert.Equal(Hex(GaugePalette.Lavender), Hex(Oklab.Mix(GaugePalette.Ember, GaugePalette.Lavender,  2.5)));
    }

    [Fact]
    public void MixKeepsMoreChromaThanTheSameBlendInSrgb()
    {
        // The reason the space matters: blending terracotta and sage straight in sRGB drags the
        // midpoint towards grey. Chroma is the distance from the neutral axis in Oklab.
        uint oklab = Oklab.Mix(GaugePalette.Terracotta, GaugePalette.SageGreen, 0.5);
        uint srgb  = SrgbMidpoint(GaugePalette.Terracotta, GaugePalette.SageGreen);

        Assert.True(Chroma(oklab) > Chroma(srgb),
                    $"the Oklab midpoint {oklab:X8} is no more saturated than the sRGB one {srgb:X8}.");
    }

    // ── Anchors ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheThreeScales_AreTheAnchorsTheyWereDesignedWith()
    {
        // Written out rather than derived: an anchor moved or a pair swapped is a different gauge on
        // every screen, and every other test here would still pass because the scale is its own spec.
        Assert.Equal(
            [(10, Hex(GaugePalette.Ember)), (30, Hex(GaugePalette.Terracotta)),
             (75, Hex(GaugePalette.SageGreen)), (92, Hex(GaugePalette.Lavender))],
            GaugePalette.Draining.Select(s => (s.Percent, Hex(s.Argb))));

        Assert.Equal(
            [(65, Hex(GaugePalette.SteelBlue)), (85, Hex(GaugePalette.Lavender))],
            GaugePalette.Charging.Select(s => (s.Percent, Hex(s.Argb))));

        Assert.Equal(
            [(80, Hex(GaugePalette.SteelBlue)), (94, Hex(GaugePalette.Orchid))],
            GaugePalette.IdleOnMains.Select(s => (s.Percent, Hex(s.Argb))));
    }

    [Fact]
    public void ThePaletteToneStayWhereTheBrandPutThem() =>
        // The six gauge tones as hex, so a slip in a constant fails here rather than on screen.
        Assert.Equal(
            ["FFC2593F", "FFC9926B", "FF7AB88F", "FF9C8FBD", "FF7FA8B8", "FFC2569B"],
            new[]
            {
                GaugePalette.Ember, GaugePalette.Terracotta, GaugePalette.SageGreen,
                GaugePalette.Lavender, GaugePalette.SteelBlue, GaugePalette.Orchid,
            }.Select(Hex));

    [Fact]
    public void EveryAnchor_ReturnsItsOwnColourExactly()
    {
        foreach (var state in EveryState())
            foreach (var stop in GaugePalette.ScaleFor(state))
                Assert.Equal($"{state} {Hex(stop.Argb)}",
                             $"{state} {Hex(GaugePalette.FillFor(stop.Percent, state))}");
    }

    [Fact]
    public void AnchorsRunUpwards_SoASampleFallsBetweenExactlyOnePair()
    {
        foreach (var state in EveryState())
        {
            var levels = GaugePalette.ScaleFor(state).Select(s => s.Percent).ToArray();
            Assert.Equal(levels.OrderBy(p => p), levels);
            Assert.Equal(levels.Distinct().Count(), levels.Length);
        }
    }

    [Fact]
    public void BelowTheFirstAnchor_TheColourIsFlat()
    {
        foreach (var state in EveryState())
        {
            var scale = GaugePalette.ScaleFor(state);
            uint first = scale[0].Argb;

            for (int pct = 0; pct <= scale[0].Percent; pct++)
                Assert.Equal($"{state} at {pct} % {Hex(first)}",
                             $"{state} at {pct} % {Hex(GaugePalette.FillFor(pct, state))}");
        }
    }

    [Fact]
    public void AboveTheLastAnchor_TheColourIsFlat()
    {
        foreach (var state in EveryState())
        {
            var scale = GaugePalette.ScaleFor(state);
            uint last = scale[^1].Argb;

            for (int pct = scale[^1].Percent; pct <= 100; pct++)
                Assert.Equal($"{state} at {pct} % {Hex(last)}",
                             $"{state} at {pct} % {Hex(GaugePalette.FillFor(pct, state))}");
        }
    }

    [Fact]
    public void OutOfRangeReadings_ClampToTheEndAnchors()
    {
        foreach (var state in EveryState())
        {
            var scale = GaugePalette.ScaleFor(state);
            Assert.Equal(Hex(scale[0].Argb),  Hex(GaugePalette.FillFor(-20, state)));
            Assert.Equal(Hex(scale[^1].Argb), Hex(GaugePalette.FillFor(140, state)));
        }
    }

    // ── Midpoints ────────────────────────────────────────────────────────────

    [Fact]
    public void BetweenTwoAnchors_EveryChannelStaysInsideThatPair()
    {
        // The failure this catches is an interpolation that overshoots — a midpoint outside the
        // range its neighbours bracket is a different colour, not a blend of them.
        foreach (var state in EveryState())
        {
            var scale = GaugePalette.ScaleFor(state);

            for (int i = 0; i < scale.Count - 1; i++)
            {
                var (from, to) = (scale[i], scale[i + 1]);
                for (int pct = from.Percent + 1; pct < to.Percent; pct++)
                {
                    uint mid = GaugePalette.FillFor(pct, state);
                    foreach (int shift in new[] { 0, 8, 16 })
                    {
                        int a = (int)((from.Argb >> shift) & 0xFF);
                        int b = (int)((to.Argb   >> shift) & 0xFF);
                        int m = (int)((mid       >> shift) & 0xFF);
                        Assert.True(m >= Math.Min(a, b) && m <= Math.Max(a, b),
                                    $"{state} at {pct} % is {mid:X8}, outside the pair "
                                    + $"{from.Argb:X8}..{to.Argb:X8} on the byte at shift {shift}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void BetweenTwoAnchors_TheColourActuallyMoves()
    {
        // A scale that returned the lower anchor everywhere would pass the bracket test above.
        foreach (var state in EveryState())
        {
            var scale = GaugePalette.ScaleFor(state);

            for (int i = 0; i < scale.Count - 1; i++)
            {
                var (from, to) = (scale[i], scale[i + 1]);
                int mid = (from.Percent + to.Percent) / 2;
                uint sample = GaugePalette.FillFor(mid, state);

                Assert.True(sample != from.Argb && sample != to.Argb,
                            $"{state} at {mid} % returned an anchor ({sample:X8}) rather than a blend.");
            }
        }
    }

    [Fact]
    public void InsideEveryPair_TheRampNeverDoublesBackInLightness()
    {
        // Each anchor pair is a straight line in Oklab, so lightness moves one way across it. The
        // tolerance is the sRGB quantisation the trip back through eight-bit channels introduces.
        const double Quantisation = 0.002;

        foreach (var state in EveryState())
        {
            var scale = GaugePalette.ScaleFor(state);

            for (int i = 0; i < scale.Count - 1; i++)
            {
                var (from, to) = (scale[i], scale[i + 1]);
                double a = Oklab.FromArgb(from.Argb).L;
                double b = Oklab.FromArgb(to.Argb).L;
                double previous = a;

                for (int pct = from.Percent; pct <= to.Percent; pct++)
                {
                    double l = Oklab.FromArgb(GaugePalette.FillFor(pct, state)).L;
                    Assert.InRange(l, Math.Min(a, b) - Quantisation, Math.Max(a, b) + Quantisation);
                    Assert.True(b >= a ? l >= previous - Quantisation : l <= previous + Quantisation,
                                $"{state} reverses at {pct} %.");
                    previous = l;
                }
            }
        }
    }

    // ── The three scales apart ───────────────────────────────────────────────

    [Fact]
    public void TheThreeScalesAreDistinctHigh_WhereTheyDescribeDifferentThings()
    {
        // 88 % is where all three are still on a ramp and none has flattened out: nearly full on
        // battery, nearly full and still charging, and nearly full held on mains. One scale for all
        // three is exactly the defect being removed.
        var seen = new[] { PowerState.Discharging, PowerState.Charging, PowerState.IdleOnMains }
            .Select(s => GaugePalette.FillFor(88, s))
            .ToArray();

        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public void HeldHighOnMains_IsTheOnlyStateThatEndsInOrchid()
    {
        // At the very top the two battery scales meet at lavender by design; orchid is what says the
        // pack is being held high on mains, which is the state that wears it.
        Assert.Equal(Hex(GaugePalette.Orchid),   Hex(GaugePalette.FillFor(95, PowerState.IdleOnMains)));
        Assert.Equal(Hex(GaugePalette.Lavender), Hex(GaugePalette.FillFor(95, PowerState.Charging)));
        Assert.Equal(Hex(GaugePalette.Lavender), Hex(GaugePalette.FillFor(95, PowerState.Discharging)));
    }

    [Fact]
    public void TheTwoMainsScalesPartCompany_AboveTheLevelThatStartsToWearThePack()
    {
        // Below 65 % both mains states are flat steel blue; above 85 % they diverge.
        Assert.Equal(Hex(GaugePalette.FillFor(60, PowerState.Charging)),
                     Hex(GaugePalette.FillFor(60, PowerState.IdleOnMains)));
        Assert.NotEqual(Hex(GaugePalette.FillFor(90, PowerState.Charging)),
                        Hex(GaugePalette.FillFor(90, PowerState.IdleOnMains)));
    }

    [Fact]
    public void BrandAmberHasNoGaugeRole()
    {
        // It keeps every other use — the discharging status glyph, the tick marks — but across most
        // of the battery range it read as a warning, which is what this change removes.
        foreach (var state in EveryState())
            for (int pct = 0; pct <= 100; pct++)
                Assert.True(GaugePalette.FillFor(pct, state) != GaugePalette.Amber,
                            $"{state} at {pct} % is brand amber.");
    }

    [Fact]
    public void TheDrainingScaleLeavesTheWarmBand_WellBelowTheOldGreenThreshold()
    {
        // The reported defect as a measurement: the old gauge was amber or terracotta everywhere
        // below 75 %. Hue in Oklab degrees; the two orange tones sit in roughly 20°–70°.
        int lastWarm = 0;
        for (int pct = 0; pct <= 100; pct++)
            if (IsWarm(GaugePalette.FillFor(pct, PowerState.Discharging))) lastWarm = pct;

        Assert.True(lastWarm < 55, $"the draining scale still reads warm at {lastWarm} %.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsWarm(uint argb)
    {
        var c = Oklab.FromArgb(argb);
        double hue = Math.Atan2(c.B, c.A) * 180.0 / Math.PI;
        return hue is > 20 and < 70;
    }

    private static double Chroma(uint argb)
    {
        var c = Oklab.FromArgb(argb);
        return Math.Sqrt(c.A * c.A + c.B * c.B);
    }

    private static uint SrgbMidpoint(uint from, uint to)
    {
        uint Channel(int shift) =>
            (uint)((((from >> shift) & 0xFF) + ((to >> shift) & 0xFF)) / 2);

        return 0xFF000000u | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }
}
