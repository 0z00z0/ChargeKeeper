using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>The battery-history sparkline (SoC / charge-limit / power, compressed-gap time axis,
/// min/max markers) and its time-scale selector. The time-scale setting and the loaded sample window
/// are process-wide, so the tray popup and the pop-out are views onto one graph.</summary>
public sealed partial class BatteryHistoryGraphControl : UserControl
{
    // Shared with the drain-anomaly gate, and read fresh so a settings change needs no restart.
    private static TimeSpan GapThreshold => BatteryHistoryService.DowntimeThreshold;

    private const double PrimaryStrokeWidth   = 2.5;
    private const double SecondaryStrokeWidth = 2.0;

    // Reserved at the top of the plot for the gap-break label, so the break strokes start below it.
    private const double GapLabelBandHeight = 15;

    // Hoisted so this small array isn't reallocated once per gap on every render.
    private static readonly double[] GapStrokeOffsets = [-2.5, 1.5];

    // Rough glyph width for the SemiBold UI font at 1em — enough to centre a short pill without a Measure().
    private const double PillCharWidthEm = 0.62;
    private const double PillPaddingX    = 8; // matches Padding(4,_,4,_) below, both sides combined

    // Debounces the live-resize repaint: SizeChanged fires on every intermediate pixel during a drag.
    private readonly DispatcherTimer _resizeRenderTimer;

    // Set on Unloaded so a background LoadWindow callback can't touch dead XAML elements.
    private bool _unloaded;

    /// <summary>Raised when the user asks to expand the graph. The host decides what that means.</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>Plot canvas row height: a fixed 126 in the dashboard, "*" in the pop-out.</summary>
    public GridLength PlotAreaHeight
    {
        get => CanvasRow.Height;
        set => CanvasRow.Height = value;
    }

    /// <summary>Shows the "⛶" glyph and gates double-click-to-expand; false inside the pop-out itself.</summary>
    public bool ShowExpandButton
    {
        get => ExpandGlyph.Visibility == Visibility.Visible;
        set => ExpandGlyph.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the compressed-gap break and duration pill; false where it would crowd the plot.</summary>
    public bool ShowGapMarkers { get; set; } = true;

    /// <summary>Shows the SoC stress heat strip; collapsing the bar collapses its Auto row too.</summary>
    public bool ShowStressHeatmap
    {
        get => StressHeatmapBar.Visibility == Visibility.Visible;
        set => StressHeatmapBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the hover crosshair; false in the narrow dashboard popup, where the readout
    /// pill would cover the lines it tracks.</summary>
    public bool ShowCrosshair { get; set; } = true;

    // Cached per Render() so a pointer move needn't redo the downsample/compressed-x/projection pipeline.
    private IReadOnlyList<BatterySample>? _hoverSamples;
    private IReadOnlyList<double>?        _hoverXs;
    private Func<double, double>?         _hoverProjectYPct;

    // The charge line's fitted runs, so the hover dot can sit on the drawn curve. The line is fitted
    // through one knot per level, not through every sample, so a raw reading is not on it.
    private IReadOnlyList<RunFit>? _hoverCurves;

    // Reused across pointer moves; Render()'s Children.Clear() detaches them, hence _crosshairAttached.
    private Microsoft.UI.Xaml.Shapes.Line?    _crosshairLine;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _crosshairDot;
    private Border?                            _crosshairPill;
    private TextBlock?                         _crosshairPillText;
    private bool                              _crosshairAttached;

    // Sample being traced, so a move within one sample touches nothing. -1 = none; reset by Render().
    private int _lastHoverIndex = -1;

    public BatteryHistoryGraphControl()
    {
        InitializeComponent();

        // The SoC swatch uses the same brush as the SoC line, so the legend can't desync from it.
        LegendSocSwatch.Background   = AppColors.HistorySocBrush;
        LegendLimitSwatch.Background = AppColors.HistoryLimitBrush;
        LegendPowerSwatch.Background = AppColors.HistoryPowerBrush;

        ExpandGlyph.Foreground = AppColors.ExpandGlyphBrush;
        ExpandGlyph.Background = AppColors.ExpandGlyphBackgroundBrush;

        // App.StartHistorySampling already loaded this span, so the first render needs no LoadWindow.
        SetSelectedScaleButton(SettingsService.Current.GraphTimeScale);

        // Narrow race: the first render can beat that background disk load, leaving the window empty.
        if (BatteryHistoryService.CurrentWindow().Count == 0)
        {
            Task.Run(() =>
            {
                BatteryHistoryService.LoadWindow(SettingsService.Current.GraphTimeScale.ToTimeSpan());
                RunOnUi(Render);
            });
        }

        _resizeRenderTimer          = new() { Interval = TimeSpan.FromMilliseconds(120) };
        _resizeRenderTimer.Tick    += (_, _) => { _resizeRenderTimer.Stop(); Render(); };

        Unloaded += (_, _) =>
        {
            _unloaded = true;
            _resizeRenderTimer.Stop();
        };
    }

    /// <summary>Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch: an
    /// exception inside a raw TryEnqueue callback bypasses Application.UnhandledException and tears
    /// the process down with nothing logged.</summary>
    private void RunOnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_unloaded) return;   // stale callback after teardown
        try { action(); }
        catch (Exception ex) { AppLog.Error("BatteryHistoryGraphControl.RunOnUi", ex); }
    });

    private void OnExpandGlyphClick(object sender, RoutedEventArgs e) =>
        ExpandRequested?.Invoke(this, EventArgs.Empty);

    // Gated so a double-click inside the already-open pop-out doesn't re-signal itself.
    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ShowExpandButton)
            ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeRenderTimer.Stop();
        _resizeRenderTimer.Start();
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ShowCrosshair) return;
        if (_hoverSamples is not { Count: > 1 } samples) return;
        if (_hoverXs is not { } xs || _hoverProjectYPct is not { } projectY) return;

        double x = e.GetCurrentPoint(SparklineCanvas).Position.X;

        int nearest = MonotoneSearch.NearestIndex(xs, x);
        // _crosshairAttached is false after a Render, so re-add even when the index matches.
        if (nearest == _lastHoverIndex && _crosshairAttached) return;
        _lastHoverIndex = nearest;

        // Dot on the drawn curve; the pill still reports the reading, which the curve need not pass
        // through. Falling back to the reading covers a hover inside a collapsed gap, where the
        // curve is broken and there is nothing to sit on.
        double dotX = xs[nearest];
        double dotY = CurveYAt(dotX) ?? projectY(samples[nearest].Soc);
        DrawCrosshair(samples[nearest], dotX, dotY);
    }

    private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e) => ClearCrosshair();

    /// <summary>
    /// Restores the time-scale row and legend to full opacity while the pointer is anywhere over the
    /// graph card; <see cref="OnGraphCardPointerExited"/> fades them back once it leaves. PointerEntered
    /// and PointerExited fire once per crossing of RootGrid's own bounds, not per child, so moving
    /// between the buttons and the sparkline inside it does not retrigger either.
    /// </summary>
    private void OnGraphCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        TimeScalePanel.Opacity = 1.0;
        LegendPanel.Opacity    = 1.0;
    }

    private void OnGraphCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        double rest = (double)Resources["GraphChromeRestOpacity"];
        TimeScalePanel.Opacity = rest;
        LegendPanel.Opacity    = rest;
    }

    /// <summary>Detaches only the crosshair's own elements; a Children.Clear() would wipe the chart.</summary>
    private void ClearCrosshair()
    {
        if (_crosshairAttached)
        {
            if (_crosshairLine is { } l) SparklineCanvas.Children.Remove(l);
            if (_crosshairDot  is { } d) SparklineCanvas.Children.Remove(d);
            if (_crosshairPill is { } p) SparklineCanvas.Children.Remove(p);
            _crosshairAttached = false;
        }
        _lastHoverIndex = -1;
    }

    /// <summary>Builds the three reusable crosshair elements once; their colours never change.</summary>
    private void EnsureCrosshairElements()
    {
        _crosshairLine ??= new Microsoft.UI.Xaml.Shapes.Line
        {
            Stroke          = SparklineStartLabel.Foreground,   // same neutral brush as axis labels
            StrokeThickness = 1,
            Opacity         = 0.5,
        };
        _crosshairDot ??= new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width           = 6,
            Height          = 6,
            Fill            = AppColors.HistorySocBrush,
            Stroke          = SparklineStartLabel.Foreground,
            StrokeThickness = 1,
        };
        if (_crosshairPill is null)
        {
            _crosshairPillText = new TextBlock
            {
                FontSize   = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = GraphLabelPillTextBrushRef.Foreground,
            };
            _crosshairPill = new Border
            {
                Background   = AnnotationPillBackgroundRef.Background,
                CornerRadius = new CornerRadius(5),
                Padding      = new Thickness(4, 1, 4, 1),
                Child        = _crosshairPillText,
            };
        }
    }

    private void DrawCrosshair(BatterySample sample, double x, double y)
    {
        EnsureCrosshairElements();

        double h = SparklineCanvas.ActualHeight;
        _crosshairLine!.X1 = x;
        _crosshairLine.X2  = x;
        _crosshairLine.Y1  = 0;
        _crosshairLine.Y2  = h;

        const double dotR = 3;
        Canvas.SetLeft(_crosshairDot!, x - dotR);
        Canvas.SetTop(_crosshairDot!,  y - dotR);

        string rate = PowerFormat.SignedRate(sample.PowerMw) ?? "0 W";
        string label = $"{sample.AtUtc.ToLocalTime():t} · {sample.Soc}% · {rate}";
        _crosshairPillText!.Text = label;
        Canvas.SetLeft(_crosshairPill!, PillLeft(x, label, fontSize: 12, SparklineCanvas.ActualWidth));
        Canvas.SetTop(_crosshairPill!,  Math.Max(0, y - 20 - 13));

        if (!_crosshairAttached)
        {
            // Added after the series, which Render() drew first, so the crosshair sits on top.
            SparklineCanvas.Children.Add(_crosshairLine);
            SparklineCanvas.Children.Add(_crosshairDot);
            SparklineCanvas.Children.Add(_crosshairPill);
            _crosshairAttached = true;
        }
    }

    /// <summary>Applies the clicked time-scale button, whose <c>Tag</c> names the <see cref="GraphTimeScale"/>.</summary>
    private void OnTimeScaleButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagName } ||
            !Enum.TryParse<GraphTimeScale>(tagName, out var scale))
            return;

        SettingsService.Update(s => s.GraphTimeScale = scale);
        SetSelectedScaleButton(scale);   // highlight immediately; don't wait on the disk read below

        // LoadWindow does a full CSV scan — real disk I/O that must not run on the UI thread.
        Task.Run(() =>
        {
            BatteryHistoryService.LoadWindow(scale.ToTimeSpan());
            AppLog.Info($"Time-scale changed to {scale}.");

            // Either host can change the scale, so a slower earlier load can finish after a faster
            // later one and repaint stale data under a button already showing the newer selection.
            if (BatteryHistoryService.CurrentSpan == scale.ToTimeSpan())
                RunOnUi(Render);
        });
    }

    /// <summary>Highlights the button for <paramref name="scale"/>; deselecting clears the local value so its style wins.</summary>
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

    /// <summary>Formats a history span as a left-edge axis label, e.g. "−42m", "−1h 05m", "−3d 07h".
    /// Round the total, then split it: rounding each part separately can produce "1h 60m".</summary>
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

    /// <summary>Redraws the sparkline, reading the canvas's own size fresh so it fits any host.</summary>
    public void Render()
    {
        SparklineCanvas.Children.Clear();
        // The Clear() above detached the reused crosshair elements along with the series.
        _crosshairAttached = false;
        _lastHoverIndex    = -1;

        var samples = BatteryHistoryService.CurrentWindow();
        if (samples.Count < 2)
        {
            SparklineStartLabel.Text  = "—";
            SparklineEndLabel.Text    = "—";
            RightAxisTopLabel.Text    = "—";
            RightAxisMidLabel.Text    = "—";
            RightAxisBottomLabel.Text = "—";
            // StressHeatmapBar is outside SparklineCanvas.Children, so the Clear() left it stale.
            StressHeatmapBar.Fill = null;
            // Drop the hover-crosshair cache too — a pointer move landing after this would
            // otherwise trace a now-stale sample set against a blank canvas.
            _hoverSamples = null;
            _hoverXs      = null;
            _hoverCurves  = null;
            ClearCrosshair();
            return;
        }

        // Canvas size is known once the element has been measured; guard against first render.
        double w = SparklineCanvas.ActualWidth;
        double h = SparklineCanvas.ActualHeight;
        if (w < 4 || h < 4)
        {
            _hoverSamples = null;
            _hoverXs      = null;
            _hoverCurves  = null;
            ClearCrosshair();
            return;
        }

        // Downsample to ~one point per horizontal pixel. Gap detection runs against the ORIGINAL
        // timestamps: after reduction, ordinary stride spacing is indistinguishable from downtime.
        int maxPoints = Math.Max(200, (int)(w * 2));
        var reduced   = HistoryDownsampler.Reduce(samples, maxPoints, GapThreshold);
        samples = reduced.Samples;
        var gapBefore = reduced.GapBeforeIndices;

        // Compressed x-axis: continuous data fills the plot width, each downtime gap collapses to a
        // fixed-width break. On a linear time axis one overnight off-period crushed the trace to a sliver.
        DateTime nowUtc = DateTime.UtcNow;
        const double pad = 4;

        // Index-based: two reduced points can be far apart in ticks purely from stride, so a ticks→X
        // function couldn't tell a gap from ordinary spacing. Shared by series, breaks and markers.
        double[] xs = BuildCompressedX(samples, gapBefore, w, pad);

        // Leading downtime is collapsed, so the oldest loaded sample can be newer than the selected span.
        SparklineStartLabel.Text = FormatAgo(nowUtc - samples[0].AtUtc);
        var sinceLast = nowUtc - samples[^1].AtUtc;
        SparklineEndLabel.Text   = sinceLast <= GapThreshold ? "now" : FormatAgo(sinceLast);

        // Left % axis, shared by SoC and charge limit; inverted because canvas Y grows downward.
        double ProjectYPct(double pct) => (h - pad) - pct / 100.0 * (h - pad * 2);

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

        // Read once: SettingsService.Current can be swapped between reads, and the two settings are
        // independent, so separate reads could pair one's old value with the other's new one.
        var settings   = SettingsService.Current;
        var lineMode   = settings.GraphLineColouring;
        bool drawShading = GraphColouring.ShouldShade(lineMode, settings.GraphShadingEnabled);

        // SoC — the headline series. Drawn before limit/power so those sit on top where they cross.
        // The line takes the setting, per run because each run is its own path with its own bounding
        // box; the fade beneath it keeps the accent whatever the line does.
        var socRuns  = ThinToLevels(CollectRuns(samples, gapBefore, s => s.Soc, xs, ProjectYPct), samples);
        var socFits  = socRuns.Select(run => FitRun(run.Points)).ToList();
        _hoverCurves = socFits;
        DrawSocFillAndLine(
            socRuns, socFits,
            run => BuildSocLineBrush(lineMode, samples, xs, run.Indices),
            AppColors.HistorySocFillBrush, plotBottomY: h - pad, drawShading);

        // Charge limit (Smart Charge Stop threshold), left axis; null while Smart Charge is off.
        // Stepped because the threshold only jumps — a ramp would suggest it drifted between samples.
        DrawSeries(samples, gapBefore, s => s.LimitPct, xs, ProjectYPct, AppColors.HistoryLimitBrush, stepped: true);

        // Charge power — dotted, right axis: distinct by dash pattern and colour as a different scale.
        DrawSeries(samples, gapBefore, s => s.PowerMw / 1000.0, xs, ProjectYWatts, AppColors.HistoryPowerBrush, dashed: true);

        // Only the marker is gated; the gaps are still collapsed and still break the line either way.
        if (ShowGapMarkers)
            for (int i = 1; i < samples.Count; i++)
                if (gapBefore.Contains(i))
                    DrawGapBreak((xs[i - 1] + xs[i]) / 2, samples[i].AtUtc - samples[i - 1].AtUtc, w, h, pad);

        // Marked on the knots, not on every sample: a marker placed at a dropped sample's x would
        // float off the line. Thinning keeps every distinct level, so the extremes are unchanged.
        DrawSparklineMarkers(samples, socRuns.SelectMany(run => run.Indices).ToList(), w, xs, ProjectYPct);

        if (ShowStressHeatmap)
            DrawStressHeatmap(samples, xs, w);
    }

    /// <summary>The charge line's brush for one run: the fixed accent, or — where the setting colours
    /// the line — a gradient over that run's own points. Mapped relative to the painted path's
    /// bounding box, as every other gradient in the app is; a stroke's absolute coordinate space is
    /// the shape's, not the plot's, so an absolute axis spanning the plot leaves the whole stroke on
    /// one stop. Stops come from <see cref="GraphColouring.LineStops"/> so the colour decision has one
    /// home.</summary>
    private static Brush BuildSocLineBrush(
        GraphLineColouring mode, IReadOnlyList<BatterySample> samples, IReadOnlyList<double> xs,
        IReadOnlyList<int> runIndices)
    {
        if (!GraphColouring.VariesByPoint(mode)) return AppColors.HistorySocBrush;

        // The colour a point falls back to, taken from the accent brush itself so the two cannot drift.
        uint accent = AppColors.ToPacked(AppColors.HistorySocBrush.Color);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (var stop in GraphColouring.LineStops(mode, samples, xs, runIndices, accent, MaxLineStops))
            brush.GradientStops.Add(new GradientStop
            {
                Offset = stop.Offset,
                Color  = AppColors.FromPacked(stop.Argb),
            });
        return brush;
    }

    /// <summary>Cap on gradient stops per run, matching the stress strip's.</summary>
    private const int MaxLineStops = 200;

    /// <summary>Fills the stress strip with a gradient whose stops sit at the same x as the SoC line's
    /// points, so it can't drift out of alignment. Stops are strided to a cap.</summary>
    private void DrawStressHeatmap(IReadOnlyList<BatterySample> samples, IReadOnlyList<double> xs, double w)
    {
        const int MaxStops = 200;
        int step = Math.Max(1, samples.Count / MaxStops);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < samples.Count; i += step)
            brush.GradientStops.Add(new GradientStop
            {
                Offset = Math.Clamp(xs[i] / w, 0, 1),
                Color  = StressColor(samples[i].Soc),
            });
        // Close at the final sample so striding can't leave the right edge on an extrapolated colour.
        if (step > 1 && (samples.Count - 1) % step != 0)
            brush.GradientStops.Add(new GradientStop
            {
                Offset = Math.Clamp(xs[^1] / w, 0, 1),
                Color  = StressColor(samples[^1].Soc),
            });
        StressHeatmapBar.Fill = brush;
    }

    /// <summary>Maps SoC to a stress colour: transparent at and below 40%, fading in to a solid
    /// <see cref="AppColors.Terracotta"/> by 100% rather than adding a near-duplicate hue.</summary>
    private static Color StressColor(int soc)
    {
        double intensity = Math.Clamp((soc - 40) / 60.0, 0, 1);
        var hot = AppColors.Terracotta;
        return Color.FromArgb((byte)(20 + intensity * 210), hot.R, hot.G, hot.B);
    }

    /// <summary>Builds the per-sample X for the compressed timeline: continuous data maps
    /// proportionally to its real duration, each downtime gap to a fixed-width break. Total break
    /// width is capped at 40% so many gaps can't starve the data of horizontal room.</summary>
    private static double[] BuildCompressedX(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, double w, double pad)
    {
        const double GapPx = 16;              // fixed on-screen width of one collapsed gap
        double plotW = Math.Max(w - pad * 2, 1);

        // One clamped delta per step, reused for both the width budget and the placement: two copies
        // disagreed on a backward clock step, plotting a sample to the LEFT of its predecessor.
        var deltas = new long[samples.Count]; // deltas[i] = ticks since sample i-1; 0 for i=0 or a gap
        int gapCount = 0;
        double activeTicks = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if (gapBefore.Contains(i)) { gapCount++; continue; }
            deltas[i] = Math.Max(0, samples[i].AtUtc.Ticks - samples[i - 1].AtUtc.Ticks);
            activeTicks += deltas[i];
        }

        // Every step a gap: with no active elapsed time to proportion by, pxPerTick would be 0 and
        // every point would cluster at the left edge, so give the gaps the full width instead.
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

    /// <summary>One continuous stretch of a series: the projected points and, in step with them, the
    /// index each came from. The indices let a caller colour a run point by point without re-deriving
    /// which samples survived the run split.</summary>
    private readonly record struct SeriesRun(List<Point> Points, List<int> Indices);

    /// <summary>Splits a series into continuous runs of projected points, breaking at timeline gaps
    /// (by index — a Δt check would misfire on ordinary stride spacing) and at null values. Shared by
    /// the line and its fill so the two can't disagree about where a run starts or ends.</summary>
    private static List<SeriesRun> CollectRuns(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, Func<BatterySample, double?> select,
        IReadOnlyList<double> xs, Func<double, double> projectY)
    {
        var runs = new List<SeriesRun>();
        List<Point>? points  = null;
        List<int>?   indices = null;
        for (int i = 0; i < samples.Count; i++)
        {
            bool gap = i > 0 && gapBefore.Contains(i);
            var value = select(samples[i]);
            if (gap || value is null) { points = null; indices = null; continue; }

            if (points is null || indices is null)
            {
                points  = [];
                indices = [];
                runs.Add(new SeriesRun(points, indices));
            }
            points.Add(new Point(xs[i], projectY(value.Value)));
            indices.Add(i);
        }
        return runs;
    }

    /// <summary>Draws one secondary series as a shape per continuous run. <paramref name="stepped"/>
    /// draws right-angle steps for a value that only changes in discrete jumps; otherwise the run is
    /// a monotone curve, which rounds off the integer staircase without bulging a real plateau.</summary>
    private void DrawSeries(
        IReadOnlyList<BatterySample> samples, IReadOnlySet<int> gapBefore, Func<BatterySample, double?> select,
        IReadOnlyList<double> xs, Func<double, double> projectY, Brush brush,
        bool dashed = false, bool stepped = false)
    {
        const double strokeThickness = SecondaryStrokeWidth;

        foreach (var run in CollectRuns(samples, gapBefore, select, xs, projectY))
        {
            var pts = run.Points;
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
                geo.Figures.Add(MonotonePath.Figure(pts, FitRun(pts).Tangents));
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

    /// <summary>One run's knot coordinates and its monotone tangents, kept together so the hover dot
    /// can be evaluated on the same fit the line was drawn from.</summary>
    private readonly record struct RunFit(double[] Xs, double[] Ys, double[] Tangents);

    private static RunFit FitRun(IReadOnlyList<Point> pts)
    {
        var xs = new double[pts.Count];
        var ys = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++) { xs[i] = pts[i].X; ys[i] = pts[i].Y; }
        return new RunFit(xs, ys, MonotoneCubic.Tangents(xs, ys));
    }

    /// <summary>The drawn charge line's y at canvas <paramref name="x"/>, or null where no run covers
    /// it — inside a collapsed gap, or beyond the first and last knot.</summary>
    private double? CurveYAt(double x)
    {
        if (_hoverCurves is not { } fits) return null;
        foreach (var fit in fits)
        {
            if (fit.Xs.Length < 2) continue;
            if (x < fit.Xs[0] || x > fit.Xs[^1]) continue;
            return MonotoneCubic.Evaluate(fit.Xs, fit.Ys, fit.Tangents, x);
        }
        return null;
    }

    /// <summary>Reduces each charge-line run to one knot per level, giving the fit room to spread a
    /// single-percent step across the time that level held. Both sides of a power-state change are
    /// held back: <see cref="GraphColouring"/> keys the line's colour on the state as well as the
    /// level, and a state that flips mid-plateau would otherwise be thinned away.</summary>
    private static List<SeriesRun> ThinToLevels(
        IReadOnlyList<SeriesRun> runs, IReadOnlyList<BatterySample> samples)
    {
        var thinned = new List<SeriesRun>(runs.Count);
        foreach (var run in runs)
        {
            var ys = new double[run.Points.Count];
            for (int i = 0; i < ys.Length; i++) ys[i] = run.Points[i].Y;

            bool StateChangesAt(int i) =>
                i > 0 && samples[run.Indices[i]].State != samples[run.Indices[i - 1]].State;

            // Both sides of a change, so the gradient keeps a stop in each of the two colours.
            var keep = PlateauKnots.Select(
                ys, i => StateChangesAt(i) || (i + 1 < ys.Length && StateChangesAt(i + 1)));

            var points  = new List<Point>(keep.Length);
            var indices = new List<int>(keep.Length);
            foreach (int k in keep) { points.Add(run.Points[k]); indices.Add(run.Indices[k]); }
            thinned.Add(new SeriesRun(points, indices));
        }
        return thinned;
    }

    /// <summary>Draws the SoC series as a gradient fill plus the line on top, from one runs+tangents
    /// computation so their edges match exactly. <paramref name="drawShading"/> omits the fill;
    /// <paramref name="fillBrush"/> is the accent fade in every case, never the line's colour.
    /// <paramref name="lineBrushFor"/> is asked per run: a relative-mapped gradient is bound to the
    /// path it paints, so runs cannot share one.</summary>
    private void DrawSocFillAndLine(
        IReadOnlyList<SeriesRun> runs, IReadOnlyList<RunFit> fits, Func<SeriesRun, Brush> lineBrushFor,
        LinearGradientBrush fillBrush, double plotBottomY, bool drawShading)
    {
        for (int r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            var pts = run.Points;
            if (pts.Count < 2) continue; // a lone point has nothing to connect to

            var m = fits[r].Tangents;

            if (drawShading)
            {
                var fillGeo = new PathGeometry();
                fillGeo.Figures.Add(MonotonePath.Figure(pts, m, closeAtY: plotBottomY));
                SparklineCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = fillGeo, Fill = fillBrush });
            }

            var lineGeo = new PathGeometry();
            lineGeo.Figures.Add(MonotonePath.Figure(pts, m));
            SparklineCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data              = lineGeo,
                StrokeThickness   = PrimaryStrokeWidth,
                StrokeLineJoin    = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap  = PenLineCap.Round,
                Stroke            = lineBrushFor(run),
            });
        }
    }

    /// <summary>Draws a collapsed gap: two diagonal strokes below the reserved label band, plus a
    /// pill saying how much time the break stands in for.</summary>
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

        // Inside its own reserved band, so the strokes — which start below it — never cross it.
        AddAnnotationPill(x, pad, FormatGap(skipped), canvasWidth, fontSize: 11);
    }

    /// <summary>Adds a small pill centred on <paramref name="centerX"/> and clamped to the canvas. Its
    /// background is deliberately opaque — not the card's translucent fill — so annotations stay
    /// legible over whatever is beneath them.</summary>
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

        Canvas.SetLeft(pill, PillLeft(centerX, text, fontSize, canvasWidth));
        // Clamp vertically too: a marker near 100% SoC computes a `top` above the canvas.
        Canvas.SetTop(pill, Math.Max(0, top));
        SparklineCanvas.Children.Add(pill);
        return pill;
    }

    /// <summary>Estimated (not measured) left edge for a centred pill, clamped to the canvas.</summary>
    private static double PillLeft(double centerX, string text, double fontSize, double canvasWidth)
    {
        double estimatedWidth = text.Length * fontSize * PillCharWidthEm + PillPaddingX;
        return Math.Clamp(centerX - estimatedWidth / 2, 0, Math.Max(0, canvasWidth - estimatedWidth));
    }

    /// <summary>Formats a skipped-time gap compactly, e.g. "45m", "13h", "2d", "1w". Rounds each
    /// tier's own value before comparing it, so 59.6 minutes promotes to "1h" rather than "60m".</summary>
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

    /// <summary>Marks the highest and lowest points with a dot and a percentage pill; no-op when flat.
    /// <paramref name="candidates"/> are the charge line's knots, so a marker lands on the drawn
    /// curve; thinning keeps every distinct level, so the extremes it finds are the data's own.</summary>
    private void DrawSparklineMarkers(
        IReadOnlyList<BatterySample> samples, IReadOnlyList<int> candidates, double canvasWidth,
        IReadOnlyList<double> xs, Func<double, double> projectY)
    {
        if (candidates.Count == 0) return;

        int maxPct = int.MinValue, minPct = int.MaxValue, maxIdx = candidates[0], minIdx = candidates[0];
        foreach (int i in candidates)
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

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = dotR * 2, Height = dotR * 2, Fill = SparklineStartLabel.Foreground,
            };
            Canvas.SetLeft(dot, cx - dotR);
            Canvas.SetTop(dot,  cy - dotR);
            SparklineCanvas.Children.Add(dot);

            // A pill, not a plain label, keeps the percentage legible over the fill and lines.
            AddAnnotationPill(cx, cy - dotR - 20, $"{pct}%", canvasWidth, fontSize: 13);
        }
    }
}
