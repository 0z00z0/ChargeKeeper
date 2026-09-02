namespace ChargeKeeper.Services;

/// <summary>
/// Remembers a suspend so the following resume can be reported as one event: how long the machine
/// was away, and what the battery did meanwhile. Without it a gap in the samples is ambiguous —
/// the machine asleep and the application not running look identical in the log.
/// </summary>
internal static class SleepWatch
{
    private static DateTimeOffset? _sleptAt;
    private static int? _levelAtSleep;

    /// <summary>Records the suspend. <paramref name="levelPercent"/> is null when no battery
    /// reading has arrived yet.</summary>
    public static void RecordSleep(DateTimeOffset at, int? levelPercent)
    {
        _sleptAt      = at;
        _levelAtSleep = levelPercent;
    }

    /// <summary>
    /// The sentence for a resume, or null when no suspend was seen — a resume without one is a
    /// Windows notification the application started too late to pair, and inventing a duration for
    /// it would put a fiction in the log.
    /// </summary>
    public static string? Wake(DateTimeOffset at, int? levelPercent)
    {
        if (_sleptAt is not { } slept) return null;

        var elapsed = at - slept;
        _sleptAt      = null;
        int? before   = _levelAtSleep;
        _levelAtSleep = null;

        return WakeSentence(elapsed, before, levelPercent);
    }

    /// <summary>Test seam only — the state is process-wide and outlives a single test.</summary>
    internal static void ResetForTests()
    {
        _sleptAt      = null;
        _levelAtSleep = null;
    }

    public const string WentToSleep = "The machine went to sleep.";

    /// <summary>Pure: the resume sentence for a given duration and pair of readings.</summary>
    public static string WakeSentence(TimeSpan asleep, int? levelBefore, int? levelAfter)
    {
        string line = $"The machine woke after {Duration(asleep)} asleep.";
        if (levelBefore is not { } before || levelAfter is not { } after) return line;

        if (after < before) return $"{line} The battery fell from {before} % to {after} % while it was away.";
        if (after > before) return $"{line} The battery rose from {before} % to {after} % while it was away.";
        return $"{line} The battery was unchanged at {before} %.";
    }

    /// <summary>A duration as a person would say it. A negative span means the clock moved
    /// backwards across the suspend, which is not something to state as a fact.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) return "an unknown time";

        int hours   = (int)span.TotalHours;
        int minutes = span.Minutes;

        if (hours == 0 && minutes == 0) return "less than a minute";
        if (hours == 0)                 return Plural(minutes, "minute");
        if (minutes == 0)               return Plural(hours, "hour");
        return $"{Plural(hours, "hour")} {Plural(minutes, "minute")}";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
