using System.Text.Json;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The pure decision table behind the lid-close delay — no power scheme, no timer, no suspend.
public class LidDelayPolicyTests
{
    // OnLidState

    [Fact]
    public void OnLidState_FirstReadingIsASeed_StartsNoWaitAndHandsTheActionBack()
    {
        // Windows invokes the power-setting callback immediately on registration with the current lid
        // state; acting on that replay would suspend the machine minutes after the app merely started.
        // It is still not a close and still arms nothing — but a start that finds the lid already
        // shut can serve no wait either, so the lid-close action goes back to Windows rather than
        // being left parked on the override with nobody serving it.
        var action = LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: false,
                                               isFirstReading: true);

        Assert.NotEqual(LidDelayAction.StartDelay, action);
        Assert.NotEqual(LidDelayAction.Suspend, action);
        Assert.Equal(LidDelayAction.HandBackUntilTheLidOpens, action);
    }

    [Fact]
    public void OnLidState_OpenedAfterTheFeatureWasTurnedOffMidWindow_StillCancels()
    {
        // The hold outlives the setting: if releasing it depended on the feature still being on,
        // turning the feature off mid-countdown would strand the machine awake.
        Assert.Equal(LidDelayAction.Cancel,
            LidDelayPolicy.OnLidState(LidState.Opened, enabled: false, delayPending: true, isFirstReading: false));
    }

    // WaitIsOver — whichever condition arrives first ends the wait.

    [Fact]
    public void WaitIsOver_TheClockArrivesFirst_EndsTheWaitWithTheTargetStillOutstanding()
    {
        // The direction the old conjunction got wrong: a thirty-minute delay must not go on draining
        // the battery towards a target it never reaches.
        Assert.True(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: true,
                                              targetSet: true, targetArrived: false));
    }

    // A battery target paused by a charger — issues #168 and #169. The flags a dropped target left
    // behind were identical to those of a wait nobody configured, and the two have opposite answers:
    // one holds the machine awake while it charges, the other has nothing to wait for.

    [Fact]
    public void WaitIsOver_TheOnlyConditionIsPausedWhileCharging_IsNotOver()
    {
        // The shipped defect: a target dropped when the charger went in landed on the "nothing to
        // wait for" short-circuit and suspended a machine at 45 % against a 10 % target.
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: false, timeArrived: false,
                                               targetSet: true, targetArrived: false,
                                               endedEarly: false, targetPaused: true));
        // Still not over where a caller has cleared the target as well: paused is not absent.
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: false, timeArrived: false,
                                               targetSet: false, targetArrived: false,
                                               endedEarly: false, targetPaused: true));
    }

    [Theory]
    [InlineData(true)]
    public void AChargerOnATargetOnlyWait_HoldsTheMachineAwake_WithOrWithoutAKeepAwakeSession(bool keepAwakeActive)
    {
        // Composed the way the service composes it. A running session used to mask the fault: the
        // same wait ended as a sleep owed rather than taken, and the machine slept when the session
        // ended with nothing reached.
        bool over = LidDelayPolicy.WaitIsOver(timeSet: false, timeArrived: false,
                                              targetSet: true, targetArrived: false,
                                              endedEarly: false, targetPaused: true);

        Assert.Equal(LidDelayAction.Hold,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: true, keepAwakeActive, waitIsOver: over));
    }

    // OnWaitProgress

    [Fact]
    public void OnWaitProgress_LidAlreadyReopened_DoesNothing()
    {
        // A stale tick: suspending here would sleep a machine the user is sitting in front of.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: false, keepAwakeActive: false, waitIsOver: true));
    }

    [Fact]
    public void OnWaitProgress_ASuppressedSleepIsNotTheSameAnswerAsACancelledOne() =>
        // The two used to be one answer, which is the whole of why a suppressed sleep was never
        // served. Pinned apart: folding them back together leaves the machine awake, lid shut, for as
        // long as Windows' own idle timeout — five hours on mains on one machine measured.
        Assert.NotEqual(
            LidDelayPolicy.OnWaitProgress(enabled: false, delayPending: true, keepAwakeActive: false, waitIsOver: true),
            LidDelayPolicy.OnWaitProgress(enabled: true,  delayPending: true, keepAwakeActive: true,  waitIsOver: true));

    // ShouldCompleteSuppressedWait

    [Fact]
    public void ShouldCompleteSuppressedWait_EveryPartIsRequired()
    {
        // Each input pinned as load-bearing from its own side: dropping any one of the four turns a
        // deferred sleep into a machine slept at the wrong moment, or one never slept at all.
        foreach (int drop in Enumerable.Range(0, 4))
            Assert.False(LidDelayPolicy.ShouldCompleteSuppressedWait(
                sleepOwed:       drop != 0,
                enabled:         drop != 1,
                lidClosed:       drop != 2,
                keepAwakeActive: drop == 3));
    }

    // DecideStartup — the crash-recovery table

    [Fact]
    public void DecideStartup_OnWithValuesAlreadySaved_ReappliesWithoutRecapturing()
    {
        // With saved values present the scheme's current lid action is the app's own "do nothing".
        // Re-capturing it would persist that as the user's setting, so restore could never put
        // anything else back and the laptop would stop sleeping on lid close for good.
        Assert.Equal(LidActionOverride.ReapplyOverride,
            LidDelayPolicy.DecideStartup(enabled: true, hasSavedAction: true));
    }

    [Fact]
    public void DecideStartup_OffWithValuesStillSaved_RestoresThem()
    {
        // The app died with the override in place, so the user's own lid action goes back first.
        Assert.Equal(LidActionOverride.Restore,
            LidDelayPolicy.DecideStartup(enabled: false, hasSavedAction: true));
    }

    // Persisted shape

    [Fact]
    public void SavedLidAction_SurvivesSettingsJson_BecauseThatIsTheCrashRecord()
    {
        // These two values are the crash recovery. Without a clean round trip the app restarts
        // believing it never touched the power scheme, stranding the lid action on "do nothing".
        var scheme = "381b4222-f694-41f0-9685-ff5bb260df2e";
        var settings = new AppSettings { LidDelayEnabled = true, LidDelayMinutes = 15,
                                         LidDelaySavedAcAction = 1, LidDelaySavedDcAction = 0,
                                         LidDelaySavedScheme = scheme };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(loaded);
        Assert.True(loaded!.LidDelayEnabled);
        Assert.Equal(15, loaded.LidDelayMinutes);
        Assert.Equal(1, loaded.LidDelaySavedAcAction);
        Assert.Equal(0, loaded.LidDelaySavedDcAction);   // a saved zero must not come back as null
        // Lid actions are per-scheme, so restoring without the scheme could write one plan's values
        // into another.
        Assert.Equal(scheme, loaded.LidDelaySavedScheme);
        Assert.True(loaded.HasSavedLidAction);
    }

    // ── ShouldTurnOffAfterLidClose ─────────────────────────────────────
    // The whole option turns on telling an expiry from an interruption: the delay stands down when it
    // did its job, never when it was stopped short.

    /// <summary>The service side of the same guard, at both entry points: a charger connected during
    /// the wait (#168) and a machine already charging as the lid closes (#169). The service owns a
    /// power scheme, a lid subscription and a suspend, so the wiring is read out of the source.</summary>
    [Fact]
    public void AChargingReading_PausesTheTargetAtArmingAndMidWait_AndNeverDropsIt()
    {
        string source = File.ReadAllText(RepoFiles.Find("Services/LidDelayService.cs"));
        string report = SourceMethods.Body(source, "OnBatteryReport");
        string arming = SourceMethods.Body(source, "StartDelay");

        Assert.Contains("_targetPaused = paused = true", report, StringComparison.Ordinal);
        Assert.Contains("_targetPaused  = true", arming, StringComparison.Ordinal);

        foreach (string body in new[] { report, arming })
        {
            // A dropped target is the wait with no condition that suspended the machine.
            Assert.DoesNotMatch(@"_targetSet\s*=\s*false", body);
            // Arming runs on the lid callback, where switching the feature off deadlocks.
            Assert.DoesNotContain("SetEnabled(", body, StringComparison.Ordinal);
        }

        // The pause has to reach the completion test, or a paused target reads as no target.
        Assert.Contains("_thermalEnded, _targetPaused",
                        SourceMethods.Body(source, "Complete"), StringComparison.Ordinal);
    }

    // ── ShouldLockOnLidClose ───────────────────────────────────────────
    // Never calls LockWorkStation: the decision is pure, and a test that actually locked would lock
    // the machine running the suite.

    [Theory]
    [InlineData(true,  true )]
    public void ShouldLockOnLidClose_IgnoresAKeepAwakeSession(bool enabled, bool lockOnClose)
    {
        // A keep-awake session vetoes the SLEEP, and the temptation is to let it veto the lock with it.
        // That is the worst case of the lot: the machine then sits awake, unlocked and lid-shut for the
        // whole session. The two decisions are independent, and this pins that down.
        Assert.Equal(LidDelayPolicy.ShouldLockOnLidClose(enabled, lockOnClose, keepAwakeActive: false),
                     LidDelayPolicy.ShouldLockOnLidClose(enabled, lockOnClose, keepAwakeActive: true));
    }

    [Fact]
    public void LockOnClose_DefaultsOn_IncludingForASettingsFileWrittenBeforeIt()
    {
        // Unlike the delay itself, the lock defaults ON: turning the delay on removes the sign-in
        // prompt a lid close normally leads to, and an existing settings.json carries no opinion about
        // a key that did not exist when it was written.
        Assert.True(new AppSettings().LidDelayLockOnClose);

        var loaded = JsonSerializer.Deserialize<AppSettings>("""{"LidDelayEnabled":true}""");

        Assert.NotNull(loaded);
        Assert.True(loaded!.LidDelayLockOnClose);
    }

    // P/Invoke smoke test — read only

    [Fact]
    public void ReadActiveLidCloseAction_SignatureIsSound_AndNeverWrites()
    {
        // A wrong P/Invoke signature fails silently, and the feature persists whatever it reads as
        // the value it later restores, so a bad read is how a user's lid setting gets destroyed.
        // Read-only on purpose: the suite must never write a power setting on the host machine.
        var before = NativeMethods.ReadActiveLidCloseAction();

        // Null is legitimate (a scheme with no lid setting); a value must be one of the four
        // documented actions rather than uninitialised memory.
        if (before is { } v)
        {
            Assert.InRange(v.Ac, 0u, 3u);
            Assert.InRange(v.Dc, 0u, 3u);
            Assert.NotEqual(Guid.Empty, v.Scheme);   // the indices are meaningless without their scheme
            Assert.Equal(before, NativeMethods.ReadActiveLidCloseAction());   // stable, nothing written
        }
    }
}
