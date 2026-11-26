using System.Text.RegularExpressions;
using ChargeKeeper.Services;
using Windows.System.Power;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>"Full by a chosen time": a ten-minute measurement with the cap lifted, then a wait with
/// the limit back, then the ordinary lift at target less the estimate and fifteen minutes.</summary>
public class ChargeByTimePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 20, 0, 0, TimeSpan.FromHours(1));
    private const int Full = 50_000;

    private static AwakeMark At(double minutes, double asleep = 0) =>
        new(T0.AddMinutes(minutes), TimeSpan.FromMinutes(minutes - asleep));

    private static ChargeReading Reading(double minutes, int mwh, BatteryStatus status = BatteryStatus.Charging,
                                         double asleep = 0) =>
        new(At(minutes, asleep), 100 * mwh / Full, status, mwh, Full);

    private static ChargeByTimeRecord Testing(DateTimeOffset target) => new(target, true, null, null, false);

    /// <summary>25 000 → 26 000 mWh over ten minutes is 6 000 mW; the 24 000 mWh left take 4 h.</summary>
    private static ChargeByTimeStep EndOfMeasurement(DateTimeOffset target, int gainMwh = 1_000) =>
        ChargeByTimePolicy.Decide(Testing(target), liftActive: true, Reading(10, 25_000 + gainMwh),
                                  lastTick: At(9.5), baseline: new ChargeBaseline(At(0), 25_000), taperFactor: null);

    [Fact]
    public void TheStartIsTheTargetLessTheEstimateAndFifteenMinutes()
    {
        var target = T0.AddHours(12);
        var step = EndOfMeasurement(target);

        Assert.Equal(ChargeByTimeAction.Wait, step.Action);
        Assert.Equal(TimeSpan.FromHours(4), step.Estimate);
        Assert.Equal(target - TimeSpan.FromHours(4) - TimeSpan.FromMinutes(15), step.StartAt);
    }

    [Fact]
    public void ATargetTooCloseChargesNowAndStatesTheLevelReachedByThen()
    {
        // Two hours at 6 000 mW from 26 000 mWh reach 38 000 of 50 000.
        var step = EndOfMeasurement(T0.AddMinutes(10).AddHours(2));

        Assert.Equal(ChargeByTimeAction.ChargeNow, step.Action);
        Assert.Equal(76, step.ExpectedPct);
    }

    [Fact]
    public void AMeasurementThatStoredNothingChargesNowWithoutAnEstimate()
    {
        var step = EndOfMeasurement(T0.AddHours(12), gainMwh: 0);

        Assert.Equal(ChargeByTimeAction.ChargeNow, step.Action);
        Assert.Null(step.Estimate);
    }

    [Fact]
    public void ASleepDuringTheMeasurementStartsItAgain()
    {
        var step = ChargeByTimePolicy.Decide(Testing(T0.AddHours(12)), liftActive: true,
            Reading(40, 30_000, asleep: 30), lastTick: At(9.5), baseline: new ChargeBaseline(At(0), 25_000), taperFactor: null);

        Assert.Equal(ChargeByTimeAction.BeginMeasurement, step.Action);
    }

    [Fact]
    public void AStartMissedAsleepRunsAtTheNextReadingAndALiftedCapWithAStartAheadIsPutBack()
    {
        var waiting = new ChargeByTimeRecord(T0.AddHours(12), false, T0.AddHours(8), null, false);
        var lastTick = At(0);

        Assert.Equal(ChargeByTimeAction.Start,
            ChargeByTimePolicy.Decide(waiting, liftActive: false, Reading(9 * 60, 20_000), lastTick, null, null).Action);
        Assert.Equal(ChargeByTimeAction.RestoreForWait,
            ChargeByTimePolicy.Decide(waiting, liftActive: true, Reading(60, 20_000), lastTick, null, null).Action);
    }

    [Fact]
    public void ALearnedTaperSlowsTheLastFifth()
    {
        // 15 000 mWh to 80 % at 6 000 mW is 2.5 h; the last 10 000 at half the rate 3 h 20 min.
        Assert.Equal(350, ChargeByTimePolicy.Estimate(25_000, Full, 6_000, 0.5).TotalMinutes, precision: 3);
    }

    [Fact]
    public void ARecordingWithASleepInItTeachesNothing()
    {
        var recorder = new ChargeTaperRecorder();
        recorder.Observe(Reading(0, 30_000));
        recorder.Observe(Reading(60, 40_000));
        recorder.Observe(Reading(180, 48_000, asleep: 30));

        var (sample, _) = recorder.Finish(Reading(240, Full, BatteryStatus.Idle, asleep: 30));
        Assert.Null(sample);
    }

    [Fact]
    public void ATimeChosenWithoutAChargerSchedulesNothing()
    {
        Assert.Equal(ChargeByTimeRefusal.NoCharger,
            ChargeByTimePolicy.Refusal(onCharger: false, pct: 50, limiting: true, running: false));
        Assert.Equal(ChargeByTimeRefusal.AlreadyFull,
            ChargeByTimePolicy.Refusal(onCharger: true, pct: 100, limiting: true, running: false));
    }

    // ---- Where the decision is carried out -------------------------------------------------

    [Fact]
    public void AfterTheMeasurementTheStartIsRecordedAndThenTheLimitPutBack()
    {
        // The start reaches the record before the device moves, so a crash in between puts the
        // limit back at the next start rather than leaving the cap off until full.
        string source = Regex.Replace(File.ReadAllText(RepoFiles.Find("Services/ChargeByTimeService.cs")),
                                      @"//[^\r\n]*", string.Empty);
        string wait = Regex.Match(source, @"case ChargeByTimeAction\.Wait:(.*?)break;", RegexOptions.Singleline).Groups[1].Value;

        int recorded = wait.IndexOf("ChargeByTimeStartAt", StringComparison.Ordinal);
        int restored = wait.IndexOf("EndLift(", StringComparison.Ordinal);
        Assert.True(recorded >= 0, "The wait no longer records its start.");
        Assert.True(restored >= 0, "The wait no longer puts the limit back after the measurement.");
        Assert.True(recorded < restored, "The start must be recorded before the limit is put back.");
        Assert.Contains("keepSchedule: true", wait[restored..], StringComparison.Ordinal);
    }
}
