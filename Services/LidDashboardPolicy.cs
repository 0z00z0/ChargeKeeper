namespace ChargeKeeper.Services;

/// <summary>
/// Pure decisions behind the dashboard's Lid close section: whether the section belongs on this
/// machine at all, which quick delays its chip row offers, and the line under its title. No power
/// scheme and no window, so the rules are unit-testable — <see cref="LidDelayService"/> owns the OS
/// side and remains the only writer.
/// </summary>
internal static class LidDashboardPolicy
{
    /// <summary>
    /// The delays the chip row offers, in minutes. Shorter than the Settings list on purpose: the
    /// dashboard is the quick surface, and the row has one popup width to spend.
    /// </summary>
    public static readonly int[] QuickMinutes = [5, 10, 30, 60];

    /// <summary>
    /// A machine with no lid has nothing to delay, so the section would claim hardware it lacks. The
    /// feature being on, or a lid action still saved from an earlier run, shows it regardless: both
    /// mean the Windows lid-close action is parked on this app's override, and the switch that undoes
    /// that has to stay reachable even where the capability query says there is no lid.
    /// </summary>
    public static bool ShouldShow(bool lidPresent, bool enabled, bool hasSavedLidAction)
        => lidPresent || enabled || hasSavedLidAction;

    /// <summary>
    /// The quick delays ascending, with the configured delay folded in when it is not already one of
    /// them — a value chosen in Settings must still be visible as the filled chip here. Clamped like
    /// every other read of the setting, so a hand-edited file cannot put an unreachable delay on a
    /// chip that then writes it back.
    /// </summary>
    public static IReadOnlyList<int> Chips(int currentMinutes)
    {
        int current = Clamp(currentMinutes);
        return QuickMinutes.Contains(current) ? QuickMinutes : [.. QuickMinutes.Append(current).Order()];
    }

    /// <summary>Chip-sized label — "5m", "30m", "1h", "1h30" — matching the keep-awake chips below it.</summary>
    public static string ShortLabel(int minutes) => minutes switch
    {
        < 60                     => $"{minutes}m",
        _ when minutes % 60 == 0 => $"{minutes / 60}h",
        _                        => $"{minutes / 60}h{minutes % 60}",
    };

    /// <summary>The line under the title. Off names what applies instead, like the sections beside it:
    /// the delay being off does not mean nothing happens, it means Windows handles the lid again.</summary>
    public static string Describe(bool enabled, int minutes) => enabled
        ? $"On — sleeps {ShortLabel(Clamp(minutes))} after the lid closes"
        : "Off — the Windows lid setting applies";

    /// <summary>
    /// Which chip is filled, or null for none. An off section still shows its chips — they are the
    /// quick way to turn it on — but none of them is filled, so the row cannot read as running.
    /// </summary>
    public static int? ActiveChip(bool enabled, int minutes) => enabled ? Clamp(minutes) : null;

    private static int Clamp(int minutes) =>
        Math.Clamp(minutes, LidDelayPolicy.MinMinutes, LidDelayPolicy.MaxMinutes);
}
