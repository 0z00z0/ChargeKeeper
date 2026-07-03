using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Power;
using Windows.Foundation;
using Windows.System.Power;
using LenovoTray.Helpers;
using LenovoTray.Services;

namespace LenovoTray.UI;

/// <summary>
/// Borderless popup that shows battery status and the current state of both Lenovo
/// power features.  Appears bottom-right above the taskbar; auto-dismisses when it
/// loses focus.  Data refreshes every 5 seconds while the window is active.
/// </summary>
public sealed partial class DashboardWindow : Window
{
    // Fixed logical width; the height is measured from the content each time the window is
    // placed, so rows appearing/disappearing (sliders, travel button) need no height constants.
    // Widened from 280 (Chunk 3) to fit the right-hand power axis plus the Chunk-4 scale buttons.
    private const int WindowWidth = 340;

    // Arc gauge geometry: 100×100 px canvas, 7-o'clock start (135°), 270° sweep.
    private const double GaugeCx         = 50;
    private const double GaugeCy         = 50;
    private const double GaugeRadius     = 38;
    private const double GaugeStartAngle = 135;
    private const double GaugeSweep      = 270;

    // Margin between window edge and work-area boundary (DIPs, scaled per monitor).
    private const int EdgeMargin = 12;

    private readonly DispatcherTimer _refreshTimer;
    private readonly App             _app;

    // When the popup was last hidden — lets the tray click that auto-dismissed it avoid re-showing.
    private DateTime _hiddenAtUtc = DateTime.MinValue;

    // Guards slider ValueChanged handlers from triggering each other recursively.
    private bool _updatingSliders = false;

    // True from the first user slider move until the debounced apply completes. While set, the
    // periodic Refresh must NOT overwrite the slider values with the device's current thresholds
    // (otherwise an in-progress edit snaps back before it's applied).
    private bool _thresholdEditPending = false;

    // Debounces auto-apply: each slider move restarts it; it fires once the user pauses.
    private readonly DispatcherTimer _thresholdApplyTimer;

    /// <summary>Time elapsed since the window was last hidden.</summary>
    public TimeSpan SinceHidden => DateTime.UtcNow - _hiddenAtUtc;

    public DashboardWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ConfigureSliderRanges();
        ConfigureWindowChrome();

        // Track arc never changes — build it once here instead of every refresh tick.
        GaugeTrack.Data = BuildArcGeometry(GaugeCx, GaugeCy, GaugeRadius, GaugeStartAngle, GaugeSweep);

        // Legend swatch colours never change — assign once instead of every render.
        LegendSocSwatch.Background   = AppColors.GaugeHighBrush;
        LegendLimitSwatch.Background = AppColors.HistoryLimitBrush;
        LegendPowerSwatch.Background = AppColors.HistoryPowerBrush;

        // Reflect the persisted time-scale choice in the button row. The in-memory window was
        // already loaded from disk at this span by App.StartHistorySampling, so no LoadWindow call
        // is needed here — CurrentWindow() already holds the right slice for the first render.
        SetSelectedScaleButton(SettingsService.Current.GraphTimeScale);

        // Narrow race: if the very first dashboard open happens before StartHistorySampling's
        // background disk load finishes, CurrentWindow() is momentarily empty even though history
        // exists on disk. Only kick a reload when that's actually the case — the normal case
        // (already loaded) does zero extra I/O, so this doesn't reintroduce the full-scan-per-open
        // cost the comment above deliberately avoids.
        if (BatteryHistoryService.CurrentWindow().Count == 0)
        {
            Task.Run(() =>
            {
                BatteryHistoryService.LoadWindow(SettingsService.Current.GraphTimeScale.ToTimeSpan());
                RunOnUi(RefreshBatteryInfo);
            });
        }

        _refreshTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => Refresh();

        _thresholdApplyTimer          = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _thresholdApplyTimer.Tick    += (_, _) => CommitThresholds();

        Activated += OnActivated;
        Closed    += (_, _) =>
        {
            _closed = true;   // gates RunOnUi: in-flight background reads must not touch a dead window
            _refreshTimer.Stop();
            _thresholdApplyTimer.Stop();
        };
    }

    // Set when the window closes (user action or the framework destroying windows during a GPU/
    // compositor reset). Background reads started before the close complete afterwards and marshal
    // back via RunOnUi — touching XAML members of a closed window then throws (e.g. AppWindow is
    // null), so RunOnUi drops the callback instead.
    private bool _closed;

    /// <summary>
    /// Sets the sliders' Minimum/Maximum/StepFrequency from code instead of XAML. Assigning
    /// <c>RangeBase.Minimum</c> through the XAML type-converter throws a XamlParseException
    /// ("Failed to assign to property RangeBase.Minimum") at LoadComponent on this Windows App
    /// SDK build, which crashed the whole dashboard. Direct property assignment bypasses that
    /// path. Guarded so the value-changed handlers don't run their cross-slider logic or persist
    /// settings during initialisation.
    /// </summary>
    private void ConfigureSliderRanges()
    {
        _updatingSliders = true;
        SetRange(StartSlider,  5,  95, 5);
        SetRange(StopSlider,  10, 100, 5);
        _updatingSliders = false;
    }

    private static void SetRange(Slider slider, double min, double max, double step)
    {
        slider.Maximum       = max;   // set Maximum first so Minimum never transiently exceeds it
        slider.Minimum       = min;
        slider.StepFrequency = step;
    }

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Triggers an immediate full refresh. Called by <c>App</c> when a battery event fires (e.g.
    /// AC connected / disconnected, or a travel-override auto-revert that re-enables Smart Charge)
    /// so both the battery gauge AND the feature badges update in real time rather than waiting
    /// for the next 5-second timer tick. The badge read (Lenovo RPC + service query) is cheap
    /// relative to how briefly this popup stays open, so refreshing it per event is fine.
    /// Must be called on the UI thread.
    /// </summary>
    internal void RefreshFromEvent() => Refresh();

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch. Mirrors
    /// App.RunOnUi: an exception thrown inside a raw DispatcherQueue.TryEnqueue callback is NOT
    /// surfaced to Application.UnhandledException — it tears the whole process down as an opaque
    /// stowed exception with nothing logged anywhere. This window has several Task.Run(...)
    /// background reads (LoadWindow's disk scan, ChargeThresholdService RPC calls) that complete
    /// and touch UI elements later, on the dispatcher — if the user closes the popup while one of
    /// those is in flight, the delegate can hit a torn-down XAML element. Catching here keeps the
    /// tray alive; the failure is logged instead of fatal.
    /// </summary>
    private void RunOnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;   // window already destroyed — a stale callback has nothing to update
        try { action(); }
        catch (Exception ex) { AppLog.Error("DashboardWindow.RunOnUi", ex); }
    });

    /// <summary>Positions the window above the system tray and shows it with fresh data.</summary>
    public void ShowNearTray()
    {
        // Load data before making the window visible to avoid a "Loading…" flash.
        Refresh();

        // Size and position to the current content; ApplyStatusBadges re-places once the
        // background read decides whether the threshold sliders / travel button are shown.
        PlaceWindow();

        AppWindow.Show();

        // Activate() fires the Activated event, which starts the refresh timer.
        Activate();
    }

    /// <summary>
    /// Resizes and repositions the window above the tray (bottom-right corner). The height is
    /// measured from the content, so it always fits exactly whatever rows are currently visible.
    /// </summary>
    private void PlaceWindow()
    {
        // AppWindow works in physical pixels, but the XAML content is in effective pixels (DIPs).
        // Measure the root grid at the fixed width to get the natural content height.
        RootGrid.Measure(new Size(WindowWidth, double.PositiveInfinity));
        int logicalHeight = Math.Clamp((int)Math.Ceiling(RootGrid.DesiredSize.Height), 200, 900);

        var (work, s) = NativeMethods.GetCursorMonitorMetrics();
        int w      = (int)Math.Ceiling(WindowWidth   * s);
        int h      = (int)Math.Ceiling(logicalHeight * s);
        int margin = (int)Math.Ceiling(EdgeMargin    * s);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.Right  - w - margin,
            work.Bottom - h - margin));
    }

    /// <summary>Hides the window without destroying it so it can be shown again cheaply.</summary>
    public void HideWindow()
    {
        _refreshTimer.Stop();
        _hiddenAtUtc = DateTime.UtcNow;
        AppWindow.Hide();
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void ConfigureWindowChrome()
    {
        AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
    }

    // ── Focus / activation ────────────────────────────────────────────────────

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Auto-dismiss when the user clicks away — popup widget behaviour.
            HideWindow();
        }
        else
        {
            _refreshTimer.Start();
        }
    }

    // ── Data refresh ──────────────────────────────────────────────────────────

    private void Refresh()
    {
        // Battery info uses WinRT APIs that must stay on the UI thread.
        RefreshBatteryInfo();

        // Badge updates read from RPC/service — do that work off-thread so a slow Lenovo
        // Power Manager response doesn't freeze the window, then marshal back to apply.
        Task.Run(() =>
        {
            var chargeState = ChargeThresholdService.Read();
            bool standbyOn  = StandbyService.IsRunning();
            // RunOnUi guards the window-may-have-closed-in-the-meantime race: an unhandled throw
            // in a raw dispatcher callback crashes the whole process (stowed exception).
            RunOnUi(() => ApplyStatusBadges(chargeState, standbyOn));
        });
    }

    private void RefreshBatteryInfo()
    {
        try
        {
            var report = Battery.AggregateBattery.GetReport();

            // Compute percentage from raw mWh values reported by the battery driver.
            int? pct = null;
            if (report.FullChargeCapacityInMilliwattHours is > 0 and { } full &&
                report.RemainingCapacityInMilliwattHours  is { } remaining)
            {
                pct = Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100);
            }

            BatteryPercentText.Text = pct.HasValue ? $"{pct}%" : "--";
            UpdateGaugeArc(pct ?? 0);

            // Idle and Charging both indicate AC is connected.
            bool onAC = report.Status is BatteryStatus.Charging or BatteryStatus.Idle;
            int  mw   = report.ChargeRateInMilliwatts ?? 0;
            // Append the rate only in its expected direction (charging on AC, draining on battery);
            // PowerFormat shows mW below 1 W so a small draw never reads as "0 W".
            string label = onAC ? "AC Power" : "Battery";
            string? rate = (onAC && mw > 0) || (!onAC && mw < 0) ? PowerFormat.SignedRate(mw) : null;
            PowerSourceText.Text = rate is null ? label : $"{label}  ·  {rate}";

            SetStatusGlyph(report.Status);

            // Time remaining / time to full.
            TimeRemainingText.Text = ComputeTimeRemaining(report);

            // History sparkline.
            UpdateSparkline();
        }
        catch
        {
            BatteryPercentText.Text = "--";
            StatusGlyph.Text        = "!";
            StatusGlyph.Foreground  = AppColors.StatusUnknownBrush;
        }
    }

    /// <summary>
    /// Sets the compact gauge-centre glyph + colour for the battery state. Replaces the old
    /// status word, which was too wide and overlapped the arc. ToolTip carries the full word.
    /// </summary>
    private void SetStatusGlyph(BatteryStatus status)
    {
        (string glyph, var brush, string tip) = status switch
        {
            BatteryStatus.Charging    => ("▲", AppColors.StatusChargingBrush,    "Charging"),
            BatteryStatus.Discharging => ("▼", AppColors.StatusDischargingBrush, "Discharging"),
            BatteryStatus.Idle        => ("●", AppColors.StatusIdleBrush,        "Full / Idle"),
            BatteryStatus.NotPresent  => ("—", AppColors.StatusUnknownBrush,     "No battery"),
            _                         => ("—", AppColors.StatusUnknownBrush,     ""),
        };
        StatusGlyph.Text       = glyph;
        StatusGlyph.Foreground = brush;
        ToolTipService.SetToolTip(StatusGlyph, tip);
    }

    // ── Time remaining ────────────────────────────────────────────────────────

    private static string ComputeTimeRemaining(BatteryReport report)
    {
        // Need a non-trivial charge rate and valid capacity values.
        if (report.ChargeRateInMilliwatts is not { } rate || Math.Abs(rate) < 100)
            return "—";
        if (report.RemainingCapacityInMilliwattHours is not { } remaining)
            return "—";

        if (rate > 0 && report.FullChargeCapacityInMilliwattHours is > 0 and { } full)
        {
            // Charging: time to reach full.
            double h = (full - remaining) / (double)rate;
            return FormatHours(h);
        }
        if (rate < 0)
        {
            // Discharging: time until empty.
            double h = remaining / (double)Math.Abs(rate);
            return FormatHours(h);
        }
        return "—";
    }

    private static string FormatHours(double h)
    {
        if (h <= 0 || double.IsInfinity(h) || double.IsNaN(h)) return "—";
        if (h > 99) return ">99h";
        var ts = TimeSpan.FromHours(h);
        return ts.TotalHours >= 1
            ? $"~{(int)ts.TotalHours}h {ts.Minutes}m"
            : $"~{ts.Minutes}m";
    }

    /// <summary>Formats a history span as a left-edge axis label, e.g. "−42m" or "−1h 05m".</summary>
    private static string FormatAgo(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "−<1m";
        return span.TotalHours >= 1
            ? $"−{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"−{span.Minutes}m";
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
        // on the UI thread, or clicking a scale button could visibly freeze the popup for a moment.
        Task.Run(() =>
        {
            BatteryHistoryService.LoadWindow(scale.ToTimeSpan());
            AppLog.Info($"Time-scale changed to {scale}.");
            RunOnUi(RefreshBatteryInfo);   // re-renders the sparkline + battery text
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

    // A hole in the timeline bigger than this (3x the sample cadence) is treated as downtime (app
    // was closed/crashed) rather than just a slightly-late sample, and gets a gap marker instead
    // of a connecting line. Derived from BatteryHistoryService.SampleIntervalSeconds — the single
    // source of truth for the cadence — instead of a second, independently-hardcoded number.
    private static readonly TimeSpan GapThreshold =
        TimeSpan.FromSeconds(BatteryHistoryService.SampleIntervalSeconds * 3);

    private void UpdateSparkline()
    {
        SparklineCanvas.Children.Clear();

        var samples = BatteryHistoryService.CurrentWindow();
        if (samples.Count < 2)
        {
            SparklineStartLabel.Text  = "—";
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
        // processing and element allocation on every 5s render tick. Gap detection runs against the
        // ORIGINAL full-resolution timestamps and is carried through as gapBefore — two adjacent
        // points that survive reduction can legitimately be far apart in time purely from the
        // stride, so re-deriving "is this a gap" from a Δt check on the reduced list would treat
        // ordinary stride spacing as downtime and shatter every series into disconnected dots.
        int maxPoints = Math.Max(200, (int)(w * 2));
        var reduced   = HistoryDownsampler.Reduce(samples, maxPoints, GapThreshold);
        samples = reduced.Samples;
        var gapBefore = reduced.GapBeforeIndices;

        // Selected window: real elapsed time from (now - loaded span) to now, NOT the span between
        // the oldest/newest sample — this keeps the x-axis stable and absolute even when history is
        // sparse (e.g. right after a restart) rather than rescaling to fit the data. Read from
        // BatteryHistoryService's own tracked span (set by the last LoadWindow call) rather than
        // re-deriving it from Settings, so there's one source of truth for "what's loaded."
        var span        = BatteryHistoryService.CurrentSpan;
        DateTime nowUtc  = DateTime.UtcNow;
        DateTime winStart = nowUtc - span;

        SparklineStartLabel.Text = FormatAgo(span);

        // Projection from (time, percent) to canvas coordinates, over the fixed selected window
        // (not the min/max of the samples) so the x-axis reflects real elapsed time. Captured once
        // here so the polyline, gap markers, and min/max markers can't drift apart.
        const double pad = 4;
        double tMin   = winStart.Ticks;
        double tRange = Math.Max((double)span.Ticks, 1);
        double ProjectX(long ticks) => pad + (ticks - tMin) / tRange * (w - pad * 2);
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

        // Draw the gap markers once (shared across all series — a restart is a restart regardless
        // of which series you're looking at), then each series over the same time projection.
        for (int i = 1; i < samples.Count; i++)
            if (gapBefore.Contains(i))
                DrawGapMarker(ProjectX(samples[i - 1].AtUtc.Ticks), h, pad);

        // Subtle gradient shade under the SoC line (AppControl-style), drawn before any series line
        // so the fill sits behind everything. Uses the same gapBefore boundaries as the line itself
        // so the fill never bridges a downtime hole either. Brush is one of 3 pre-built, cached
        // instances (AppColors) — never allocated here, unlike the first pass.
        DrawGradientFill(samples, gapBefore, s => s.Soc, ProjectX, ProjectYPct, h - pad, socFillBrush);

        // SoC — solid line, left axis, drawn heavier than the other two (primary: true) since it's
        // the headline series. Drawn first among the three so limit/power (added next) sit
        // visually on top where they cross it.
        DrawSeries(samples, gapBefore, s => s.Soc, ProjectX, ProjectYPct, socBrush, dashed: false, primary: true);

        // Charge limit (Smart Charge Stop threshold) — stepped line, left axis, in the SAME muted
        // amber as the gauge's threshold tick marks so the concept has one colour everywhere.
        // Null when Smart Charge is off; skip those points so no line is drawn across the off
        // period. Stepped (rather than a straight line between samples) because the threshold only
        // ever changes in discrete jumps when the user edits it — a linear ramp between two sampled
        // values would misleadingly suggest it drifted gradually over the sample interval.
        DrawSeries(samples, gapBefore, s => s.LimitPct, ProjectX, ProjectYPct, AppColors.HistoryLimitBrush, stepped: true);

        // Charge power — dotted line, right axis, visually distinct both by dash pattern and by
        // colour (muted lavender) as a different scale.
        DrawSeries(samples, gapBefore, s => s.PowerMw / 1000.0, ProjectX, ProjectYWatts, AppColors.HistoryPowerBrush, dashed: true);

        DrawSparklineMarkers(samples, w, ProjectX, ProjectYPct);
    }

    /// <summary>Formats a right-axis power value as a plain number, e.g. "12W" or "0W".</summary>
    private static string FormatWatts(double watts) =>
        $"{Math.Round(watts, MidpointRounding.AwayFromZero):0}W";

    // Stroke width for the primary series (SoC) — heavier than the secondary series so it reads
    // as the "main" line at a glance. Bumped up from the first pass (1.75/1.25), which read as too
    // thin/hard to distinguish at high DPI (e.g. a 4K display) once three series share the plot.
    private const double PrimaryStrokeWidth   = 2.5;
    private const double SecondaryStrokeWidth = 2.0;

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
        Func<long, double> projectX, Func<double, double> projectY, Brush brush,
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

            double x = projectX(s.AtUtc.Ticks);
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
        Func<long, double> projectX, Func<double, double> projectY, double plotBottomY,
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
            segment.Add(new Point(projectX(s.AtUtc.Ticks), projectY(value.Value)));
        }
        FlushSegment();
    }

    /// <summary>
    /// Draws a short vertical dashed line spanning the plot height at a gap's start-x, making a
    /// restart/downtime hole in the timeline visually obvious (rather than silently skipping it).
    /// </summary>
    private void DrawGapMarker(double x, double canvasHeight, double pad)
    {
        var dash = new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = x, X2 = x,
            Y1 = pad, Y2 = canvasHeight - pad,
            Stroke              = SparklineStartLabel.Foreground,
            StrokeThickness     = 1,
            StrokeDashArray     = [2, 2],
            Opacity             = 0.6,
        };
        SparklineCanvas.Children.Add(dash);
    }

    /// <summary>
    /// Annotates the highest and lowest points of the sparkline with a neutral dot and a
    /// percentage label — position and the label already say "extreme", so the dot no longer
    /// needs red/green colour coding (that read as an alarm alongside the graph's own colours).
    /// No-op when the visible range is flat (nothing meaningful to mark).
    /// </summary>
    private void DrawSparklineMarkers(
        IReadOnlyList<BatterySample> samples, double canvasWidth,
        Func<long, double> projectX, Func<double, double> projectY)
    {
        int maxPct = int.MinValue, minPct = int.MaxValue, maxIdx = 0, minIdx = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Soc > maxPct) { maxPct = samples[i].Soc; maxIdx = i; }
            if (samples[i].Soc < minPct) { minPct = samples[i].Soc; minIdx = i; }
        }
        if (maxPct == minPct) return;

        // Same neutral, theme-aware brush as the axis/time labels for both markers.
        AddMarker(samples[maxIdx].AtUtc.Ticks, maxPct, SparklineStartLabel.Foreground);
        AddMarker(samples[minIdx].AtUtc.Ticks, minPct, SparklineStartLabel.Foreground);

        void AddMarker(long ticks, int pct, Brush brush)
        {
            const double dotR = 3;
            double cx = projectX(ticks);
            double cy = projectY(pct);

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = dotR * 2, Height = dotR * 2, Fill = brush,
            };
            Canvas.SetLeft(dot, cx - dotR);
            Canvas.SetTop(dot,  cy - dotR);
            SparklineCanvas.Children.Add(dot);

            const double labelW = 30; // approximate width budget for edge clamping
            var label = new TextBlock
            {
                Text       = $"{pct}%",
                FontSize   = 11,
                // Reuse the axis labels' themed brush so the annotation tracks light/dark mode.
                Foreground = SparklineStartLabel.Foreground,
            };
            Canvas.SetLeft(label, Math.Clamp(cx - labelW / 2, 0, Math.Max(0, canvasWidth - labelW)));
            Canvas.SetTop(label,  cy - dotR - 13); // ~13px above the dot centre (bigger label needs more room)
            SparklineCanvas.Children.Add(label);
        }
    }

    // Called on the UI thread after the background read completes.
    private void ApplyStatusBadges(ChargeThresholdState? chargeState, bool standbyOn)
    {
        // ── Smart Charge ──────────────────────────────────────────────────────
        if (chargeState is { Capable: true })
        {
            SetFeatureBadge(SmartChargeBadge, SmartChargeIndicator, chargeState.Enabled);
            SmartChargeDetailText.Text = chargeState.Enabled switch
            {
                true when chargeState.Start > 0 && chargeState.Stop > 0
                    => $"Custom: {chargeState.Start}% → {chargeState.Stop}%",
                true  => "On — reading thresholds…",
                false => "Off — charges to 100%"
            };
        }
        else
        {
            // Read failed (driver/DLL missing or RPC error) or firmware reports not capable.
            SmartChargeBadge.Background     = AppColors.BadgeInactiveBrush;
            SmartChargeIndicator.Background = AppColors.IndicatorOrangeBrush;
            SmartChargeDetailText.Text      = chargeState is null ? "Unavailable" : "Not supported";
        }

        // ── Gauge threshold tick markers ──────────────────────────────────────
        if (chargeState is { Capable: true, Enabled: true, Start: > 0, Stop: > 0 })
        {
            double startAngle = GaugeStartAngle + GaugeSweep * chargeState.Start / 100.0;
            double stopAngle  = GaugeStartAngle + GaugeSweep * chargeState.Stop  / 100.0;
            GaugeStartTick.Data       = BuildTickGeometry(GaugeCx, GaugeCy, startAngle);
            GaugeStopTick.Data        = BuildTickGeometry(GaugeCx, GaugeCy, stopAngle);
            GaugeStartTick.Visibility = Visibility.Visible;
            GaugeStopTick.Visibility  = Visibility.Visible;
        }
        else
        {
            GaugeStartTick.Visibility = Visibility.Collapsed;
            GaugeStopTick.Visibility  = Visibility.Collapsed;
        }

        // ── Threshold sliders ─────────────────────────────────────────────────
        // Sync the sliders to the device value ONLY when the user isn't mid-edit; otherwise the
        // 5 s refresh would clobber an in-progress change before the debounced apply runs.
        bool showSliders = chargeState is { Capable: true, Enabled: true };
        if (showSliders && !_thresholdEditPending && chargeState!.Start > 0 && chargeState.Stop > 0)
        {
            _updatingSliders  = true;
            StartSlider.Value = chargeState.Start;
            StopSlider.Value  = chargeState.Stop;
            StartValueText.Text = $"{chargeState.Start}%";
            StopValueText.Text  = $"{chargeState.Stop}%";
            _updatingSliders  = false;
        }
        ThresholdSliders.Visibility = showSliders ? Visibility.Visible : Visibility.Collapsed;

        // ── Travel override ("charge to 100 % once") ──────────────────────────
        // Shown whenever Smart Charge is capable, so it stays available to cancel even while
        // active (when the threshold is temporarily lifted and the sliders are hidden).
        if (chargeState is { Capable: true })
        {
            TravelOverrideButton.Visibility = Visibility.Visible;
            TravelOverrideButton.Content = TravelOverrideService.ActionLabel;
        }
        else
        {
            TravelOverrideButton.Visibility = Visibility.Collapsed;
        }

        // Resize the window to fit whatever is now visible.
        if (AppWindow.IsVisible)
            PlaceWindow();

        // ── Smart Standby ─────────────────────────────────────────────────────
        SetFeatureBadge(SmartStandbyBadge, SmartStandbyIndicator, standbyOn);
        SmartStandbyDetailText.Text = standbyOn
            ? "Active — scheduling idle sleep"
            : "Off — always Modern Standby";
    }

    /// <summary>Applies active/inactive colours to a feature badge + indicator pair.</summary>
    private static void SetFeatureBadge(Border badge, Border indicator, bool on)
    {
        badge.Background     = on ? AppColors.BadgeActiveBrush     : AppColors.BadgeInactiveBrush;
        indicator.Background = on ? AppColors.IndicatorAccentBrush : AppColors.IndicatorGreyBrush;
    }

    // ── Threshold slider handlers ─────────────────────────────────────────────

    private void OnStartSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSliders) return;
        int val = (int)e.NewValue;
        StartValueText.Text = $"{val}%";
        // Enforce at least a 5% gap between start and stop.
        if (StopSlider.Value <= val)
        {
            _updatingSliders   = true;
            StopSlider.Value   = Math.Min(val + 5, 100);
            StopValueText.Text = $"{(int)StopSlider.Value}%";
            _updatingSliders   = false;
        }
        QueueThresholdApply();
    }

    private void OnStopSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSliders) return;
        int val = (int)e.NewValue;
        StopValueText.Text = $"{val}%";
        // Enforce at least a 5% gap.
        if (StartSlider.Value >= val)
        {
            _updatingSliders    = true;
            StartSlider.Value   = Math.Max(val - 5, 5);
            StartValueText.Text = $"{(int)StartSlider.Value}%";
            _updatingSliders    = false;
        }
        QueueThresholdApply();
    }

    /// <summary>Marks an edit pending and (re)starts the debounce so rapid drags apply only once.</summary>
    private void QueueThresholdApply()
    {
        _thresholdEditPending = true;   // freezes the periodic refresh from reverting the sliders
        _thresholdApplyTimer.Stop();
        _thresholdApplyTimer.Start();
    }

    /// <summary>Auto-applies the current slider values (debounced). Replaces the old Apply button.</summary>
    private void CommitThresholds()
    {
        _thresholdApplyTimer.Stop();
        int start = (int)StartSlider.Value;
        int stop  = (int)StopSlider.Value;
        Task.Run(() =>
        {
            bool ok = ChargeThresholdService.SetThresholds(start, stop);
            RunOnUi(() =>
            {
                if (ok)
                {
                    // Threshold is now custom — clear any active preset name.
                    SettingsService.Current.ActivePreset = null;
                    SettingsService.Save();
                }
                else
                {
                    SmartChargeDetailText.Text = "Error — check driver";
                }
                // Edit is done; let the next refresh resync (device now matches the sliders).
                _thresholdEditPending = false;
                Refresh();
            });
        });
    }

    // ── Travel override ("charge to 100 % once") ───────────────────────────────

    private void OnTravelOverrideButton(object sender, RoutedEventArgs e)
    {
        if (TravelOverrideService.IsActive)
            TravelOverrideService.Cancel();
        else
            TravelOverrideService.Activate();

        // Re-read so the button label, badge, and sliders reflect the new state immediately.
        Refresh();
    }

    // ── Arc gauge ─────────────────────────────────────────────────────────────

    private void UpdateGaugeArc(int percent)
    {
        // Track geometry is constant and set in the constructor — only fill changes here.
        GaugeFill.Data = percent > 0
            ? BuildArcGeometry(GaugeCx, GaugeCy, GaugeRadius, GaugeStartAngle, GaugeSweep * percent / 100.0)
            : null;

        GaugeFill.Stroke = percent switch
        {
            <= 20 => AppColors.GaugeLowBrush,
            <= 50 => AppColors.GaugeMedBrush,
            _     => AppColors.GaugeHighBrush
        };
    }

    /// <summary>
    /// Builds a short radial tick-mark line on the gauge arc at the given clock-face angle.
    /// Used to mark the Smart Charge start and stop thresholds.
    /// </summary>
    private static Geometry BuildTickGeometry(double cx, double cy, double angleDeg)
    {
        const double innerR = GaugeRadius - 6;
        const double outerR = GaugeRadius + 6;
        double rad = (angleDeg - 90) * Math.PI / 180;
        return new LineGeometry
        {
            StartPoint = new Point(cx + innerR * Math.Cos(rad), cy + innerR * Math.Sin(rad)),
            EndPoint   = new Point(cx + outerR * Math.Cos(rad), cy + outerR * Math.Sin(rad)),
        };
    }

    /// <summary>
    /// Builds a <see cref="PathGeometry"/> for a circular arc.
    /// Angles follow clock-face convention (0° = 12 o'clock, increasing clockwise).
    /// </summary>
    private static Geometry BuildArcGeometry(
        double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        // A full 360° arc is degenerate in SVG/XAML — cap slightly below.
        sweepDeg = Math.Min(sweepDeg, 359.99);

        // Rotate reference frame: clock-face 0° maps to math 270° (i.e. subtract 90°).
        double startRad = (startDeg - 90) * Math.PI / 180;
        double endRad   = (startDeg + sweepDeg - 90) * Math.PI / 180;

        var startPt = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
        var endPt   = new Point(cx + r * Math.Cos(endRad),   cy + r * Math.Sin(endRad));

        var figure = new PathFigure { StartPoint = startPt, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point          = endPt,
            Size           = new Size(r, r),
            IsLargeArc     = sweepDeg > 180,
            SweepDirection = SweepDirection.Clockwise,
            RotationAngle  = 0
        });

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        return geo;
    }
}
