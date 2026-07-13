namespace ChargeKeeper.Services;

/// <summary>
/// Pure "reject-on-save" validation for the Settings window's Smart Charge preset editor (TODO
/// #19). Deliberately separate from <c>DashboardWindow.OnThresholdRangeChanged</c>'s min-gap
/// enforcement: that method LIVE-NUDGES the other RangeSelector thumb while the user drags a
/// two-handled slider representing the CURRENTLY ACTIVE threshold — a different interaction
/// concern from this one, which validates a fully-typed name/start/stop triple from a text-entry
/// preset row and returns a reason to reject the edit outright, without silently correcting
/// anything. Follows this codebase's established "pure Build/Validate + thin UI caller" pattern
/// (see <see cref="HaStateBuilder"/> and its tests) so the highest-regression-risk rules here —
/// empty/duplicate names, minimum gap — are unit-tested directly rather than only reachable by
/// clicking through the UI.
/// </summary>
internal static class PresetEditValidator
{
    /// <summary>Minimum points between Start and Stop — mirrors DashboardWindow's own floor.</summary>
    internal const int MinGap = 5;

    /// <summary>
    /// Valid inclusive range for either threshold value. MinThreshold mirrors
    /// DashboardWindow.ConfigureThresholdRange's own live-slider floor of 5 (not the vendor
    /// layer's bare minimum of 1) — LenovoChargeThreshold.SetThresholds hard-rejects Start&lt;1
    /// outright, but a value of 1-4 would pass that check and still be a silent no-op-feeling
    /// edit inconsistent with every other threshold control in this app.
    /// </summary>
    internal const int MinThreshold = 5;
    internal const int MaxThreshold = 100;

    /// <summary>
    /// Names reserved by the UI itself — see <c>SettingsWindow.RefreshUnknownPresetCombo</c>'s
    /// "Do nothing" sentinel entry in the unknown-network-preset picker. A preset actually named
    /// this would be indistinguishable from that sentinel and could never be selected there.
    /// </summary>
    private static readonly string[] ReservedNames = ["Do nothing"];

    /// <summary>
    /// Validates a preset's name/thresholds before it is written to
    /// <see cref="AppSettings.Presets"/>. Returns null when valid, or a user-facing reason
    /// otherwise (the caller shows it inline and does NOT save).
    /// </summary>
    /// <param name="name">The name as currently typed in the row.</param>
    /// <param name="start">The row's current RangeSelector start value.</param>
    /// <param name="stop">The row's current RangeSelector stop value.</param>
    /// <param name="existingNames">Every OTHER preset's current name (case-insensitive compared).</param>
    /// <param name="originalName">
    /// This preset's own name before the edit (null when adding a brand-new preset). Excluded from
    /// the duplicate check via <paramref name="existingNames"/> normally already excluding it, but
    /// passed separately so a caller that passes the FULL name list (including this row's own,
    /// unedited entry) still doesn't false-positive on a no-op rename.
    /// </param>
    internal static string? Validate(string name, int start, int stop,
        IEnumerable<string> existingNames, string? originalName)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name for this preset.";

        string trimmed = name.Trim();

        if (ReservedNames.Any(r => string.Equals(r, trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"\"{trimmed}\" is reserved — pick a different name.";

        bool duplicate = existingNames.Any(n =>
            !string.Equals(n, originalName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
            return $"A preset named \"{trimmed}\" already exists.";

        if (start < MinThreshold || start > MaxThreshold || stop < MinThreshold || stop > MaxThreshold)
            return $"Thresholds must be between {MinThreshold} and {MaxThreshold}%.";

        if (stop - start < MinGap)
            return $"Stop must be at least {MinGap} points above Start.";

        return null;
    }
}
