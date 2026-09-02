using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The log could not tell a machine that slept from an application that stopped: both are a gap in
/// the samples. These pin the pair of entries that separates them.
/// </summary>
public class SleepWatchTests : IDisposable
{
    public SleepWatchTests() => SleepWatch.ResetForTests();

    public void Dispose()
    {
        SleepWatch.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private static string AppSource() => File.ReadAllText(RepoFiles.Find("App.xaml.cs"));

    private static readonly DateTimeOffset Slept = new(2026, 9, 1, 17, 39, 29, TimeSpan.Zero);

    [Fact]
    public void AResumeWithoutASuspend_SaysNothingRatherThanInventingADuration()
    {
        Assert.Null(SleepWatch.Wake(Slept.AddMinutes(5), 40));
    }

    [Fact]
    public void ASuspendAndResume_ReportTheTimeAwayAndWhatTheBatteryDid()
    {
        SleepWatch.RecordSleep(Slept, 40);

        Assert.Equal(
            "The machine woke after 16 minutes asleep. The battery fell from 40 % to 39 % while it was away.",
            SleepWatch.Wake(Slept.AddMinutes(16), 39));
    }

    /// <summary>A second resume must not replay the first: the pairing is consumed by the wake it
    /// belongs to.</summary>
    [Fact]
    public void ASecondResume_HasNothingLeftToPairWith()
    {
        SleepWatch.RecordSleep(Slept, 40);
        SleepWatch.Wake(Slept.AddMinutes(16), 39);

        Assert.Null(SleepWatch.Wake(Slept.AddMinutes(20), 38));
    }

    [Fact]
    public void ARisingBattery_IsReportedAsARise()
    {
        Assert.Equal(
            "The machine woke after 2 hours 5 minutes asleep. The battery rose from 30 % to 88 % while it was away.",
            SleepWatch.WakeSentence(TimeSpan.FromMinutes(125), 30, 88));
    }

    [Fact]
    public void AnUnchangedBattery_IsReportedAsUnchanged()
    {
        Assert.Equal("The machine woke after 4 minutes asleep. The battery was unchanged at 55 %.",
                     SleepWatch.WakeSentence(TimeSpan.FromMinutes(4), 55, 55));
    }

    /// <summary>No reading at one end means no claim about the drain — a missing level must not be
    /// reported as no change.</summary>
    [Fact]
    public void AMissingReading_LeavesTheBatteryOutRatherThanGuessing()
    {
        Assert.Equal("The machine woke after 10 minutes asleep.",
                     SleepWatch.WakeSentence(TimeSpan.FromMinutes(10), null, 39));
        Assert.Equal("The machine woke after 10 minutes asleep.",
                     SleepWatch.WakeSentence(TimeSpan.FromMinutes(10), 40, null));
    }

    [Theory]
    [InlineData(0,   "less than a minute")]
    [InlineData(1,   "1 minute")]
    [InlineData(16,  "16 minutes")]
    [InlineData(60,  "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(125, "2 hours 5 minutes")]
    [InlineData(61,  "1 hour 1 minute")]
    public void ADuration_ReadsAsAPersonWouldSayIt(int minutes, string expected)
    {
        Assert.Equal(expected, SleepWatch.Duration(TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>A clock that moved backwards across the suspend is not a duration to state as fact.</summary>
    [Fact]
    public void ABackwardsClock_IsNotReportedAsATime()
    {
        Assert.Equal("an unknown time", SleepWatch.Duration(TimeSpan.FromMinutes(-3)));
    }

    [Fact]
    public void WentToSleep_IsAPlainSentence()
    {
        Assert.Equal("The machine went to sleep.", SleepWatch.WentToSleep);
    }

    /// <summary>Both halves have to be wired to the Windows power broadcast, or the pair never
    /// appears in the log the investigation reads.</summary>
    [Fact]
    public void BothHalvesOfThePair_AreWiredToTheWindowsPowerBroadcast()
    {
        string body = SourceMethods.Body(AppSource(), "OnPowerModeChanged");

        Assert.Contains("PowerModes.Suspend", body, StringComparison.Ordinal);
        Assert.Contains("SleepWatch.RecordSleep", body, StringComparison.Ordinal);
        Assert.Contains("SleepWatch.WentToSleep", body, StringComparison.Ordinal);
        Assert.Contains("ReportWake()", body, StringComparison.Ordinal);
    }

    /// <summary>The level on waking is read fresh. The cached one is from before the suspend, and
    /// using it would report every drain as nothing.</summary>
    [Fact]
    public void TheLevelOnWaking_IsReadFreshRatherThanTakenFromTheCache()
    {
        string body = SourceMethods.Body(AppSource(), "ReportWake");

        Assert.Contains("Battery.AggregateBattery.GetReport()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastIconState", body, StringComparison.Ordinal);
    }
}
