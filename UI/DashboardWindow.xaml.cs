using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Power;
using Windows.Foundation;
using Windows.System.Power;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

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

        // The graph control has no reference to App/window-management — it only signals intent.
        HistoryGraph.ExpandRequested += (_, _) => _app.ShowHistoryWindow();

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
            HistoryGraph.Render();
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
