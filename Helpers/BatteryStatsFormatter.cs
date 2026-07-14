namespace ChargeKeeper.Helpers;

/// <summary>
/// Formats the POWER/REMAINING stat text — shared between <see cref="ChargeKeeper.UI.DashboardWindow"/>
/// (small popup) and <see cref="ChargeKeeper.UI.BatteryHistoryWindow"/> (pop-out), so the same
/// "AC Power (60W charger) · +45 W" / "~2h 14m to full" text logic isn't duplicated between the two
/// windows that both show it (the pop-out grew its own copy of these stats so charger wattage — too
/// wide for the small dashboard — has somewhere with room to sit).
/// <para>
/// Takes plain primitives rather than a <c>Windows.Devices.Power.BatteryReport</c> directly: that
/// WinRT type has no public constructor (only the OS can produce one via <c>Battery.GetReport()</c>),
/// so a method built around it can't be unit tested without a live battery. Callers extract the few
/// fields this needs from their own report before calling.
/// </para>
/// </summary>
internal static class BatteryStatsFormatter
{
    /// <summary>
    /// "On AC" as the app defines it everywhere (tray icon, tooltip, both windows' POWER stat):
    /// Charging, or Idle — Idle means full/threshold-held, which only happens while externally
    /// powered. Shared so the call sites can't drift (they used to carry comments promising to stay
    /// "identical" to each other); takes the enum rather than a <c>BatteryReport</c> so it stays
    /// unit-testable (see the class doc).
    /// </summary>
    public static bool IsOnAC(Windows.System.Power.BatteryStatus status) =>
        status is Windows.System.Power.BatteryStatus.Charging or Windows.System.Power.BatteryStatus.Idle;

    /// <summary>
    /// POWER line: source label (+ optional adapter wattage) and, when meaningfully non-zero in the
    /// expected direction, the live rate. <paramref name="onAC"/> and <paramref name="adapterWattage"/>
    /// are passed in rather than re-derived here so callers control their own caching/query cadence
    /// for the RPC-backed wattage read.
    /// </summary>
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

    /// <summary>
    /// REMAINING line: a direction-aware duration, e.g. "~2h 14m to full" (charging) or
    /// "~3h remaining" (discharging) — the label itself stays static ("REMAINING") in both windows,
    /// so the value must carry the direction or a charging reading would misleadingly read as
    /// "battery time left" instead of "time until full".
    /// </summary>
    public static string FormatTimeRemaining(int? chargeRateMw, int? remainingMwh, int? fullChargeMwh)
    {
        if (chargeRateMw is not { } rate || Math.Abs(rate) < 100) return "—";
        if (remainingMwh is not { } remaining) return "—";

        if (rate > 0)
            return HoursToFull(rate, remainingMwh, fullChargeMwh) is { } h
                ? FormatHours(h, chargingDirection: true) : "—";
        if (rate < 0)
            return FormatHours(remaining / (double)Math.Abs(rate), chargingDirection: false);
        return "—";
    }

    /// <summary>
    /// Hours until full while charging at a meaningful (&gt;=100 mW) rate; null otherwise. The single
    /// numeric time-to-full source shared by the REMAINING stat text (above) and the Home Assistant
    /// <c>remaining_charge_time</c> sensor (<see cref="ChargeKeeper.Services.HaStateBuilder"/>), so the
    /// two can't drift on the rate guard. Pure, so it's unit-tested directly.
    /// </summary>
    internal static double? HoursToFull(int chargeRateMw, int? remainingMwh, int? fullChargeMwh)
    {
        if (chargeRateMw < 100) return null;
        if (remainingMwh is not { } remaining || fullChargeMwh is not > 0) return null;
        double h = (fullChargeMwh.Value - remaining) / (double)chargeRateMw;
        if (h <= 0 || double.IsInfinity(h) || double.IsNaN(h)) return null;
        return h;
    }

    // Internal (not private) so unit tests can verify the hour/minute formatting and boundary
    // behaviour directly, same "internal test seam" convention as BatteryHistoryService.Format.
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
