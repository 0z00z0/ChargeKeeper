namespace ChargeKeeper.Services;

/// <summary>The charge-control actions an inbound MQTT command can trigger, behind an interface so
/// the entity table's command seam can be tested with a spy instead of the live vendor RPC.</summary>
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
/// makes. Behind an interface for the same reason as <see cref="IChargeControlActions"/>: the command
/// seam is testable without touching settings.json, the power scheme or a vendor service.</summary>
internal interface ISettingsActions
{
    /// <summary>The configured preset names, which are the two preset-backed selects' options. Read on
    /// every announcement pass, because the list changes while the app runs.</summary>
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

/// <summary>The one rule a charge threshold arriving on its own has to obey: its companion stays put,
/// and the pair stays <see cref="PresetEditValidator.MinGap"/> apart. Pure, because it is the only
/// part of the charge-control seam that decides rather than acts.</summary>
/// <remarks>This clamps where a refusal would be worse. The two thresholds are one control split over
/// two entities, so a receiver's slider can legitimately be dragged to a value that is only invalid
/// against the other half — refusing it would leave the slider showing a value the device never took,
/// with nothing on screen to say why. Every bound the entity itself declares is still enforced by
/// refusal, before this is reached.</remarks>
internal static class ChargeThresholdCommands
{
    /// <summary>A new start against the stop in force. Stop is kept; start is held far enough below it.</summary>
    public static (int Start, int Stop) WithStart(int wanted, int stop)
    {
        int upper = Math.Max(PresetEditValidator.MinThreshold, stop - PresetEditValidator.MinGap);
        return (Math.Clamp(wanted, PresetEditValidator.MinThreshold, upper), stop);
    }

    /// <summary>A new stop against the start in force. Start is kept; stop is held far enough above it.</summary>
    public static (int Start, int Stop) WithStop(int wanted, int start)
    {
        int lower = Math.Min(PresetEditValidator.MaxThreshold, start + PresetEditValidator.MinGap);
        return (start, Math.Clamp(wanted, lower, PresetEditValidator.MaxThreshold));
    }
}

/// <summary>The live <see cref="IChargeControlActions"/>, routing each command onto the shared
/// <see cref="ChargeControlService"/> the tray menu also drives. Every method runs synchronously and
/// the vendor RPC blocks for seconds, so it belongs on the command worker rather than on the receive
/// callback — which is where the module runs the work a verdict carries.</summary>
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

/// <summary>The live <see cref="ISettingsActions"/>. Each write goes through the same service the
/// Settings window calls, never straight at settings.json, so the side effects — the power-scheme
/// override, the OS keep-awake hold, the vendor service control — happen exactly as they do from the
/// UI. Runs on the command worker, where a blocking write is expected.</summary>
internal sealed class SettingsActions : ISettingsActions
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
