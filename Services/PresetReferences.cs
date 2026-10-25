using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>Gives presets their identifiers and turns the name references an older document holds
/// into identifier references, once, as the document is read.</summary>
internal static class PresetReferences
{
    /// <summary>Reports whether anything changed, so the document is written back.</summary>
    internal static bool Convert(AppSettings s)
    {
        bool changed = false;
        foreach (var preset in s.Presets.Where(p => p.Id.Length == 0))
        {
            preset.Id = StableId.New();
            changed = true;
        }

        foreach (var rule in s.NetworkLocationRules.Where(r => r.PresetName is not null))
        {
            if (rule.PresetId.Length == 0)
                rule.PresetId = Resolve(s, rule.PresetName!, $"network profile '{NetworkProfiles.NameOf(rule)}'") ?? "";
            rule.PresetName = null;
            changed = true;
        }

        if (s.UnknownNetworkPresetName is { } fallback)
        {
            s.UnknownNetworkPresetId ??= Resolve(s, fallback, "the unknown-network choice");
            s.UnknownNetworkPresetName = null;
            changed = true;
        }
        return changed;
    }

    // Exact, as every lookup by name was; a blank name meant no preset and is not worth a line.
    private static string? Resolve(AppSettings s, string name, string holder)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var matches = s.Presets.Where(p => p.Name == name).ToList();
        if (matches.Count == 0)
        {
            AppLog.Info($"Settings: {holder} named the preset '{name}', which does not exist, so the reference is dropped.");
            return null;
        }
        if (matches.Count > 1)
            AppLog.Info($"Settings: {holder} named the preset '{name}', which {matches.Count} presets share; the first is taken.");
        return matches[0].Id;
    }
}
