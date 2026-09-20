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
    bool LidDelayTimeEnabled,
    int LidDelayMinutes,
    bool LidDischargeEnabled,
    int LidDischargeTargetPercent,
    bool LidDelayLockOnClose,
    bool LidDelayOffAfterSleep,
    bool SmartStandbyRunning,
    int? ScreenBrightness,
    FocusSessionStage FocusStage,
    int? FocusRemainingMinutes,
    int FocusSessionMinutes,
    bool FocusBlocksNetwork,
    bool FocusDimsScreen,
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
    int DowntimeGapMinutes,
    LidWaitState LidWait,
    int? LidWaitRemainingMinutes,
    int? KeepAwakeRemainingMinutes,
    AppChange? LastChange,
    DateTimeOffset? LastChangeAt,
    LidEventKind? LastLidEvent,
    DateTimeOffset? LastLidEventAt);

/// <summary>What the hardware can actually do, from the vendor gates the UI already uses. Announcing
/// a control the machine cannot honour would leave the receiver with an entity that silently does
/// nothing.</summary>
internal readonly record struct PublishCapabilities(
    SmartChargeSurface SmartCharge, bool LidClose, bool SmartStandby, bool ScreenBrightness)
{
    /// <summary>A machine with every gate open — the baseline the tests compare against.</summary>
    public static readonly PublishCapabilities Full = new(SmartChargeSurface.Numeric, true, true, true);
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
    /// unknown, so it is published rather than left absent. Spelled out rather than left as the
    /// reserved word: that word is what the receiver reads as no reading at all, and a matched-
    /// nothing answer is a different thing from a reading that never arrived.</summary>
    public const string NoProfile = "No profile matched";

    public static SurfaceState Read(string appVersion)
    {
        var session  = KeepAwakeService.Current;
        var location = NetworkLocationService.LastKnown;
        var adapter  = NetworkLocationService.LastKnownAdapter;
        // Empty means the first debounced evaluation has not landed yet, not "no network".
        if (location.IsEmpty && adapter.IsEmpty)
            (location, adapter) = NetworkLocationService.DetectCurrentDetailed();

        bool standby = IsStandbyRunning();
        var lidWait = LidDelayService.WaitNow();
        var focus = FocusSessionService.Current;
        var lastChange = AppChangeLog.Last;
        var lidEvent = LidEventLog.Last;
        var lidEventAt = LidEventLog.LastAt;
        var now = DateTimeOffset.Now;
        return SettingsService.Read(s => From(s, session, location, adapter, standby, appVersion,
                                              lidWait, focus, lastChange, lidEvent, lidEventAt, now));
    }

    /// <summary>Whole minutes from now to an instant, rounded up and never below zero, or null when
    /// there is no instant to count to. Rounded up so a countdown reads one until the moment it
    /// lands, rather than sitting on zero for the last half minute.</summary>
    /// <remarks>Nothing caches the answer: the countdown is composed on every read, so a value that
    /// went out a minute ago is never republished as though it still stood.</remarks>
    internal static int? MinutesUntil(DateTimeOffset? instant, DateTimeOffset now) =>
        instant is { } at ? Math.Max(0, (int)Math.Ceiling((at - now).TotalMinutes)) : null;

    /// <summary>
    /// The projection itself, over supplied state rather than the singletons, so what does and does
    /// not reach an entity is testable. Nothing here reads the broker block: it lives in the module's
    /// own settings file, its credentials are a secret, and the rest of it describes the transport
    /// rather than the machine.
    /// </summary>
    internal static SurfaceState From(
        AppSettings s, KeepAwakeSession? session, NetworkLocation location, NetworkAdapterInfo adapter,
        bool standbyRunning, string appVersion, LidWaitSnapshot lidWait, FocusSnapshot focus,
        AppChangeRecord? lastChange, LidEventObservation? lidEvent, DateTimeOffset? lidEventAt,
        DateTimeOffset now) => new(
            TravelOverrideActive:   s.TravelOverrideActive,
            KeepAwakeActive:        session is not null,
            KeepAwakeFor:           KeepAwakePolicy.ShortLabel(
                                        session?.Request ?? KeepAwakePolicy.DefaultRequest(s.KeepAwakePresets)),
            KeepAwakeExpires:       session?.ExpiresAt,
            KeepAwakeDisplayOn:     s.KeepAwakeDisplayOn,
            LidDelayEnabled:           s.LidDelayEnabled,
            LidDelayTimeEnabled:       s.LidDelayTimeEnabled,
            LidDelayMinutes:           s.LidDelayMinutes,
            LidDischargeEnabled:       s.LidDischargeEnabled,
            LidDischargeTargetPercent: s.LidDischargeTargetPercent,
            LidDelayLockOnClose:       s.LidDelayLockOnClose,
            LidDelayOffAfterSleep:     s.LidDelayOffAfterSleep,
            SmartStandbyRunning:    standbyRunning,
            // Read off the display rather than out of settings: the level lives in the panel, and
            // Windows or a function key moves it without this application hearing anything.
            ScreenBrightness:       ScreenBrightnessService.Current,
            FocusStage:             focus.Stage,
            // Absent whenever no session is running, so a machine at rest reports nothing rather
            // than a countdown of zero.
            FocusRemainingMinutes:  MinutesUntil(focus.EndsAt, now),
            FocusSessionMinutes:    s.FocusSessionMinutes,
            // A running session reports the levers it actually owns; with none running the two read
            // the defaults the next session would start from.
            FocusBlocksNetwork:     focus.IsRunning ? focus.BlocksNetwork : s.FocusBlocksNetwork,
            FocusDimsScreen:        focus.IsRunning ? focus.DimsScreen : s.FocusDimsScreen,
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
            DowntimeGapMinutes:     s.DowntimeGapMinutes,
            LidWait:                   lidWait.State,
            LidWaitRemainingMinutes:   MinutesUntil(lidWait.SleepsAt, now),
            // Absent for a session with no clock expiry — "until turned off" counts down to nothing.
            KeepAwakeRemainingMinutes: MinutesUntil(session?.ExpiresAt, now),
            LastChange:                lastChange?.What,
            LastChangeAt:              lastChange?.When,
            // The observation, as against the change above it: what the switch reported and whether
            // it changed anything. The idle reading beside it stays out of the broker — it is a
            // per-event fact, not a live state, and a continuously moving figure would be noise.
            LastLidEvent:              lidEvent?.Kind,
            LastLidEventAt:            lidEventAt);

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
        SmartStandby: StandbyService.IsSupported,
        // Not a vendor read: a machine whose only screen is an external monitor reports no display
        // the brightness interface reaches, and announcing a control it cannot honour would leave
        // the receiver with a slider that does nothing.
        ScreenBrightness: ScreenBrightnessService.IsSupported);

    // A published reading, not a capability: the facade is best-effort by contract, and a vendor RPC
    // that does throw must not take the whole surface read with it.
    private static bool IsStandbyRunning()
    {
        try { return StandbyService.IsRunning(); }
        catch (Exception ex) { AppLog.Error("SurfaceReader.StandbyRunning", ex); return false; }
    }
}
