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
    private const int WindowWidth = 280;

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

        _refreshTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => Refresh();

        _thresholdApplyTimer          = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _thresholdApplyTimer.Tick    += (_, _) => CommitThresholds();

        Activated += OnActivated;
        Closed    += (_, _) => { _refreshTimer.Stop(); _thresholdApplyTimer.Stop(); };
    }

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
        int logicalHeight = Math.Clamp((int)Math.Ceiling(RootGrid.DesiredSize.Height), 200, 720);

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
            // Guard: the window may close between this off-thread read and the marshalled apply.
            // An unhandled throw in a dispatcher callback crashes the process (stowed exception).
            DispatcherQueue.TryEnqueue(() =>
            {
                try { ApplyStatusBadges(chargeState, standbyOn); }
                catch { /* window tearing down — a stale badge update must not be fatal */ }
            });
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
            UpdateSparkline(pct ?? 0);
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

    // ── History sparkline ─────────────────────────────────────────────────────

    private void UpdateSparkline(int currentPct)
    {
        SparklineCanvas.Children.Clear();

        var samples = BatteryHistoryService.GetWindow(TimeSpan.FromHours(1));
        if (samples.Length < 2)
        {
            SparklineStartLabel.Text = "—";
            return;
        }

        // Time-axis label: span between the oldest and newest sample (right edge is "now").
        SparklineStartLabel.Text = FormatAgo(samples[^1].At - samples[0].At);

        // Canvas size is known once the element has been measured; guard against first render.
        double w = SparklineCanvas.ActualWidth;
        double h = SparklineCanvas.ActualHeight;
        if (w < 4 || h < 4) return;

        // Projection from (time, percent) to canvas coordinates. Captured once here so the
        // polyline and the min/max markers can't drift apart on padding/inversion changes.
        const double pad = 4;
        double tMin   = samples[0].At.Ticks;
        double tRange = Math.Max((double)(samples[^1].At.Ticks - samples[0].At.Ticks), 1);
        double ProjectX(long ticks) => pad + (ticks - tMin) / tRange * (w - pad * 2);
        // Y axis: 0% at bottom, 100% at top; invert because canvas Y grows downward.
        double ProjectY(int pct)    => (h - pad) - pct / 100.0 * (h - pad * 2);

        var poly = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            StrokeThickness = 1.5,
            StrokeLineJoin  = PenLineJoin.Round,
            Stroke          = currentPct switch
            {
                <= 20 => AppColors.GaugeLowBrush,
                <= 50 => AppColors.GaugeMedBrush,
                _     => AppColors.GaugeHighBrush,
            },
        };

        foreach (var (at, pct) in samples)
            poly.Points.Add(new Point(ProjectX(at.Ticks), ProjectY(pct)));

        SparklineCanvas.Children.Add(poly);

        DrawSparklineMarkers(samples, w, ProjectX, ProjectY);
    }

    /// <summary>
    /// Annotates the highest and lowest points of the sparkline with a coloured dot and a
    /// percentage label. No-op when the visible range is flat (nothing meaningful to mark).
    /// </summary>
    private void DrawSparklineMarkers(
        (DateTime At, int Pct)[] samples, double canvasWidth,
        Func<long, double> projectX, Func<int, double> projectY)
    {
        int maxPct = int.MinValue, minPct = int.MaxValue, maxIdx = 0, minIdx = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i].Pct > maxPct) { maxPct = samples[i].Pct; maxIdx = i; }
            if (samples[i].Pct < minPct) { minPct = samples[i].Pct; minIdx = i; }
        }
        if (maxPct == minPct) return;

        AddMarker(samples[maxIdx].At.Ticks, maxPct, AppColors.GaugeHighBrush);
        AddMarker(samples[minIdx].At.Ticks, minPct, AppColors.GaugeLowBrush);

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

            const double labelW = 26; // approximate width budget for edge clamping
            var label = new TextBlock
            {
                Text       = $"{pct}%",
                FontSize   = 9,
                // Reuse the axis labels' themed brush so the annotation tracks light/dark mode.
                Foreground = SparklineStartLabel.Foreground,
            };
            Canvas.SetLeft(label, Math.Clamp(cx - labelW / 2, 0, Math.Max(0, canvasWidth - labelW)));
            Canvas.SetTop(label,  cy - dotR - 11); // ~11px above the dot centre
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
        badge.Background     = on ? AppColors.BadgeActiveBrush    : AppColors.BadgeInactiveBrush;
        indicator.Background = on ? AppColors.IndicatorGreenBrush : AppColors.IndicatorGreyBrush;
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
            DispatcherQueue.TryEnqueue(() =>
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
