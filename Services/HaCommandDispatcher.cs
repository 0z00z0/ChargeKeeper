namespace ChargeKeeper.Services;

/// <summary>The charge-control actions an inbound MQTT command can trigger, behind an interface so the
/// dispatcher's routing can be tested with a spy instead of the live vendor RPC.</summary>
internal interface IChargeControlActions
{
    /// <summary>Current start/stop to combine a single-bound number-set against; falls back to a valid
    /// default pair when Smart Charge is off or unset.</summary>
    (int Start, int Stop) CurrentThresholds();

    /// <summary>Writes explicit thresholds, enabling Smart Charge and superseding any override.</summary>
    void ApplyThresholds(int start, int stop);

    /// <summary>Turns Smart Charge on/off; on while a "charge to 100 %" override runs cancels it.</summary>
    void SetSmartChargeEnabled(bool enable);

    void ChargeToFullOnce();

    /// <summary>Applies the named preset; an unconfigured name is a no-op.</summary>
    void ApplyPreset(string name);
}

/// <summary>The settings an inbound MQTT command can write — the same writes the Settings window
/// makes. Behind an interface for the same reason as <see cref="IChargeControlActions"/>: the routing
/// is testable without touching settings.json, the power scheme or a vendor service.</summary>
internal interface IHaSettingsActions
{
    /// <summary>The configured preset names, for the one membership check the parser cannot do: the
    /// list changes while the app runs.</summary>
    IReadOnlyList<string> PresetNames();

    void SetKeepAwake(bool on);
    void StartKeepAwake(KeepAwakeRequest request);
    void SetKeepAwakeDisplayOn(bool on);
    void SetLidDelay(bool on);
    void SetLidDelayMinutes(int minutes);
    void SetLidDelayLock(bool on);
    void SetSmartStandby(bool on);
    void SetLowBatteryWarning(bool on);
    void SetLowBatteryLevel(int percent);
    void SetHighBatteryWarning(bool on);
    void SetHighBatteryLevel(int percent);
    void SetDrainWarning(bool on);
    void SetDrainRate(int percentPerHour);
    void SetNetworkProfiles(bool on);
    void SetUnknownNetworkPreset(string? name);
    void SetStartupDelay(int seconds);
    void SetIconMode(TrayIconMode mode);
    void SetDowntimeGap(int minutes);
}

/// <summary>Pure routing from a parsed <see cref="HaCommand"/> to an action, including the clamp that
/// keeps a single-bound threshold set MinGap from its companion. False means the command was refused
/// and nothing was applied.</summary>
internal static class HaCommandDispatcher
{
    public static bool Dispatch(HaCommand cmd, IChargeControlActions charge, IHaSettingsActions settings)
    {
        switch (cmd.Kind)
        {
            case HaCommandKind.SmartCharge:
                charge.SetSmartChargeEnabled(cmd.BoolValue);
                return true;

            case HaCommandKind.ChargeStart:
            {
                var (_, stop) = charge.CurrentThresholds();
                // Keep the companion Stop fixed; clamp the new Start so Stop stays at least MinGap above.
                int upper = Math.Max(PresetEditValidator.MinThreshold, stop - PresetEditValidator.MinGap);
                int start = Math.Clamp(cmd.IntValue, PresetEditValidator.MinThreshold, upper);
                charge.ApplyThresholds(start, stop);
                return true;
            }

            case HaCommandKind.ChargeStop:
            {
                var (start, _) = charge.CurrentThresholds();
                // Keep the companion Start fixed; clamp the new Stop so it stays at least MinGap above.
                int lower = Math.Min(PresetEditValidator.MaxThreshold, start + PresetEditValidator.MinGap);
                int stop = Math.Clamp(cmd.IntValue, lower, PresetEditValidator.MaxThreshold);
                charge.ApplyThresholds(start, stop);
                return true;
            }

            case HaCommandKind.ChargeToFull:
                charge.ChargeToFullOnce();
                return true;

            case HaCommandKind.SetPreset:
                charge.ApplyPreset(cmd.StringValue);
                return true;

            case HaCommandKind.KeepAwake:
                settings.SetKeepAwake(cmd.BoolValue);
                return true;

            case HaCommandKind.KeepAwakeFor:
                // The parser only produces a request for input it accepted, so a null here is a
                // refusal, not a default to fall back on.
                if (cmd.Request is not { } request) return false;
                settings.StartKeepAwake(request);
                return true;

            case HaCommandKind.KeepAwakeDisplayOn:  settings.SetKeepAwakeDisplayOn(cmd.BoolValue); return true;
            case HaCommandKind.LidDelay:            settings.SetLidDelay(cmd.BoolValue);           return true;
            case HaCommandKind.LidDelayMinutes:     settings.SetLidDelayMinutes(cmd.IntValue);     return true;
            case HaCommandKind.LidDelayLock:        settings.SetLidDelayLock(cmd.BoolValue);       return true;
            case HaCommandKind.SmartStandby:        settings.SetSmartStandby(cmd.BoolValue);       return true;
            case HaCommandKind.LowBatteryWarning:   settings.SetLowBatteryWarning(cmd.BoolValue);  return true;
            case HaCommandKind.LowBatteryLevel:     settings.SetLowBatteryLevel(cmd.IntValue);     return true;
            case HaCommandKind.HighBatteryWarning:  settings.SetHighBatteryWarning(cmd.BoolValue); return true;
            case HaCommandKind.HighBatteryLevel:    settings.SetHighBatteryLevel(cmd.IntValue);    return true;
            case HaCommandKind.DrainWarning:        settings.SetDrainWarning(cmd.BoolValue);       return true;
            case HaCommandKind.DrainRate:           settings.SetDrainRate(cmd.IntValue);           return true;
            case HaCommandKind.NetworkProfiles:     settings.SetNetworkProfiles(cmd.BoolValue);    return true;
            case HaCommandKind.StartupDelay:        settings.SetStartupDelay(cmd.IntValue);        return true;
            case HaCommandKind.DowntimeGap:         settings.SetDowntimeGap(cmd.IntValue);         return true;

            case HaCommandKind.IconMode:
                settings.SetIconMode((TrayIconMode)cmd.IntValue);
                return true;

            case HaCommandKind.UnknownNetworkPreset:
            {
                // Checked here rather than in the parser because the preset list changes while the app
                // runs. The sentinel is "route nowhere", and is stored as no preset at all.
                if (string.Equals(cmd.StringValue, PresetEditValidator.UnknownNetworkSentinel,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    settings.SetUnknownNetworkPreset(null);
                    return true;
                }
                string? match = settings.PresetNames()
                    .FirstOrDefault(n => string.Equals(n, cmd.StringValue, StringComparison.OrdinalIgnoreCase));
                if (match is null) return false;
                settings.SetUnknownNetworkPreset(match);
                return true;
            }

            default:
                return false;
        }
    }
}

/// <summary>The live <see cref="IChargeControlActions"/>, routing each command onto the shared
/// <see cref="ChargeControlService"/> the tray menu also drives. Every method runs synchronously and
/// the vendor RPC blocks for seconds, so the caller must dispatch off the MQTT receive callback.</summary>
internal sealed class ChargeControlActions : IChargeControlActions
{
    // A fresh device read: the app's cached snapshot only refreshes on a battery tick, so two queued
    // commands would both see the pre-write pair. Null in tests, which fall back to a live read.
    private readonly Func<(int Start, int Stop)?>? _currentThresholds;

    public ChargeControlActions(Func<(int Start, int Stop)?>? currentThresholds = null)
        => _currentThresholds = currentThresholds;

    public (int Start, int Stop) CurrentThresholds()
    {
        if (_currentThresholds is { } provider)
            return provider.Invoke() is { } cached && IsValidPair(cached.Start, cached.Stop)
                ? cached
                : DefaultThresholds();

        var s = ChargeThresholdService.Read();
        if (s is not null && IsValidPair(s.Start, s.Stop))
            return (s.Start, s.Stop);
        return DefaultThresholds();
    }

    // A valid Smart Charge pair: both thresholds in range and at least MinGap apart.
    private static bool IsValidPair(int start, int stop) =>
        start >= PresetEditValidator.MinThreshold &&
        stop  <= PresetEditValidator.MaxThreshold &&
        stop - start >= PresetEditValidator.MinGap;

    // Default pair when Smart Charge is off or unset (firmware may read back 0/0). Taken from the
    // built-in "Daily" preset so it can't drift; the literal covers a user who deleted that preset.
    private static (int Start, int Stop) DefaultThresholds()
    {
        var daily = SettingsService.Read(s => s.Presets.FirstOrDefault(p => p.Name == "Daily"));
        return daily is { Start: >= PresetEditValidator.MinThreshold, Stop: <= PresetEditValidator.MaxThreshold }
               && daily.Stop - daily.Start >= PresetEditValidator.MinGap
            ? (daily.Start, daily.Stop)
            : (60, 80);
    }

    public void ApplyThresholds(int start, int stop) =>
        ChargeControlService.SetExplicitThresholds(start, stop);

    public void SetSmartChargeEnabled(bool enable) => ChargeControlService.SetSmartChargeEnabled(enable);

    // Activate() owns its background work and revert timer, raising StateChanged once it settles.
    public void ChargeToFullOnce() => TravelOverrideService.Activate();

    public void ApplyPreset(string name) => ChargeControlService.ApplyPresetByName(name);
}

/// <summary>The live <see cref="IHaSettingsActions"/>. Each write goes through the same service the
/// Settings window calls, never straight at settings.json, so the side effects — the power-scheme
/// override, the OS keep-awake hold, the vendor service control — happen exactly as they do from the
/// UI. Runs on the command worker, where a blocking write is expected.</summary>
internal sealed class HaSettingsActions : IHaSettingsActions
{
    /// <summary>Raised after a write lands, so the publisher reflects the new value without waiting
    /// for a battery tick.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> PresetNames() => SettingsService.Read(s => s.Presets.Select(p => p.Name).ToList());

    public void SetKeepAwake(bool on)
    {
        if (on)
            KeepAwakeService.Activate(
                KeepAwakePolicy.DefaultRequest(SettingsService.Read(s => s.KeepAwakePresets.ToList())),
                "MQTT command");
        else
            KeepAwakeService.Deactivate("MQTT command");
        Raise();
    }

    public void StartKeepAwake(KeepAwakeRequest request)
    {
        KeepAwakeService.Activate(request, "MQTT command");
        Raise();
    }

    public void SetKeepAwakeDisplayOn(bool on) => Write(s => s.KeepAwakeDisplayOn = on);

    // SetEnabled owns the power-scheme capture and restore, and refuses rather than promising a delay
    // the machine will not honour.
    public void SetLidDelay(bool on)
    {
        LidDelayService.SetEnabled(on);
        Raise();
    }

    public void SetLidDelayMinutes(int minutes) => Write(s => s.LidDelayMinutes = minutes);
    public void SetLidDelayLock(bool on)        => Write(s => s.LidDelayLockOnClose = on);

    public void SetSmartStandby(bool on)
    {
        StandbyService.SetEnabled(on);
        Raise();
    }

    public void SetLowBatteryWarning(bool on)   => Write(s => s.LowBatteryWarningEnabled = on);
    public void SetLowBatteryLevel(int percent) => Write(s => s.LowBatteryWarningPct = percent);
    public void SetHighBatteryWarning(bool on)  => Write(s => s.HighBatteryWarningEnabled = on);
    public void SetHighBatteryLevel(int percent)=> Write(s => s.HighBatteryWarningPct = percent);
    public void SetDrainWarning(bool on)        => Write(s => s.DrainAnomalyWarningEnabled = on);
    public void SetDrainRate(int percentPerHour)=> Write(s => s.DrainAnomalyPercentPerHour = percentPerHour);
    public void SetNetworkProfiles(bool on)     => Write(s => s.NetworkProfilesEnabled = on);
    public void SetUnknownNetworkPreset(string? name) => Write(s => s.UnknownNetworkPresetName = name);
    public void SetStartupDelay(int seconds)    => Write(s => s.StartupDelaySeconds = seconds);
    public void SetIconMode(TrayIconMode mode)  => Write(s => s.IconMode = mode);
    public void SetDowntimeGap(int minutes)     => Write(s => s.DowntimeGapMinutes = minutes);

    private void Write(Action<AppSettings> mutate)
    {
        SettingsService.Update(mutate);
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
