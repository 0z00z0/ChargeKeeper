namespace ChargeKeeper.Helpers;

/// <summary>Formats a battery charge/discharge rate, so the sign glyph, rounding and unit cannot
/// drift between the dashboard power line and the tray tooltip.</summary>
internal static class PowerFormat
{
    /// <summary>
    /// Renders a rate in milliwatts (positive = charging in) with a real minus sign (U+2212). Rates
    /// below 1 W stay in mW, so a small but non-zero draw never collapses to "0 W". Returns null for
    /// a zero rate, so the caller can omit the field entirely.
    /// </summary>
    public static string? SignedRate(int milliwatts)
    {
        if (milliwatts == 0) return null;

        char sign  = milliwatts > 0 ? '+' : '−';   // + or −
        int  absMw = Math.Abs(milliwatts);
        return absMw < 1000
            ? $"{sign}{absMw} mW"
            : $"{sign}{absMw / 1000.0:F0} W";
    }

    /// <summary>
    /// Renders a %/hour battery rate (positive = charging in) with the same real minus sign (U+2212)
    /// and leading-sign convention as <see cref="SignedRate(int)"/>. One decimal place: SoC is only
    /// ever read to the whole percent, so a rate extrapolated from it is rarely a round number.
    /// Null in, null out, so the caller falls back to the same "no reading yet" placeholder the other
    /// dashboard stats already use.
    /// </summary>
    public static string? SignedPercentPerHour(double? percentPerHour)
    {
        if (percentPerHour is not { } rate) return null;
        char sign = rate < 0 ? '−' : '+';   // + or −
        return $"{sign}{Math.Abs(rate):F1} %/h";
    }
}
