using System.Text.Json;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure decision table behind the lid-close delay (issue #90) — no power scheme, no timer, no suspend.
public class LidDelayPolicyTests
{
    // ── OnLidState ───────────────────────────────────────────────────────────────

    [Fact]
    public void OnLidState_Closed_StartsTheDelay()
    {
        Assert.Equal(LidDelayAction.StartDelay,
            LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: false, isFirstReading: false));
    }

    [Fact]
    public void OnLidState_Closed_FeatureOff_DoesNothing()
    {
        // The feature off must mean the app is inert: Windows' own lid action is back in place and
        // handles the close.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnLidState(LidState.Closed, enabled: false, delayPending: false, isFirstReading: false));
    }

    [Fact]
    public void OnLidState_FirstReadingIsASeed_NeverALidClose()
    {
        // Windows invokes the power-setting callback IMMEDIATELY on registration with the CURRENT lid
        // state. Acting on that replay would start a delay — and suspend the machine N minutes later —
        // because the app started, which is the one outcome nobody asked for.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnLidState(LidState.Closed, enabled: true, delayPending: false, isFirstReading: true));
    }

    [Fact]
    public void OnLidState_ClosedAgainWhileCountingDown_DoesNotRestartTheWindow()
    {
        // The notification can repeat. A repeat that re-armed the timer would silently extend a
        // countdown the user is already waiting on.
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
        // The hold is ours and outlives the setting — releasing it can never depend on the feature
        // still being on, or turning the feature off mid-countdown would strand the machine awake.
        Assert.Equal(LidDelayAction.Cancel,
            LidDelayPolicy.OnLidState(LidState.Opened, enabled: false, delayPending: true, isFirstReading: false));
    }

    // ── OnTimerFired ─────────────────────────────────────────────────────────────

    [Fact]
    public void OnTimerFired_WindowStillOpen_Suspends()
    {
        Assert.Equal(LidDelayAction.Suspend,
            LidDelayPolicy.OnTimerFired(enabled: true, delayPending: true, keepAwakeActive: false));
    }

    [Fact]
    public void OnTimerFired_LidAlreadyReopened_DoesNothing()
    {
        // A stale tick: the cancel already released the hold, so suspending here would sleep a machine
        // the user is sitting in front of.
        Assert.Equal(LidDelayAction.None,
            LidDelayPolicy.OnTimerFired(enabled: true, delayPending: false, keepAwakeActive: false));
    }

    [Fact]
    public void OnTimerFired_KeepAwakeSessionRunning_ReleasesTheHoldButDoesNotSleep()
    {
        // A keep-awake session is an explicit "do not sleep this machine" the user asked for BY HAND,
        // and it outranks a background rule about lids. Closing the lid on a long build with "keep
        // awake until 17:00" running must not suspend the machine and kill the build.
        Assert.Equal(LidDelayAction.Cancel,
            LidDelayPolicy.OnTimerFired(enabled: true, delayPending: true, keepAwakeActive: true));
    }

    [Fact]
    public void OnTimerFired_FeatureTurnedOffMidWindow_ReleasesTheHoldButDoesNotSleep()
    {
        Assert.Equal(LidDelayAction.Cancel,
            LidDelayPolicy.OnTimerFired(enabled: false, delayPending: true, keepAwakeActive: false));
    }

    // ── DelayFor ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DelayFor_UsesTheConfiguredMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), LidDelayPolicy.DelayFor(10));
    }

    [Fact]
    public void DelayFor_ZeroOrNegative_ClampsToTheFloor_NotAnInstantSleep()
    {
        // Reachable by hand-editing settings.json. A zero delay would make lid close sleep INSTANTLY
        // through a feature whose entire purpose is to delay it.
        Assert.Equal(TimeSpan.FromMinutes(LidDelayPolicy.MinMinutes), LidDelayPolicy.DelayFor(0));
        Assert.Equal(TimeSpan.FromMinutes(LidDelayPolicy.MinMinutes), LidDelayPolicy.DelayFor(-30));
    }

    [Fact]
    public void DelayFor_AbsurdlyLarge_ClampsToTheCeiling()
    {
        // Bounds the worst case: a lidded laptop held awake in a bag until the battery is flat.
        Assert.Equal(TimeSpan.FromMinutes(LidDelayPolicy.MaxMinutes), LidDelayPolicy.DelayFor(100_000));
    }

    // ── DecideStartup — the crash-recovery table ─────────────────────────────────

    [Fact]
    public void DecideStartup_OnWithNothingSaved_CapturesTheUsersValuesFirst()
    {
        Assert.Equal(LidActionOverride.CaptureAndOverride,
            LidDelayPolicy.DecideStartup(enabled: true, hasSavedAction: false));
    }

    [Fact]
    public void DecideStartup_OnWithValuesAlreadySaved_ReappliesWithoutRecapturing()
    {
        // THE cell that makes this a table. With saved values present, the scheme's current lid action
        // is our OWN "do nothing" — re-capturing it would persist that as the user's setting, and the
        // feature could then never restore anything but "do nothing", permanently stopping the laptop
        // sleeping on lid close.
        Assert.Equal(LidActionOverride.ReapplyOverride,
            LidDelayPolicy.DecideStartup(enabled: true, hasSavedAction: true));
    }

    [Fact]
    public void DecideStartup_OffWithValuesStillSaved_RestoresThem()
    {
        // The crash-recovery path: the app died with the override in place, so the user's own lid
        // action is put back before anything else happens.
        Assert.Equal(LidActionOverride.Restore,
            LidDelayPolicy.DecideStartup(enabled: false, hasSavedAction: true));
    }

    [Fact]
    public void DecideStartup_OffWithNothingSaved_LeavesThePowerSchemeAlone()
    {
        // The default state for every user who never turns the feature on — it must not touch a
        // system setting to discover it has nothing to do.
        Assert.Equal(LidActionOverride.None,
            LidDelayPolicy.DecideStartup(enabled: false, hasSavedAction: false));
    }

    // ── Persisted shape ──────────────────────────────────────────────────────────

    [Fact]
    public void LidDelay_IsOffByDefault_AndSavesNoLidAction()
    {
        // Never on by default: enabling it changes a Windows power setting outside the app, which is
        // only ever the user's call.
        var s = new AppSettings();
        Assert.False(s.LidDelayEnabled);
        Assert.Equal(10, s.LidDelayMinutes);
        Assert.Null(s.LidDelaySavedAcAction);
        Assert.Null(s.LidDelaySavedDcAction);
    }

    [Fact]
    public void SavedLidAction_IsNullable_SoSavedZeroIsNotMistakenForNothingSaved()
    {
        // "Do nothing" IS a legitimate value a user can already have set (index 0). If these were
        // plain ints, that user's setting would be indistinguishable from "we saved nothing", and
        // restore would skip them.
        var s = new AppSettings { LidDelaySavedAcAction = 0, LidDelaySavedDcAction = 0 };
        Assert.True(s.HasSavedLidAction);

        Assert.False(new AppSettings().HasSavedLidAction);
    }

    [Fact]
    public void SavedLidAction_SurvivesSettingsJson_BecauseThatIsTheCrashRecord()
    {
        // These two values ARE the crash recovery: if they do not survive a round trip through
        // settings.json, the app restarts believing it never touched the power scheme and the user's
        // lid-close action is stranded on "do nothing" with nothing left that knows better.
        var settings = new AppSettings { LidDelayEnabled = true, LidDelayMinutes = 15,
                                         LidDelaySavedAcAction = 1, LidDelaySavedDcAction = 0 };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(loaded);
        Assert.True(loaded!.LidDelayEnabled);
        Assert.Equal(15, loaded.LidDelayMinutes);
        Assert.Equal(1, loaded.LidDelaySavedAcAction);
        Assert.Equal(0, loaded.LidDelaySavedDcAction);   // a saved zero must not come back as null
        Assert.True(loaded.HasSavedLidAction);
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
        // An existing install upgrading into this feature must come up inert — off, nothing saved —
        // rather than with a half-state that drives a restore of values that were never captured.
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{"KeepAwakeDisplayOn":true}""");

        Assert.NotNull(loaded);
        Assert.False(loaded!.LidDelayEnabled);
        Assert.False(loaded.HasSavedLidAction);
        Assert.Equal(10, loaded.LidDelayMinutes);
    }

    [Fact]
    public void HasSavedLidAction_IsTrueWhenEitherSideIsStored()
    {
        // A half-written pair still means the power scheme was touched, so it must still drive a
        // restore rather than being treated as clean.
        Assert.True(new AppSettings { LidDelaySavedAcAction = 1 }.HasSavedLidAction);
        Assert.True(new AppSettings { LidDelaySavedDcAction = 1 }.HasSavedLidAction);
    }

    // ── P/Invoke smoke test — READ ONLY ──────────────────────────────────────────

    [Fact]
    public void ReadLidCloseAction_SignatureIsSound_AndNeverWrites()
    {
        // Same reasoning as the SetThreadExecutionState smoke test: a wrong P/Invoke signature here
        // fails SILENTLY (a non-zero return marshalled wrong, or a garbage index), and this feature
        // persists whatever it reads as the value it will later restore — so a bad read is how a
        // user's lid setting gets destroyed. Deliberately read-only: the test suite must never write
        // a power setting on the machine running it.
        var before = NativeMethods.ReadLidCloseAction();

        // Null is a legitimate answer (a machine with no lid setting in its scheme); a value must be
        // one of the four documented actions rather than uninitialised memory.
        if (before is { } v)
        {
            Assert.InRange(v.Ac, 0u, 3u);
            Assert.InRange(v.Dc, 0u, 3u);
            Assert.Equal(before, NativeMethods.ReadLidCloseAction());   // stable, and nothing was written
        }
    }
}
