using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Shared colour constants and pre-allocated brushes. Centralises magic hex values and avoids
/// allocating new brush objects on every UI refresh.
/// </summary>
internal static class AppColors
{
    // SteelBlue is the app's primary "active/positive" accent: charging glyph, active badges,
    // selected controls, the history graph's SoC line. The gauge palette bytes live in GaugePalette,
    // shared with the GDI+ tray-icon renderer, which cannot use Windows.UI.Color.
    internal static readonly Color SteelBlue   = FromPacked(GaugePalette.SteelBlue);
    internal static readonly Color Orange      = Color.FromArgb(255, 0xFF, 0x8C, 0x00);
    internal static readonly Color Grey        = Color.FromArgb(255, 0x9E, 0x9E, 0x9E);
    internal static readonly Color Amber       = FromPacked(GaugePalette.Amber);   // brand amber
    internal static readonly Color Blue        = Color.FromArgb(255, 0x36, 0xB0, 0xE6);  // brand blue (idle)

    /// <summary>A packed 0xAARRGGBB value from <see cref="GaugePalette"/> as a WinUI colour.</summary>
    internal static Color FromPacked(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // Battery status glyph (gauge centre).
    internal static readonly SolidColorBrush StatusChargingBrush    = new(SteelBlue);  // charging  ▲
    internal static readonly SolidColorBrush StatusIdleBrush        = new(Blue);       // full/idle ●
    internal static readonly SolidColorBrush StatusDischargingBrush = new(Amber);      // draining  ▼
    internal static readonly SolidColorBrush StatusUnknownBrush     = new(Grey);       // none / —

    // Badge backgrounds (semi-transparent fills).
    internal static readonly SolidColorBrush BadgeActiveBrush =
        new(Color.FromArgb(20, SteelBlue.R, SteelBlue.G, SteelBlue.B));
    internal static readonly SolidColorBrush BadgeInactiveBrush = new(Color.FromArgb(12, 0x80, 0x80, 0x80));

    // ~3x more opaque than BadgeActiveBrush: at 8 % a 34 px selected button is too faint to read.
    internal static readonly SolidColorBrush TimeScaleSelectedBrush =
        new(Color.FromArgb(60, SteelBlue.R, SteelBlue.G, SteelBlue.B));

    // The "in use" preset marker, on both the Settings rows and the dashboard preset buttons. A tint
    // will not do here: even at 24 % SteelBlue composites to a near-black grey against the studio
    // surfaces, so the marker takes the accent solid. SteelBlue is a LIGHT accent (relative luminance
    // 0.40), which makes black the legible label colour on it (~9:1) in either theme, where white
    // would reach only 2.3:1.
    internal static readonly SolidColorBrush AccentBrush   = new(SteelBlue);
    internal static readonly SolidColorBrush OnAccentBrush = new(Microsoft.UI.Colors.Black);

    // The charge-limit tone the chrome reuses. The gauge arc itself takes no brush from here: its
    // colour is continuous, so the dashboard samples GaugePalette and repaints one owned brush.
    internal static readonly Color Terracotta = FromPacked(GaugePalette.Terracotta);  // dusty terracotta

    // History graph series: one fixed accent each, not a level-based switch, which read as a
    // traffic light next to the red/green min-max markers. SoC and Limit reuse the dashboard's own
    // SteelBlue and Terracotta.
    internal static readonly SolidColorBrush HistorySocBrush   = new(SteelBlue);
    internal static readonly SolidColorBrush HistoryLimitBrush = new(Terracotta);
    internal static readonly SolidColorBrush HistoryPowerBrush = new(FromPacked(GaugePalette.Lavender));

    // Terracotta, not the vivid Orange, so the pop-out trigger matches the dashboard's orange tint.
    internal static readonly SolidColorBrush ExpandGlyphBrush = new(Terracotta);
    internal static readonly SolidColorBrush ExpandGlyphBackgroundBrush =
        new(Color.FromArgb(34, Terracotta.R, Terracotta.G, Terracotta.B));

    // Gradient fill under the SoC line: SteelBlue fading to fully transparent.
    internal static readonly LinearGradientBrush HistorySocFillBrush = BuildFadeBrush(SteelBlue);

    private static LinearGradientBrush BuildFadeBrush(Color c) => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint   = new Point(0, 1),
        GradientStops =
        {
            new GradientStop { Color = c, Offset = 0.0 },
            new GradientStop { Color = Color.FromArgb(0, c.R, c.G, c.B), Offset = 1.0 },
        },
    };
}
