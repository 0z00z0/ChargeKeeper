using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>Whether the application is actually watching the battery. A tray icon exists from the
/// moment the process starts and survives a start-up that failed halfway, so without this a dead
/// instance is indistinguishable from a working one.</summary>
internal enum MonitoringState
{
    /// <summary>Start-up has not reached the battery subscription yet.</summary>
    Starting,

    /// <summary>Subscribed to battery events; warnings will be raised.</summary>
    Watching,

    /// <summary>Start-up failed. Nothing is being watched and no warning can be raised.</summary>
    Failed,

    /// <summary>Torn down deliberately — the tray Exit, or Windows signing out.</summary>
    Stopped,
}

/// <summary>
/// The single record of whether monitoring came up, and the one place that decides the tray icon
/// and tooltip may not look normal while it has not. Static because the tray icon, the battery
/// subscription and the start-up path are all owned by the one application object.
/// </summary>
internal static class StartupHealth
{
    private static MonitoringState _state = MonitoringState.Starting;

    public static MonitoringState State => _state;

    /// <summary>True while the tray icon must not present the application as working.</summary>
    public static bool IsDegraded => _state is MonitoringState.Failed;

    public static void MarkWatching() => Transition(MonitoringState.Watching);

    public static void MarkFailed() => Transition(MonitoringState.Failed);

    public static void MarkStopped() => Transition(MonitoringState.Stopped);

    /// <summary>Test seam only — the state is process-wide and outlives a single test.</summary>
    internal static void ResetForTests() => _state = MonitoringState.Starting;

    private static void Transition(MonitoringState next) => _state = next;
}

/// <summary>
/// The sentences the log carries about whether the battery is being watched. Plain enough to read
/// in a "what happened" list: what happened, and what it means for the person reading. The
/// diagnostic detail behind a failure is logged separately at Error level and is not repeated here.
/// </summary>
internal static class HealthMessages
{
    /// <summary>The tray tooltip while start-up has failed. Kept inside the shell's 127-character
    /// tooltip limit so it cannot be silently truncated.</summary>
    public const string DegradedTooltip =
        "⚠ ChargeKeeper is not watching the battery\n" +
        "Start-up failed. No battery warnings will be given.\n" +
        "Restart it to try again.";

    /// <summary>Monitoring is live: the reading it started from, and the levels it will warn at.</summary>
    public static string MonitoringStarted(int levelPercent, PowerState state,
                                           bool lowEnabled,  int lowPercent,
                                           bool highEnabled, int highPercent)
    {
        string reading = $"Battery monitoring started. The battery is at {levelPercent} % and " +
                         $"{StateWord(state)}.";
        return $"{reading} {WarningLevels(lowEnabled, lowPercent, highEnabled, highPercent)}";
    }

    /// <summary>Start-up failed. Says the consequence rather than the cause — the tray icon is
    /// present either way, and this line is the only thing that separates the two.</summary>
    public const string MonitoringDidNotStart =
        "Battery monitoring did not start, so the battery level is not being watched and no " +
        "battery warnings will be given. The tray icon carries a warning mark until the " +
        "application is restarted.";

    public const string MonitoringStopped =
        "Battery monitoring stopped. No battery warnings will be given until the application " +
        "runs again.";

    /// <summary>The tray icon could not be placed in the notification area. Monitoring is unaffected,
    /// and saying so is the point: the missing icon otherwise reads as the application being gone.</summary>
    public const string TrayIconMissing =
        "The tray icon could not be placed in the notification area. The battery is still being " +
        "watched and warnings will still be given, but the application cannot be reached from the tray.";

    private static string WarningLevels(bool lowEnabled, int lowPercent, bool highEnabled, int highPercent)
        => (lowEnabled, highEnabled) switch
        {
            (true, true)  => $"A warning is set for {lowPercent} % on the way down and " +
                             $"{highPercent} % on the way up.",
            (true, false) => $"A warning is set for {lowPercent} % on the way down.",
            (false, true) => $"A warning is set for {highPercent} % on the way up.",
            _             => "No battery warnings are set.",
        };

    private static string StateWord(PowerState state) => state switch
    {
        PowerState.Charging    => "charging",
        PowerState.IdleOnMains => "on mains without charging",
        _                      => "discharging",
    };
}
