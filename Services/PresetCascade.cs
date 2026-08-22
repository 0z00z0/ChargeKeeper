namespace ChargeKeeper.Services;

/// <summary>Rename/delete cascade for threshold presets. A preset's <see cref="ThresholdPreset.Name"/> is
/// also referenced by every <see cref="NetworkLocationRule.PresetName"/> and by
/// <see cref="AppSettings.UnknownNetworkPresetName"/>, and missing one silently orphans a network rule.
/// Takes a plain <see cref="AppSettings"/> so callers can run it inside <c>SettingsService.Update</c>.</summary>
internal static class PresetCascade
{
    /// <summary>Fixes up every cross-reference. Does NOT touch <see cref="AppSettings.Presets"/> —
    /// the caller renames the preset itself.</summary>
    internal static void Rename(AppSettings s, string oldName, string newName)
    {
        if (oldName == newName) return;

        if (s.UnknownNetworkPresetName == oldName) s.UnknownNetworkPresetName = newName;
        foreach (var rule in s.NetworkLocationRules)
            if (rule.PresetName == oldName) rule.PresetName = newName;
    }

    /// <summary>Removes the preset and re-points every reference to it at <paramref name="fallbackName"/>.</summary>
    /// <param name="fallbackName">Null clears the references. <see cref="NetworkLocationRule.PresetName"/>
    /// is non-nullable, so it clears to <c>""</c> — which matches nothing, rather than silently
    /// reactivating whichever preset happens to be first.</param>
    internal static void Delete(AppSettings s, string name, string? fallbackName)
    {
        // First match only, not RemoveAll: settings.json can still arrive with a duplicate name (a
        // hand edit or a sync conflict), and RemoveAll would destroy two presets on one click.
        int index = s.Presets.FindIndex(p => p.Name == name);
        if (index >= 0) s.Presets.RemoveAt(index);

        if (s.UnknownNetworkPresetName == name) s.UnknownNetworkPresetName = fallbackName;
        foreach (var rule in s.NetworkLocationRules)
            if (rule.PresetName == name) rule.PresetName = fallbackName ?? "";
    }
}
