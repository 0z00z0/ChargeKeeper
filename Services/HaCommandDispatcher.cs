namespace ChargeKeeper.Services;

/// <summary>
/// The charge-control actions an inbound MQTT command can trigger (issue #30), behind an interface
/// so <see cref="HaCommandDispatcher.Dispatch"/>'s routing + threshold-gap arithmetic is unit-tested
/// with a spy instead of against the live vendor RPC. <see cref="ChargeControlActions"/> is the real
/// implementation used by <see cref="HomeAssistantService"/>.
/// </summary>
internal interface IChargeControlActions
{
    /// <summary>Current Smart Charge start/stop to combine a single-bound number-set against; a
    /// sensible default (e.g. 60–80) when Smart Charge is off/unset so the first set is still valid.</summary>
    (int Start, int Stop) CurrentThresholds();

    /// <summary>Writes explicit thresholds to the device (enabling Smart Charge), superseding any override.</summary>
    void ApplyThresholds(int start, int stop);

    /// <summary>Turns Smart Charge on/off (on while a "charge to 100 %" override runs cancels it).</summary>
    void SetSmartChargeEnabled(bool enable);

    /// <summary>Starts the one-shot "charge to 100 % once" travel override.</summary>
    void ChargeToFullOnce();

    /// <summary>Applies the named preset (no-op if the name isn't a configured preset).</summary>
    void ApplyPreset(string name);
}

/// <summary>
/// Pure routing from a parsed <see cref="HaCommand"/> to an <see cref="IChargeControlActions"/> call.
/// The single non-trivial bit — combining a single-bound charge_start/charge_stop number-set with its
/// companion value while keeping the app's minimum gap — lives here so it's unit-testable.
/// </summary>
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

/// <summary>
/// The live <see cref="IChargeControlActions"/> — routes each command to ChargeKeeper's existing
/// services. Every method runs SYNCHRONOUSLY (the vendor RPC blocks for seconds): the caller
/// (<see cref="HomeAssistantService"/>) drives dispatch on a single-worker background queue, OFF the
/// MQTT receive callback, so a read-modify-write pair completes before the next command starts and
/// the callback thread is never blocked — the offloading + serialization now live at that one seam
/// rather than being sprinkled per-method here. Mirrors the tray menu's own charge-control call sites
/// so behaviour can't drift: enabling Smart Charge mid-override cancels the override
/// (<c>TrayMenu.Toggle</c>), applying explicit thresholds funnels through
/// <see cref="TravelOverrideService.ApplyExplicitThresholds"/>, and a preset apply repeats
/// <c>TrayMenu.ApplyPreset</c> (write + persist ActivePreset).
/// </summary>
internal sealed class ChargeControlActions : IChargeControlActions
{
    public (int Start, int Stop) CurrentThresholds()
    {
        var s = ChargeThresholdService.Read();
        // Use the live thresholds only when they're a valid Smart Charge pair; otherwise the default.
        if (s is { Start: >= PresetEditValidator.MinThreshold, Stop: <= PresetEditValidator.MaxThreshold } &&
            s.Stop - s.Start >= PresetEditValidator.MinGap)
            return (s.Start, s.Stop);
        return DefaultThresholds();
    }

    // Sensible default when Smart Charge is off/unset (firmware may read back 0/0), so the first
    // single-bound number-set still forms a valid pair. Derived from the built-in "Daily" preset
    // rather than a duplicated literal, so it can't drift from SettingsService's default; a hard
    // fallback covers a user who deleted the "Daily" preset entirely.
    private static (int Start, int Stop) DefaultThresholds()
    {
        var daily = SettingsService.Current.Presets.FirstOrDefault(p => p.Name == "Daily");
        return daily is { Start: >= PresetEditValidator.MinThreshold, Stop: <= PresetEditValidator.MaxThreshold }
               && daily.Stop - daily.Start >= PresetEditValidator.MinGap
            ? (daily.Start, daily.Stop)
            : (60, 80);
    }

    public void ApplyThresholds(int start, int stop)
    {
        try { TravelOverrideService.ApplyExplicitThresholds(start, stop); } catch { }
    }

    public void SetSmartChargeEnabled(bool enable)
    {
        try
        {
            // Re-enabling Smart Charge while "charge to 100 % once" runs means "threshold back
            // on" — the override's cancel path (restores saved thresholds + disarms the revert).
            // A bare SetEnabled(true) would instead apply firmware 0/0 as defaults. Mirrors
            // TrayMenu.Toggle.
            if (enable && TravelOverrideService.IsActive) TravelOverrideService.Cancel();
            else ChargeThresholdService.SetEnabled(enable);
        }
        catch { }
    }

    public void ChargeToFullOnce()
    {
        // Activate() manages its own background work + revert timer.
        try { TravelOverrideService.Activate(); } catch { }
    }

    public void ApplyPreset(string name)
    {
        try
        {
            var preset = SettingsService.Current.Presets.FirstOrDefault(p => p.Name == name);
            if (preset is null) return;   // enumerated select, but ignore an unknown name defensively
            // Same order as TrayMenu.ApplyPreset: explicit-threshold write, then persist ActivePreset.
            if (TravelOverrideService.ApplyExplicitThresholds(preset.Start, preset.Stop))
                SettingsService.Update(s => s.ActivePreset = preset.Name);
        }
        catch { }
    }
}
