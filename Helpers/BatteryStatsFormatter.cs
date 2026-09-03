namespace ChargeKeeper.Helpers;

/// <summary>
/// Formats the POWER/REMAINING stat text, shared by the dashboard popup and the pop-out graph so the
/// two cannot drift.
/// <para>Takes plain primitives rather than a <c>Windows.Devices.Power.BatteryReport</c>: that WinRT
/// type has no public constructor, so anything built around it cannot be unit-tested without a live
/// battery.</para>
/// </summary>
internal static class BatteryStatsFormatter
{
    /// <summary>"On AC" as the app defines it everywhere: Charging, or Idle — Idle means
    /// full/threshold-held, which only happens while externally powered.</summary>
    public static bool IsOnAC(Windows.System.Power.BatteryStatus status) =>
        status is Windows.System.Power.BatteryStatus.Charging or Windows.System.Power.BatteryStatus.Idle;

    /// <summary>POWER line: source label, optional adapter wattage, and the live rate when it is
    /// non-zero in the expected direction. <paramref name="adapterWattage"/> is passed in rather than
    /// read here, so callers own the cadence of that RPC-backed query.</summary>
    public static string FormatPowerSource(bool onAC, int chargeRateMw, int? adapterWattage)
    {
        string label = onAC
            ? (adapterWattage is { } watts ? $"AC Power ({watts}W charger)" : "AC Power")
            : "Battery";
        string? rate = (onAC && chargeRateMw > 0) || (!onAC && chargeRateMw < 0)
            ? PowerFormat.SignedRate(chargeRateMw)
            : null;
        return rate is null ? label : $"{label}  ·  {rate}";
    }

    /// <summary>REMAINING line, e.g. "~2h 14m to full" or "~3h remaining". The label stays static in
    /// both windows, so the value must carry the direction or a charging reading would read as
    /// "battery time left".</summary>
    public static string FormatTimeRemaining(int? chargeRateMw, int? remainingMwh, int? fullChargeMwh)
    {
        if (chargeRateMw is not { } rate || PowerFlows.From(rate) is null or PowerFlow.Rest) return "—";
        if (remainingMwh is not { } remaining) return "—";

        if (rate > 0)
            return HoursToFull(rate, remainingMwh, fullChargeMwh) is { } h
                ? FormatHours(h, chargingDirection: true) : "—";
        if (rate < 0)
            return FormatHours(remaining / (double)Math.Abs(rate), chargingDirection: false);
        return "—";
    }

    /// <summary>Hours until full while charging at a meaningful rate; null otherwise. The rate guard
    /// is <see cref="PowerFlows.RestBandMw"/>, shared with the tray's flow mark and the Home Assistant
    /// <c>remaining_charge_time</c> sensor so no two surfaces can drift on what counts as flow.</summary>
    internal static double? HoursToFull(int chargeRateMw, int? remainingMwh, int? fullChargeMwh)
    {
        if (chargeRateMw < PowerFlows.RestBandMw) return null;
        if (remainingMwh is not { } remaining || fullChargeMwh is not > 0) return null;
        double h = (fullChargeMwh.Value - remaining) / (double)chargeRateMw;
        if (h <= 0 || double.IsInfinity(h) || double.IsNaN(h)) return null;
        return h;
    }

    /// <summary>RATE line: charge/discharge as %/hour, signed so positive reads as charging — the
    /// live counterpart to the overnight-drain anomaly's own %/hour extrapolation
    /// (<see cref="ChargeKeeper.Services.DrainAnomalyPolicy.PercentPerHour"/>). "—" before enough
    /// history has accumulated to trust a rate, the same placeholder <see cref="FormatTimeRemaining"/>
    /// falls back to when there is nothing to show.</summary>
    public static string FormatChargeRate(double? percentPerHour) =>
        PowerFormat.SignedPercentPerHour(percentPerHour) ?? "—";

    // Internal, not private, as a test seam for the hour/minute formatting and its boundaries.
    internal static string FormatHours(double h, bool chargingDirection)
    {
        if (h <= 0 || double.IsInfinity(h) || double.IsNaN(h)) return "—";
        if (h > 99) return ">99h";
        var ts = TimeSpan.FromHours(h);
        string duration = ts.TotalHours >= 1
            ? $"~{(int)ts.TotalHours}h {ts.Minutes}m"
            : $"~{ts.Minutes}m";
        return chargingDirection ? $"{duration} to full" : $"{duration} remaining";
    }
}
