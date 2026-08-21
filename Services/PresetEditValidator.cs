namespace ChargeKeeper.Services;

/// <summary>Pure "reject-on-save" validation for a preset's name and thresholds, used by the Settings
/// editor and by <see cref="ChargeControlService.ApplyPresetByName"/> for presets that never pass through
/// it. Rejects outright rather than silently correcting, unlike the dashboard slider's live nudge.</summary>
internal static class PresetEditValidator
{
    /// <summary>Minimum points between Start and Stop — mirrors DashboardWindow's own floor.</summary>
    internal const int MinGap = 5;

    /// <summary>MinThreshold mirrors the dashboard slider's floor of 5, not the vendor layer's bare
    /// minimum of 1: 1-4 passes the vendor check but is inconsistent with every other control.</summary>
    internal const int MinThreshold = 5;
    internal const int MaxThreshold = 100;

    /// <summary>The unknown-network picker's "route nowhere" entry. Shared with the combo so a preset
    /// can never be named the same and become indistinguishable from it there.</summary>
    internal const string UnknownNetworkSentinel = "Do nothing";

    private static readonly string[] ReservedNames = [UnknownNetworkSentinel];

    /// <summary>Null when valid, else a user-facing reason the caller shows inline without saving.</summary>
    /// <param name="existingNames">Other presets' names, compared case-insensitively.</param>
    /// <param name="originalName">This preset's name before the edit, or null when adding. Excluded from
    /// the duplicate check so a caller passing the FULL name list doesn't trip on a no-op rename.</param>
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
