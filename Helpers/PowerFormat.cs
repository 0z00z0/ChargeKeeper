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
}
