namespace ChargeKeeper.Services;

/// <summary>
/// Windows' own idle sleep, in words — the rule a released hold hands back to. Pure: no power
/// scheme and no window, so the wording is testable without the OS.
/// <see cref="Helpers.NativeMethods.ReadSleepDelay"/> reads the setting and this states it.
/// </summary>
internal static class SleepDelayPolicy
{
    /// <summary>
    /// The value for the source the machine is running on. The two differ by a large factor, so the
    /// wrong one is a wrong number rather than a stale one. Null carries through a failed read.
    /// </summary>
    public static uint? ForPowerSource((uint AcSeconds, uint DcSeconds)? delay, bool onBattery) =>
        delay is not { } d ? null : onBattery ? d.DcSeconds : d.AcSeconds;

    /// <summary>
    /// The rule in one line, or null when the setting could not be read — silence beats a wrong
    /// number. It is a rule and not a countdown: Windows starts the clock when the machine goes
    /// idle, which this application cannot see, so no moment is promised and none exists. The period
    /// is a floor, because another application holding its own power request pushes it out.
    /// </summary>
    public static string? Describe(uint? seconds, bool onBattery)
    {
        if (seconds is not { } value) return null;

        string source = onBattery ? "On battery" : "On mains";
        return value == 0
            ? $"{source}: Windows never sleeps when idle."
            : $"{source}: Windows sleeps after {Period(value)} idle. Other holds extend that.";
    }

    /// <summary>
    /// A delay as the app says spans elsewhere — "10 m", "5 h", "1 h 30 m". Whole minutes above a
    /// minute: a hand-edited seconds remainder is not worth a third unit in a sentence.
    /// </summary>
    public static string Period(uint seconds)
    {
        if (seconds < 60) return $"{seconds} s";

        uint minutes = seconds / 60;
        return minutes switch
        {
            < 60                     => $"{minutes} m",
            _ when minutes % 60 == 0 => $"{minutes / 60} h",
            _                        => $"{minutes / 60} h {minutes % 60} m",
        };
    }
}
