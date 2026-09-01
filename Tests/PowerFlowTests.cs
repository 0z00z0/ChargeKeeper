using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The reading-to-symbol decision, with no drawing involved. The tray mark exists because the
/// reported power state cannot see a machine that is plugged in and still draining; these pin the
/// rule that reads the truth out of the rate's sign instead.
/// </summary>
public class PowerFlowTests
{
    [Fact]
    public void APositiveRateIsChargeGoingIn()
    {
        Assert.Equal(PowerFlow.In, PowerFlows.From(26375));
        Assert.Equal(PowerFlow.In, PowerFlows.From(120));
    }

    [Fact]
    public void ANegativeRateIsChargeGoingOut()
    {
        Assert.Equal(PowerFlow.Out, PowerFlows.From(-22558));
        Assert.Equal(PowerFlow.Out, PowerFlows.From(-120));
    }

    /// <summary>The whole reason the mark exists. A pack falling while the adapter is connected
    /// reports a mains power state, and only the rate's sign says the pack is losing charge. The
    /// figures are a real recorded episode: −22.7 W on mains with the level dropping 70 → 68 %.
    /// </summary>
    [Fact]
    public void PluggedInButDraining_ResolvesToOut_NotIn()
    {
        Assert.Equal(PowerFlow.Out, PowerFlows.From(-22739));
        Assert.Equal(PowerFlow.Out, PowerFlows.From(-34371));

        // Stated the other way round, so a rule that ignored the sign could not pass: the same
        // magnitude with the other sign is the opposite answer.
        Assert.NotEqual(PowerFlows.From(22739), PowerFlows.From(-22739));
    }

    [Fact]
    public void ARateInsideTheBandIsAtRest()
    {
        Assert.Equal(PowerFlow.Rest, PowerFlows.From(0));
        Assert.Equal(PowerFlow.Rest, PowerFlows.From(50));
        Assert.Equal(PowerFlow.Rest, PowerFlows.From(-50));
    }

    /// <summary>Both edges of the band, exactly. The band is half-open: its own magnitude counts as
    /// flow, one below it does not.</summary>
    [Fact]
    public void TheBandEdgesFallOnTheDeclaredSide()
    {
        Assert.Equal(PowerFlow.Rest, PowerFlows.From( PowerFlows.RestBandMw - 1));
        Assert.Equal(PowerFlow.Rest, PowerFlows.From(-PowerFlows.RestBandMw + 1));
        Assert.Equal(PowerFlow.In,   PowerFlows.From( PowerFlows.RestBandMw));
        Assert.Equal(PowerFlow.Out,  PowerFlows.From(-PowerFlows.RestBandMw));
    }

    /// <summary>An absent reading must draw nothing. Null is not a rate of zero: the app coalesces
    /// the reading to 0 for the surfaces that need a number, and a mark drawn from that would claim
    /// "at rest" on a machine with no battery at all.</summary>
    [Fact]
    public void AnUnavailableReadingProducesNoFlowAtAll()
    {
        Assert.Null(PowerFlows.From(null));
    }

    /// <summary>The band is compared as bounds rather than through Math.Abs, which throws on
    /// int.MinValue. A driver returning a garbage extreme must still resolve, not crash the tick.
    /// </summary>
    [Fact]
    public void TheExtremesOfTheRangeResolveWithoutThrowing()
    {
        Assert.Equal(PowerFlow.Out, PowerFlows.From(int.MinValue));
        Assert.Equal(PowerFlow.In,  PowerFlows.From(int.MaxValue));
    }

    /// <summary>The marks are the dashboard's status glyphs, so the two surfaces cannot drift into
    /// two vocabularies for one idea.</summary>
    [Fact]
    public void EachFlowCarriesItsOwnMark()
    {
        Assert.Equal("▲", PowerFlows.Glyph(PowerFlow.In));
        Assert.Equal("▼", PowerFlows.Glyph(PowerFlow.Out));
        Assert.Equal("●", PowerFlows.Glyph(PowerFlow.Rest));

        var marks = new[] { PowerFlow.In, PowerFlow.Out, PowerFlow.Rest }.Select(PowerFlows.Glyph);
        Assert.Equal(3, marks.Distinct().Count());
    }

    /// <summary>The rest band is the same figure the remaining-time estimates refuse to divide by,
    /// so a rate the tray calls "at rest" can never produce a time estimate and vice versa.</summary>
    [Fact]
    public void TheRestBandIsTheSameGuardTheTimeEstimatesUse()
    {
        Assert.Null(BatteryStatsFormatter.HoursToFull(PowerFlows.RestBandMw - 1, 20_000, 50_000));
        Assert.NotNull(BatteryStatsFormatter.HoursToFull(PowerFlows.RestBandMw, 20_000, 50_000));

        Assert.Equal("—", BatteryStatsFormatter.FormatTimeRemaining(PowerFlows.RestBandMw - 1, 20_000, 50_000));
        Assert.Equal("—", BatteryStatsFormatter.FormatTimeRemaining(-PowerFlows.RestBandMw + 1, 20_000, 50_000));
    }
}
