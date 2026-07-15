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
/// The live <see cref="IChargeControlActions"/> — routes each command onto the shared
/// <see cref="ChargeControlService"/>, the SAME composition the tray menu drives (issue #40 item 4),
/// so the "cancel override vs SetEnabled" / "ApplyExplicitThresholds + persist ActivePreset"
/// orchestration lives in exactly one place and can't drift between the MQTT and tray paths — and an
/// MQTT-driven change fires <see cref="ChargeControlService.StateChanged"/>, which reconciles the
/// tray/tooltip/dashboard just like a tray-driven change (previously the MQTT path skipped that
/// reconcile and the tray went stale). Every method runs SYNCHRONOUSLY (the vendor RPC blocks for
/// seconds): the caller (<see cref="HomeAssistantService"/>) drives dispatch on a single-worker
/// background queue, OFF the MQTT receive callback, so a read-modify-write pair completes before the
/// next command starts and the callback thread is never blocked. <see cref="CurrentThresholds"/> is
/// the one bit of MQTT-only logic that stays here — the companion value the dispatcher combines a
/// single-bound number-set against, sourced from the app's cached threshold state (not a pre-write EC
/// read) so one charge_start/charge_stop command costs one EC write + the after-write fresh read only.
/// </summary>
internal sealed class ChargeControlActions : IChargeControlActions
{
    // The app's already-maintained cached thresholds (from HomeAssistantService's CurrentStateProvider
    // snapshot). Preferred over a fresh device read so a single-bound charge_start/charge_stop set
    // doesn't cost an extra blocking EC RPC just to fetch its companion value — the guaranteed-fresh
    // read still happens AFTER the write, via HomeAssistantService.PublishFreshStateAfterCommand. Null
    // only in tests / when no provider is wired, where we fall back to the live read.
    private readonly Func<(int Start, int Stop)?>? _cachedThresholds;

    public ChargeControlActions(Func<(int Start, int Stop)?>? cachedThresholds = null)
        => _cachedThresholds = cachedThresholds;

    public (int Start, int Stop) CurrentThresholds()
    {
        // Prefer the app's cached threshold state — no pre-write EC read on the common path.
        if (_cachedThresholds is { } provider)
            return provider.Invoke() is { } cached && IsValidPair(cached.Start, cached.Stop)
                ? cached
                : DefaultThresholds();

        // No cache wired (shouldn't happen in production): fall back to a live read.
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
        // Shared composition (fires ChargeControlService.StateChanged → tray/tooltip/dashboard/MQTT reflect).
        try { ChargeControlService.SetExplicitThresholds(start, stop); } catch { }
    }

    public void SetSmartChargeEnabled(bool enable)
    {
        // Shared composition owns the "re-enable mid-override → cancel override" rule (mirrors TrayMenu.Toggle).
        try { ChargeControlService.SetSmartChargeEnabled(enable); } catch { }
    }

    public void ChargeToFullOnce()
    {
        // Activate() manages its own background work + revert timer, and raises
        // TravelOverrideService.StateChanged when it settles — HomeAssistantService reflects that.
        try { TravelOverrideService.Activate(); } catch { }
    }

    public void ApplyPreset(string name)
    {
        // Shared composition: explicit-threshold write + persist ActivePreset, ignoring an unknown name.
        try { ChargeControlService.ApplyPresetByName(name); } catch { }
    }
}
