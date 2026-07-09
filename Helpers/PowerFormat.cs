namespace ChargeKeeper.Helpers;

/// <summary>
/// Formats a battery charge/discharge rate the same way everywhere it is shown
/// (dashboard power line + tray tooltip), so the sign glyph, rounding and unit never drift apart.
/// </summary>
internal static class PowerFormat
{
    /// <summary>
    /// Renders a power rate (milliwatts; positive = charging in, negative = draining out) as a
    /// signed string with a real minus sign (U+2212). Rates below 1 W are shown in mW so a small
    /// but non-zero draw never collapses to "0 W" / "−0 W". Returns <c>null</c> for a zero rate
    /// so the caller can omit the field entirely.
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
