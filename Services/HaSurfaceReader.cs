namespace ChargeKeeper.Services;

/// <summary>
/// Gathers the current <see cref="HaSurfaceState"/> from settings and the live services. The one
/// impure half of the settings surface: the payload it feeds and the entity set it is published
/// against are both pure.
/// </summary>
/// <remarks>Runs on the MQTT threads, so it must not block on the UI. <see cref="StandbyService"/>'s
/// read reaches a vendor service, which is why this is called on state changes rather than on a
/// timer.</remarks>
internal static class HaSurfaceReader
{
    public static HaSurfaceState Read(string appVersion)
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
    /// not reach a payload is testable. Nothing here reads <see cref="AppSettings.MqttUsername"/>,
    /// <see cref="AppSettings.MqttPassword"/> or the broker address: the credentials are a secret, and
    /// the rest of the connection block describes the transport rather than the machine.
    /// </summary>
    internal static HaSurfaceState From(
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

    /// <summary>The vendor capabilities the announcement is gated on, read once per publish.</summary>
    public static HaCapabilities Capabilities() => new(
        SmartCharge:  ThresholdCapabilityPolicy.Classify(
                          ChargeThresholdService.Read(), ChargeThresholdService.SupportsNumericThresholds),
        LidClose:     LidDelayService.IsSupported,
        SmartStandby: IsStandbySupported());

    // The facade is best-effort by contract, but a vendor RPC that does throw must not take the whole
    // publish with it.
    private static bool IsStandbyRunning()
    {
        try { return StandbyService.IsRunning(); }
        catch (Exception ex) { AppLog.Error("HaSurfaceReader.StandbyRunning", ex); return false; }
    }

    private static bool IsStandbySupported()
    {
        try { return StandbyService.IsSupported; }
        catch (Exception ex) { AppLog.Error("HaSurfaceReader.StandbySupported", ex); return false; }
    }
}
