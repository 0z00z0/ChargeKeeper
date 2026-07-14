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
        string batteryState =
            isCharging ? HaDiscovery.StateCharging :
            soc >= 100 ? HaDiscovery.StateFull :
                         HaDiscovery.StateNotCharging;

        bool scEnabled = threshold is { Capable: true, Enabled: true, Start: > 0, Stop: > 0 };
        // Off (or the "charge to 100% once" travel override) → stop 100, start omitted. This single
        // "off" branch also covers the override because activating it calls
        // ChargeThresholdService.SetEnabled(false), so the live threshold read already comes back
        // Enabled: false while the override is active — no separate travel-override parameter needed.
        int? start = scEnabled ? threshold!.Start : null;
        int? stop  = scEnabled ? threshold!.Stop  : 100;

        // A wattage reading only belongs to the current AC session; never publish a stale one while
        // on battery (the caller already nulls it, but gate here too so the mapping is self-contained).
        int? watts = onAc ? adapterWatts : null;

        return new HaState(
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
    /// HA entity is unavailable while discharging/idle (issue #29). Mirrors
    /// <see cref="Helpers.BatteryStatsFormatter.FormatTimeRemaining"/>'s charging branch.
    /// </summary>
    internal static int? RemainingMinutesToFull(bool isCharging, int chargeRateMw, int? remainingMwh, int? fullMwh)
    {
        if (!isCharging || chargeRateMw < 100) return null;
        if (remainingMwh is not { } remaining || fullMwh is not > 0) return null;
        double hours = (fullMwh.Value - remaining) / (double)chargeRateMw;
        if (hours <= 0 || double.IsInfinity(hours) || double.IsNaN(hours)) return null;
        return (int)Math.Round(hours * 60);
    }
}
