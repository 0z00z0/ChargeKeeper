namespace ChargeKeeper.Helpers;

/// <summary>
/// Framework-neutral source of truth for the charge-state gauge palette and its tier thresholds,
/// shared by the WinUI surfaces (<see cref="AppColors"/>, via <c>Windows.UI.Color</c>) and the GDI+
/// tray-icon renderer (<see cref="IconGenerator"/>, via <c>System.Drawing.Color</c>). The two
/// frameworks have no shared Color type, but packed ARGB bytes cross that divide fine — before
/// this, the values were literal duplicates "kept in sync BY HAND" across three files, and the
/// charging colour actually drifted once during the TODO #34 batch before being caught.
/// </summary>
internal static class GaugePalette
{
    // Tier thresholds: green above GreenAbovePct, the low/orange tier at or below LowAtOrBelowPct,
    // amber in between. Consumed by both the dashboard gauge's switch and the tray icon's.
    internal const int GreenAbovePct   = 75;
    internal const int LowAtOrBelowPct = 25;

    // Packed 0xAARRGGBB. Names/meanings documented at the consuming ends (AppColors' section
    // comments own the design rationale; these are just the bytes).
    internal const uint SageGreen  = 0xFF7AB88F;   // > GreenAbovePct
    internal const uint Amber      = 0xFFD8A657;   // middle tier / brand amber
    internal const uint Terracotta = 0xFFC9926B;   // ≤ LowAtOrBelowPct / charge-limit accent
    internal const uint SteelBlue  = 0xFF7FA8B8;   // charging/on-AC override + app accent
}
