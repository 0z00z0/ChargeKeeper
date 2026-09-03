namespace ChargeKeeper.Services;

/// <summary>
/// Pure decisions behind the dashboard's Lid delay section: whether the section belongs on this
/// machine at all, which quick delays its chip row offers, and the line under its title. No power
/// scheme and no window, so the rules are unit-testable — <see cref="LidDelayService"/> owns the OS
/// side and remains the only writer.
/// </summary>
internal static class LidDashboardPolicy
{
    /// <summary>
    /// Above this many presets in total the two groups need a full width each, so they stack. At or
    /// below it they sit side by side, which keeps the popup short.
    /// </summary>
    public const int MaxPresetsSideBySide = 6;

    /// <summary>
    /// Whether the delay group and the battery-level group fit beside each other. Both a count rule
    /// and a width rule: a narrow popup cannot hold two columns however few presets there are.
    /// </summary>
    public static bool GroupsSideBySide(int delayCount, int levelCount, double availableWidth,
                                        double minGroupWidth)
        => delayCount + levelCount <= MaxPresetsSideBySide
           && availableWidth >= minGroupWidth * 2;

    /// <summary>
    /// A machine with no lid has nothing to delay, so the section would claim hardware it lacks. The
    /// feature being on, or a lid action still saved from an earlier run, shows it regardless: both
    /// mean the Windows lid-close action is parked on this app's override, and the switch that undoes
    /// that has to stay reachable even where the capability query says there is no lid.
    /// </summary>
    public static bool ShouldShow(bool lidPresent, bool enabled, bool hasSavedLidAction)
        => lidPresent || enabled || hasSavedLidAction;

    /// <summary>
    /// The saved delays as chips, with the configured delay folded in when no saved one carries it —
    /// a value the page holds must still be visible as the filled chip here. Clamped like every other
    /// read of the setting, so a hand-edited file cannot put an unreachable delay on a chip that then
    /// writes it back.
    /// </summary>
    public static IReadOnlyList<int> DelayChips(IEnumerable<int> savedMinutes, int currentMinutes)
    {
        int current = Clamp(currentMinutes);
        var saved = savedMinutes.Select(Clamp).Distinct().Order().ToList();
        if (!saved.Contains(current)) saved = [.. saved.Append(current).Order()];
        return saved;
    }

    /// <summary>The saved battery targets as chips, with the configured target folded in on the same
    /// terms as <see cref="DelayChips"/>.</summary>
    public static IReadOnlyList<int> LevelChips(IEnumerable<int> savedPercent, int currentPercent)
    {
        int current = LidDischargeWatch.Clamp(currentPercent);
        var saved = savedPercent.Select(LidDischargeWatch.Clamp).Distinct().Order().ToList();
        if (!saved.Contains(current)) saved = [.. saved.Append(current).Order()];
        return saved;
    }

    /// <summary>Chip-sized label — "5m", "30m", "1h", "1h30" — matching the keep-awake chips below it.</summary>
    public static string ShortLabel(int minutes) => minutes switch
    {
        < 60                     => $"{minutes}m",
        _ when minutes % 60 == 0 => $"{minutes / 60}h",
        _                        => $"{minutes / 60}h{minutes % 60}",
    };

    /// <summary>Chip-sized label for a battery target.</summary>
    public static string LevelLabel(int percent) => $"{LidDischargeWatch.Clamp(percent)} %";

    /// <summary>
    /// The line under the title. Off names what applies instead, like the sections beside it: the
    /// delay being off does not mean nothing happens, it means Windows handles the lid again. The two
    /// conditions are alternatives, so with both set the line says which arrives first decides, and
    /// with neither set it says the machine sleeps straight away.
    /// </summary>
    public static string Describe(bool enabled, bool timeEnabled, int minutes,
                                  bool dischargeEnabled, int targetPercent)
    {
        if (!enabled) return "Off — the Windows lid setting applies";

        string time  = $"{ShortLabel(Clamp(minutes))} after the lid closes";
        string level = $"at {LevelLabel(targetPercent)} battery";

        return (timeEnabled, dischargeEnabled) switch
        {
            (true,  true ) => $"On — sleeps {time}, or {level}, whichever comes first",
            (true,  false) => $"On — sleeps {time}",
            (false, true ) => $"On — sleeps {level}",
            (false, false) => "On — sleeps as soon as the lid closes",
        };
    }

    /// <summary>
    /// Which delay chip is filled, or null for none. An off section still shows its chips — they are
    /// the quick way to turn it on — but none of them is filled, so the row cannot read as running.
    /// The clock being off leaves the row unfilled for the same reason.
    /// </summary>
    public static int? ActiveChip(bool enabled, bool timeEnabled, int minutes)
        => enabled && timeEnabled ? Clamp(minutes) : null;

    /// <summary>Which battery-level chip is filled, on the same terms as <see cref="ActiveChip"/>.</summary>
    public static int? ActiveLevelChip(bool enabled, bool dischargeEnabled, int percent)
        => enabled && dischargeEnabled ? LidDischargeWatch.Clamp(percent) : null;

    private static int Clamp(int minutes) =>
        Math.Clamp(minutes, LidDelayPolicy.MinMinutes, LidDelayPolicy.MaxMinutes);
}
