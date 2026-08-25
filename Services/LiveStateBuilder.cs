using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>A snapshot of the live battery and charge-control values the published entities read. A
/// nullable field is unknown, and its entity publishes the reset literal rather than a fabricated
/// value.</summary>
internal readonly record struct LiveState(
    int Soc,
    string BatteryState,
    bool LowPowerMode,
    int PowerMw,
    bool IsCharging,
    bool OnAc,
    string? Health,
    int? RemainingMinutes,
    bool SmartChargeEnabled,
    int? ChargeStart,
    int? ChargeStop,
    int? AdapterWatts,
    string? ActivePreset,
    int? FullMwh = null,
    int? DesignMwh = null);

/// <summary>Pure mapping from the live battery values to a <see cref="LiveState"/>. The derivations
/// follow the Home Assistant mobile app, so the entities read the same alongside it. Takes the preset
/// list rather than a preset name: the published active preset is derived from the thresholds passed
/// alongside it, so no caller can publish a name the device has moved off.</summary>
internal static class LiveStateBuilder
{
    /// <summary>Charging, as the receiver's own battery-state vocabulary spells it.</summary>
    public const string StateCharging = "Charging";

    public const string StateNotCharging = "Not Charging";

    public const string StateFull = "Full";

    public static LiveState Build(
        int soc, int chargeRateMw, bool onAc, BatteryStatus status,
        ChargeThresholdState? threshold, int? adapterWatts,
        int? remainingMwh, int? fullMwh, int? designMwh,
        bool lowPowerMode, IReadOnlyList<ThresholdPreset>? presets)
    {
        bool isCharging = status == BatteryStatus.Charging;
        // "Full" needs external power: a pack at 100 % that has just been unplugged is Not Charging.
        string batteryState =
            isCharging          ? StateCharging :
            soc >= 100 && onAc  ? StateFull :
                                  StateNotCharging;

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
            ActivePreset: ActivePresetPolicy.Match(presets, threshold)?.Name,
            FullMwh: fullMwh,
            DesignMwh: designMwh);
    }

    /// <summary>The Smart Charge flag and the reflected Charge start/stop numbers. Not limiting → stop
    /// reads 100, charging allowed to full. Start is omitted unless the device reports one — HP and
    /// Surface cap without a start threshold, and the number entity declares a minimum of
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
    internal static LiveState ApplyChargeControl(
        LiveState baseState, ChargeThresholdState? threshold, IReadOnlyList<ThresholdPreset>? presets)
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
    /// figure is missing, so the entity reads "unknown" rather than a fabricated value.</summary>
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

    /// <summary>Milliwatts as watts to one decimal, the unit the power sensor declares. Null stays
    /// null, so an absent reading reaches the entity as one.</summary>
    internal static double? Watts(int? milliwatts) =>
        milliwatts is { } mw ? OneDecimal(mw / 1000.0) : null;

    /// <summary>Milliwatt-hours as watt-hours to one decimal, for the two capacity sensors. A
    /// non-positive figure is no reading: firmware reports 0 when it has none.</summary>
    internal static double? WattHours(int? milliwattHours) =>
        milliwattHours is > 0 and { } mwh ? OneDecimal(mwh / 1000.0) : null;

    // Away from zero, not to even. These three readings were rounded by the receiver's own template
    // filter before the app rounded them itself, and that filter rounds a half up — so the default
    // banker's rounding would move a published value by a tenth on the upgrade.
    private static double OneDecimal(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
