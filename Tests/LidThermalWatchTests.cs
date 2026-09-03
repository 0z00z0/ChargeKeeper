using System.Text.RegularExpressions;
using ChargeKeeper.Services;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The temperature ceiling that ends a lid-close hold. The rules are exercised without heating a
/// laptop; what the tests are really pinning is the two ways this feature can be worse than the risk
/// it guards against — firing on a reading it should not trust, and sleeping a machine that is not
/// in a hold at all.
/// </summary>
public class LidThermalWatchTests
{
    [Fact]
    public void NothingIsArmed_SoNoReadingDecidesAnything()
    {
        var watch = new LidThermalWatch();
        Assert.False(watch.IsWatching);
        Assert.Equal(LidThermalDecision.NotWatching, watch.OnReading(120));
    }

    [Fact]
    public void BelowTheCeiling_TheHoldCarriesOn()
    {
        var watch = new LidThermalWatch();
        watch.Arm(85);
        Assert.Equal(LidThermalDecision.Hold, watch.OnReading(84.9));
        Assert.True(watch.IsWatching);
    }

    [Theory]
    [InlineData(85.0)]
    [InlineData(96.0)]
    public void AtOrAboveTheCeiling_TheHoldEnds(double celsius)
    {
        var watch = new LidThermalWatch();
        watch.Arm(85);
        Assert.Equal(LidThermalDecision.CeilingReached, watch.OnReading(celsius));
    }

    [Fact]
    public void TheCeilingReleasesItself_SoOneReadingCannotEndTheHoldTwice()
    {
        var watch = new LidThermalWatch();
        watch.Arm(85);

        Assert.Equal(LidThermalDecision.CeilingReached, watch.OnReading(90));
        Assert.False(watch.IsWatching);
        Assert.Equal(LidThermalDecision.NotWatching, watch.OnReading(90));
    }

    [Fact]
    public void AMissingReadingStandsTheSafeguardDown_RatherThanTriggeringIt()
    {
        // A value that is not there is not a hot machine. Firing on one would sleep a working
        // machine repeatedly for no reason, which is worse than the defect being guarded against.
        var watch = new LidThermalWatch();
        watch.Arm(85);

        Assert.Equal(LidThermalDecision.NoReading, watch.OnReading(null));
        Assert.True(watch.IsWatching);
    }

    [Fact]
    public void DisarmingLeavesNothingToFire()
    {
        var watch = new LidThermalWatch();
        watch.Arm(85);
        watch.Disarm();

        Assert.Equal(LidThermalDecision.NotWatching, watch.OnReading(120));
    }

    [Theory]
    [InlineData(0,   LidThermalWatch.MinCelsius)]
    [InlineData(85,  85)]
    [InlineData(500, LidThermalWatch.MaxCelsius)]
    public void TheCeilingIsClampedToAPlausibleBand(int configured, int expected)
    {
        var watch = new LidThermalWatch();
        watch.Arm(configured);
        Assert.Equal(expected, watch.Ceiling);
    }

    // How the hold uses it.

    [Fact]
    public void AnEarlyEndOutranksTheConditionsTheWaitWasOn() =>
        // The point of the ceiling is to act before the wait would have, so it does not queue behind
        // a delay that still has an hour to run.
        Assert.True(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: false,
                                              targetSet: true, targetArrived: false, endedEarly: true));

    [Fact]
    public void WithoutAnEarlyEndTheConditionsStillDecide() =>
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: false,
                                               targetSet: false, targetArrived: false));

    [Fact]
    public void TheCeilingIsArmedWithTheHoldAndOnlyWhereAReadingExists()
    {
        // Not a background monitor: it belongs to the hold, and a ceiling watching a value that
        // never arrives is a safeguard that cannot act.
        string body = SourceMethods.Body(
            Regex.Replace(File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "LidDelayService.cs"))),
                          @"//[^\r\n]*", string.Empty),
            "StartDelay");

        Assert.Contains("LidThermalCeilingEnabled", body, StringComparison.Ordinal);
        Assert.Contains("ThermalStatusService.PublishableCelsius is not null", body, StringComparison.Ordinal);
        Assert.Contains("_thermal.Arm(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHoldStandsTheCeilingDownWhenItEnds()
    {
        string source = File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "LidDelayService.cs")));
        Assert.Contains("_thermal.Disarm()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheActionIsSleep_NeverShutdown()
    {
        // A shutdown taken on a temperature reading throws away unsaved work, and a temperature
        // reading is the input least worth trusting that far.
        string source = File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "LidDelayService.cs")));
        foreach (string forbidden in new[] { "ExitWindowsEx", "InitiateShutdown", "Shutdown(" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatHappenedIsSaidAtTheNextWake()
    {
        // Nobody sees a notification inside a closed bag, so the event is recorded and reported
        // later — and cleared as it is reported, or it would be repeated at every resume.
        string app = Regex.Replace(File.ReadAllText(RepoFiles.Find("App.xaml.cs")), @"//[^\r\n]*", string.Empty);
        string body = SourceMethods.Body(app, "ReportAnEarlySleepIfOneIsOwed");

        Assert.Contains("LidThermalSleptAtCelsius = null", body, StringComparison.Ordinal);
        Assert.Contains("NotifySleptWhileHot", body, StringComparison.Ordinal);
        Assert.Contains("PowerLog.Event", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadingReachesTheHistoryFileSoAJourneyCanBeLookedAtLater()
    {
        // The shape of a real journey is what a sensible ceiling is chosen from, and a series that
        // sits flat all day is visible proof the reading is worthless on that machine.
        var sample = new BatterySample(DateTime.UtcNow, 51, 80, -12_000, PowerState.Discharging, 72.5);
        string line = BatteryHistoryService.Format(sample);

        Assert.EndsWith("72.5", line, StringComparison.Ordinal);
        Assert.True(BatteryHistoryService.TryParse(line, out var back));
        Assert.Equal(72.5, back.TemperatureC);
    }

    [Fact]
    public void ARowWrittenBeforeTheColumnStillParses()
    {
        // Every earlier row carries five columns, and the file is the user's own history.
        Assert.True(BatteryHistoryService.TryParse(
            "2026-09-03T17:24:05+02:00,51,80,-12000,Discharging", out var sample));

        Assert.Equal(51, sample.Soc);
        Assert.Null(sample.TemperatureC);
    }

    [Fact]
    public void TheColumnIsNamedInTheHeader() =>
        Assert.Contains("temperature_c", BatteryHistoryService.HeaderColumns, StringComparison.Ordinal);
}
