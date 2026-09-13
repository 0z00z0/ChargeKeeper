namespace ChargeKeeper.Services;

/// <summary>The things the application does to this machine, as one closed list. Everything else it
/// publishes is a setting or a reading, and each of those already shows its own movement.</summary>
internal enum AppChange
{
    /// <summary>The lid shut and a wait began.</summary>
    LidClosed,

    /// <summary>The lid opened and the wait was cancelled.</summary>
    LidOpened,

    /// <summary>Neither the delay nor a battery target was outstanding.</summary>
    WaitEndedWithNothingToWaitFor,

    /// <summary>The configured delay ran out.</summary>
    WaitEndedOnTheDelay,

    /// <summary>The battery came down to its target level.</summary>
    WaitEndedOnTheBatteryTarget,

    /// <summary>The machine reached its temperature ceiling.</summary>
    WaitEndedOnTheTemperatureCeiling,

    /// <summary>A keep-awake session took the hold.</summary>
    KeepAwakeStarted,

    /// <summary>A keep-awake session released the hold, by request or by its own expiry.</summary>
    KeepAwakeEnded,
}

/// <summary>One change and the moment it happened.</summary>
internal readonly record struct AppChangeRecord(AppChange What, DateTimeOffset When);

/// <summary>
/// The last thing the application did, for the published surface. One value, not a history: a
/// receiver keeps its own history of whatever it is shown, so a second copy here would only be a
/// second thing to get wrong.
/// </summary>
internal static class AppChangeLog
{
    private static readonly System.Threading.Lock _sync = new();
    private static AppChangeRecord? _last;

    /// <summary>Raised (off the UI thread) whenever a change is recorded, so the surface publishes
    /// it rather than waiting for a battery tick.</summary>
    public static event Action? Recorded;

    /// <summary>The last change, or null when nothing has happened this session.</summary>
    public static AppChangeRecord? Last { get { lock (_sync) return _last; } }

    /// <summary>Records a change as of now.</summary>
    public static void Record(AppChange what)
    {
        lock (_sync) _last = new AppChangeRecord(what, DateTimeOffset.Now);

        // Never let a subscriber's failure escape: this is reached from timer callbacks, where an
        // escaped exception terminates the process.
        try { Recorded?.Invoke(); }
        catch (Exception ex) { AppLog.Error("AppChangeLog.Recorded", ex); }
    }

    /// <summary>The end of a lid-close wait as a change. One mapping, so the wait's own vocabulary
    /// and the published one cannot drift apart.</summary>
    public static AppChange From(LidWaitEnd end) => end switch
    {
        LidWaitEnd.DelayElapsed     => AppChange.WaitEndedOnTheDelay,
        LidWaitEnd.BatteryTarget    => AppChange.WaitEndedOnTheBatteryTarget,
        LidWaitEnd.TooHot           => AppChange.WaitEndedOnTheTemperatureCeiling,
        _                           => AppChange.WaitEndedWithNothingToWaitFor,
    };

    /// <summary>The change as it is published, in the app's own vocabulary.</summary>
    public static string Label(AppChange change) => change switch
    {
        AppChange.LidClosed                        => "Lid closed",
        AppChange.LidOpened                        => "Lid opened",
        AppChange.WaitEndedWithNothingToWaitFor    => "Wait ended with nothing to wait for",
        AppChange.WaitEndedOnTheDelay              => "Wait ended on the delay",
        AppChange.WaitEndedOnTheBatteryTarget      => "Wait ended on the battery target",
        AppChange.WaitEndedOnTheTemperatureCeiling => "Wait ended on the temperature ceiling",
        AppChange.KeepAwakeStarted                 => "Keep awake started",
        _                                          => "Keep awake ended",
    };

    /// <summary>Every word the entity can publish, in the order the changes are declared.</summary>
    public static IReadOnlyList<string> Words { get; } =
        [.. Enum.GetValues<AppChange>().Select(Label)];
}
