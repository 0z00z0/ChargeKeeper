using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// The battery-history sparkline graph (SoC / charge-limit / power series, compressed-gap time
/// axis, min/max markers) plus its time-scale ("period") selector row — extracted from
/// <see cref="DashboardWindow"/> into a standalone control so the exact same graph can be hosted
/// both in the small tray popup (fixed <see cref="PlotAreaHeight"/>) and in a bigger, resizable
/// pop-out window (<c>BatteryHistoryWindow</c>, canvas row left at its default "*" so it grows
/// with the window) without duplicating the drawing code.
/// <para>
/// <see cref="Render"/> reads <see cref="SparklineCanvas"/>'s own <c>ActualWidth</c>/<c>ActualHeight</c>
/// fresh on every call, so it already adapts to whatever size the host gives the control — the
/// only thing a resizable host needs to add is calling <see cref="Render"/> again when the canvas
/// is resized, which this control already does itself via <see cref="OnCanvasSizeChanged"/>.
/// </para>
/// <para>
/// The time-scale selection (<see cref="SettingsService.AppSettings.GraphTimeScale"/>) and the
/// loaded sample window (<see cref="BatteryHistoryService"/>) are both process-wide shared state
/// by design — every instance of this control (small dashboard, pop-out) is a view onto the same
/// graph, not an independently-scoped one, so changing the scale in either place updates both.
/// </para>
/// </summary>
public sealed partial class BatteryHistoryGraphControl : UserControl
{
    // A hole in the timeline bigger than this is treated as downtime (app was closed/crashed)
    // rather than just a slightly-late sample, and gets a gap marker instead of a connecting line.
    // User-configurable (tray Settings → "Downtime gap threshold", BuildDowntimeGapMenu) via
    // SettingsService.Current.DowntimeGapMinutes, so this can't be a cached `static readonly`
    // computed once at type-init the way it used to be (TimeSpan.FromSeconds(
    // BatteryHistoryService.SampleIntervalSeconds * 3), ~1 minute) — it must react to the setting
    // changing without an app restart, so it's read fresh from settings on every access instead.
    // 0 ("None") maps to TimeSpan.MaxValue, not TimeSpan.Zero: the setting means "disable gap
    // detection" (never treat any hole as downtime), the opposite of a literal zero-minute
    // threshold, which would nonsensically flag every sample boundary as a gap.
    private static TimeSpan GapThreshold =>
        SettingsService.Current.DowntimeGapMinutes <= 0
            ? TimeSpan.MaxValue
            : TimeSpan.FromMinutes(SettingsService.Current.DowntimeGapMinutes);

    // Stroke width for the primary series (SoC) — heavier than the secondary series so it reads
    // as the "main" line at a glance.
    private const double PrimaryStrokeWidth   = 2.5;
    private const double SecondaryStrokeWidth = 2.0;

    // Vertical band reserved at the top of the plot for the gap-break duration label, so the break
    // strokes below start clear of it instead of crossing through the text.
    private const double GapLabelBandHeight = 15;

    // The two diagonal strokes' x-offsets from the break's centre — hoisted out of DrawGapBreak so
    // this small array isn't reallocated once per gap on every render.
    private static readonly double[] GapStrokeOffsets = [-2.5, 1.5];

    // Rough average glyph width for the SemiBold UI font at 1em, used to estimate a pill's width
    // without a Measure() pass (see AddAnnotationPill) — labels are always short, fixed-format
    // digit/letter/percent strings ("13h", "74%"), so pixel-perfect width isn't needed, only enough
    // to centre and edge-clamp the pill reasonably.
    private const double PillCharWidthEm = 0.62;
    private const double PillPaddingX    = 8; // matches Padding(4,_,4,_) below, both sides combined

    // Debounces the live-resize repaint: SparklineCanvas.SizeChanged fires on every intermediate
    // pixel while the user drags a resizable host's border, and a full canvas rebuild on every one
    // of those would be wasted work — restart the timer on each event, repaint once it settles.
    // Same start/stop idiom as DashboardWindow's threshold-slider debounce, just much shorter:
    // a resize needs to feel live, not "committed once you pause".
    private readonly DispatcherTimer _resizeRenderTimer;

    // Set on Unloaded so a background LoadWindow callback that completes after the control (and
    // its host window) is torn down doesn't touch dead XAML elements.
    private bool _unloaded;

    /// <summary>
    /// Raised when the user asks to expand the graph — via the "⛶" corner glyph or by
    /// double-clicking the plot. The control has no reference to the app or window-management —
    /// it only signals intent; the host decides what "expand" means.
    /// </summary>
    public event EventHandler? ExpandRequested;

    /// <summary>
    /// Plot canvas row height. The small embedded dashboard sets this to a fixed 126 (matching the
    /// pre-extraction layout exactly) so its popup's content-measured size stays pixel-identical;
    /// the resizable pop-out window leaves this at the control's own XAML default ("*") so the
    /// canvas grows to fill whatever space the window gives it.
    /// </summary>
    public GridLength PlotAreaHeight
    {
        get => CanvasRow.Height;
        set => CanvasRow.Height = value;
    }

    /// <summary>
    /// Shows/hides the "⛶" expand glyph AND gates the double-click-to-expand gesture. The pop-out
    /// window hosts this same control to show the already-expanded graph, where an expand
    /// affordance pointing at itself would be meaningless.
    /// </summary>
    public bool ShowExpandButton
    {
        get => ExpandGlyph.Visibility == Visibility.Visible;
        set => ExpandGlyph.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows/hides the compressed-gap "time-split" break + duration pill (<see cref="DrawGapBreak"/>)
    /// drawn at each detected downtime gap. Unlike <see cref="ShowExpandButton"/> there's no single
    /// fixed XAML element to toggle — the breaks are drawn per-render, one per gap, inside
    /// <see cref="Render"/> — so this is a plain settable property read at the top of that loop
    /// instead. The small embedded dashboard sets this false (a compact popup has no room for the
    /// break + pill without crowding the plot); the bigger pop-out window leaves it at the default
    /// true, unchanged from today's behaviour.
    /// </summary>
    public bool ShowGapMarkers { get; set; } = true;

    /// <summary>
    /// Shows/hides the SoC "stress" heat strip below the plot (TODO #25) — a slim gradient bar
    /// colour-coding time spent at high SoC (more stressful for long-term battery health per
    /// common lithium-ion guidance) vs moderate SoC (gentler), across the visible window. Unlike
    /// <see cref="ShowGapMarkers"/> this DOES have a single fixed XAML element (<c>StressHeatmapBar</c>),
    /// so hiding it also collapses the Auto row it lives in — no separate flag needed to keep the
    /// row from reserving space. The small embedded dashboard sets this false (no vertical room for
    /// another row without crowding the plot, same reasoning as ShowGapMarkers); the bigger pop-out
    /// window leaves it at the default true.
    /// </summary>
    public bool ShowStressHeatmap
    {
        get => StressHeatmapBar.Visibility == Visibility.Visible;
        set => StressHeatmapBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows/hides the hover crosshair (TODO #27) — a thin vertical trace line + dot on the SoC
    /// line + a "time · SoC% · rate" pill, following the cursor over the plot. Explicitly
    /// crosshair-only, no pan/zoom (that idea was deliberately deferred to a separate future item —
    /// see TODO.md). Small dashboard popup disables it: at 340px wide there's barely room for the
    /// plot itself, let alone a readout pill without it constantly overlapping the lines it's
    /// tracking; the bigger pop-out window leaves this at the default true.
    /// </summary>
    public bool ShowCrosshair { get; set; } = true;

    // Cached from the most recent Render() so OnCanvasPointerMoved can look up the nearest sample
    // without redoing the whole downsample/compressed-x/axis-projection pipeline on every mouse
    // move — Render() only runs on a 5s timer or a resize, but pointer moves can fire dozens of
    // times a second.
    private IReadOnlyList<BatterySample>? _hoverSamples;
    private IReadOnlyList<double>?        _hoverXs;
    private Func<double, double>?         _hoverProjectYPct;

    // The specific elements the crosshair itself owns (line, dot, pill) — tracked so each pointer
    // move can remove exactly those without touching the real chart underneath them (a full
    // SparklineCanvas.Children.Clear() would wipe the actual series too).
    private readonly List<UIElement> _crosshairElements = [];

    public BatteryHistoryGraphControl()
    {
        InitializeComponent();

        // Legend swatch colours never change — assign once instead of every render.
        LegendSocSwatch.Background   = AppColors.GaugeHighBrush;
        LegendLimitSwatch.Background = AppColors.HistoryLimitBrush;
        LegendPowerSwatch.Background = AppColors.HistoryPowerBrush;

        // Expand affordance: the palette's muted orange (Terracotta), tinted background + glyph,
        // so it reads as a deliberate, visible button rather than a bare corner glyph.
        ExpandGlyph.Foreground = AppColors.ExpandGlyphBrush;
        ExpandGlyph.Background = AppColors.ExpandGlyphBackgroundBrush;

        // Reflect the persisted time-scale choice in the button row. The in-memory window was
        // already loaded from disk at this span by App.StartHistorySampling, so no LoadWindow call
        // is needed here — CurrentWindow() already holds the right slice for the first render.
        SetSelectedScaleButton(SettingsService.Current.GraphTimeScale);

        // Narrow race: if the very first graph render happens before StartHistorySampling's
        // background disk load finishes, CurrentWindow() is momentarily empty even though history
        // exists on disk. Only kick a reload when that's actually the case — the normal case
        // (already loaded) does zero extra I/O.
        if (BatteryHistoryService.CurrentWindow().Count == 0)
        {
            Task.Run(() =>
            {
                BatteryHistoryService.LoadWindow(SettingsService.Current.GraphTimeScale.ToTimeSpan());
                RunOnUi(Render);
            });
        }

        _resizeRenderTimer          = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _resizeRenderTimer.Tick    += (_, _) => { _resizeRenderTimer.Stop(); Render(); };

        Unloaded += (_, _) =>
        {
            _unloaded = true;
            _resizeRenderTimer.Stop();
        };
    }

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch. An exception
    /// thrown inside a raw DispatcherQueue.TryEnqueue callback is NOT surfaced to
    /// Application.UnhandledException — it tears the whole process down as an opaque stowed
    /// exception with nothing logged anywhere. This control has background Task.Run reads
    /// (LoadWindow's disk scan) that complete and touch UI elements later, on the dispatcher — if
    /// the host window closes while one of those is in flight, the delegate can hit a torn-down
    /// XAML element. Catching here keeps the app alive; the failure is logged instead of fatal.
    /// </summary>
    private void RunOnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_unloaded) return;   // control already torn down — a stale callback has nothing to update
        try { action(); }
        catch (Exception ex) { AppLog.Error("BatteryHistoryGraphControl.RunOnUi", ex); }
    });

    private void OnExpandGlyphClick(object sender, RoutedEventArgs e) =>
        ExpandRequested?.Invoke(this, EventArgs.Empty);

    // Double-click anywhere on the plot = expand, same as the corner glyph — but only where the
    // expand affordance is enabled at all, so double-clicking inside the already-open pop-out
    // (ShowExpandButton="False") does nothing instead of re-signalling itself.
    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ShowExpandButton)
            ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    // Restarts the debounce on every intermediate resize event; Render() only runs once the size
    // has settled for _resizeRenderTimer's interval.
    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeRenderTimer.Stop();
        _resizeRenderTimer.Start();
    }

    // ── Hover crosshair (TODO #27) ────────────────────────────────────────────

    /// <summary>
    /// Traces the nearest sample to the cursor's X position — a linear scan over <see cref="_hoverXs"/>
    /// rather than a binary search: at the sizes <see cref="Render"/> already downsamples to
    /// (roughly one point per horizontal pixel), a full scan on a mouse-move event is cheap enough
    /// that the extra complexity of a binary search wouldn't be worth it.
    /// </summary>
    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ShowCrosshair) return;
        if (_hoverSamples is not { Count: > 1 } samples) return;
        if (_hoverXs is not { } xs || _hoverProjectYPct is not { } projectY) return;

        double x = e.GetCurrentPoint(SparklineCanvas).Position.X;

        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < xs.Count; i++)
        {
            double d = Math.Abs(xs[i] - x);
            if (d < best) { best = d; nearest = i; }
        }

        DrawCrosshair(samples[nearest], xs[nearest], projectY(samples[nearest].Soc));
    }

    private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e) => ClearCrosshair();

    /// <summary>
    /// Removes exactly the crosshair's own elements (line, dot, pill) from the canvas — NOT a
    /// <c>SparklineCanvas.Children.Clear()</c>, which would also wipe the real chart underneath.
    /// </summary>
    private void ClearCrosshair()
    {
        foreach (var element in _crosshairElements)
            SparklineCanvas.Children.Remove(element);
        _crosshairElements.Clear();
    }

    /// <summary>
    /// Draws the hover crosshair at the given sample: a thin vertical trace line spanning the plot
    /// height, a small dot marking that sample's actual point on the SoC line, and a "time · SoC% ·
    /// rate" readout pill. Redrawn (via <see cref="ClearCrosshair"/> then re-add) on every pointer
    /// move rather than moved in place — WinUI has no cheap "reposition without a full Children
    /// mutation" primitive here, and at typical mouse-move frequencies this is not a hot enough
    /// path to justify the extra bookkeeping a move-in-place version would need.
    /// </summary>
    private void DrawCrosshair(BatterySample sample, double x, double y)
    {
        ClearCrosshair();

        double h = SparklineCanvas.ActualHeight;
        var line = new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = x, Y1 = 0, X2 = x, Y2 = h,
            Stroke          = SparklineStartLabel.Foreground,   // same neutral brush as axis labels
            StrokeThickness = 1,
            Opacity         = 0.5,
        };
        SparklineCanvas.Children.Add(line);
        _crosshairElements.Add(line);

        const double dotR = 3;
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width           = dotR * 2,
            Height          = dotR * 2,
            Fill            = AppColors.HistorySocBrush,
            Stroke          = SparklineStartLabel.Foreground,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(dot, x - dotR);
        Canvas.SetTop(dot, y - dotR);
        SparklineCanvas.Children.Add(dot);
        _crosshairElements.Add(dot);

        string rate  = PowerFormat.SignedRate(sample.PowerMw) ?? "0 W";
        string label = $"{sample.AtUtc.ToLocalTime():t} · {sample.Soc}% · {rate}";
        var pill = AddAnnotationPill(x, Math.Max(0, y - 20 - 13), label, SparklineCanvas.ActualWidth, fontSize: 12);
        _crosshairElements.Add(pill);
    }

    // ── Time-scale selector ───────────────────────────────────────────────────

    /// <summary>
    /// Handles a click on one of the seven time-scale buttons (15m/1h/.../14d). The clicked
    /// button's <c>Tag</c> holds the <see cref="GraphTimeScale"/> member name (set in XAML).
    /// Persists the choice, does the (disk) window reload at the new span, and re-renders.
    /// </summary>
    private void OnTimeScaleButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagName } ||
            !Enum.TryParse<GraphTimeScale>(tagName, out var scale))
            return;

        SettingsService.Current.GraphTimeScale = scale;
        SettingsService.Save();
        SetSelectedScaleButton(scale);   // highlight immediately; don't wait on the disk read below

        // LoadWindow does a full CSV scan (up to 14 days of rows) — real disk I/O that must not run
        // on the UI thread, or clicking a scale button could visibly freeze the host for a moment.
        Task.Run(() =>
        {
            BatteryHistoryService.LoadWindow(scale.ToTimeSpan());
            AppLog.Info($"Time-scale changed to {scale}.");

            // With two independent hosts (small dashboard + pop-out) now able to trigger a scale
            // change at any time, a slower earlier load can complete after a faster later one. Only
            // repaint if this load is still the most recently requested span — an out-of-order
            // completion otherwise briefly (or, if never re-triggered, permanently) shows stale data
            // under a button that already highlights the newer, correct selection.
            if (BatteryHistoryService.CurrentSpan == scale.ToTimeSpan())
                RunOnUi(Render);
        });
    }

    /// <summary>
    /// Highlights the button matching <paramref name="scale"/> and restores the rest to their
    /// themed resting look. Deselecting clears the local Background/Foreground value rather than
    /// re-resolving a theme brush by string key, so the button falls back to whatever
    /// <c>TimeScaleButtonStyle</c>'s setters provide (and stays correct if that style changes).
    /// </summary>
    private void SetSelectedScaleButton(GraphTimeScale scale)
    {
        foreach (var button in TimeScalePanel.Children.OfType<Button>())
        {
            bool selected = button.Tag is string tagName &&
                             Enum.TryParse<GraphTimeScale>(tagName, out var buttonScale) &&
                             buttonScale == scale;
            if (selected)
            {
                button.Background = AppColors.TimeScaleSelectedBrush;
                button.Foreground = AppColors.StatusChargingBrush;
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    // ── History sparkline ─────────────────────────────────────────────────────

    /// <summary>
    /// Formats a history span as a left-edge axis label, e.g. "−42m", "−1h 05m", "−3d 07h", or
    /// "−2w 00d" — a full-axis-edge label, not <see cref="FormatGap"/>'s compact single-unit
    /// gap-pill style ("13h"/"2d"): this one keeps a "−" prefix and a space-separated two-unit
    /// format for every tier once the span reaches an hour, matching that existing minutes/hours
    /// style straight through the added days/weeks tiers (warranted since the 14-day max window —
    /// <see cref="GraphTimeScale.FourteenDays"/> — spans exactly two weeks, same ceiling
    /// <see cref="FormatGap"/> already tiers up to).
    /// <para>
    /// Rounds ONCE, to the nearest whole minute, then derives every coarser tier's two displayed
    /// parts from that single rounded total via integer division/modulo — not by rounding each
    /// tier's TotalXxx independently the way <see cref="FormatGap"/> does, since that pattern
    /// only ever has to justify ONE displayed number per call. A two-part display (e.g. "Xh XXm")
    /// has an extra failure mode a single-part one doesn't: rounding each part separately can let
    /// the smaller part round up to its own modulus and land ON the boundary it should have
    /// promoted past (e.g. "1h 60m" instead of "2h 00m"). Deriving both parts from one rounded
    /// total makes that structurally impossible — the remainder of an integer modulo op can never
    /// equal the modulus itself.
    /// </para>
    /// </summary>
    private static string FormatAgo(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "−<1m";

        long totalMinutes = (long)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
        if (totalMinutes < 60) return $"−{totalMinutes}m";

        long totalHours  = totalMinutes / 60;
        int  minutesPart = (int)(totalMinutes % 60);
        if (totalHours < 24) return $"−{totalHours}h {minutesPart:00}m";

        long totalDays = totalHours / 24;
        int  hoursPart = (int)(totalHours % 24);
        if (totalDays < 7) return $"−{totalDays}d {hoursPart:00}h";

        long totalWeeks = totalDays / 7;
        int  daysPart   = (int)(totalDays % 7);
        return $"−{totalWeeks}w {daysPart:00}d";
    }

    /// <summary>
    /// Redraws the sparkline from the currently-loaded history window. Reads
    /// <see cref="SparklineCanvas"/>'s own <c>ActualWidth</c>/<c>ActualHeight</c> fresh every call,
    /// so it adapts automatically to whatever size the host control is — a bigger, resizable host
    /// needs no changes to this method, only to call it again when the canvas resizes (see
    /// <see cref="OnCanvasSizeChanged"/>).
    /// </summary>
    public void Render()
    {
        SparklineCanvas.Children.Clear();

        var samples = BatteryHistoryService.CurrentWindow();
        if (samples.Count < 2)
        {
            SparklineStartLabel.Text  = "—";
            SparklineEndLabel.Text    = "—";
            RightAxisTopLabel.Text    = "—";
            RightAxisMidLabel.Text    = "—";
            RightAxisBottomLabel.Text = "—";
            // StressHeatmapBar lives outside SparklineCanvas.Children, so the Clear() above didn't
            // touch it — without this it would keep showing a stale gradient, misaligned with the
            // now-blank plot above it, from whatever the last successful render was.
            StressHeatmapBar.Fill = null;
            // Drop the hover-crosshair cache too — a pointer move landing after this would
            // otherwise trace a now-stale sample set against a blank canvas.
            _hoverSamples = null;
            _hoverXs      = null;
            ClearCrosshair();
            return;
        }

        // Canvas size is known once the element has been measured; guard against first render.
        double w = SparklineCanvas.ActualWidth;
        double h = SparklineCanvas.ActualHeight;
        if (w < 4 || h < 4) return;

        // Downsample to roughly one point per horizontal pixel (with headroom for fidelity) so a
        // 14-day/1-week window — tens of thousands of raw samples — doesn't force full-resolution
        // processing and element allocation on every render. Gap detection runs against the
        // ORIGINAL full-resolution timestamps and is carried through as gapBefore — two adjacent
        // points that survive reduction can legitimately be far apart in time purely from the
        // stride, so re-deriving "is this a gap" from a Δt check on the reduced list would treat
        // ordinary stride spacing as downtime and shatter every series into disconnected dots.
        int maxPoints = Math.Max(200, (int)(w * 2));
        var reduced   = HistoryDownsampler.Reduce(samples, maxPoints, GapThreshold);
        samples = reduced.Samples;
        var gapBefore = reduced.GapBeforeIndices;

        // Compressed x-axis: continuous data segments fill the plot width, while each downtime gap
        // (app closed/crashed) collapses to a small FIXED-width break instead of occupying its full
        // real duration. A linear absolute-time axis meant a long off-period (e.g. overnight)
        // crushed the actual battery trace into a sliver; here the last active period before a
        // shutdown stays large and readable, with a time-split symbol marking where time was cut.
        DateTime nowUtc = DateTime.UtcNow;
        const double pad = 4;

        // Per-sample X for the compressed axis, index-based (a gap is defined by index — two reduced
        // points can be far apart in ticks purely from stride, so a ticks→X function couldn't tell a
        // real gap from ordinary spacing). Shared by every series, the gap breaks, and the min/max
        // markers so they can't drift apart.
        double[] xs = BuildCompressedX(samples, gapBefore, w, pad);

        // Honest edge labels for a compressed axis: the left edge is the OLDEST loaded sample's age
        // (leading downtime is collapsed, so it can be more recent than the full selected span), and
        // the right edge is the newest sample — "now" while sampling is live.
        SparklineStartLabel.Text = FormatAgo(nowUtc - samples[0].AtUtc);
        var sinceLast = nowUtc - samples[^1].AtUtc;
        SparklineEndLabel.Text   = sinceLast <= GapThreshold ? "now" : FormatAgo(sinceLast);

        // Left % axis: 0% at bottom, 100% at top; invert because canvas Y grows downward. Shared
        // by both the SoC and charge-limit series since they're the same 0-100% scale.
        double ProjectYPct(double pct) => (h - pad) - pct / 100.0 * (h - pad * 2);

        // Cache this render's samples/xs/projection for the hover crosshair (TODO #27) — see the
        // field doc comments for why this is cached rather than recomputed per pointer move.
        _hoverSamples     = samples;
        _hoverXs          = xs;
        _hoverProjectYPct = ProjectYPct;

        // Right W axis: auto-scaled to the visible window's min/max power, always including 0.
        double minW = 0, maxW = 0;
        foreach (var s in samples)
        {
            double watts = s.PowerMw / 1000.0;
            if (watts < minW) minW = watts;
            if (watts > maxW) maxW = watts;
        }
        double wRange = Math.Max(maxW - minW, 1); // avoid div-by-zero when power is flat at 0
        double ProjectYWatts(double watts) => (h - pad) - (watts - minW) / wRange * (h - pad * 2);

        RightAxisTopLabel.Text    = FormatWatts(maxW);
        RightAxisBottomLabel.Text = FormatWatts(minW);
        RightAxisMidLabel.Text    = FormatWatts(minW + wRange / 2);

        // Fixed accent for the whole line, not a level-based switch (Nordic mist) — the battery's
        // current % no longer recolours history that may be days old.
        var socBrush     = AppColors.HistorySocBrush;
        var socFillBrush = AppColors.HistorySocFillBrush;

        // Subtle gradient shade under the SoC line (AppControl-style), drawn first so the fill sits
        // behind everything. Uses the same gapBefore boundaries as the line itself so the fill never
        // bridges a downtime hole either. Brush is one of 3 pre-built, cached instances (AppColors).
        DrawGradientFill(samples, gapBefore, s => s.Soc, xs, ProjectYPct, h - pad, socFillBrush);

        // SoC — solid line, left axis, drawn heavier than the other two (primary: true) since it's
        // the headline series. Drawn first among the three so limit/power (added next) sit
        // visually on top where they cross it.
        DrawSeries(samples, gapBefore, s => s.Soc, xs, ProjectYPct, socBrush, dashed: false, primary: true);

        // Charge limit (Smart Charge Stop threshold) — stepped line, left axis, in the SAME muted
        // amber as the gauge's threshold tick marks so the concept has one colour everywhere.
        // Null when Smart Charge is off; skip those points so no line is drawn across the off
        // period. Stepped (rather than a straight line between samples) because the threshold only
        // ever changes in discrete jumps when the user edits it — a linear ramp between two sampled
        // values would misleadingly suggest it drifted gradually over the sample interval.
        DrawSeries(samples, gapBefore, s => s.LimitPct, xs, ProjectYPct, AppColors.HistoryLimitBrush, stepped: true);

        // Charge power — dotted line, right axis, visually distinct both by dash pattern and by
        // colour (muted lavender) as a different scale.
        DrawSeries(samples, gapBefore, s => s.PowerMw / 1000.0, xs, ProjectYWatts, AppColors.HistoryPowerBrush, dashed: true);

        // Time-split break symbol + skipped-duration label at each collapsed gap, drawn ON TOP of
        // the series so both stay legible over the lines. The break x is the midpoint of the gap's
        // fixed-width band; its duration is the real Δt between the two samples straddling the gap.
        // Gated by ShowGapMarkers: the small embedded dashboard popup suppresses these (no room for
        // the break + pill without crowding the plot); the bigger pop-out window keeps them. Note
        // the gaps themselves are still respected everywhere else (BuildCompressedX still collapses
        // them, CollectRuns still breaks the line there) — only the visual break marker is hidden.
        if (ShowGapMarkers)
            for (int i = 1; i < samples.Count; i++)
                if (gapBefore.Contains(i))
                    DrawGapBreak((xs[i - 1] + xs[i]) / 2, samples[i].AtUtc - samples[i - 1].AtUtc, w, h, pad);

        DrawSparklineMarkers(samples, w, xs, ProjectYPct);

        if (ShowStressHeatmap)
            DrawStressHeatmap(samples, xs, w);
    }

    /// <summary>
    /// Fills <see cref="StressHeatmapBar"/> with a per-sample-stop <see cref="LinearGradientBrush"/>
    /// (TODO #25): one <see cref="GradientStop"/> per sample, positioned at the SAME x (as a 0-1
    /// fraction of plot width) as that sample's point on the SoC line above it, coloured by
    /// <see cref="StressColor"/>. Reuses <paramref name="xs"/> — already monotonically
    /// non-decreasing by construction (<see cref="BuildCompressedX"/>) — rather than a fresh
    /// per-pixel scan, so the strip stays cheap to rebuild every render and can never visually
    /// drift out of alignment with the series it sits under.
    /// </summary>
    private void DrawStressHeatmap(IReadOnlyList<BatterySample> samples, IReadOnlyList<double> xs, double w)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < samples.Count; i++)
            brush.GradientStops.Add(new GradientStop
            {
                Offset = Math.Clamp(xs[i] / w, 0, 1),
                Color  = StressColor(samples[i].Soc),
            });
        StressHeatmapBar.Fill = brush;
    }

    /// <summary>
    /// Maps a SoC percentage to a stress colour: fully transparent at and below 40% (the low end
    /// of this app's own Smart Charge defaults — a "gentle" level, not a stressful one), fading in
    /// to a solid <see cref="AppColors.Terracotta"/> — the same muted orange already used for the
    /// gauge's low-battery tier and the charge-limit line — by 100%. Reuses Terracotta rather than
    /// introducing a new colour, matching this app's established "reuse the palette instead of
    /// adding near-duplicate hues" convention (see the Arc-gauge-fills comment in AppColors.cs).
    /// </summary>
    private static Color StressColor(int soc)
    {
        double intensity = Math.Clamp((soc - 40) / 60.0, 0, 1);
        var hot = AppColors.Terracotta;
        return Color.FromArgb((byte)(20 + intensity * 210), hot.R, hot.G, hot.B);
    }

    /// <summary>
    /// Builds the per-sample X coordinate for the compressed timeline: continuous data maps
    /// proportionally to its real duration, but every downtime gap (an index in
    /// <paramref name="gapBefore"/>) collapses to a small fixed-width break instead of its full
    /// span. Total break width is capped at 40% of the plot so many gaps can't starve the data of
    /// horizontal room.
    /// </summary>
    private static double[] BuildCompressedX(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, double w, double pad)
    {
        const double GapPx = 16;              // fixed on-screen width of one collapsed gap
        double plotW = Math.Max(w - pad * 2, 1);

        // Each non-gap step's clamped tick delta is computed ONCE here and reused below for both
        // the width budget and the actual placement — computing it twice (as an earlier version of
        // this method did) let the two copies disagree on a backward clock step (NTP correction,
        // manual clock change): the budget pass clamped a negative delta to 0 but the placement
        // pass didn't, so that sample could plot to the LEFT of its predecessor.
        var deltas = new long[samples.Count]; // deltas[i] = ticks since sample i-1; 0 for i=0 or a gap
        int gapCount = 0;
        double activeTicks = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if (gapBefore.Contains(i)) { gapCount++; continue; }
            deltas[i] = Math.Max(0, samples[i].AtUtc.Ticks - samples[i - 1].AtUtc.Ticks);
            activeTicks += deltas[i];
        }

        // Degenerate case: every inter-sample step is a gap (e.g. exactly two samples straddling one
        // restart, with nothing else loaded) — there's no active elapsed time to proportion by, so
        // pxPerTick would fall back to 0 and every point would cluster at the left edge, wasting most
        // of the canvas. Give the gap(s) the FULL width instead of capping them to 40 %; there's no
        // real data competing for the rest of it anyway.
        double totalGapPx = activeTicks > 0 ? Math.Min(gapCount * GapPx, plotW * 0.4) : plotW;
        double perGapPx   = gapCount > 0 ? totalGapPx / gapCount : 0;
        double pxPerTick  = activeTicks > 0 ? (plotW - gapCount * perGapPx) / activeTicks : 0;

        var xs = new double[samples.Count];
        double x = pad;
        xs[0] = x;
        for (int i = 1; i < samples.Count; i++)
        {
            x += gapBefore.Contains(i) ? perGapPx : deltas[i] * pxPerTick;
            xs[i] = x;
        }
        return xs;
    }

    /// <summary>Formats a right-axis power value as a plain number, e.g. "12W" or "0W".</summary>
    private static string FormatWatts(double watts) =>
        $"{Math.Round(watts, MidpointRounding.AwayFromZero):0}W";

    /// <summary>
    /// Splits a series into continuous runs of already-projected (x, y) points, breaking at both
    /// timeline gaps (per <paramref name="gapBefore"/> — indices preceded by a REAL gap in the
    /// original, pre-downsampling data; a plain Δt check here would misfire on ordinary stride
    /// spacing after reduction) and at null values (e.g. charge limit while Smart Charge is off).
    /// Shared by <see cref="DrawSeries"/> and <see cref="DrawGradientFill"/> so the line and its
    /// fill can never disagree about where a run starts or ends.
    /// </summary>
    private static List<List<Point>> CollectRuns(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, Func<BatterySample, double?> select,
        IReadOnlyList<double> xs, Func<double, double> projectY)
    {
        var runs = new List<List<Point>>();
        List<Point>? current = null;
        for (int i = 0; i < samples.Count; i++)
        {
            bool gap = i > 0 && gapBefore.Contains(i);
            var value = select(samples[i]);
            if (gap || value is null) { current = null; continue; }

            if (current is null) { current = []; runs.Add(current); }
            current.Add(new Point(xs[i], projectY(value.Value)));
        }
        return runs;
    }

    /// <summary>
    /// Draws one time-series as one or more shapes, one per continuous run from
    /// <see cref="CollectRuns"/>. Rounded joins and end caps throughout give every series a soft,
    /// modern look rather than sharp/square segment ends. When <paramref name="stepped"/> is set,
    /// each transition is drawn as a right-angle step (hold the previous value horizontally, then
    /// jump) instead of a curve — appropriate for a value that only changes in discrete jumps (the
    /// Smart Charge threshold), where anything smoothed would misleadingly suggest it drifted
    /// gradually between samples. Non-stepped series (SoC, power) are drawn as a monotone cubic
    /// Hermite curve (see <see cref="BuildMonotoneFigure"/>) instead of straight segments — this
    /// softens the "staircase" look from adjacent same/1%-apart integer samples without the
    /// overshoot risk of a plain spline, so a genuine plateau still reads as flat.
    /// </summary>
    private void DrawSeries(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, Func<BatterySample, double?> select,
        IReadOnlyList<double> xs, Func<double, double> projectY, Brush brush,
        bool dashed = false, bool primary = false, bool stepped = false)
    {
        double strokeThickness = primary ? PrimaryStrokeWidth : SecondaryStrokeWidth;

        foreach (var pts in CollectRuns(samples, gapBefore, select, xs, projectY))
        {
            if (pts.Count < 2) continue; // a lone point has nothing to connect to

            Microsoft.UI.Xaml.Shapes.Shape shape;
            if (stepped)
            {
                var polyline = new Microsoft.UI.Xaml.Shapes.Polyline();
                double lastY = pts[0].Y;
                polyline.Points.Add(pts[0]);
                for (int i = 1; i < pts.Count; i++)
                {
                    if (pts[i].Y != lastY) polyline.Points.Add(new Point(pts[i].X, lastY));
                    polyline.Points.Add(pts[i]);
                    lastY = pts[i].Y;
                }
                shape = polyline;
            }
            else
            {
                var geo = new PathGeometry();
                geo.Figures.Add(BuildMonotoneFigure(pts));
                shape = new Microsoft.UI.Xaml.Shapes.Path { Data = geo };
            }

            shape.StrokeThickness    = strokeThickness;
            shape.StrokeLineJoin     = PenLineJoin.Round;
            shape.StrokeStartLineCap = PenLineCap.Round;
            shape.StrokeEndLineCap   = PenLineCap.Round;
            shape.Stroke             = brush;
            if (dashed) shape.StrokeDashArray = [3, 2];
            SparklineCanvas.Children.Add(shape);
        }
    }

    /// <summary>
    /// Builds a smooth interpolating figure through <paramref name="pts"/> using monotone cubic
    /// Hermite tangents (see <see cref="MonotoneTangents"/>): the curve passes through every real
    /// sample (unlike an approximating/least-squares smooth) and is guaranteed not to overshoot
    /// past a flat run's own value the way a plain Catmull-Rom/natural spline can — the property
    /// that keeps a genuine plateau (e.g. sitting at 100% for hours) reading as flat instead of a
    /// gentle bump, while still rounding off the small staircases between adjacent integer-percent
    /// samples. When <paramref name="closeAtY"/> is set, the figure is closed down to that Y and
    /// back to the start X (for <see cref="DrawGradientFill"/>'s fill shape, so its edge matches
    /// the line above it exactly); otherwise the figure is left open, for the line itself.
    /// </summary>
    private static PathFigure BuildMonotoneFigure(IReadOnlyList<Point> pts, double? closeAtY = null)
    {
        var xs = new double[pts.Count];
        var ys = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++) { xs[i] = pts[i].X; ys[i] = pts[i].Y; }
        var m = MonotoneTangents(xs, ys);

        var figure = new PathFigure { StartPoint = pts[0], IsClosed = closeAtY.HasValue };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double h = xs[i + 1] - xs[i];
            figure.Segments.Add(new BezierSegment
            {
                // Standard Hermite→Bezier conversion for a curve linear-in-x: the tangent VECTOR
                // at each end is (h, slope*h), and the Bezier control points sit 1/3 of that
                // vector in from each endpoint.
                Point1 = new Point(xs[i]     + h / 3, ys[i]     + m[i]     * h / 3),
                Point2 = new Point(xs[i + 1] - h / 3, ys[i + 1] - m[i + 1] * h / 3),
                Point3 = pts[i + 1],
            });
        }

        if (closeAtY is { } bottomY)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(pts[^1].X, bottomY) });
            figure.Segments.Add(new LineSegment { Point = new Point(pts[0].X,  bottomY) });
        }

        return figure;
    }

    /// <summary>
    /// Fritsch-Carlson monotone cubic Hermite tangents for the points (xs[i], ys[i]). Interior
    /// tangents start at zero wherever the neighbouring secant slopes disagree in sign or either
    /// is exactly flat (a local extremum or the edge of a plateau) — the rest average the two
    /// neighbouring slopes. A clamping pass then caps every tangent so no segment's curve can rise
    /// or fall past its own two endpoints, which is what makes the interpolation monotone
    /// (overshoot-free) instead of a plain spline that can bulge past a flat run's edges.
    /// </summary>
    private static double[] MonotoneTangents(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        int n = xs.Count;
        var m = new double[n];
        if (n < 2) return m;

        var d = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            double dx = xs[k + 1] - xs[k];
            d[k] = dx > 0 ? (ys[k + 1] - ys[k]) / dx : 0;
        }

        static int Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        m[0]     = d[0];
        m[n - 1] = d[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            bool sameSign = d[k - 1] != 0 && Sign(d[k - 1]) == Sign(d[k]);
            m[k] = sameSign ? (d[k - 1] + d[k]) / 2 : 0;
        }

        for (int k = 0; k < n - 1; k++)
        {
            if (d[k] == 0) { m[k] = 0; m[k + 1] = 0; continue; }
            double alpha = m[k] / d[k];
            double beta  = m[k + 1] / d[k];
            if (alpha < 0) m[k] = 0;
            if (beta  < 0) m[k + 1] = 0;
            double s = alpha * alpha + beta * beta;
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[k]     = tau * alpha * d[k];
                m[k + 1] = tau * beta  * d[k];
            }
        }
        return m;
    }

    /// <summary>
    /// Draws a soft gradient shade under a series' segments (AppControl-style): a pre-built
    /// (cached, never allocated here) brush fading from the line's colour to fully transparent at
    /// the plot's bottom edge. Uses the identical <see cref="CollectRuns"/> boundaries as
    /// <see cref="DrawSeries"/> so the fill never bridges a downtime hole, and the SAME monotone
    /// curve (<see cref="BuildMonotoneFigure"/>) as its upper edge so the fill boundary never
    /// diverges from the smoothed line drawn on top of it. Must be called before the corresponding
    /// line series so the fill layers underneath it.
    /// </summary>
    private void DrawGradientFill(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, Func<BatterySample, double?> select,
        IReadOnlyList<double> xs, Func<double, double> projectY, double plotBottomY,
        LinearGradientBrush fill)
    {
        foreach (var pts in CollectRuns(samples, gapBefore, select, xs, projectY))
        {
            if (pts.Count < 2) continue;

            var geo = new PathGeometry();
            geo.Figures.Add(BuildMonotoneFigure(pts, closeAtY: plotBottomY));
            SparklineCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geo, Fill = fill });
        }
    }

    /// <summary>
    /// Draws the time-split break for a collapsed downtime gap: two short parallel diagonal strokes
    /// (the familiar "broken axis" mark) spanning the plot height below the reserved label band,
    /// plus a high-contrast pill label of how much time the break stands in for (e.g. "13h", "2d").
    /// Makes it obvious the timeline was cut here — and by roughly how much — rather than silently
    /// skipping it or drawing it full-length.
    /// </summary>
    private void DrawGapBreak(double x, TimeSpan skipped, double canvasWidth, double canvasHeight, double pad)
    {
        var stroke = SparklineStartLabel.Foreground;
        double linesTop = pad + GapLabelBandHeight;

        foreach (double dx in GapStrokeOffsets)
            SparklineCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x + dx - 2, Y1 = canvasHeight - pad,
                X2 = x + dx + 2, Y2 = linesTop,
                Stroke             = stroke,
                StrokeThickness    = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                Opacity            = 0.75,
            });

        // "How much time was skipped" pill, centred on the break inside its own reserved band —
        // never overlapped by the strokes, which start below it.
        AddAnnotationPill(x, pad, FormatGap(skipped), canvasWidth, fontSize: 11);
    }

    /// <summary>
    /// Adds a small opaque "pill" (rounded solid-background + bold text) to the sparkline canvas,
    /// horizontally centred on <paramref name="centerX"/> with its top edge at <paramref name="top"/>
    /// and clamped to stay within the canvas. A plain themed-foreground TextBlock alone wasn't
    /// enough contrast for annotations that sit on top of coloured lines and a gradient fill — a
    /// genuinely opaque background (AnnotationPillBackgroundRef, NOT the card's own translucent
    /// ControlFillColorDefaultBrush) guarantees legibility regardless of what's beneath. Returns the
    /// created <see cref="Border"/> so transient callers (e.g. the hover crosshair, TODO #27, which
    /// must remove its own pill on every pointer move without touching the rest of the canvas) can
    /// track and remove it later; per-render callers (gap breaks, min/max markers) just ignore it.
    /// </summary>
    private Border AddAnnotationPill(double centerX, double top, string text, double canvasWidth, double fontSize)
    {
        var label = new TextBlock
        {
            Text       = text,
            FontSize   = fontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = GraphLabelPillTextBrushRef.Foreground,
        };
        var pill = new Border
        {
            Background   = AnnotationPillBackgroundRef.Background,
            CornerRadius = new CornerRadius(5),
            Padding      = new Thickness(4, 1, 4, 1),
            Child        = label,
        };

        // Estimated, not measured: Measure() requires a layout pass (real cost on every gap-break
        // and every min/max marker, every render) and, called here — before the pill is ever added
        // to SparklineCanvas.Children — risks returning a degenerate 0×0 DesiredSize on the very
        // first render (before the control's first layout pass).
        double estimatedWidth = text.Length * fontSize * PillCharWidthEm + PillPaddingX;
        double left = Math.Clamp(centerX - estimatedWidth / 2, 0, Math.Max(0, canvasWidth - estimatedWidth));
        Canvas.SetLeft(pill, left);
        // Clamp vertically too: a min/max marker near 100% SoC places `top` just above the dot,
        // which is already near the plot's own top edge — without this, the pill can compute a
        // negative Top and render clipped above (or entirely outside) the canvas.
        Canvas.SetTop(pill, Math.Max(0, top));
        SparklineCanvas.Children.Add(pill);
        return pill;
    }

    /// <summary>
    /// Formats a skipped-time gap compactly for the break label, e.g. "45m", "13h", "2d", "1w".
    /// Rounds each tier's OWN value before comparing it to that tier's boundary — checking the raw
    /// (unrounded) value against 60/24/7 let a value just under a boundary round UP to the boundary
    /// itself instead of promoting to the next unit (e.g. 59.6 minutes rounding to "60m" rather than
    /// "1h", or 23.6 hours to "24h" rather than "1d").
    /// </summary>
    private static string FormatGap(TimeSpan gap)
    {
        int minutes = (int)Math.Round(gap.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 60) return $"{Math.Max(1, minutes)}m";
        int hours = (int)Math.Round(gap.TotalHours, MidpointRounding.AwayFromZero);
        if (hours < 24) return $"{hours}h";
        int days = (int)Math.Round(gap.TotalDays, MidpointRounding.AwayFromZero);
        if (days < 7) return $"{days}d";
        return $"{(int)Math.Round(gap.TotalDays / 7, MidpointRounding.AwayFromZero)}w";
    }

    /// <summary>
    /// Annotates the highest and lowest points of the sparkline with a neutral dot and a
    /// percentage label — position and the label already say "extreme", so the dot no longer
    /// needs red/green colour coding (that read as an alarm alongside the graph's own colours).
    /// No-op when the visible range is flat (nothing meaningful to mark).
    /// </summary>
    private void DrawSparklineMarkers(
        IReadOnlyList<BatterySample> samples, double canvasWidth,
        IReadOnlyList<double> xs, Func<double, double> projectY)
    {
        int maxPct = int.MinValue, minPct = int.MaxValue, maxIdx = 0, minIdx = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Soc > maxPct) { maxPct = samples[i].Soc; maxIdx = i; }
            if (samples[i].Soc < minPct) { minPct = samples[i].Soc; minIdx = i; }
        }
        if (maxPct == minPct) return;

        AddMarker(xs[maxIdx], maxPct);
        AddMarker(xs[minIdx], minPct);

        void AddMarker(double cx, int pct)
        {
            const double dotR = 3;
            double cy = projectY(pct);

            // Same neutral, theme-aware brush as the axis/time labels.
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = dotR * 2, Height = dotR * 2, Fill = SparklineStartLabel.Foreground,
            };
            Canvas.SetLeft(dot, cx - dotR);
            Canvas.SetTop(dot,  cy - dotR);
            SparklineCanvas.Children.Add(dot);

            // Pill (not a plain themed-foreground label) so the percentage stays legible over the
            // gradient fill and lines it sits above — ~20px clears the dot plus the pill's own padding.
            AddAnnotationPill(cx, cy - dotR - 20, $"{pct}%", canvasWidth, fontSize: 13);
        }
    }
}
