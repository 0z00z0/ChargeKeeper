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

/// <summary>Pure routing from a parsed <see cref="HaCommand"/> to an <see cref="IChargeControlActions"/>
/// call, including the clamp that keeps a single-bound threshold set MinGap from its companion.</summary>
internal static class HaCommandDispatcher
{
    public static void Dispatch(HaCommand cmd, IChargeControlActions actions)
    {
        switch (cmd.Kind)
        {
            case HaCommandKind.SmartCharge:
                actions.SetSmartChargeEnabled(cmd.BoolValue);
                break;

            case HaCommandKind.ChargeStart:
            {
                var (_, stop) = actions.CurrentThresholds();
                // Keep the companion Stop fixed; clamp the new Start so Stop stays at least MinGap above.
                int upper = Math.Max(PresetEditValidator.MinThreshold, stop - PresetEditValidator.MinGap);
                int start = Math.Clamp(cmd.IntValue, PresetEditValidator.MinThreshold, upper);
                actions.ApplyThresholds(start, stop);
                break;
            }

            case HaCommandKind.ChargeStop:
            {
                var (start, _) = actions.CurrentThresholds();
                // Keep the companion Start fixed; clamp the new Stop so it stays at least MinGap above.
                int lower = Math.Min(PresetEditValidator.MaxThreshold, start + PresetEditValidator.MinGap);
                int stop = Math.Clamp(cmd.IntValue, lower, PresetEditValidator.MaxThreshold);
                actions.ApplyThresholds(start, stop);
                break;
            }

            case HaCommandKind.ChargeToFull:
                actions.ChargeToFullOnce();
                break;

            case HaCommandKind.SetPreset:
                actions.ApplyPreset(cmd.StringValue);
                break;
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

    public void ApplyThresholds(int start, int stop)
    {
        // clearActivePreset:true — a hand-picked range belongs to no named preset, so the HA numbers
        // must clear it just as the dashboard's threshold slider does.
        ChargeControlService.SetExplicitThresholds(start, stop, clearActivePreset: true);
    }

    public void SetSmartChargeEnabled(bool enable) => ChargeControlService.SetSmartChargeEnabled(enable);

    // Activate() owns its background work and revert timer, raising StateChanged once it settles.
    public void ChargeToFullOnce() => TravelOverrideService.Activate();

    public void ApplyPreset(string name) => ChargeControlService.ApplyPresetByName(name);
}
