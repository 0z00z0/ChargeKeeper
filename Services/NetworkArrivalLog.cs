namespace ChargeKeeper.Services;

/// <summary>
/// One log line on arriving at a network a profile matches, naming the profile and what it carries,
/// ahead of the lines each reaction writes for itself. Subscribes before every other reaction to a
/// location change, so the line lands above theirs.
/// </summary>
internal static class NetworkArrivalLog
{
    private static int _started;

    /// <summary>Called once at startup, before any other <see cref="NetworkLocationService.LocationChanged"/>
    /// subscriber: an event's handlers run in the order they subscribed.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        NetworkLocationService.LocationChanged += OnLocationChanged;
    }

    private static void OnLocationChanged(NetworkLocation location)
    {
        string? line = SettingsService.Read(s => s.FindNetworkRule(location) is { } rule ? Describe(s, rule) : null);
        if (line is not null) AppLog.Info(line);
    }

    /// <summary>The line for arriving where <paramref name="rule"/> matches. Lists what will actually
    /// happen: the preset and the hold only while network profiles are switched on, and only the
    /// scripts the runner would start.</summary>
    internal static string Describe(AppSettings settings, NetworkLocationRule rule)
    {
        var parts = new List<string>();
        bool presetOrHold = !string.IsNullOrWhiteSpace(rule.PresetName) || rule.KeepAwakeHere;

        if (presetOrHold && !settings.NetworkProfilesEnabled)
            parts.Add("network profiles are switched off, so no preset or keep-awake applies");
        else
        {
            if (!string.IsNullOrWhiteSpace(rule.PresetName))
                parts.Add(settings.Presets.FirstOrDefault(p => p.Name == rule.PresetName) is { } preset
                    ? $"Smart Charge preset '{preset.Name}' ({preset.Start}/{preset.Stop})"
                    : $"Smart Charge preset '{rule.PresetName}', which no longer exists, so none is applied");
            if (rule.KeepAwakeHere)
                parts.Add("keep-awake until the network changes");
        }

        var known = settings.NetworkLocationRules.Select(r => r.Id).ToList();
        foreach (var script in ScriptTriggerPolicy.Matching(settings.Scripts, ScriptTrigger.NetworkJoined, rule.Id, known))
            parts.Add($"script '{script.DisplayName}' on joining");

        string carries = parts.Count > 0 ? string.Join("; ", parts) : "nothing is configured for it";
        return $"Network profile '{NetworkProfiles.NameOf(rule)}' matched: {carries}";
    }
}
