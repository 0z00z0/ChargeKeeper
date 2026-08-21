namespace ChargeKeeper.Services;

/// <summary>The single composition point for a charge-control change, whatever the trigger, so every
/// view reconciles through <see cref="StateChanged"/> no matter which drove it. Runs synchronously on
/// the caller's thread — the vendor RPC blocks, so call sites wrap it in a <c>Task.Run</c>.</summary>
internal static class ChargeControlService
{
    /// <summary>Fired on the caller's background thread once an operation settles, so handlers marshal
    /// their own UI work. An override revert settles elsewhere — subscribers that must reflect one also
    /// take <see cref="TravelOverrideService.StateChanged"/>.</summary>
    public static event Action? StateChanged;

    /// <summary>Settable seam so the branches are unit-tested against a fake, not the vendor RPC.</summary>
    internal static IChargeControlPrimitives Primitives { get; set; } = new LiveChargeControlPrimitives();

    /// <summary>Re-enabling during a travel override goes through the override's cancel path, not a bare
    /// <c>SetEnabled(true)</c> — that would apply firmware's 0/0 defaults and leave the auto-revert armed
    /// to clobber them. The cancel restores nothing when the override saved nothing, hence the fall-through.</summary>
    public static void SetSmartChargeEnabled(bool enable)
    {
        if (enable && Primitives.IsOverrideActive)
        {
            bool restoresThresholds = Primitives.HasSavedRevertThresholds;
            Primitives.CancelOverride();
            if (!restoresThresholds) Primitives.SetEnabled(true);
        }
        else Primitives.SetEnabled(enable);
        StateChanged?.Invoke();
    }

    /// <summary>Every threshold write that is not a named preset. Writing valid non-zero thresholds is
    /// itself how Smart Charge is (re)enabled, so this activates it as a side effect, by design.</summary>
    /// <param name="clearActivePreset">True for a manual edit, which makes the value "custom" — cleared
    /// on a successful write only, or the UI would claim no preset while the device never moved.</param>
    public static bool SetExplicitThresholds(int start, int stop, bool clearActivePreset = false)
    {
        bool ok = Primitives.ApplyExplicitThresholds(start, stop);
        if (ok && clearActivePreset) Primitives.SetActivePreset(null);
        StateChanged?.Invoke();
        return ok;
    }

    /// <summary>Persists ActivePreset only when the write succeeded. Returns false — no state change,
    /// no event — when the name is blank, matches no preset, or the preset is out of policy.</summary>
    public static bool ApplyPresetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var preset = Primitives.FindPreset(name);
        if (preset is null) return false;

        // Presets also arrive from settings.json, the MQTT select and a network rule, none of which
        // pass the Settings editor. A hand-edited file yields 0/0, which reaches the device as a
        // failing write after the travel override's saved thresholds have already been cleared.
        if (PresetEditValidator.Validate(preset.Name, preset.Start, preset.Stop, [], preset.Name) is { } reason)
        {
            AppLog.Info($"ChargeControl: preset \"{preset.Name}\" not applied — {reason}");
            return false;
        }

        bool ok = Primitives.ApplyExplicitThresholds(preset.Start, preset.Stop);
        if (ok) Primitives.SetActivePreset(preset.Name);
        StateChanged?.Invoke();
        return ok;
    }
}

/// <summary>The static-service primitives <see cref="ChargeControlService"/> composes, behind an
/// interface so its branches are unit-testable with a fake.</summary>
internal interface IChargeControlPrimitives
{
    bool IsOverrideActive { get; }

    /// <summary>False when Smart Charge was already off at activation, so cancelling writes nothing.</summary>
    bool HasSavedRevertThresholds { get; }

    void CancelOverride();
    void SetEnabled(bool enable);

    /// <summary>Supersedes any override; returns the write's success flag.</summary>
    bool ApplyExplicitThresholds(int start, int stop);

    ThresholdPreset? FindPreset(string name);

    /// <summary>Null clears it, for a custom-threshold write.</summary>
    void SetActivePreset(string? name);
}

internal sealed class LiveChargeControlPrimitives : IChargeControlPrimitives
{
    public bool IsOverrideActive => TravelOverrideService.IsActive;
    public bool HasSavedRevertThresholds => TravelOverrideService.HasSavedRevertThresholds;
    public void CancelOverride() => TravelOverrideService.Cancel();
    public void SetEnabled(bool enable) => ChargeThresholdService.SetEnabled(enable);
    public bool ApplyExplicitThresholds(int start, int stop) => TravelOverrideService.ApplyExplicitThresholds(start, stop);
    public ThresholdPreset? FindPreset(string name) => SettingsService.Current.Presets.FirstOrDefault(p => p.Name == name);
    public void SetActivePreset(string? name) => SettingsService.Update(s => s.ActivePreset = name);
}
