namespace ChargeKeeper.Services;

/// <summary>
/// The single composition point for a charge-control change, WHATEVER the trigger — the tray menu
/// (<c>TrayMenu.Toggle</c> / <c>TrayMenu.ApplyPreset</c>) OR an inbound MQTT command
/// (<see cref="HaCommandDispatcher"/> via <see cref="ChargeControlActions"/>). Before this each
/// caller hand-copied the same "cancel override vs SetEnabled" and "ApplyExplicitThresholds +
/// persist ActivePreset" sequences, and they had drifted — the MQTT path skipped the tray reconcile,
/// so the tray/tooltip/dashboard went stale after an MQTT command (issue #40, items 3 &amp; 4). The
/// orchestration now lives here in ONE place and fires <see cref="StateChanged"/> after every
/// operation, so every view reconciles no matter which trigger drove the change.
/// <para>
/// The three composed operations mirror the tray's own call sites exactly (see <c>TrayMenu</c>):
/// <list type="bullet">
/// <item><see cref="SetSmartChargeEnabled"/> — re-enabling mid-override cancels the override.</item>
/// <item><see cref="SetExplicitThresholds"/> — write Start/Stop (this is also how Smart Charge is
///   (re)enabled), used by the MQTT charge_start/charge_stop numbers.</item>
/// <item><see cref="ApplyPresetByName"/> — explicit-threshold write + persist ActivePreset.</item>
/// </list>
/// Every operation runs SYNCHRONOUSLY on the caller's thread (each call site already wraps it in a
/// background <c>Task.Run</c>, since the vendor RPC blocks for seconds).
/// </para>
/// </summary>
internal static class ChargeControlService
{
    /// <summary>
    /// Fired after any composed charge-control operation settles (device write done + settings
    /// persisted). Subscribers — <c>TrayMenu.QueueRefresh</c>, the app tray tooltip/dashboard, and
    /// <see cref="HomeAssistantService"/>'s MQTT reflect — resync so an MQTT-driven change reconciles
    /// the SAME views a tray-driven change does. Fires on the caller's thread (a background
    /// <c>Task.Run</c> at every call site); handlers marshal their own UI work.
    /// <para>
    /// Note: the "charge to 100 % once" travel override runs its own async restore and raises
    /// <see cref="TravelOverrideService.StateChanged"/> when THAT settles — subscribers that must
    /// also reflect an override revert (the tray already does, and <see cref="HomeAssistantService"/>
    /// does) subscribe to both events.
    /// </para>
    /// </summary>
    public static event Action? StateChanged;

    /// <summary>
    /// The static-service primitives the composition is built from. A settable seam (rather than
    /// direct static calls) so the composition's branches are unit-tested with a fake instead of
    /// against the live vendor RPC + settings file. Production uses
    /// <see cref="LiveChargeControlPrimitives"/>; tests swap it and restore it.
    /// </summary>
    internal static IChargeControlPrimitives Primitives { get; set; } = new LiveChargeControlPrimitives();

    /// <summary>
    /// Set Smart Charge on/off. Mirrors <c>TrayMenu.Toggle</c>'s rule: re-enabling while a
    /// "charge to 100 % once" travel override is running is "threshold back on" — which is the
    /// override's cancel path (restore the saved pre-override thresholds AND disarm the auto-revert),
    /// NOT a bare <c>SetEnabled(true)</c> (that would apply firmware's 0/0 defaults and leave the
    /// revert armed to clobber them at the next full charge). Disabling, or enabling with no override
    /// active, is a plain SetEnabled.
    /// </summary>
    public static void SetSmartChargeEnabled(bool enable)
    {
        if (enable && Primitives.IsOverrideActive) Primitives.CancelOverride();
        else Primitives.SetEnabled(enable);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Write explicit Start/Stop thresholds. The single composition point for every threshold write
    /// that is NOT a named preset — the MQTT charge_start/charge_stop numbers, the dashboard slider
    /// drag, and the Settings preset-edit / delete-fallback push. Funnels through
    /// <see cref="TravelOverrideService.ApplyExplicitThresholds"/> — writing valid non-zero thresholds
    /// is itself how Smart Charge is (re)enabled, so adjusting a threshold ACTIVATES Smart Charge, as
    /// intended (issue #40 item 3). Always fires <see cref="StateChanged"/> so the tray/tooltip/MQTT
    /// reconcile immediately instead of waiting for the next battery tick.
    /// <para>
    /// <paramref name="clearActivePreset"/> reproduces the dashboard slider's semantics: a manual
    /// threshold edit makes the value "custom", so the persisted <c>ActivePreset</c> is cleared (on a
    /// SUCCESSFUL write only — a failed device write must not leave the UI claiming no preset when the
    /// device never moved). The Settings preset-edit / delete-fallback callers leave it
    /// <c>false</c>: they manage <c>ActivePreset</c> themselves (the edited/fallback preset stays
    /// active), so the write must not touch it. Returns the vendor write's success flag.
    /// </para>
    /// </summary>
    public static bool SetExplicitThresholds(int start, int stop, bool clearActivePreset = false)
    {
        bool ok = Primitives.ApplyExplicitThresholds(start, stop);
        if (ok && clearActivePreset) Primitives.SetActivePreset(null);
        StateChanged?.Invoke();
        return ok;
    }

    /// <summary>
    /// Apply the named preset. Mirrors <c>TrayMenu.ApplyPreset</c>: explicit-threshold write, then
    /// persist ActivePreset ONLY when the write succeeded (a failed device write mustn't leave the
    /// UI claiming a preset that never took). Returns false (no state change, no event) when the
    /// name is blank or matches no configured preset.
    /// </summary>
    public static bool ApplyPresetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var preset = Primitives.FindPreset(name);
        if (preset is null) return false;

        bool ok = Primitives.ApplyExplicitThresholds(preset.Start, preset.Stop);
        if (ok) Primitives.SetActivePreset(preset.Name);
        StateChanged?.Invoke();
        return ok;
    }
}

/// <summary>
/// The static-service primitives <see cref="ChargeControlService"/> composes, behind an interface so
/// the composition's branches (override-cancel vs SetEnabled; apply-preset found/not-found/failed)
/// are unit-testable with a fake instead of against the live vendor RPC + settings file.
/// </summary>
internal interface IChargeControlPrimitives
{
    /// <summary>Whether a "charge to 100 % once" travel override is currently active.</summary>
    bool IsOverrideActive { get; }

    /// <summary>Cancel the active override (restore saved thresholds + disarm the auto-revert).</summary>
    void CancelOverride();

    /// <summary>Turn Smart Charge on/off on the device.</summary>
    void SetEnabled(bool enable);

    /// <summary>Write explicit thresholds (supersedes any override); returns the write's success flag.</summary>
    bool ApplyExplicitThresholds(int start, int stop);

    /// <summary>Resolve a configured preset by name, or null if none matches.</summary>
    ThresholdPreset? FindPreset(string name);

    /// <summary>Persist the active preset name, or clear it (null) for a custom-threshold write.</summary>
    void SetActivePreset(string? name);
}

/// <summary>Production backend for <see cref="ChargeControlService"/> — the real static services.</summary>
internal sealed class LiveChargeControlPrimitives : IChargeControlPrimitives
{
    public bool IsOverrideActive => TravelOverrideService.IsActive;
    public void CancelOverride() => TravelOverrideService.Cancel();
    public void SetEnabled(bool enable) => ChargeThresholdService.SetEnabled(enable);
    public bool ApplyExplicitThresholds(int start, int stop) => TravelOverrideService.ApplyExplicitThresholds(start, stop);
    public ThresholdPreset? FindPreset(string name) => SettingsService.Current.Presets.FirstOrDefault(p => p.Name == name);
    public void SetActivePreset(string? name) => SettingsService.Update(s => s.ActivePreset = name);
}
