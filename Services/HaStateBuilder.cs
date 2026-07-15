using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>
/// Pure mapping from ChargeKeeper's live battery values to an <see cref="HaState"/> for MQTT
/// publishing (TODO #28/#29). Extracted from <c>App.OnBatteryReportUpdated</c> so the "which fields
/// are known" gating and the HA mobile-app-aligned derivations (battery state, health, remaining
/// charge time) are unit-testable without a live battery or a broker.
/// <list type="bullet">
/// <item><b>Battery state</b> mirrors the HA app: Charging while the firmware reports Charging,
/// Full at 100 % (or Idle at 100 %), otherwise Not Charging — a threshold-held Idle below 100 %
/// reads Not Charging, NOT Full, since the pack isn't actually full.</item>
/// <item><b>Health</b> is derived from capacity wear (full ÷ design): ≥80 % Good, ≥60 % Degraded,
/// else Poor; unknown when either capacity is missing.</item>
/// <item><b>Remaining charge time</b> is minutes to full while charging at a meaningful rate; null
/// (→ HA "unknown") while discharging or idle.</item>
/// <item><b>Smart Charge</b> is "on" only when the controller is capable AND enabled AND both
/// thresholds are valid; when off, Charge stop is reported as 100 (charging allowed to full) while
/// Charge start is omitted. Adapter wattage is known only while on AC.</item>
/// </list>
/// </summary>
internal static class HaStateBuilder
{
    public static HaState Build(
        int soc, int chargeRateMw, bool onAc, BatteryStatus status,
        ChargeThresholdState? threshold, int? adapterWatts,
        int? remainingMwh, int? fullMwh, int? designMwh,
        bool lowPowerMode, string? activePreset)
    {
        bool isCharging = status == BatteryStatus.Charging;
        // "Full" only when actually externally powered — a pack sitting at 100 % while DISCHARGING
        // (just unplugged) is Not Charging, not Full (issue #29/#30 review).
        string batteryState =
            isCharging          ? HaDiscovery.StateCharging :
            soc >= 100 && onAc  ? HaDiscovery.StateFull :
                                  HaDiscovery.StateNotCharging;

        var (scEnabled, start, stop) = ChargeControlFields(threshold);

        // A wattage reading only belongs to the current AC session; never publish a stale one while
        // on battery (the caller already nulls it, but gate here too so the mapping is self-contained).
        int? watts = onAc ? adapterWatts : null;

        return new(
            Soc: soc,
            BatteryState: batteryState,
            LowPowerMode: lowPowerMode,
            PowerMw: chargeRateMw,
            IsCharging: isCharging,
            OnAc: onAc,
            Health: DeriveHealth(fullMwh, designMwh),
            RemainingMinutes: RemainingMinutesToFull(isCharging, chargeRateMw, remainingMwh, fullMwh),
            SmartChargeEnabled: scEnabled,
            ChargeStart: start,
            ChargeStop: stop,
            AdapterWatts: watts,
            ActivePreset: activePreset);
    }

    /// <summary>
    /// The Smart Charge on/off flag and the reflected Charge start/stop numbers derived from a live
    /// threshold read — the single source shared by <see cref="Build"/> (battery-tick path) and
    /// <see cref="ApplyChargeControl"/> (the post-command fresh-state republish, issue #30 review),
    /// so the "off → stop 100, start omitted" semantics can't drift between the two.
    /// <para>
    /// "Off" (or the "charge to 100 % once" travel override) → stop 100, start omitted. This single
    /// "off" branch also covers the override because activating it calls
    /// <c>ChargeThresholdService.SetEnabled(false)</c>, so the live threshold read already comes back
    /// <c>Enabled: false</c> while the override is active — no separate travel-override parameter needed.
    /// </para>
    /// </summary>
    internal static (bool Enabled, int? Start, int? Stop) ChargeControlFields(ChargeThresholdState? threshold)
    {
        bool scEnabled = threshold is { Capable: true, Enabled: true, Start: > 0, Stop: > 0 };
        int? start = scEnabled ? threshold!.Start : null;
        int? stop  = scEnabled ? threshold!.Stop  : 100;
        return (scEnabled, start, stop);
    }

    /// <summary>
    /// Returns <paramref name="baseState"/> with only its charge-control fields (Smart Charge flag,
    /// Charge start/stop, active preset) replaced from a FRESH device read + preset name — used to
    /// republish immediately after an inbound MQTT command writes new thresholds, without waiting for
    /// the next battery tick and without touching App's stale threshold cache (issue #30 review).
    /// </summary>
    internal static HaState ApplyChargeControl(HaState baseState, ChargeThresholdState? threshold, string? activePreset)
    {
        var (scEnabled, start, stop) = ChargeControlFields(threshold);
        return baseState with
        {
            SmartChargeEnabled = scEnabled,
            ChargeStart = start,
            ChargeStop = stop,
            ActivePreset = activePreset,
        };
    }

    /// <summary>
    /// Battery health from capacity wear (current full-charge capacity ÷ design capacity). Null when
    /// either figure is missing/non-positive so the HA entity reads "unknown" rather than a fake value.
    /// </summary>
    internal static string? DeriveHealth(int? fullMwh, int? designMwh)
    {
        if (fullMwh is not > 0 || designMwh is not > 0) return null;
        double ratio = (double)fullMwh.Value / designMwh.Value;
        return ratio >= 0.80 ? "Good"
             : ratio >= 0.60 ? "Degraded"
             :                  "Poor";
    }

    /// <summary>
    /// Minutes until full while charging at a meaningful (&gt;=100 mW) rate; null otherwise so the
    /// HA entity is unavailable while discharging/idle (issue #29). Reuses
    /// <see cref="Helpers.BatteryStatsFormatter.HoursToFull"/> — the SAME numeric time-to-full source
    /// the dashboard/pop-out REMAINING stat uses — so the two can't drift on the rate guard.
    /// </summary>
    internal static int? RemainingMinutesToFull(bool isCharging, int chargeRateMw, int? remainingMwh, int? fullMwh)
    {
        if (!isCharging) return null;
        if (Helpers.BatteryStatsFormatter.HoursToFull(chargeRateMw, remainingMwh, fullMwh) is not { } hours)
            return null;
        return (int)Math.Round(hours * 60);
    }
}
