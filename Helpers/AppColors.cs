using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Shared color constants and pre-allocated brushes.
/// Centralises magic hex values and avoids allocating new brush objects on every UI refresh.
/// </summary>
internal static class AppColors
{
    // ── Semantic state ─────────────────────────────────────────────────────────
    // SteelBlue is the app's primary "active/positive" accent (charging glyph, active badges,
    // selected controls, the gauge's high-charge fill, and the history graph's SoC line) — a
    // dusty steel-blue that replaced an earlier saturated green so the whole dashboard reads as
    // one consistent accent colour instead of green-in-some-places, blue-in-others.
    internal static readonly Color SteelBlue   = Color.FromArgb(255, 0x7F, 0xA8, 0xB8);
    internal static readonly Color Orange      = Color.FromArgb(255, 0xFF, 0x8C, 0x00);
    internal static readonly Color Grey        = Color.FromArgb(255, 0x9E, 0x9E, 0x9E);
    internal static readonly Color Amber       = Color.FromArgb(255, 0xD8, 0xA6, 0x57);  // brand amber
    internal static readonly Color Blue        = Color.FromArgb(255, 0x36, 0xB0, 0xE6);  // brand blue (idle)

    // ── Battery status glyph (gauge centre) ─────────────────────────────────────
    internal static readonly SolidColorBrush StatusChargingBrush    = new(SteelBlue);  // charging  ▲
    internal static readonly SolidColorBrush StatusIdleBrush        = new(Blue);       // full/idle ●
    internal static readonly SolidColorBrush StatusDischargingBrush = new(Amber);      // draining  ▼
    internal static readonly SolidColorBrush StatusUnknownBrush     = new(Grey);       // none / —

    // ── Badge backgrounds (semi-transparent fills) ──────────────────────────────
    internal static readonly SolidColorBrush BadgeActiveBrush =
        new(Color.FromArgb(20, SteelBlue.R, SteelBlue.G, SteelBlue.B));
    internal static readonly SolidColorBrush BadgeInactiveBrush = new(Color.FromArgb(12, 0x80, 0x80, 0x80));

    // BadgeActiveBrush's ~8% opacity reads fine on the large Smart Charge/Standby badges but is
    // too faint to mark a small (34px) selected button in the time-scale row — this is ~3x more
    // opaque, tuned for that smaller control instead.
    internal static readonly SolidColorBrush TimeScaleSelectedBrush =
        new(Color.FromArgb(60, SteelBlue.R, SteelBlue.G, SteelBlue.B));

    // ── Indicator dots ─────────────────────────────────────────────────────────
    internal static readonly SolidColorBrush IndicatorAccentBrush = new(SteelBlue);
    internal static readonly SolidColorBrush IndicatorOrangeBrush = new(Orange);
    internal static readonly SolidColorBrush IndicatorGreyBrush   = new(Grey);

    // ── Arc gauge fills (by battery level) ─────────────────────────────────────
    // Dusty, muted tones for the low/medium warning tiers instead of the original bright/vivid
    // red-orange — those read as an alarm and, at medium level (21-50%, a very common battery
    // range), a vivid orange arc looked "stuck" even though the level-based switch was working
    // correctly the whole time. The medium tier reuses the SAME Terracotta as the history graph's
    // charge-limit line (below), and the high tier reuses SteelBlue — so the gauge, the badges,
    // and the graph all draw from one small, deliberately coordinated palette.
    internal static readonly Color DustyRed   = Color.FromArgb(255, 0xC9, 0x60, 0x5A);  // muted brick-red
    internal static readonly Color Terracotta = Color.FromArgb(255, 0xC9, 0x92, 0x6B);  // dusty terracotta
    internal static readonly SolidColorBrush GaugeLowBrush  = new(DustyRed);    // ≤ 20 %
    internal static readonly SolidColorBrush GaugeMedBrush  = new(Terracotta);  // ≤ 50 % — matches HistoryLimitBrush
    internal static readonly SolidColorBrush GaugeHighBrush = new(SteelBlue);   // > 50 %

    // ── History graph series ("Nordic mist") ────────────────────────────────────
    // One fixed accent per series, not a level-based switch — the old red/amber/green cycling
    // for SoC read as a jarring traffic light, especially alongside red/green min-max markers.
    // Each series now stays one steady colour across the whole line regardless of level; SoC
    // reuses SteelBlue and Limit reuses Terracotta so the graph and the rest of the dashboard
    // (including the gauge's medium tier, above) match.
    internal static readonly SolidColorBrush HistorySocBrush   = new(SteelBlue);
    internal static readonly SolidColorBrush HistoryLimitBrush = new(Terracotta);
    internal static readonly SolidColorBrush HistoryPowerBrush = new(Color.FromArgb(255, 0x9C, 0x8F, 0xBD));  // muted lavender

    // Gradient fill under the SoC line: SteelBlue fading to fully transparent. Cached once —
    // never allocated per render tick (see the class doc comment).
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
