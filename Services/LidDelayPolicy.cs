namespace ChargeKeeper.Services;

internal enum LidState { Opened, Closed }

/// <summary>What <see cref="LidDelayService"/> should do next.</summary>
internal enum LidDelayAction
{
    None,
    /// <summary>Take the OS hold and arm the delay timer.</summary>
    StartDelay,
    /// <summary>Release the OS hold and disarm the timer, without sleeping.</summary>
    Cancel,
    /// <summary>Release the OS hold, then suspend the machine.</summary>
    Suspend,
    /// <summary>Keep the OS hold and stay pending: no condition has arrived yet.</summary>
    Hold,
    /// <summary>Give Windows its own lid-close action back until the lid next opens. The process
    /// started with the lid already shut, so no wait can be resumed and the override would otherwise
    /// leave nobody serving the close.</summary>
    HandBackUntilTheLidOpens,
    /// <summary>Take the override again, now the lid is open and a later close can be served.</summary>
    TakeTheOverrideBack,
}

/// <summary>How a lid close finished, for the one decision that has to tell an expiry from an
/// interruption.</summary>
internal enum LidDelayOutcome
{
    /// <summary>The wait ran its course — a condition arrived and the machine was suspended.</summary>
    Slept,
    /// <summary>The lid was reopened before the machine slept.</summary>
    LidReopened,
    /// <summary>The wait ended without sleeping: the feature was switched off, a keep-awake session
    /// vetoed the sleep, or Windows refused the suspend.</summary>
    StoppedShort,
}

/// <summary>The feature parks the user's own LIDACTION on "do nothing"; these are the four states
/// that pairing can be found in.</summary>
internal enum LidActionOverride
{
    /// <summary>Leave the power scheme alone.</summary>
    None,
    /// <summary>Read and persist the user's own AC/DC actions, then override them.</summary>
    CaptureAndOverride,
    /// <summary>Saved values already exist and the feature is on — re-assert the override only.</summary>
    ReapplyOverride,
    /// <summary>Saved values exist but the feature is off — put the user's own actions back.</summary>
    Restore,
}

/// <summary>
/// Pure decision table behind the lid-close delay — no P/Invoke, no timer, no power scheme — so the
/// rules are unit-testable without touching the OS. <see cref="LidDelayService"/> owns the OS side.
/// </summary>
internal static class LidDelayPolicy
{
    /// <summary>Bounds on the configured delay; only a hand-edited settings.json lands outside them.
    /// Zero would sleep instantly through a feature meant to delay it.</summary>
    public const int MinMinutes = 1;
    public const int MaxMinutes = 240;

    /// <summary>The configured delay as a span, clamped to <see cref="MinMinutes"/>…<see cref="MaxMinutes"/>.</summary>
    public static TimeSpan DelayFor(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, MinMinutes, MaxMinutes));

    /// <summary>
    /// Windows invokes the power-setting callback immediately on registration, before any real
    /// transition, so <paramref name="isFirstReading"/> only seeds the state — treating that replay
    /// as a close would suspend the machine merely because the app started. A close while a delay is
    /// pending is ignored: the notification repeats, and must not extend the countdown.
    /// </summary>
    /// <param name="handedBack">Whether the lid action has been given back to Windows because the
    /// process started with the lid already shut. The lid opening is what makes it safe to take the
    /// override again: a later close can then be served in full.</param>
    public static LidDelayAction OnLidState(LidState state, bool enabled, bool delayPending,
                                            bool isFirstReading, bool handedBack = false)
    {
        if (state == LidState.Opened)
        {
            if (delayPending) return LidDelayAction.Cancel;
            return handedBack ? LidDelayAction.TakeTheOverrideBack : LidDelayAction.None;
        }

        if (!enabled || delayPending) return LidDelayAction.None;

        // A start with the lid already shut. The replay is not a transition, so no wait is armed —
        // but the override has already been applied by the startup reconcile, and leaving it there
        // parks the lid action on "do nothing" with nobody serving the close. The action goes back
        // to Windows until the lid opens.
        //
        // The wait is declined rather than resumed because the moment the lid closed does not
        // survive the restart: a fresh full wait would hold the machine awake for longer than was
        // ever configured, and a shortened one would be a number with nothing behind it. Windows'
        // own lid-close action is the only behaviour that is knowably what its owner asked for.
        if (isFirstReading)
            return handedBack ? LidDelayAction.None : LidDelayAction.HandBackUntilTheLidOpens;

        return LidDelayAction.StartDelay;
    }

    /// <summary>
    /// Whichever condition arrives first ends the wait. A thirty-minute delay must not drain the
    /// battery past a target, and a fifteen-per-cent target must not hold the machine awake for an
    /// hour, so the two are alternatives rather than a pair that both have to be satisfied. A
    /// condition that is not set never arrives and therefore never ends the wait on its own; with
    /// neither set there is nothing to wait for and the wait is over at once.
    /// </summary>
    public static bool WaitIsOver(bool timeSet, bool timeArrived, bool targetSet, bool targetArrived)
    {
        if (!timeSet && !targetSet) return true;
        return (timeSet && timeArrived) || (targetSet && targetArrived);
    }

    /// <summary>
    /// A running keep-awake session vetoes the sleep — an explicit "do not sleep this machine" the
    /// user asked for by hand. The veto does not re-arm, so re-closing the lid starts a fresh delay.
    /// It is read only once the wait is over, so a reading arriving mid-wait cannot cancel a delay
    /// that still had time to run; both vetoes then outrank the wait itself, so a feature switched
    /// off mid-wait releases the hold rather than sleeping the machine.
    /// </summary>
    public static LidDelayAction OnWaitProgress(bool enabled, bool delayPending, bool keepAwakeActive,
                                                bool waitIsOver)
    {
        if (!delayPending) return LidDelayAction.None;             // lid reopened; a stale tick
        if (!waitIsOver) return LidDelayAction.Hold;
        if (!enabled || keepAwakeActive) return LidDelayAction.Cancel;
        return LidDelayAction.Suspend;
    }

    /// <summary>
    /// Whether the delay switches itself off now this lid close is over, leaving Windows' own
    /// lid-close action back in charge of the next one.
    /// </summary>
    /// <remarks>
    /// Only <see cref="LidDelayOutcome.Slept"/> qualifies: the feature stands down when it did its
    /// job, not when it was stopped short. A lid reopened before the machine slept expired nothing —
    /// the delay is still owed its chance — and neither the keep-awake veto, a refused suspend, nor
    /// switching the feature off by hand is a wait that ran its course. Reading a cancellation as an
    /// expiry would retire the feature on the one lid close it never got to serve.
    /// </remarks>
    public static bool ShouldTurnOffAfterLidClose(bool offAfterSleep, LidDelayOutcome outcome) =>
        offAfterSleep && outcome == LidDelayOutcome.Slept;

    /// <summary>
    /// Whether a lid close should lock the workstation. <paramref name="keepAwakeActive"/> is taken
    /// and deliberately ignored: that session vetoes the sleep, and locking on the same veto would
    /// leave the machine awake, unlocked and lid-shut for as long as the session runs.
    /// </summary>
    public static bool ShouldLockOnLidClose(bool enabled, bool lockOnClose, bool keepAwakeActive)
        => enabled && lockOnClose;

    /// <summary>
    /// With saved values present the scheme's current action is this app's own "do nothing", so
    /// re-capturing would persist that as the user's setting and the laptop could never go back to
    /// sleeping on lid close. Saved values with the feature off means a previous run died mid-override.
    /// </summary>
    public static LidActionOverride DecideStartup(bool enabled, bool hasSavedAction) => (enabled, hasSavedAction) switch
    {
        (true,  false) => LidActionOverride.CaptureAndOverride,
        (true,  true ) => LidActionOverride.ReapplyOverride,
        (false, true ) => LidActionOverride.Restore,
        (false, false) => LidActionOverride.None,
    };
}
