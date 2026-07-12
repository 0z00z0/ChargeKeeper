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
    // selected controls, and the history graph's SoC line) — a dusty steel-blue that replaced an
    // earlier saturated green so the whole dashboard reads as one consistent accent colour instead
    // of green-in-some-places, blue-in-others. TODO #34 reintroduced a genuine green (SageGreen,
    // below) as a deliberate, scoped exception to that rule: the arc gauge and tray icon now
    // colour-code charge state (green/yellow/orange/blue), a stronger signal there than accent
    // uniformity — see the "Arc gauge fills" section for how that interacts with SteelBlue/
    // GaugeHighBrush, which stays exactly as it was for an unrelated reason.
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
    // TODO #34: colour-code charge state — green > 75 %, yellow 26-75 %, orange ≤ 25 %, and a
    // charging/on-AC override (GaugeChargingBrush) that forces blue regardless of level. Still
    // dusty/muted tones, not the vivid traffic-light red-orange-green this kind of scheme usually
    // means: SageGreen is the one genuinely new colour added for this (no muted green existed in
    // the palette before); the yellow and orange tiers deliberately REUSE existing colours rather
    // than adding near-duplicates — Amber already reads as a golden yellow, and Terracotta's dusty
    // orange-tan hue reads as muted orange convincingly enough that a distinct new "orange" would
    // have been redundant. The old DustyRed (a true muted red, ≤ 20 % tier) is gone: nothing in
    // this new red/orange-less scheme called for a red, and it had no other callers.
    internal static readonly Color SageGreen  = Color.FromArgb(255, 0x7A, 0xB8, 0x8F);  // dusty sage green
    internal static readonly Color Terracotta = Color.FromArgb(255, 0xC9, 0x92, 0x6B);  // dusty terracotta
    internal static readonly SolidColorBrush GaugeGreenBrush    = new(SageGreen);  // > 75 %
    internal static readonly SolidColorBrush GaugeMedBrush      = new(Amber);      // 26-75 % ("yellow")
    // Terracotta moves here from the old medium tier — it keeps its shared-colour relationship
    // with HistoryLimitBrush (below) so "the charge-limit/warning colour" still reads as one
    // concept across the dashboard, just tied to the new low/orange tier instead of the old medium
    // one.
    internal static readonly SolidColorBrush GaugeLowBrush      = new(Terracotta); // ≤ 25 % ("orange") — matches HistoryLimitBrush
    // Reverted to SteelBlue (was the vivid Blue) per user feedback — Blue read as jarring next to
    // the muted green/yellow/orange tiers, and didn't match StatusChargingBrush (the ▲ glyph) or
    // the threshold-slider/SoC-line accent, which were already SteelBlue. Charging/on-AC now
    // reads as the same muted accent everywhere instead of two different blues.
    internal static readonly SolidColorBrush GaugeChargingBrush = new(SteelBlue);  // charging/on-AC override, any %

    // GaugeHighBrush intentionally KEEPS its original SteelBlue value and is no longer read by the
    // arc gauge's own top tier (that's GaugeGreenBrush now, above) — BatteryHistoryGraphControl's
    // constructor still binds its SoC legend swatch directly to this brush so the swatch matches
    // HistorySocBrush/the actual SoC line colour (both SteelBlue). Repointing or removing this
    // constant would silently desync that legend from the line it labels, so it stays as dead
    // weight from the gauge's point of view on purpose. > 50 % is its original (now unused by the
    // gauge) threshold comment, kept for history.
    internal static readonly SolidColorBrush GaugeHighBrush = new(SteelBlue);   // > 50 % (legacy; see above)

    // ── History graph series ("Nordic mist") ────────────────────────────────────
    // One fixed accent per series, not a level-based switch — the old red/amber/green cycling
    // for SoC read as a jarring traffic light, especially alongside red/green min-max markers.
    // Each series now stays one steady colour across the whole line regardless of level; SoC
    // reuses SteelBlue and Limit reuses Terracotta so the graph and the rest of the dashboard
    // (including the gauge's low/orange tier, above) match.
    internal static readonly SolidColorBrush HistorySocBrush   = new(SteelBlue);
    internal static readonly SolidColorBrush HistoryLimitBrush = new(Terracotta);
    internal static readonly SolidColorBrush HistoryPowerBrush = new(Color.FromArgb(255, 0x9C, 0x8F, 0xBD));  // muted lavender

    // ── Graph "Expand" affordance ────────────────────────────────────────────────
    // Terracotta again (not the vivid Orange) — the palette's orange tint everywhere else on
    // the dashboard is this muted tone, so the pop-out trigger reads as "part of the same
    // system" rather than introducing a second, brighter orange.
    internal static readonly SolidColorBrush ExpandGlyphBrush = new(Terracotta);
    internal static readonly SolidColorBrush ExpandGlyphBackgroundBrush =
        new(Color.FromArgb(34, Terracotta.R, Terracotta.G, Terracotta.B));

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
