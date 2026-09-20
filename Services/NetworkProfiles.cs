namespace ChargeKeeper.Services;

/// <summary>
/// The network-profile feature as one mechanism: which preset a network wins, what switching the
/// feature on and off does, and the reading every surface asks about. The Settings page, the tray
/// menu and an MQTT command all resolve through here, so none of them can decide a network
/// differently from the others.
/// </summary>
internal static class NetworkProfiles
{
    /// <summary>
    /// How a preset is applied, set once at startup to the tray's own apply. An injection point
    /// rather than a direct call: the apply marshals its own threads and refresh, and a test has no
    /// device to write to.
    /// </summary>
    public static Action<string>? ApplyPreset { get; set; }

    private static readonly Lock _sync = new();
    private static bool _started;

    // The Windows power plan a profile asks for, parked and restored the same crash-safe way the
    // lid-close wait parks the battery sleep timeout.
    private static readonly PowerPlanPark _powerPlan =
        new(new WindowsPowerPlanSetting(), new SettingsPowerPlanRecord(),
            (what, cause) => PowerLog.Event(what, cause));

    /// <summary>Wires the power-plan reaction to the machine moving, and settles the plan for where it
    /// is now — which is also what puts back a plan a run that ended mid-profile left switched.
    /// Called once at startup; never unsubscribed, since the subscription lives for the process.</summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        NetworkLocationService.LocationChanged += location =>
            ReconcilePowerPlan(location, "the network changed");
        ReconcilePowerPlan(CurrentLocation(), "starting up");
    }

    /// <summary>
    /// The preset a location gets: the matching profile's, and the unknown-network preset where a
    /// network resolved but matches no profile. No network at all is not an unknown network — it is
    /// nothing to react to — and a profile naming no preset applies nothing rather than falling
    /// through to the unknown-network one, because the network is known.
    /// </summary>
    internal static string? WinningPresetName(AppSettings settings, NetworkLocation location) =>
        settings.FindNetworkRule(location) is { } rule ? Named(rule.PresetName)
        : location.IsEmpty                            ? null
                                                      : Named(settings.UnknownNetworkPresetName);

    private static string? Named(string? presetName) =>
        string.IsNullOrWhiteSpace(presetName) ? null : presetName;

    /// <summary>
    /// The Windows power plan a location gets, or null where nothing asks for one. Unlike the preset
    /// there is no unknown-network fallback: a network nobody named is not a reason to change what
    /// the machine is running on.
    /// </summary>
    internal static Guid? WinningPowerPlan(AppSettings settings, NetworkLocation location) =>
        settings.NetworkProfilesEnabled
        && settings.FindNetworkRule(location) is { PowerPlan: { } plan }
        && Guid.TryParse(plan, out var wanted)
            ? wanted
            : null;

    /// <summary>
    /// Applies the power plan the profiles ask for at <paramref name="location"/>, or puts back the
    /// plan they displaced once none of them asks for one — the feature switched off, no profile
    /// matched, or the matched profile naming no plan. What a rule edit, a rule delete and the
    /// feature's own switch all need, none of which moves the machine.
    /// </summary>
    public static void ReconcilePowerPlan(NetworkLocation location, string cause) =>
        _powerPlan.Reconcile(WinningPowerPlan(SettingsService.Current, location), cause);

    /// <summary>Applies whatever profile wins at <paramref name="location"/>. Nothing happens while
    /// the feature is off, or where the winning name is no longer a preset.</summary>
    public static void ApplyWinner(NetworkLocation location)
    {
        var settings = SettingsService.Current;
        if (!settings.NetworkProfilesEnabled) return;
        if (WinningPresetName(settings, location) is not { } presetName) return;
        if (!settings.Presets.Any(p => p.Name == presetName)) return;
        ApplyPreset?.Invoke(presetName);
    }

    /// <summary>
    /// The one route by which the feature is switched, whichever surface asked. Switched on, the
    /// profile that wins here is applied at once: the location service raises nothing while the
    /// machine stays put, so anything else means waiting for a dock or a restart. Switched off, a
    /// hold a profile took is released; the charge preset is deliberately left running, because
    /// switching the feature off is not a request to change what the battery is doing.
    /// </summary>
    public static void SetEnabled(bool on, string cause)
    {
        SettingsService.Update(s => s.NetworkProfilesEnabled = on);

        var location = CurrentLocation();
        if (on) ApplyWinner(location);
        KeepAwakeService.ReconcileNetworkHold(location, cause);
        ReconcilePowerPlan(location, cause);
    }

    /// <summary>The cached reading, falling back to a live one only before the first evaluation has
    /// landed.</summary>
    public static NetworkLocation CurrentLocation()
    {
        var location = NetworkLocationService.LastKnown;
        return location.IsEmpty ? NetworkLocationService.DetectCurrent() : location;
    }
}
