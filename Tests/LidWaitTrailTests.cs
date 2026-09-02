using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// A lid-close wait that says nothing while it runs cannot be told apart from one that never
/// started, so the absence of these lines proves nothing. These pin what a running wait says, what
/// it says at the moment it ends, and — the ordering guards at the bottom — that the end line and
/// the one-off stand-down both reach disk before the suspend rather than after it.
/// </summary>
public class LidWaitTrailTests
{
    private static string ServiceSource() => File.ReadAllText(RepoFiles.Find("Services/LidDelayService.cs"));

    private static LidWaitTrail TimeOnly(int minutes = 30)
    {
        var trail = new LidWaitTrail();
        trail.Start(timeSet: true, delayMinutes: minutes, targetPercent: null, levelNow: 80);
        return trail;
    }

    private static LidWaitTrail BatteryOnly(int target = 40, int level = 80)
    {
        var trail = new LidWaitTrail();
        trail.Start(timeSet: false, delayMinutes: 30, targetPercent: target, levelNow: level);
        return trail;
    }

    private static LidWaitTrail Both(int minutes = 30, int target = 40, int level = 80)
    {
        var trail = new LidWaitTrail();
        trail.Start(timeSet: true, delayMinutes: minutes, targetPercent: target, levelNow: level);
        return trail;
    }

    // ---- the owner's two numbers -------------------------------------------------------------

    [Fact]
    public void TheReportSpacing_IsFiveMinutesAndFivePerCent()
    {
        Assert.Equal(5, LidWaitTrail.MinutesBetweenTimeReports);
        Assert.Equal(5, LidWaitTrail.PercentBetweenBatteryReports);
        Assert.Equal(TimeSpan.FromMinutes(5), LidWaitTrail.TimeReportInterval);
    }

    // ---- the delay report, on its own ---------------------------------------------------------

    [Fact]
    public void ADelayReport_SaysHowFarIntoTheDelayTheWaitIs()
    {
        Assert.Equal("Still waiting with the lid closed: 5 minutes into the 30 minute delay.",
                     TimeOnly().OnElapsed(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ADelayReport_ReadsAsASentenceAtOneMinuteToo()
    {
        Assert.Equal("Still waiting with the lid closed: 1 minute into the 30 minute delay.",
                     TimeOnly().OnElapsed(TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]   // the last tick lands with the end of the wait
    [InlineData(45)]   // a tick that outlived the wait
    public void ADelayReport_IsNotDueOutsideTheWait(int minutes)
    {
        Assert.Null(TimeOnly().OnElapsed(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void AWaitWithNoDelaySet_ReportsNoDelayProgressAtAll()
    {
        Assert.Null(BatteryOnly().OnElapsed(TimeSpan.FromMinutes(5)));
        Assert.Null(BatteryOnly().OnElapsed(TimeSpan.FromMinutes(20)));
    }

    // ---- the battery report, on its own -------------------------------------------------------

    [Fact]
    public void ABatteryReport_SaysWhereTheBatteryIsAndWhereItIsHeading()
    {
        Assert.Equal("Still waiting with the lid closed: the battery is at 75 %, on its way down to the 40 % target.",
                     BatteryOnly(target: 40, level: 80).OnBatteryReading(75));
    }

    [Fact]
    public void ABatteryReport_IsNotDueUntilTheLevelHasFallenFivePoints()
    {
        var trail = BatteryOnly(level: 80);

        Assert.Null(trail.OnBatteryReading(79));
        Assert.Null(trail.OnBatteryReading(76));
        Assert.NotNull(trail.OnBatteryReading(75));
    }

    [Fact]
    public void ABatteryReport_SpacesItselfFromTheLevelLastReported()
    {
        var trail = BatteryOnly(level: 80);

        Assert.NotNull(trail.OnBatteryReading(75));
        Assert.Null(trail.OnBatteryReading(71));      // four points since the last report
        Assert.NotNull(trail.OnBatteryReading(70));
    }

    [Fact]
    public void AWaitWithNoBatteryTarget_ReportsNoBatteryProgressAtAll()
    {
        var trail = TimeOnly();

        Assert.Null(trail.OnBatteryReading(60));
        Assert.Null(trail.OnBatteryReading(10));
    }

    // ---- both conditions at once --------------------------------------------------------------

    [Fact]
    public void AWaitOnBothConditions_ReportsOnBothIndependently()
    {
        var trail = Both(minutes: 30, target: 40, level: 80);

        Assert.NotNull(trail.OnElapsed(TimeSpan.FromMinutes(5)));
        Assert.NotNull(trail.OnBatteryReading(75));
        Assert.NotNull(trail.OnElapsed(TimeSpan.FromMinutes(10)));
        Assert.NotNull(trail.OnBatteryReading(70));
    }

    [Fact]
    public void OneConditionsReports_DoNotConsumeTheOthers()
    {
        var trail = Both(level: 80);

        // A battery fall that is not yet worth reporting must not silence the clock, and a delay
        // report must not advance the level the battery is spaced from.
        Assert.Null(trail.OnBatteryReading(78));
        Assert.NotNull(trail.OnElapsed(TimeSpan.FromMinutes(5)));
        Assert.NotNull(trail.OnBatteryReading(75));
    }

    // ---- the end of the wait ------------------------------------------------------------------

    [Fact]
    public void AWaitEndedByTheDelay_NamesTheDelayAndItsLength()
    {
        var trail = Both(minutes: 30, target: 40, level: 80);
        trail.Arrived(LidWaitEnd.DelayElapsed);

        string line = trail.End(72);

        Assert.Equal("The lid-close wait ended because the 30 minute delay ran out.", line);
        Assert.DoesNotContain("battery", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWaitEndedByTheBattery_NamesTheTargetAndTheLevelItEndedOn()
    {
        var trail = Both(minutes: 30, target: 40, level: 80);
        trail.Arrived(LidWaitEnd.BatteryTarget);

        string line = trail.End(39);

        Assert.Equal("The lid-close wait ended because the battery reached its target of 40 %, standing at 39 %.",
                     line);
        Assert.DoesNotContain("delay ran out", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstConditionToArrive_IsTheOneTheEndLineNames()
    {
        var trail = Both();
        trail.Arrived(LidWaitEnd.BatteryTarget);
        trail.Arrived(LidWaitEnd.DelayElapsed);   // a moment later, and too late to take the credit

        Assert.Contains("battery reached its target", trail.End(39), StringComparison.Ordinal);
    }

    [Fact]
    public void AWaitWithNothingLeftToWaitFor_SaysSoRatherThanNamingACondition()
    {
        var trail = new LidWaitTrail();
        trail.Start(timeSet: false, delayMinutes: 30, targetPercent: null, levelNow: 80);

        Assert.Equal("The lid-close wait ended because there was nothing left to wait for: " +
                     "neither the delay nor a battery target was set.", trail.End(80));
    }

    [Fact]
    public void ClearingTheTrail_StopsBothReportsAndForgetsWhatEndedTheWait()
    {
        var trail = Both();
        trail.Arrived(LidWaitEnd.DelayElapsed);
        trail.Clear();

        Assert.Null(trail.OnElapsed(TimeSpan.FromMinutes(5)));
        Assert.Null(trail.OnBatteryReading(60));
        Assert.Contains("nothing left to wait for", trail.End(60), StringComparison.Ordinal);
    }

    [Fact]
    public void StartingASecondWait_ReportsAgainstTheNewValuesRatherThanTheOldOnes()
    {
        var trail = Both(minutes: 30, target: 40, level: 80);
        trail.Arrived(LidWaitEnd.DelayElapsed);

        trail.Start(timeSet: true, delayMinutes: 10, targetPercent: 20, levelNow: 55);

        Assert.Equal("Still waiting with the lid closed: 5 minutes into the 10 minute delay.",
                     trail.OnElapsed(TimeSpan.FromMinutes(5)));
        Assert.Contains("the 20 % target", trail.OnBatteryReading(50), StringComparison.Ordinal);
        Assert.Contains("nothing left to wait for", trail.End(50), StringComparison.Ordinal);
    }

    // ---- ordering: the whole point of the exercise ---------------------------------------------

    /// <summary>
    /// The end line has to reach disk before the machine goes down. SetSuspendState does not return
    /// until the machine resumes, so a line written after it is never written at all on a machine
    /// that does not come back with the application running — and its absence would then prove
    /// nothing.
    /// </summary>
    [Fact]
    public void TheEndOfWaitLine_IsWrittenBeforeTheSuspendCall()
    {
        string body = SourceMethods.Body(ServiceSource(), "Complete");

        int line    = body.IndexOf("PowerLog.Say(ended)", StringComparison.Ordinal);
        int suspend = body.IndexOf("NativeMethods.Suspend()", StringComparison.Ordinal);

        Assert.True(line    >= 0, "The end-of-wait line is not written in Complete.");
        Assert.True(suspend >= 0, "Complete no longer contains the suspend call.");
        Assert.True(line < suspend, "The end-of-wait line must be written before the suspend call.");
    }

    /// <summary>The same reason: a stand-down written after the suspend call is applied on waking,
    /// so a machine that never wakes with the application running keeps a one-off delay armed for
    /// the next lid close.</summary>
    [Fact]
    public void TheOneOffStandDown_IsAppliedBeforeTheSuspendCall()
    {
        string body = SourceMethods.Body(ServiceSource(), "Complete");

        int standDown = body.IndexOf("TurnOffIfDue(LidDelayOutcome.Slept)", StringComparison.Ordinal);
        int suspend   = body.IndexOf("NativeMethods.Suspend()", StringComparison.Ordinal);

        Assert.True(standDown >= 0, "The one-off stand-down is not applied in Complete.");
        Assert.True(standDown < suspend, "The stand-down must be applied before the suspend call.");
    }

    /// <summary>One stand-down, before the suspend. A second call after it would put the write back
    /// on the resume path this guard exists to keep it off.</summary>
    [Fact]
    public void TheOneOffStandDown_IsNotAlsoAppliedOnTheResumePath()
    {
        string body = SourceMethods.Body(ServiceSource(), "Complete");
        int suspend = body.IndexOf("NativeMethods.Suspend()", StringComparison.Ordinal);

        Assert.DoesNotContain("TurnOffIfDue(LidDelayOutcome.Slept)", body[suspend..], StringComparison.Ordinal);
    }

    /// <summary>The end line is composed from the wait's own record before that record is cleared;
    /// composing it afterwards would describe an empty wait.</summary>
    [Fact]
    public void TheEndOfWaitLine_IsComposedBeforeTheWaitIsCleared()
    {
        string body = SourceMethods.Body(ServiceSource(), "Complete");

        int composed = body.IndexOf("_trail.End(", StringComparison.Ordinal);
        int cleared  = body.IndexOf("ClearLocked()", StringComparison.Ordinal);

        Assert.True(composed >= 0 && cleared >= 0);
        Assert.True(composed < cleared);
    }

    /// <summary>A refused suspend is not a lid close that ran its course, so the stand-down taken
    /// on its behalf has to be put back.</summary>
    [Fact]
    public void ARefusedSuspend_PutsAStandDownBack()
    {
        string body = SourceMethods.Body(ServiceSource(), "Complete");
        int suspend = body.IndexOf("NativeMethods.Suspend()", StringComparison.Ordinal);

        Assert.Contains("SetEnabled(true,", body[suspend..], StringComparison.Ordinal);
    }

    // ---- wiring: the reports actually run ------------------------------------------------------

    /// <summary>The delay report runs only for the condition it reports on: with no delay set there
    /// is no elapsed fraction to report.</summary>
    [Fact]
    public void TheDelayReport_IsArmedOnlyWhenTheDelayItselfIs()
    {
        string body = SourceMethods.Body(ServiceSource(), "StartDelay");

        Assert.Contains("OnHeartbeat", body, StringComparison.Ordinal);
        Assert.Contains("LidWaitTrail.TimeReportInterval", body, StringComparison.Ordinal);
        Assert.Contains("_heartbeat = armedTimer", body, StringComparison.Ordinal);
    }

    /// <summary>Elapsed time is measured, not counted in ticks: a coalesced or delayed timer would
    /// otherwise report the time it meant to fire at rather than the time that passed.</summary>
    [Fact]
    public void TheDelayReport_MeasuresElapsedTimeRatherThanCountingTicks()
    {
        string body = SourceMethods.Body(ServiceSource(), "OnHeartbeat");

        Assert.Contains("_waitStartedAt", body, StringComparison.Ordinal);
        Assert.Contains("_trail.OnElapsed", body, StringComparison.Ordinal);
    }

    /// <summary>The battery report rides the readings that arrive anyway, so it needs no timer of
    /// its own — and it is asked for only while a target is still outstanding.</summary>
    [Fact]
    public void TheBatteryReport_RunsOffTheReadingsWhileTheTargetIsOutstanding()
    {
        string body = SourceMethods.Body(ServiceSource(), "OnBatteryReport");

        Assert.Contains("LidDischargeDecision.Hold", body, StringComparison.Ordinal);
        Assert.Contains("_trail.OnBatteryReading(percent)", body, StringComparison.Ordinal);
    }

    /// <summary>A report timer left running past the wait would keep talking about a lid close that
    /// is over.</summary>
    [Fact]
    public void TheDelayReportTimer_IsDisposedWhenTheWaitEnds()
    {
        string body = SourceMethods.Body(ServiceSource(), "ClearLocked");

        Assert.Contains("_heartbeat?.Dispose()", body, StringComparison.Ordinal);
        Assert.Contains("_trail.Clear()", body, StringComparison.Ordinal);
    }

    /// <summary>The stand-down says so in the same plain register as the rest of the trail.</summary>
    [Fact]
    public void TheStandDown_SaysItHappenedBeforeTheMachineSlept()
    {
        Assert.Equal("The lid-close delay was set to run once, so it switched itself off before " +
                     "putting the machine to sleep.", LidWaitTrail.SwitchedOffBeforeSleeping);

        string body = SourceMethods.Body(ServiceSource(), "TurnOffIfDue");
        Assert.Contains("PowerLog.Say(LidWaitTrail.SwitchedOffBeforeSleeping)", body, StringComparison.Ordinal);
    }
}
