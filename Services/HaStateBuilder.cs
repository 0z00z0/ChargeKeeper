using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>Pure mapping from the live battery values to an <see cref="HaState"/> for MQTT publishing.
/// The derivations follow the Home Assistant mobile app, so the entities read the same alongside it.
/// Takes the preset list rather than a preset name: the published <c>active_preset</c> is derived from
/// the thresholds passed alongside it, so no caller can publish a name the device has moved off.</summary>
internal static class HaStateBuilder
{
    public static HaState Build(
        int soc, int chargeRateMw, bool onAc, BatteryStatus status,
        ChargeThresholdState? threshold, int? adapterWatts,
        int? remainingMwh, int? fullMwh, int? designMwh,
        bool lowPowerMode, IReadOnlyList<ThresholdPreset>? presets)
    {
        bool isCharging = status == BatteryStatus.Charging;
        // "Full" needs external power: a pack at 100 % that has just been unplugged is Not Charging.
        string batteryState =
            isCharging          ? HaDiscovery.StateCharging :
            soc >= 100 && onAc  ? HaDiscovery.StateFull :
                                  HaDiscovery.StateNotCharging;

        var (scEnabled, start, stop) = ChargeControlFields(threshold);

        // A wattage reading belongs to the current AC session only; never publish a stale one on battery.
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
            ActivePreset: ActivePresetPolicy.Match(presets, threshold)?.Name);
    }

    /// <summary>The Smart Charge flag and the reflected Charge start/stop numbers. Not limiting → stop
    /// reads 100, charging allowed to full. Start is omitted unless the device reports one — HP and
    /// Surface cap without a start threshold, and the HA number entity declares a minimum of
    /// <see cref="PresetEditValidator.MinThreshold"/>, so 0 is not publishable.</summary>
    internal static (bool Enabled, int? Start, int? Stop) ChargeControlFields(ChargeThresholdState? threshold)
    {
        bool scEnabled = threshold is { IsLimiting: true };
        int? start = threshold is { HasStartThreshold: true } ? threshold.Start : null;
        int? stop  = scEnabled ? threshold!.Stop : 100;
        return (scEnabled, start, stop);
    }

    /// <summary>Returns <paramref name="baseState"/> with only its charge-control fields replaced from a
    /// fresh device read, for the republish right after an inbound command writes new thresholds. The
    /// active preset re-derives from that fresh read, never from <paramref name="baseState"/>.</summary>
    internal static HaState ApplyChargeControl(
        HaState baseState, ChargeThresholdState? threshold, IReadOnlyList<ThresholdPreset>? presets)
    {
        var (scEnabled, start, stop) = ChargeControlFields(threshold);
        return baseState with
        {
            SmartChargeEnabled = scEnabled,
            ChargeStart = start,
            ChargeStop = stop,
            ActivePreset = ActivePresetPolicy.Match(presets, threshold)?.Name,
        };
    }

    /// <summary>Battery health from capacity wear (full-charge ÷ design capacity). Null when either
    /// figure is missing, so the HA entity reads "unknown" rather than a fabricated value.</summary>
    internal static string? DeriveHealth(int? fullMwh, int? designMwh)
    {
        if (fullMwh is not > 0 || designMwh is not > 0) return null;
        double ratio = (double)fullMwh.Value / designMwh.Value;
        return ratio >= 0.80 ? "Good"
             : ratio >= 0.60 ? "Degraded"
             :                  "Poor";
    }

    /// <summary>Minutes until full while charging at a meaningful rate; null otherwise. Shares
    /// <see cref="Helpers.BatteryStatsFormatter.HoursToFull"/> with the dashboard's REMAINING stat so
    /// the two can't drift on the rate guard.</summary>
    internal static int? RemainingMinutesToFull(bool isCharging, int chargeRateMw, int? remainingMwh, int? fullMwh)
    {
        if (!isCharging) return null;
        if (Helpers.BatteryStatsFormatter.HoursToFull(chargeRateMw, remainingMwh, fullMwh) is not { } hours)
            return null;
        return (int)Math.Round(hours * 60);
    }
}
