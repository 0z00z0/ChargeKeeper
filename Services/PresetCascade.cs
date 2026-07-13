namespace ChargeKeeper.Services;

/// <summary>
/// Pure rename/delete cascade for threshold presets (TODO #19) — flagged in the issue as the
/// highest-regression-risk piece of the Settings window's preset editor. A preset's
/// <see cref="ThresholdPreset.Name"/> is referenced from three OTHER places in
/// <see cref="AppSettings"/>: <see cref="AppSettings.ActivePreset"/>, every
/// <see cref="NetworkLocationRule.PresetName"/>, and <see cref="AppSettings.UnknownNetworkPresetName"/>.
/// Renaming or deleting a preset without updating all three would silently orphan a network rule
/// (it would keep pointing at a preset name that no longer exists and simply stop matching
/// anything) or leave the tray's Presets submenu unable to find which item should show a check
/// mark. Operates on a plain <see cref="AppSettings"/> instance so it is unit-testable without
/// <see cref="SettingsService"/>'s file I/O — callers invoke it from inside
/// <c>SettingsService.Update(s => PresetCascade.Rename(s, oldName, newName))</c> so the mutation
/// and the save happen atomically under the same lock.
/// </summary>
internal static class PresetCascade
{
    /// <summary>
    /// Renames <paramref name="oldName"/> to <paramref name="newName"/> everywhere it's
    /// referenced. Does NOT touch <see cref="AppSettings.Presets"/> itself — the caller is
    /// expected to have already renamed (or be about to rename) the actual
    /// <see cref="ThresholdPreset.Name"/>; this only fixes up the three cross-references so they
    /// keep pointing at the same preset under its new name. No-op when the name didn't change.
    /// </summary>
    internal static void Rename(AppSettings s, string oldName, string newName)
    {
        if (oldName == newName) return;

        if (s.ActivePreset == oldName) s.ActivePreset = newName;
        if (s.UnknownNetworkPresetName == oldName) s.UnknownNetworkPresetName = newName;
        foreach (var rule in s.NetworkLocationRules)
            if (rule.PresetName == oldName) rule.PresetName = newName;
    }

    /// <summary>
    /// Removes the preset named <paramref name="name"/> from <see cref="AppSettings.Presets"/> and
    /// re-points every reference to it at <paramref name="fallbackName"/>.
    /// </summary>
    /// <param name="fallbackName">
    /// Preset name to re-point references at (e.g. the first remaining preset), or null to clear
    /// them — mirrors <see cref="AppSettings.ActivePreset"/>'s own "null = no active preset"
    /// meaning. <see cref="NetworkLocationRule.PresetName"/> is non-nullable, so a null fallback
    /// clears it to <c>""</c> there instead — an empty PresetName matches nothing (mirrors how a
    /// rule with no match key at all already matches nothing; see <see cref="NetworkLocationRule.Matches"/>),
    /// which is the correct "this rule's preset was deleted and nothing replaced it" behaviour
    /// rather than silently reactivating whatever preset happens to be first.
    /// </param>
    internal static void Delete(AppSettings s, string name, string? fallbackName)
    {
        // Remove only the first match, not RemoveAll: PresetEditValidator now blocks creating a
        // duplicate name going forward, but settings.json can still arrive with one (a hand edit,
        // or a sync/merge conflict) — RemoveAll would silently destroy two presets on one click.
        int index = s.Presets.FindIndex(p => p.Name == name);
        if (index >= 0) s.Presets.RemoveAt(index);

        if (s.ActivePreset == name) s.ActivePreset = fallbackName;
        if (s.UnknownNetworkPresetName == name) s.UnknownNetworkPresetName = fallbackName;
        foreach (var rule in s.NetworkLocationRules)
            if (rule.PresetName == name) rule.PresetName = fallbackName ?? "";
    }
}
