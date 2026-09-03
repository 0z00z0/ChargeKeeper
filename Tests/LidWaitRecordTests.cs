using System.Text.RegularExpressions;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What a lid-close wait leaves behind in the power trail, and what a start with the lid already
/// shut decides. Both are recorded rather than inferred: a wait that says nothing while it runs
/// cannot be told apart from one that never started, and the two have opposite answers to "did the
/// application do this".
/// </summary>
public class LidWaitRecordTests
{
    private static string ServiceSource() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "LidDelayService.cs")));

    // ── The delay length: every write reaches the trail (#153) ───────────────────────────────

    [Fact]
    public void OnlyTheServiceWritesTheDelayLength()
    {
        // Three surfaces offer the length — the Settings page, the dashboard chip and the Home
        // Assistant number — and a write that bypasses the service is a change to the armed wait
        // with nothing in the trail to show it happened.
        foreach (string relative in new[]
                 {
                     Path.Combine("UI", "SettingsWindow.xaml.cs"),
                     Path.Combine("UI", "DashboardWindow.xaml.cs"),
                     Path.Combine("Services", "MqttCommandActions.cs"),
                 })
        {
            string source = File.ReadAllText(RepoFiles.Find(relative));
            Assert.DoesNotContain("LidDelayMinutes =", source, StringComparison.Ordinal);
            Assert.Contains("SetDelayMinutes", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachSurfaceNamesItselfInTheTrail()
    {
        // The reason the entry exists is to tell the three apart: the Home Assistant route changes
        // the armed wait with nothing local to observe.
        Assert.Contains("\"the Settings page\"",
                        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml.cs"))),
                        StringComparison.Ordinal);
        Assert.Contains("\"the dashboard\"",
                        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "DashboardWindow.xaml.cs"))),
                        StringComparison.Ordinal);
        Assert.Contains("\"Home Assistant\"",
                        File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "MqttCommandActions.cs"))),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteEntryNamesTheValueOnBothSidesOfIt()
    {
        string body = SourceMethods.Body(
            Regex.Replace(ServiceSource(), @"//[^\r\n]*", string.Empty), "SetDelayMinutes");

        Assert.Contains("previous", body, StringComparison.Ordinal);
        Assert.Contains("PowerLog.Event", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArmedSpanIsTheConfiguredOne()
    {
        // One expression, used for the timer and for the line that reports it — the mismatch this
        // pins is a trail naming a span other than the one that was armed.
        string body = SourceMethods.Body(
            Regex.Replace(ServiceSource(), @"//[^\r\n]*", string.Empty), "StartDelay");

        Assert.Contains("var delay = LidDelayPolicy.DelayFor(s.LidDelayMinutes);", body, StringComparison.Ordinal);
        Assert.Contains("new System.Threading.Timer(_ => OnTimerFired(), null, delay,", body, StringComparison.Ordinal);
        Assert.Contains("{delay.TotalMinutes:0} min", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0,   LidDelayPolicy.MinMinutes)]
    [InlineData(10,  10)]
    [InlineData(240, 240)]
    [InlineData(999, LidDelayPolicy.MaxMinutes)]
    public void TheSpanArmedIsTheClampedConfiguredValue(int configured, int expected) =>
        Assert.Equal(expected, (int)LidDelayPolicy.DelayFor(configured).TotalMinutes);

    // ── The battery target: recorded in every direction (#155) ───────────────────────────────

    // The outcomes arrive by name: LidTargetArm is internal, and an internal type cannot be a
    // public test method's parameter.
    [Theory]
    [InlineData(false, false, null,            "SwitchedOff")]
    [InlineData(false, true,  null,            "SwitchedOff")]
    [InlineData(true,  false, null,            "NoReading")]
    [InlineData(true,  true,  "Hold",          "Armed")]
    [InlineData(true,  true,  "TargetReached", "AlreadyThere")]
    [InlineData(true,  true,  "Charging",      "Charging")]
    public void EveryArmOutcomeIsDecided(bool enabled, bool hasReading, string? decision, string expected)
    {
        LidDischargeDecision? made = decision is null ? null : Enum.Parse<LidDischargeDecision>(decision);
        Assert.Equal(Enum.Parse<LidTargetArm>(expected), LidTargetArming.Decide(enabled, hasReading, made));
    }

    [Fact]
    public void EveryArmOutcomeHasSomethingToSay()
    {
        foreach (var arm in Enum.GetValues<LidTargetArm>())
        {
            var (what, why) = LidTargetArming.Describe(arm, 15, 51);
            Assert.False(string.IsNullOrWhiteSpace(what), $"{arm} has no headline.");
            Assert.False(string.IsNullOrWhiteSpace(why),  $"{arm} has no reason.");
        }
    }

    [Fact]
    public void TheOutcomeThatMeansSomethingIsWrongSaysSo()
    {
        // A target that is configured while no reading has ever reached the service is a broken
        // feed, not a state anyone asked for, so it does not share the wording of the ordinary ones.
        var (_, missing)    = LidTargetArming.Describe(LidTargetArm.NoReading,   15, null);
        var (_, switchedOff) = LidTargetArming.Describe(LidTargetArm.SwitchedOff, 15, null);

        Assert.NotEqual(switchedOff, missing);
        Assert.Contains("no battery reading", missing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLidCloseRecordsWhicheverOutcomeItReached()
    {
        // Unconditional: the entry that used to exist only for an armed target is what made a
        // target that never armed invisible.
        string body = SourceMethods.Body(
            Regex.Replace(ServiceSource(), @"//[^\r\n]*", string.Empty), "StartDelay");

        Assert.Contains("LidTargetArming.Decide", body, StringComparison.Ordinal);
        Assert.Contains("LidTargetArming.Describe", body, StringComparison.Ordinal);
    }

    // ── A start with the lid already shut (#154) ─────────────────────────────────────────────

    [Fact]
    public void AStartWithTheLidShutHandsTheActionBackRatherThanDoingNothing() =>
        // The state this replaces was neither side's: no wait running, and Windows' own lid-close
        // action taken away for the rest of the wait.
        Assert.Equal(LidDelayAction.HandBackUntilTheLidOpens,
                     LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: false,
                                               isFirstReading: true, handedBack: false));

    [Fact]
    public void TheReplayStillNeverSuspendsAMachineWhoseLidIsOpen() =>
        Assert.Equal(LidDelayAction.None,
                     LidDelayPolicy.OnLidState(LidState.Opened, enabled: true, delayPending: false,
                                               isFirstReading: true, handedBack: false));

    [Fact]
    public void TheOverrideIsTakenBackWhenTheLidOpens() =>
        Assert.Equal(LidDelayAction.TakeTheOverrideBack,
                     LidDelayPolicy.OnLidState(LidState.Opened, enabled: true, delayPending: false,
                                               isFirstReading: false, handedBack: true));

    [Fact]
    public void HandingBackHappensOnceRatherThanOnEveryClosedReading() =>
        Assert.Equal(LidDelayAction.None,
                     LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: false,
                                               isFirstReading: true, handedBack: true));

    [Fact]
    public void AFeatureThatIsOffDecidesNothingOnTheReplay() =>
        // Nothing was overridden, so there is nothing to hand back.
        Assert.Equal(LidDelayAction.None,
                     LidDelayPolicy.OnLidState(LidState.Closed, enabled: false, delayPending: false,
                                               isFirstReading: true, handedBack: false));

    [Fact]
    public void ARealCloseAfterTheHandBackStillArmsAWait() =>
        // Only the replay declines. Once the lid has opened and closed again, the wait is served in
        // full.
        Assert.Equal(LidDelayAction.StartDelay,
                     LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: false,
                                               isFirstReading: false, handedBack: false));

    [Fact]
    public void BothSidesOfTheHandBackAreRecorded()
    {
        string body = SourceMethods.Body(
            Regex.Replace(ServiceSource(), @"//[^\r\n]*", string.Empty), "OnLidState");

        Assert.Contains("HandBackUntilTheLidOpens", body, StringComparison.Ordinal);
        Assert.Contains("TakeTheOverrideBack", body, StringComparison.Ordinal);
        Assert.Contains("RestoreSavedAction", body, StringComparison.Ordinal);
        Assert.Contains("CaptureAndOverride", body, StringComparison.Ordinal);
    }
}
