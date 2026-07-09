using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
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
    // A hole in the timeline bigger than this (3x the sample cadence) is treated as downtime (app
    // was closed/crashed) rather than just a slightly-late sample, and gets a gap marker instead
    // of a connecting line. Derived from BatteryHistoryService.SampleIntervalSeconds — the single
    // source of truth for the cadence — instead of a second, independently-hardcoded number.
    private static readonly TimeSpan GapThreshold =
        TimeSpan.FromSeconds(BatteryHistoryService.SampleIntervalSeconds * 3);

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
    /// Raised when the user clicks the "Expand" button. The control has no reference to the app
    /// or window-management — it only signals intent; the host decides what "expand" means.
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
    /// Shows/hides the "⤢ Expand" trigger. The pop-out window hosts this same control to show the
    /// already-expanded graph, where an "Expand" button pointing at itself would be meaningless.
    /// </summary>
    public bool ShowExpandButton
    {
        get => ExpandButton.Visibility == Visibility.Visible;
        set => ExpandButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public BatteryHistoryGraphControl()
    {
        InitializeComponent();

        // Legend swatch colours never change — assign once instead of every render.
        LegendSocSwatch.Background   = AppColors.GaugeHighBrush;
        LegendLimitSwatch.Background = AppColors.HistoryLimitBrush;
        LegendPowerSwatch.Background = AppColors.HistoryPowerBrush;

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

    private void OnExpandButtonClick(object sender, RoutedEventArgs e) =>
        ExpandRequested?.Invoke(this, EventArgs.Empty);

    // Restarts the debounce on every intermediate resize event; Render() only runs once the size
    // has settled for _resizeRenderTimer's interval.
    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeRenderTimer.Stop();
        _resizeRenderTimer.Start();
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

    /// <summary>Formats a history span as a left-edge axis label, e.g. "−42m" or "−1h 05m".</summary>
    private static string FormatAgo(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "−<1m";
        return span.TotalHours >= 1
            ? $"−{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"−{span.Minutes}m";
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
        for (int i = 1; i < samples.Count; i++)
            if (gapBefore.Contains(i))
                DrawGapBreak((xs[i - 1] + xs[i]) / 2, samples[i].AtUtc - samples[i - 1].AtUtc, w, h, pad);

        DrawSparklineMarkers(samples, w, xs, ProjectYPct);
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
    /// Draws one time-series as one or more polyline segments, splitting at both timeline gaps
    /// (per <paramref name="gapBefore"/> — indices preceded by a REAL gap in the original,
    /// pre-downsampling data; a plain Δt check here would misfire on ordinary stride spacing after
    /// reduction) and at null values (e.g. charge limit while Smart Charge is off) so no line is
    /// drawn across a period with no meaningful value. Rounded joins and end caps throughout give
    /// every series a soft, modern look rather than sharp/square segment ends. When
    /// <paramref name="stepped"/> is set, each transition is drawn as a right-angle step (hold the
    /// previous value horizontally, then jump) instead of a straight diagonal — appropriate for a
    /// value that only changes in discrete jumps (the Smart Charge threshold), where a diagonal
    /// would misleadingly suggest it drifted gradually between samples.
    /// </summary>
    private void DrawSeries(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, Func<BatterySample, double?> select,
        IReadOnlyList<double> xs, Func<double, double> projectY, Brush brush,
        bool dashed = false, bool primary = false, bool stepped = false)
    {
        Microsoft.UI.Xaml.Shapes.Polyline? segment = null;
        double lastY = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            bool gap = i > 0 && gapBefore.Contains(i);
            var value = select(s);

            if (gap || value is null)
            {
                segment = null; // end the current run; nothing to draw for a missing value
                continue;
            }

            double x = xs[i];
            double y = projectY(value.Value);

            if (segment is null)
            {
                segment = new Microsoft.UI.Xaml.Shapes.Polyline
                {
                    StrokeThickness    = primary ? PrimaryStrokeWidth : SecondaryStrokeWidth,
                    StrokeLineJoin     = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap   = PenLineCap.Round,
                    Stroke             = brush,
                };
                if (dashed) segment.StrokeDashArray = [3, 2];
                SparklineCanvas.Children.Add(segment);
            }
            else if (stepped && y != lastY)
            {
                segment.Points.Add(new Point(x, lastY));
            }
            segment.Points.Add(new Point(x, y));
            lastY = y;
        }
    }

    /// <summary>
    /// Draws a soft gradient shade under a series' segments (AppControl-style): a pre-built
    /// (cached, never allocated here) brush fading from the line's colour to fully transparent at
    /// the plot's bottom edge. Uses the identical <paramref name="gapBefore"/> boundaries as
    /// <see cref="DrawSeries"/> so the fill never bridges a downtime hole. Must be called before
    /// the corresponding line series so the fill layers underneath it.
    /// </summary>
    private void DrawGradientFill(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, Func<BatterySample, double?> select,
        IReadOnlyList<double> xs, Func<double, double> projectY, double plotBottomY,
        LinearGradientBrush fill)
    {
        List<Point>? segment = null;
        void FlushSegment()
        {
            if (segment is not { Count: >= 2 } pts) { segment = null; return; }
            var polygon = new Microsoft.UI.Xaml.Shapes.Polygon { Fill = fill };
            foreach (var p in pts) polygon.Points.Add(p);
            // Close the shape along the plot's bottom edge back to the segment's start-x.
            polygon.Points.Add(new Point(pts[^1].X, plotBottomY));
            polygon.Points.Add(new Point(pts[0].X,  plotBottomY));
            SparklineCanvas.Children.Add(polygon);
            segment = null;
        }

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            bool gap = i > 0 && gapBefore.Contains(i);
            var value = select(s);

            if (gap || value is null) { FlushSegment(); continue; }

            segment ??= [];
            segment.Add(new Point(xs[i], projectY(value.Value)));
        }
        FlushSegment();
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
    /// ControlFillColorDefaultBrush) guarantees legibility regardless of what's beneath.
    /// </summary>
    private void AddAnnotationPill(double centerX, double top, string text, double canvasWidth, double fontSize)
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
            CornerRadius = new CornerRadius(3),
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
