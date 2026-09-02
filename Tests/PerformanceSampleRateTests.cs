using System.Globalization;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The advertised rate range is 10 Hz down to 0.1 Hz. These hold the ladder to those two ends: a
/// step added outside them widens what the feature offers, and a step whose period is zero would
/// schedule a timer that never stops firing.
/// </summary>
public class PerformanceSampleRateTests
{
    private static IEnumerable<PerformanceSampleRate> Steps => PerformanceSampleRates.All;

    [Fact]
    public void EveryStepSitsInsideTheAdvertisedRange() =>
        Assert.All(Steps, rate =>
        {
            int ms = rate.PeriodMilliseconds();
            Assert.InRange(ms,
                PerformanceSampleRates.FastestMilliseconds,
                PerformanceSampleRates.SlowestMilliseconds);
        });

    /// <summary>Inside the range is not enough: the ladder has to REACH both ends, or the setting
    /// quietly stops offering the range it claims.</summary>
    [Fact]
    public void TheLadderReachesBothEndsOfTheRange()
    {
        var periods = Steps.Select(r => r.PeriodMilliseconds()).ToList();

        Assert.Equal(PerformanceSampleRates.FastestMilliseconds, periods.Min());
        Assert.Equal(PerformanceSampleRates.SlowestMilliseconds, periods.Max());
    }

    [Fact]
    public void TheEndsAreTenHertzAndATenthOfAHertz()
    {
        Assert.Equal(10.0, 1000.0 / PerformanceSampleRates.FastestMilliseconds, 6);
        Assert.Equal(0.1,  1000.0 / PerformanceSampleRates.SlowestMilliseconds, 6);
    }

    [Fact]
    public void TheStepsRunFastToSlowWithNoRepeats()
    {
        var periods = Steps.Select(r => r.PeriodMilliseconds()).ToList();

        Assert.Equal(periods.Count, periods.Distinct().Count());
        Assert.Equal(periods.Order(), periods);
    }

    [Fact]
    public void NoStepHasAZeroOrNegativePeriod() =>
        Assert.All(Steps, rate => Assert.True(rate.Period() > TimeSpan.Zero));

    [Fact]
    public void PeriodAndHertzAgree() =>
        Assert.All(Steps, rate =>
            Assert.Equal(1000.0 / rate.PeriodMilliseconds(), rate.Hertz(), 6));

    // ── Values from outside the enum ────────────────────────────────────────────────────────────

    /// <summary>Settings enums round-trip as strings, but the converter also accepts integers, so a
    /// hand-edited number reaches here undefined.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(99)]
    public void AValueNamingNoStepResolvesToTheDefault(int stored)
    {
        var rate = (PerformanceSampleRate)stored;

        Assert.Equal(PerformanceSampleRates.Default, PerformanceSampleRates.Normalise(rate));
        Assert.Equal(PerformanceSampleRates.Default.PeriodMilliseconds(), rate.PeriodMilliseconds());
        Assert.InRange(rate.PeriodMilliseconds(),
            PerformanceSampleRates.FastestMilliseconds,
            PerformanceSampleRates.SlowestMilliseconds);
    }

    [Fact]
    public void EveryDefinedStepIsLeftAloneByNormalise() =>
        Assert.All(Steps, rate => Assert.Equal(rate, PerformanceSampleRates.Normalise(rate)));

    [Fact]
    public void TheDefaultIsOneOfTheSteps() =>
        Assert.Contains(PerformanceSampleRates.Default, Steps);

    // ── The label ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shown to a reader, so it renders in the machine's culture rather than a pinned one. The two
    /// cultures are built with user overrides OFF: a machine can carry a regional override that puts
    /// a comma decimal separator on en-GB, which is exactly what reading CurrentCulture is for, but
    /// makes a test that reads it a measurement of the machine rather than of the code.
    /// </summary>
    [Fact]
    public void TheLabelFollowsTheMachineCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nb-NO", useUserOverride: false);
            Assert.Equal("0,5 Hz", PerformanceSampleRate.HalfHz.Label());
            Assert.Equal("10 Hz",  PerformanceSampleRate.TenHz.Label());

            CultureInfo.CurrentCulture = new CultureInfo("en-GB", useUserOverride: false);
            Assert.Equal("0.5 Hz", PerformanceSampleRate.HalfHz.Label());
            Assert.Equal("10 Hz",  PerformanceSampleRate.TenHz.Label());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void EveryStepHasANonEmptyLabel() =>
        Assert.All(Steps, rate => Assert.False(string.IsNullOrWhiteSpace(rate.Label())));
}
