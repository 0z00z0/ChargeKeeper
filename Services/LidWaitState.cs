namespace ChargeKeeper.Services;

/// <summary>What the lid-close feature is doing right now, as a reader of the published surface
/// needs it: whether a wait is running at all, and what it is waiting on.</summary>
/// <remarks>Deliberately not <see cref="LidWaitEnd"/>, which says how a wait finished. These are the
/// states a wait passes through, and the two lists have no member in common.</remarks>
internal enum LidWaitState
{
    /// <summary>The feature is switched off, so no lid close starts a wait.</summary>
    Off,

    /// <summary>On, with no wait running — the lid is open, or Windows has the lid-close action
    /// back until it next opens.</summary>
    Idle,

    /// <summary>Waiting for the configured delay to run out.</summary>
    WaitingForTheTimer,

    /// <summary>Waiting for the battery to come down to its target level.</summary>
    WaitingForTheBatteryTarget,

    /// <summary>Waiting on both conditions; whichever arrives first ends the wait.</summary>
    WaitingForEither,

    /// <summary>Waiting with nothing that can arrive yet: a charger has paused the battery target
    /// and the timer was never set, so the machine is held awake until the charger is removed.</summary>
    WaitingWithNothingLeftToReach,
}

/// <summary>The wait as the published surface reports it: the state, and the instant the delay
/// would suspend the machine. Composed under the service's own lock, so the two cannot
/// disagree.</summary>
/// <param name="SleepsAt">Null where no timer is running, which is every state but the two the
/// timer takes part in — a battery target has no predictable arrival.</param>
internal readonly record struct LidWaitSnapshot(LidWaitState State, DateTimeOffset? SleepsAt);

/// <summary>Derives <see cref="LidWaitState"/> from the conditions a wait armed with, and the word
/// each state is published as. Pure, so what a reader sees is testable without closing a lid.</summary>
internal static class LidWaitStates
{
    /// <summary>The state the feature is in. A running wait answers on its own terms whatever the
    /// setting says: the setting can be switched off mid-wait, and the wait carries on.</summary>
    public static LidWaitState From(bool featureOn, bool waiting, bool timerSet, bool targetSet)
    {
        if (!waiting) return featureOn ? LidWaitState.Idle : LidWaitState.Off;

        return (timerSet, targetSet) switch
        {
            (true, true)  => LidWaitState.WaitingForEither,
            (true, false) => LidWaitState.WaitingForTheTimer,
            (false, true) => LidWaitState.WaitingForTheBatteryTarget,
            _             => LidWaitState.WaitingWithNothingLeftToReach,
        };
    }

    /// <summary>The state as it is published, in the app's own vocabulary.</summary>
    public static string Label(LidWaitState state) => state switch
    {
        LidWaitState.Off                        => "Off",
        LidWaitState.Idle                       => "Idle",
        LidWaitState.WaitingForTheTimer         => "Waiting for the timer",
        LidWaitState.WaitingForTheBatteryTarget => "Waiting for the battery target",
        LidWaitState.WaitingForEither           => "Waiting for the timer or the battery target",
        _                                       => "Waiting with nothing left to reach",
    };

    /// <summary>Every word the entity can publish, in the order the states are declared.</summary>
    public static IReadOnlyList<string> Words { get; } =
        [.. Enum.GetValues<LidWaitState>().Select(Label)];
}
