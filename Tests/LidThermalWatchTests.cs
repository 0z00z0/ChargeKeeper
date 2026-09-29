using System.Text.RegularExpressions;
using ChargeKeeper.Services;
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
    [Theory]
    [InlineData(85.0)]
    public void AtOrAboveTheCeiling_TheHoldEnds(double celsius)
    {
        var watch = new LidThermalWatch();
        watch.Arm(85);
        Assert.Equal(LidThermalDecision.CeilingReached, watch.OnReading(celsius));
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

    // How the hold uses it.

    [Fact]
    public void TheCeilingIsArmedWithTheHoldAndOnlyWhereAReadingIsCurrentlyAvailable()
    {
        // Not a background monitor: it belongs to the hold, and a ceiling watching a value that
        // never arrives is a safeguard that cannot act.
        string body = SourceMethods.Body(
            Regex.Replace(File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "LidDelayService.cs"))),
                          @"//[^\r\n]*", string.Empty),
            "StartDelay");

        Assert.Contains("LidThermalCeilingEnabled", body, StringComparison.Ordinal);
        Assert.Contains("ThermalStatusService.PublishableCelsius", body, StringComparison.Ordinal);
        Assert.Contains("_thermal.Arm(", body, StringComparison.Ordinal);
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
    public void ARowWrittenBeforeTheColumnStillParses()
    {
        // Every earlier row carries five columns, and the file is the user's own history.
        Assert.True(BatteryHistoryService.TryParse(
            "2026-09-03T17:24:05+02:00,51,80,-12000,Discharging", out var sample));

        Assert.Equal(51, sample.Soc);
        Assert.Null(sample.TemperatureC);
    }
}
