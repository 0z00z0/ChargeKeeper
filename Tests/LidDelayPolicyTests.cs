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
    public void OnLidState_Closed_StartsTheDelay()
    {
        Assert.Equal(LidDelayAction.StartDelay,
            LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: false, isFirstReading: false));
    }

    [Fact]
    public void OnLidState_Closed_FeatureOff_DoesNothing()
    {
        // With the feature off, Windows' own lid action is back in place and handles the close.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnLidState(LidState.Closed, enabled: false, delayPending: false, isFirstReading: false));
    }

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
    public void OnLidState_ClosedAgainWhileCountingDown_DoesNotRestartTheWindow()
    {
        // The notification can repeat, and a re-armed timer would silently extend a countdown the
        // user is already waiting on.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: true, isFirstReading: false));
    }

    [Fact]
    public void OnLidState_OpenedWithinTheWindow_Cancels()
    {
        Assert.Equal(LidDelayAction.Cancel,
            LidDelayPolicy.OnLidState(LidState.Opened, enabled: true, delayPending: true, isFirstReading: false));
    }

    [Fact]
    public void OnLidState_Opened_NothingPending_DoesNothing()
    {
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnLidState(LidState.Opened, enabled: true, delayPending: false, isFirstReading: false));
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

    [Fact]
    public void WaitIsOver_TheBatteryTargetArrivesFirst_EndsTheWaitWithTimeStillToRun()
    {
        // The other direction: a fifteen-per-cent target must not leave the machine running for the
        // rest of an hour it no longer needs.
        Assert.True(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: false,
                                              targetSet: true, targetArrived: true));
    }

    [Fact]
    public void WaitIsOver_BothConditionsSetAndNeitherArrived_KeepsWaiting()
    {
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: false,
                                               targetSet: true, targetArrived: false));
    }

    [Fact]
    public void WaitIsOver_NoBatteryTarget_TheClockAloneDecides()
    {
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: false,
                                               targetSet: false, targetArrived: false));
        Assert.True(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: true,
                                              targetSet: false, targetArrived: false));
    }

    [Fact]
    public void WaitIsOver_NoDelay_TheBatteryTargetAloneDecides()
    {
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: false, timeArrived: false,
                                               targetSet: true, targetArrived: false));
        Assert.True(LidDelayPolicy.WaitIsOver(timeSet: false, timeArrived: false,
                                              targetSet: true, targetArrived: true));
    }

    [Fact]
    public void WaitIsOver_AConditionThatIsNotSet_NeverEndsTheWaitOnItsOwn()
    {
        // An unset condition carrying a stale "arrived" must not decide anything: only a condition
        // the user actually asked for can end the wait.
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: false, timeArrived: true,
                                               targetSet: true, targetArrived: false));
        Assert.False(LidDelayPolicy.WaitIsOver(timeSet: true, timeArrived: false,
                                               targetSet: false, targetArrived: true));
    }

    [Fact]
    public void WaitIsOver_NeitherConditionSet_IsOverAtOnce()
    {
        // Nothing to wait for, so the machine sleeps rather than sitting awake with the lid shut for
        // a condition that can never arrive.
        Assert.True(LidDelayPolicy.WaitIsOver(timeSet: false, timeArrived: false,
                                              targetSet: false, targetArrived: false));
    }

    // OnWaitProgress

    [Fact]
    public void OnWaitProgress_WaitOverAndTheLidStillShut_Suspends()
    {
        Assert.Equal(LidDelayAction.Suspend,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: true, keepAwakeActive: false, waitIsOver: true));
    }

    [Fact]
    public void OnWaitProgress_NoConditionArrivedYet_KeepsTheHold()
    {
        Assert.Equal(LidDelayAction.Hold,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: true, keepAwakeActive: false, waitIsOver: false));
    }

    [Fact]
    public void OnWaitProgress_LidAlreadyReopened_DoesNothing()
    {
        // A stale tick: suspending here would sleep a machine the user is sitting in front of.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: false, keepAwakeActive: false, waitIsOver: true));
    }

    [Fact]
    public void OnWaitProgress_KeepAwakeSessionRunning_ReleasesTheHoldButDoesNotSleep()
    {
        // A keep-awake session is an explicit request and outranks a background rule about lids:
        // closing the lid on a long build must not kill it.
        Assert.Equal(LidDelayAction.Cancel,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: true, keepAwakeActive: true, waitIsOver: true));
    }

    [Fact]
    public void OnWaitProgress_FeatureTurnedOffMidWait_ReleasesTheHoldButDoesNotSleep()
    {
        Assert.Equal(LidDelayAction.Cancel,
            LidDelayPolicy.OnWaitProgress(enabled: false, delayPending: true, keepAwakeActive: false, waitIsOver: true));
    }

    [Fact]
    public void OnWaitProgress_MidWaitVetoes_DoNotCutTheWaitShort()
    {
        // The vetoes are read only once something has arrived, so a battery reading mid-wait cannot
        // cancel a delay that still had time to run.
        Assert.Equal(LidDelayAction.Hold,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: true, keepAwakeActive: true, waitIsOver: false));
        Assert.Equal(LidDelayAction.Hold,
            LidDelayPolicy.OnWaitProgress(enabled: false, delayPending: true, keepAwakeActive: false, waitIsOver: false));
    }

    [Fact]
    public void OnWaitProgress_LidReopened_OutranksEverythingElse()
    {
        // A stale tick outranks everything: the machine the user reopened must not be held or slept.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnWaitProgress(enabled: true, delayPending: false, keepAwakeActive: false, waitIsOver: false));
    }

    // DelayFor

    [Fact]
    public void DelayFor_UsesTheConfiguredMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), LidDelayPolicy.DelayFor(10));
    }

    [Fact]
    public void DelayFor_ZeroOrNegative_ClampsToTheFloor_NotAnInstantSleep()
    {
        // Reachable by hand-editing settings.json, and a zero delay would sleep the machine instantly
        // through a feature whose purpose is to delay it.
        Assert.Equal(TimeSpan.FromMinutes(LidDelayPolicy.MinMinutes), LidDelayPolicy.DelayFor(0));
        Assert.Equal(TimeSpan.FromMinutes(LidDelayPolicy.MinMinutes), LidDelayPolicy.DelayFor(-30));
    }

    [Fact]
    public void DelayFor_AbsurdlyLarge_ClampsToTheCeiling()
    {
        // Bounds the worst case: a lidded laptop held awake in a bag until the battery is flat.
        Assert.Equal(TimeSpan.FromMinutes(LidDelayPolicy.MaxMinutes), LidDelayPolicy.DelayFor(100_000));
    }

    // DecideStartup — the crash-recovery table

    [Fact]
    public void DecideStartup_OnWithNothingSaved_CapturesTheUsersValuesFirst()
    {
        Assert.Equal(LidActionOverride.CaptureAndOverride,
            LidDelayPolicy.DecideStartup(enabled: true, hasSavedAction: false));
    }

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

    [Fact]
    public void DecideStartup_OffWithNothingSaved_LeavesThePowerSchemeAlone()
    {
        // The default state must not touch a system setting to discover it has nothing to do.
        Assert.Equal(LidActionOverride.None,
            LidDelayPolicy.DecideStartup(enabled: false, hasSavedAction: false));
    }

    // Persisted shape

    [Fact]
    public void LidDelay_IsOffByDefault_AndSavesNoLidAction()
    {
        // Enabling it changes a Windows power setting outside the app, which is only ever the
        // user's call.
        var s = new AppSettings();
        Assert.False(s.LidDelayEnabled);
        Assert.Equal(10, s.LidDelayMinutes);
        Assert.Null(s.LidDelaySavedAcAction);
        Assert.Null(s.LidDelaySavedDcAction);
    }

    [Fact]
    public void SavedLidAction_IsNullable_SoSavedZeroIsNotMistakenForNothingSaved()
    {
        // "Do nothing" is a legitimate user setting (index 0). As plain ints it would be
        // indistinguishable from "nothing saved", and restore would skip it.
        var s = new AppSettings { LidDelaySavedAcAction = 0, LidDelaySavedDcAction = 0 };
        Assert.True(s.HasSavedLidAction);

        Assert.False(new AppSettings().HasSavedLidAction);
    }

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

    [Fact]
    public void DischargeTargets_AreOrdinarySettingsJson()
    {
        // Declared and stored exactly as the keep-awake presets are: a list on AppSettings, seeded in
        // its initialiser and persisted with the rest of the file, not a store of their own.
        var settings = new AppSettings
        {
            LidDischargeEnabled       = true,
            LidDischargeTargetPercent = 40,
            LidDischargePresets       = [new(40, "Storage"), new(60)],
        };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(loaded);
        Assert.True(loaded!.LidDischargeEnabled);
        Assert.Equal(40, loaded.LidDischargeTargetPercent);
        Assert.Equal(2, loaded.LidDischargePresets.Count);
        Assert.Equal(new LidDischargeTarget(40, "Storage"), loaded.LidDischargePresets[0]);
        Assert.Equal(new LidDischargeTarget(60), loaded.LidDischargePresets[1]);
    }

    [Fact]
    public void DischargeTargets_AreSeededWithDefaults()
    {
        // A settings file written before the feature existed still opens the page on a usable list.
        var seeded = new AppSettings().LidDischargePresets;
        Assert.NotEmpty(seeded);
        Assert.All(seeded, t => Assert.InRange(t.Percent, LidDischargeWatch.MinPercent, LidDischargeWatch.MaxPercent));
    }

    [Fact]
    public void DischargeTarget_IsOffByDefault()
    {
        // On by default would change what a lid close does on every machine that upgrades.
        Assert.False(new AppSettings().LidDischargeEnabled);
    }

    [Fact]
    public void HasSavedLidAction_IsNotWrittenToSettingsJson()
    {
        // It is derived from the two saved values; persisting it would let a stale copy contradict
        // them after a hand edit.
        Assert.DoesNotContain(nameof(AppSettings.HasSavedLidAction),
                              JsonSerializer.Serialize(new AppSettings()), StringComparison.Ordinal);
    }

    [Fact]
    public void LidDelaySettings_AbsentFromAnOlderFile_LoadAsOffWithNothingSaved()
    {
        // An upgrading install must come up inert rather than in a half-state that drives a restore
        // of values never captured.
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{"KeepAwakeDisplayOn":true}""");

        Assert.NotNull(loaded);
        Assert.False(loaded!.LidDelayEnabled);
        Assert.False(loaded.HasSavedLidAction);
        Assert.Equal(10, loaded.LidDelayMinutes);
    }

    [Fact]
    public void HasSavedLidAction_IsTrueWhenEitherSideIsStored()
    {
        // A half-written pair still means the power scheme was touched, so it must drive a restore.
        Assert.True(new AppSettings { LidDelaySavedAcAction = 1 }.HasSavedLidAction);
        Assert.True(new AppSettings { LidDelaySavedDcAction = 1 }.HasSavedLidAction);
    }

    // ── ShouldTurnOffAfterLidClose ─────────────────────────────────────
    // The whole option turns on telling an expiry from an interruption: the delay stands down when it
    // did its job, never when it was stopped short.

    [Fact]
    public void ShouldTurnOffAfterLidClose_TheDelayElapsedAndTheMachineSlept_SwitchesOff()
    {
        // The one outcome that counts. A discharge target met after the wait reaches the same
        // suspend, so both routes arrive here as Slept.
        Assert.True(LidDelayPolicy.ShouldTurnOffAfterLidClose(offAfterSleep: true, LidDelayOutcome.Slept));
    }

    [Fact]
    public void ShouldTurnOffAfterLidClose_OptionOff_LeavesTheFeatureOnEvenAfterSleeping()
    {
        // Off by default, so an upgrading install keeps a standing delay standing.
        Assert.False(LidDelayPolicy.ShouldTurnOffAfterLidClose(offAfterSleep: false, LidDelayOutcome.Slept));
    }

    [Fact]
    public void ShouldTurnOffAfterLidClose_LidReopenedBeforeSleeping_LeavesTheFeatureOn()
    {
        // Nothing expired: the machine never slept, so the delay has not yet served the lid close it
        // was turned on for. Retiring it here would cost the user the very next one.
        Assert.False(LidDelayPolicy.ShouldTurnOffAfterLidClose(offAfterSleep: true, LidDelayOutcome.LidReopened));
    }

    [Fact]
    public void ShouldTurnOffAfterLidClose_StoppedShort_LeavesTheFeatureOn()
    {
        // A keep-awake session vetoing the sleep, a suspend Windows refused, and the feature switched
        // off by hand all end the wait without it running its course.
        Assert.False(LidDelayPolicy.ShouldTurnOffAfterLidClose(offAfterSleep: true, LidDelayOutcome.StoppedShort));
    }

    [Fact]
    public void ShouldTurnOffAfterLidClose_AnInterruptionNeverSwitchesOff_WhicheverWayTheOptionIsSet()
    {
        // The boundary the option rests on, pinned from both sides: every interruption is inert, so
        // reading one as an expiry can only show up as a failure here.
        foreach (var outcome in new[] { LidDelayOutcome.LidReopened, LidDelayOutcome.StoppedShort })
        {
            Assert.False(LidDelayPolicy.ShouldTurnOffAfterLidClose(offAfterSleep: true, outcome));
            Assert.False(LidDelayPolicy.ShouldTurnOffAfterLidClose(offAfterSleep: false, outcome));
        }
    }

    [Fact]
    public void ShouldTurnOffAfterLidClose_SleepingIsTheOnlyOutcomeThatQualifies() =>
        // Exhaustive over the enum, so an outcome added later has to be judged rather than inheriting
        // whatever the expression happens to do with it.
        Assert.Equal(
            [LidDelayOutcome.Slept],
            Enum.GetValues<LidDelayOutcome>()
                .Where(o => LidDelayPolicy.ShouldTurnOffAfterLidClose(offAfterSleep: true, o)));

    [Fact]
    public void OffAfterSleep_IsOffByDefault_IncludingForASettingsFileWrittenBeforeIt()
    {
        // On by default would silently retire a delay every existing install relies on standing.
        Assert.False(new AppSettings().LidDelayOffAfterSleep);

        var loaded = JsonSerializer.Deserialize<AppSettings>("""{"LidDelayEnabled":true}""");

        Assert.NotNull(loaded);
        Assert.False(loaded!.LidDelayOffAfterSleep);
    }

    [Fact]
    public void OffAfterSleep_SurvivesSettingsJson()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(new AppSettings { LidDelayOffAfterSleep = true }));

        Assert.NotNull(loaded);
        Assert.True(loaded!.LidDelayOffAfterSleep);
    }

    // ── ShouldLockOnLidClose ───────────────────────────────────────────
    // Never calls LockWorkStation: the decision is pure, and a test that actually locked would lock
    // the machine running the suite.

    [Fact]
    public void ShouldLockOnLidClose_FeatureAndSettingOn_Locks()
    {
        Assert.True(LidDelayPolicy.ShouldLockOnLidClose(enabled: true, lockOnClose: true, keepAwakeActive: false));
    }

    [Fact]
    public void ShouldLockOnLidClose_SettingOff_DoesNotLock()
    {
        // The setting is the only opt-out. Reading it the wrong way round would lock a machine whose
        // owner turned the lock off and leave the one who left it on unlocked.
        Assert.False(LidDelayPolicy.ShouldLockOnLidClose(enabled: true, lockOnClose: false, keepAwakeActive: false));
        Assert.False(LidDelayPolicy.ShouldLockOnLidClose(enabled: true, lockOnClose: false, keepAwakeActive: true));
    }

    [Fact]
    public void ShouldLockOnLidClose_FeatureOff_DoesNotLock()
    {
        // With the feature off, Windows own lid action is back in place and locking is its business.
        Assert.False(LidDelayPolicy.ShouldLockOnLidClose(enabled: false, lockOnClose: true, keepAwakeActive: false));
    }

    [Theory]
    [InlineData(true,  true )]
    [InlineData(true,  false)]
    [InlineData(false, true )]
    [InlineData(false, false)]
    public void ShouldLockOnLidClose_IgnoresAKeepAwakeSession(bool enabled, bool lockOnClose)
    {
        // A keep-awake session vetoes the SLEEP, and the temptation is to let it veto the lock with it.
        // That is the worst case of the lot: the machine then sits awake, unlocked and lid-shut for the
        // whole session. The two decisions are independent, and this pins that down.
        Assert.Equal(LidDelayPolicy.ShouldLockOnLidClose(enabled, lockOnClose, keepAwakeActive: false),
                     LidDelayPolicy.ShouldLockOnLidClose(enabled, lockOnClose, keepAwakeActive: true));
    }

    [Fact]
    public void ShouldLockOnLidClose_LocksDuringAKeepAwakeSession()
    {
        Assert.True(LidDelayPolicy.ShouldLockOnLidClose(enabled: true, lockOnClose: true, keepAwakeActive: true));
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
