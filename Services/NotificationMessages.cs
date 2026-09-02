namespace ChargeKeeper.Services;

/// <summary>Which notification a log line is about, named as it reads in a sentence.</summary>
internal enum NotificationKind
{
    LowBattery,
    HighBattery,
    ChargeComplete,
    ChargingStarted,
    DrainAnomaly,
}

/// <summary>
/// The sentences the log carries about battery warnings: whether one was raised, suppressed,
/// shown or refused. Plain enough to read in a "what happened" list, so a person can tell
/// "attempted and failed" from "never attempted" without reading code. Pure — no clock, no I/O.
/// </summary>
internal static class NotificationMessages
{
    /// <summary>Windows would not register the application for notifications, so every later
    /// warning would vanish without a trace. The single highest-value line on this path.</summary>
    public const string Unavailable =
        "Notifications are unavailable — Windows did not accept the application's registration, " +
        "so no battery warnings can be shown.";

    public static string Shown(NotificationKind kind, int? atPercent) =>
        atPercent is { } pct
            ? $"{Subject(kind)} was shown on screen at {pct} %."
            : $"{Subject(kind)} was shown on screen.";

    /// <summary><paramref name="windowsReason"/> is what Windows said, so the line separates a
    /// refusal from a warning that was never attempted.</summary>
    public static string CouldNotBeShown(NotificationKind kind, int? atPercent, string windowsReason)
    {
        string where = atPercent is { } pct ? $" at {pct} %" : "";
        return $"{Subject(kind)} could not be shown{where}. Windows refused it: {OneLine(windowsReason)}";
    }

    public static string LowThresholdCrossed(int warnAtPercent, int levelPercent) =>
        $"The battery fell past the {warnAtPercent} % warning level, reaching {levelPercent} % " +
        "while discharging.";

    public static string LowRepeatSuppressed(int warnAtPercent, int levelPercent) =>
        $"The battery is below the {warnAtPercent} % warning level at {levelPercent} %, but a " +
        "warning has already been given for this discharge.";

    public static string LowWarningReArmed(int levelPercent) =>
        $"The battery rose to {levelPercent} %, so the low-battery warning is ready to be given again.";

    /// <summary>The latch is an in-memory one, so a restart silently re-arms a warning that was
    /// already given. Saying so stops a second warning at the same level reading as a defect.</summary>
    public static string LowWarningResetByRestart(int warnAtPercent) =>
        "The low-battery warning was reset when the application restarted. A warning will be " +
        $"given again below {warnAtPercent} %.";

    private static string Subject(NotificationKind kind) => kind switch
    {
        NotificationKind.LowBattery      => "A low-battery warning",
        NotificationKind.HighBattery     => "A high-battery warning",
        NotificationKind.ChargeComplete  => "A charge-complete notice",
        NotificationKind.ChargingStarted => "A charging-started notice",
        _                                => "An unusual-drain warning",
    };

    /// <summary>A Windows failure text can carry newlines, which would break the line into
    /// fragments the log reader cannot attribute.</summary>
    private static string OneLine(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length == 0 ? "no reason given." : flat;
    }
}
