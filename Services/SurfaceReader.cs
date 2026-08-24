namespace ChargeKeeper.Services;

/// <summary>
/// The settings, network and diagnostic values behind every entity that does not come off a battery
/// reading. Read on its own signal, so a settings change does not have to wait for a battery tick and
/// a battery tick does not re-read the settings.
/// </summary>
/// <remarks>The broker credentials are deliberately absent, and there is no field they could reach:
/// publishing them over the very broker they authenticate to would put them in plain text in the
/// receiver's log and in a retained topic.</remarks>
internal readonly record struct SurfaceState(
    bool TravelOverrideActive,
    bool KeepAwakeActive,
    string KeepAwakeFor,
    DateTimeOffset? KeepAwakeExpires,
    bool KeepAwakeDisplayOn,
    bool LidDelayEnabled,
    int LidDelayMinutes,
    bool LidDelayLockOnClose,
    bool SmartStandbyRunning,
    bool LowBatteryWarning,
    int LowBatteryLevel,
    bool HighBatteryWarning,
    int HighBatteryLevel,
    bool DrainWarning,
    int DrainRate,
    bool NetworkProfilesEnabled,
    string UnknownNetworkPreset,
    string? NetworkAlias,
    string? NetworkIpAddress,
    string? NetworkAdapterName,
    string? MatchedNetworkProfile,
    string AppVersion,
    int StartupDelaySeconds,
    TrayIconMode IconMode,
    int DowntimeGapMinutes);

/// <summary>What the hardware can actually do, from the vendor gates the UI already uses. Announcing
/// a control the machine cannot honour would leave the receiver with an entity that silently does
/// nothing.</summary>
internal readonly record struct PublishCapabilities(
    SmartChargeSurface SmartCharge, bool LidClose, bool SmartStandby)
{
    /// <summary>A machine with every gate open — the baseline the tests compare against.</summary>
    public static readonly PublishCapabilities Full = new(SmartChargeSurface.Numeric, true, true);
}

/// <summary>
/// Gathers the current <see cref="SurfaceState"/> from settings and the live services. The one impure
/// half of the settings surface: the entity table it feeds is pure.
/// </summary>
/// <remarks>Runs on the MQTT threads, so it must not block on the UI. <see cref="StandbyService"/>'s
/// read reaches a vendor service, which is why this is called on state changes rather than on a
/// timer.</remarks>
internal static class SurfaceReader
{
    /// <summary>Nothing matched, for the profile sensor. A matched-nothing reading is known, not
    /// unknown, so it is published rather than left absent.</summary>
    public const string NoProfile = "None";

    public static SurfaceState Read(string appVersion)
    {
        var session  = KeepAwakeService.Current;
        var location = NetworkLocationService.LastKnown;
        var adapter  = NetworkLocationService.LastKnownAdapter;
        // Empty means the first debounced evaluation has not landed yet, not "no network".
        if (location.IsEmpty && adapter.IsEmpty)
            (location, adapter) = NetworkLocationService.DetectCurrentDetailed();

        bool standby = IsStandbyRunning();
        return SettingsService.Read(s => From(s, session, location, adapter, standby, appVersion));
    }

    /// <summary>
    /// The projection itself, over supplied state rather than the singletons, so what does and does
    /// not reach an entity is testable. Nothing here reads the broker block: it lives in the module's
    /// own settings file, its credentials are a secret, and the rest of it describes the transport
    /// rather than the machine.
    /// </summary>
    internal static SurfaceState From(
        AppSettings s, KeepAwakeSession? session, NetworkLocation location, NetworkAdapterInfo adapter,
        bool standbyRunning, string appVersion) => new(
            TravelOverrideActive:   s.TravelOverrideActive,
            KeepAwakeActive:        session is not null,
            KeepAwakeFor:           KeepAwakePolicy.ShortLabel(
                                        session?.Request ?? KeepAwakePolicy.DefaultRequest(s.KeepAwakePresets)),
            KeepAwakeExpires:       session?.ExpiresAt,
            KeepAwakeDisplayOn:     s.KeepAwakeDisplayOn,
            LidDelayEnabled:        s.LidDelayEnabled,
            LidDelayMinutes:        s.LidDelayMinutes,
            LidDelayLockOnClose:    s.LidDelayLockOnClose,
            SmartStandbyRunning:    standbyRunning,
            LowBatteryWarning:      s.LowBatteryWarningEnabled,
            LowBatteryLevel:        s.LowBatteryWarningPct,
            HighBatteryWarning:     s.HighBatteryWarningEnabled,
            HighBatteryLevel:       s.HighBatteryWarningPct,
            DrainWarning:           s.DrainAnomalyWarningEnabled,
            DrainRate:              s.DrainAnomalyPercentPerHour,
            NetworkProfilesEnabled: s.NetworkProfilesEnabled,
            UnknownNetworkPreset:   s.UnknownNetworkPresetName ?? PresetEditValidator.UnknownNetworkSentinel,
            NetworkAlias:           adapter.Alias,
            NetworkIpAddress:       adapter.IpAddress,
            NetworkAdapterName:     adapter.AdapterName,
            MatchedNetworkProfile:  s.FindNetworkRule(location)?.Name,
            AppVersion:             appVersion,
            StartupDelaySeconds:    s.StartupDelaySeconds,
            IconMode:               s.IconMode,
            DowntimeGapMinutes:     s.DowntimeGapMinutes);

    /// <summary>The vendor capabilities the announcement is gated on.</summary>
    /// <remarks>Deliberately unguarded. A vendor read that fails has to reach the caller as a throw:
    /// the announcement layer reads a throw as "the capability could not be read" and keeps whatever
    /// the record already says, while a false says the capability is absent and withholds every entity
    /// behind it. An EC that does not answer and a WMI call that times out are the first, not the
    /// second, and a resume from standby is exactly when they are least likely to answer.</remarks>
    public static PublishCapabilities Capabilities() => new(
        SmartCharge:  ThresholdCapabilityPolicy.Classify(
                          ChargeThresholdService.Read(), ChargeThresholdService.SupportsNumericThresholds),
        LidClose:     LidDelayService.IsSupported,
        SmartStandby: StandbyService.IsSupported);

    // A published reading, not a capability: the facade is best-effort by contract, and a vendor RPC
    // that does throw must not take the whole surface read with it.
    private static bool IsStandbyRunning()
    {
        try { return StandbyService.IsRunning(); }
        catch (Exception ex) { AppLog.Error("SurfaceReader.StandbyRunning", ex); return false; }
    }
}
