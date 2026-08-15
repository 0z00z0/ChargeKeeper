namespace ChargeKeeper.Services;

/// <summary>Lid switch position as Windows reports it (issue #90).</summary>
internal enum LidState { Opened, Closed }

/// <summary>What <see cref="LidDelayService"/> should do next (issue #90).</summary>
internal enum LidDelayAction
{
    /// <summary>Nothing to do.</summary>
    None,
    /// <summary>Take the OS hold and arm the delay timer.</summary>
    StartDelay,
    /// <summary>Release the OS hold and disarm the timer, WITHOUT sleeping.</summary>
    Cancel,
    /// <summary>Release the OS hold, then suspend the machine.</summary>
    Suspend,
}

/// <summary>
/// What to do with the Windows lid-close action at startup (issue #90). The feature works by parking
/// the user's own LIDACTION on "do nothing"; these are the four states that pairing can be found in.
/// </summary>
internal enum LidActionOverride
{
    /// <summary>Leave the power scheme alone.</summary>
    None,
    /// <summary>Read and PERSIST the user's own AC/DC actions, then override them.</summary>
    CaptureAndOverride,
    /// <summary>Saved values already exist and the feature is on — re-assert the override only.</summary>
    ReapplyOverride,
    /// <summary>Saved values exist but the feature is off — put the user's own actions back.</summary>
    Restore,
}

/// <summary>
/// PURE decision table behind the lid-close delay (issue #90) — no P/Invoke, no timer, no power
/// scheme — so the parts that are easy to get wrong (the seeding callback Windows fires at
/// registration, a duplicate lid notification, a timer that outlives its window, and above all
/// re-capturing an already-overridden lid action as if it were the user's own) are unit-testable
/// without touching the OS. House style; see <see cref="KeepAwakePolicy"/>.
/// <see cref="LidDelayService"/> owns the OS side.
/// </summary>
internal static class LidDelayPolicy
{
    /// <summary>
    /// Bounds on the configured delay. A hand-edited settings.json is the only way to land outside
    /// them, and both ends matter: 0 or negative would make lid-close sleep INSTANTLY through a
    /// feature the user enabled to delay it, and an absurd upper value would leave a lidded laptop
    /// awake in a bag for days. Clamping keeps a nonsense value merely wrong, never harmful.
    /// </summary>
    public const int MinMinutes = 1;
    public const int MaxMinutes = 240;

    /// <summary>The configured delay as a span, clamped to <see cref="MinMinutes"/>…<see cref="MaxMinutes"/>.</summary>
    public static TimeSpan DelayFor(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, MinMinutes, MaxMinutes));

    /// <summary>
    /// Reaction to a lid-state notification.
    /// <para><paramref name="isFirstReading"/> is the one non-obvious input: Windows invokes the
    /// power-setting callback IMMEDIATELY on registration with the CURRENT lid state, before any
    /// real transition. Treating that replay as a lid close would start a delay — and eventually
    /// suspend the machine — merely because the app started up, so the first reading only ever
    /// seeds the known state.</para>
    /// <para>A close while a delay is already pending is ignored rather than restarting the window:
    /// the notification can repeat, and a repeat must not extend a countdown the user is waiting on.</para>
    /// </summary>
    public static LidDelayAction OnLidState(LidState state, bool enabled, bool delayPending, bool isFirstReading)
    {
        if (state == LidState.Opened)
            return delayPending ? LidDelayAction.Cancel : LidDelayAction.None;

        if (!enabled || isFirstReading || delayPending) return LidDelayAction.None;
        return LidDelayAction.StartDelay;
    }

    /// <summary>
    /// Reaction to the delay timer reaching the end of its window. Suspends only when the window is
    /// still genuinely open.
    /// <para><paramref name="keepAwakeActive"/> vetoes the sleep: a running keep-awake session is an
    /// explicit "do not sleep this machine" the user asked for BY HAND, and it outranks a background
    /// rule about lids. Without this, closing the lid on a long build with "keep awake until 17:00"
    /// running would suspend the machine anyway and kill the build. The hold is still released, so
    /// the machine sleeps normally once that session ends.</para>
    /// </summary>
    public static LidDelayAction OnTimerFired(bool enabled, bool delayPending, bool keepAwakeActive)
    {
        if (!delayPending) return LidDelayAction.None;             // lid reopened; a stale tick
        if (!enabled || keepAwakeActive) return LidDelayAction.Cancel;
        return LidDelayAction.Suspend;
    }

    /// <summary>
    /// What to do with the Windows lid-close action at startup, from the feature's on/off state and
    /// whether the user's own values are already stored.
    /// <para>The <see cref="LidActionOverride.ReapplyOverride"/> cell is the whole reason this is a
    /// table rather than an if: with saved values present, the scheme's CURRENT lid action is our own
    /// "do nothing", so re-capturing it would persist that as the user's own setting and the feature
    /// could then never restore anything but "do nothing" — permanently stopping the laptop sleeping
    /// on lid close. Saved values are written exactly once and are never overwritten while they exist.</para>
    /// <para>The <see cref="LidActionOverride.Restore"/> cell is the crash-recovery path: saved values
    /// with the feature off means the app died (or was killed) with the override still in place, so
    /// the next start puts the user's setting back before anything else happens.</para>
    /// </summary>
    public static LidActionOverride DecideStartup(bool enabled, bool hasSavedAction) => (enabled, hasSavedAction) switch
    {
        (true,  false) => LidActionOverride.CaptureAndOverride,
        (true,  true ) => LidActionOverride.ReapplyOverride,
        (false, true ) => LidActionOverride.Restore,
        (false, false) => LidActionOverride.None,
    };
}
