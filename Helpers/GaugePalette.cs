namespace ChargeKeeper.Helpers;

/// <summary>
/// Framework-neutral source of truth for the charge-state gauge palette and its tier thresholds,
/// shared by <see cref="AppColors"/> (WinUI) and <see cref="IconGenerator"/> (GDI+). The two
/// frameworks share no Color type, but packed ARGB bytes cross that divide.
/// </summary>
internal static class GaugePalette
{
    // Green above GreenAbovePct, the low/orange tier at or below LowAtOrBelowPct, amber in between.
    internal const int GreenAbovePct   = 75;
    internal const int LowAtOrBelowPct = 25;

    // Packed 0xAARRGGBB.
    internal const uint SageGreen  = 0xFF7AB88F;   // > GreenAbovePct
    internal const uint Amber      = 0xFFD8A657;   // middle tier / brand amber
    internal const uint Terracotta = 0xFFC9926B;   // ≤ LowAtOrBelowPct / charge-limit accent
    internal const uint SteelBlue  = 0xFF7FA8B8;   // charging/on-AC override + app accent
}
