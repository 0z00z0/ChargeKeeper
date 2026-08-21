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
    public static LidDelayAction OnLidState(LidState state, bool enabled, bool delayPending, bool isFirstReading)
    {
        if (state == LidState.Opened)
            return delayPending ? LidDelayAction.Cancel : LidDelayAction.None;

        if (!enabled || isFirstReading || delayPending) return LidDelayAction.None;
        return LidDelayAction.StartDelay;
    }

    /// <summary>
    /// A running keep-awake session vetoes the sleep — an explicit "do not sleep this machine" the
    /// user asked for by hand. The veto does not re-arm, so re-closing the lid starts a fresh delay.
    /// </summary>
    public static LidDelayAction OnTimerFired(bool enabled, bool delayPending, bool keepAwakeActive)
    {
        if (!delayPending) return LidDelayAction.None;             // lid reopened; a stale tick
        if (!enabled || keepAwakeActive) return LidDelayAction.Cancel;
        return LidDelayAction.Suspend;
    }

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
