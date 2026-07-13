using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    // 42 (up from 38): fills more of the 100x100 canvas — the ceiling here is set by the tick
    // marks, not the arc itself (arc StrokeThickness=10 alone would allow up to 45; ticks add
    // 6 beyond GaugeRadius at their own 2.5 stroke, so outerR=48 is the largest radius that
    // still keeps the tick tips a safe ~1px inside the 100px canvas edge).
    private const double GaugeRadius     = 42;
    private const double GaugeStartAngle = 135;
    private const double GaugeSweep      = 270;

    // Margin between window edge and work-area boundary (DIPs, scaled per monitor).
    private const int EdgeMargin = 12;

    private readonly DispatcherTimer _refreshTimer;
    private readonly App             _app;

    // When the popup was last hidden — lets the tray click that auto-dismissed it avoid re-showing.
    private DateTime _hiddenAtUtc = DateTime.MinValue;

    // Guards OnThresholdRangeChanged from reacting to its own programmatic writes — both the
    // periodic device-value sync (ApplyStatusBadges) and the handler's own min-gap enforcement
    // set RangeStart/RangeEnd, which would otherwise re-enter the handler and queue a bogus apply.
    private bool _updatingSliders = false;

    // True from the first user slider move until the debounced apply completes. While set, the
    // periodic Refresh must NOT overwrite the slider values with the device's current thresholds
    // (otherwise an in-progress edit snaps back before it's applied).
    private bool _thresholdEditPending = false;

    // Monotonic edit counter: bumped on every slider change so a debounced apply that completes
    // AFTER a newer edit has started can tell it's stale and must NOT clear the edit-pending freeze
    // (which would let Refresh() snap the sliders back and the newer edit then commit the reverted
    // values — a silently-lost adjustment).
    private int _thresholdEditGeneration;

    // Debounces auto-apply: each slider move restarts it; it fires once the user pauses.
    private readonly DispatcherTimer _thresholdApplyTimer;

    /// <summary>Time elapsed since the window was last hidden.</summary>
    public TimeSpan SinceHidden => DateTime.UtcNow - _hiddenAtUtc;

    public DashboardWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ConfigureThresholdRange();
        ConfigureWindowChrome();

        // Track arc never changes — build it once here instead of every refresh tick.
        GaugeTrack.Data = BuildArcGeometry(GaugeCx, GaugeCy, GaugeRadius, GaugeStartAngle, GaugeSweep);

        // Threshold-tick strokes come from the shared palette rather than XAML hex literals (which
        // had to be hand-synced with Terracotta and once drifted): HistoryLimitBrush, so the
        // "charge limit" concept is the same brush here and in the history graph. GaugeFill's own
        // stroke is set per-refresh by UpdateGaugeArc before the window is ever shown.
        GaugeStartTick.Stroke = AppColors.HistoryLimitBrush;
        GaugeStopTick.Stroke  = AppColors.HistoryLimitBrush;

        // The graph control has no reference to App/window-management — it only signals intent.
        HistoryGraph.ExpandRequested += (_, _) => _app.ShowHistoryWindow();

        // A compact 340px popup has no room for the gap-break diagonal strokes + duration pill,
        // the SoC stress heat strip (TODO #25), or the hover-crosshair readout pill (TODO #27),
        // without crowding the plot — the bigger pop-out window (which leaves all three at their
        // default true) is where that detail belongs.
        HistoryGraph.ShowGapMarkers    = false;
        HistoryGraph.ShowStressHeatmap = false;
        HistoryGraph.ShowCrosshair     = false;

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
    /// Sets the RangeSelector's Minimum/Maximum/StepFrequency from code instead of XAML. Assigning
    /// them through the XAML type-converter throws a XamlParseException ("Failed to assign to
    /// property...") at LoadComponent on this Windows App SDK build — the same issue the old
    /// Slider.Minimum had. Bounds cover the union of the old two sliders' separate ranges
    /// (Start was 5-95, Stop was 10-100); the min-gap enforcement in OnThresholdRangeChanged keeps
    /// Start effectively capped at 95 and Stop at a minimum of 10 in practice. Guarded so the
    /// value-changed handler doesn't queue a bogus apply during initialisation.
    /// </summary>
    private void ConfigureThresholdRange()
    {
        _updatingSliders = true;
        ThresholdRange.Maximum       = 100;   // set Maximum first so Minimum never transiently exceeds it
        ThresholdRange.Minimum       = 5;
        ThresholdRange.StepFrequency = 5;
        _updatingSliders = false;
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

    private void ConfigureWindowChrome() =>
        WindowChrome.ApplyPopup(this, resizable: false, alwaysOnTop: true);

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

            // Shared "on AC" definition (IsOnAC) — the same call the tray icon path uses, so the
            // gauge and the tray icon structurally agree on when to show blue (TODO #34).
            bool onAC = BatteryStatsFormatter.IsOnAC(report.Status);
            UpdateGaugeArc(pct ?? 0, onAC);

            // Adapter wattage (TODO #41) lives in the pop-out window only now — the small popup's
            // fixed 340px width can't fit "AC Power (60W charger) · +45 W" without overflowing its
            // own card. Plain source + rate here; BatteryHistoryWindow shows the wattage.
            PowerSourceText.Text = BatteryStatsFormatter.FormatPowerSource(
                onAC, report.ChargeRateInMilliwatts ?? 0, adapterWattage: null);

            SetStatusGlyph(report.Status);

            // Time remaining / time to full — label stays "REMAINING"; the value itself carries the
            // direction ("~2h 14m to full" vs "~3h remaining") so it's never ambiguous.
            TimeRemainingText.Text = BatteryStatsFormatter.FormatTimeRemaining(
                report.ChargeRateInMilliwatts, report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);

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
            _updatingSliders = true;
            ThresholdRange.RangeStart = chargeState.Start;
            ThresholdRange.RangeEnd   = chargeState.Stop;
            StartValueText.Text = $"{chargeState.Start}%";
            StopValueText.Text  = $"{chargeState.Stop}%";
            _updatingSliders = false;
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

    // ── Threshold range handler ───────────────────────────────────────────────

    /// <summary>
    /// Fires whenever either thumb moves. RangeSelector already keeps RangeStart from passing
    /// RangeEnd (or vice versa) by dragging the other thumb along with it — but that means the two
    /// can end up EQUAL (zero gap) rather than crossing, which <c>LenovoChargeThreshold.SetThresholds</c>
    /// rejects outright (<c>start &gt;= stop</c>). This restores the old two-slider behaviour's
    /// minimum 5-point gap by nudging whichever thumb DIDN'T just move.
    /// </summary>
    private void OnThresholdRangeChanged(object sender, RangeChangedEventArgs e)
    {
        if (_updatingSliders) return;

        int start = (int)ThresholdRange.RangeStart;
        int stop  = (int)ThresholdRange.RangeEnd;

        if (stop - start < 5)
        {
            _updatingSliders = true;
            if (e.ChangedRangeProperty == RangeSelectorProperty.MinimumValue)
            {
                // User moved the start thumb: push stop up — and when that hits the 100 ceiling,
                // pull start back down instead. Without the second step, dragging start into the
                // 96-100 range left a sub-5 (even zero) gap, and SetThresholds rejects
                // start >= stop, so the write silently failed until the next device resync.
                stop = (int)(ThresholdRange.RangeEnd = Math.Min(start + 5, 100));
                if (stop - start < 5) start = (int)(ThresholdRange.RangeStart = stop - 5);
            }
            else
            {
                // User moved the stop thumb: push start down — mirrored 5-point floor case.
                start = (int)(ThresholdRange.RangeStart = Math.Max(stop - 5, 5));
                if (stop - start < 5) stop = (int)(ThresholdRange.RangeEnd = start + 5);
            }
            _updatingSliders = false;
        }

        StartValueText.Text = $"{start}%";
        StopValueText.Text  = $"{stop}%";
        QueueThresholdApply();
    }

    /// <summary>Marks an edit pending and (re)starts the debounce so rapid drags apply only once.</summary>
    private void QueueThresholdApply()
    {
        _thresholdEditPending = true;   // freezes the periodic refresh from reverting the sliders
        _thresholdEditGeneration++;     // supersede any in-flight commit's claim to clear that freeze
        _thresholdApplyTimer.Stop();
        _thresholdApplyTimer.Start();
    }

    /// <summary>Auto-applies the current slider values (debounced). Replaces the old Apply button.</summary>
    private void CommitThresholds()
    {
        _thresholdApplyTimer.Stop();
        int gen   = _thresholdEditGeneration;   // this apply's edit; a newer drag bumps it
        int start = (int)ThresholdRange.RangeStart;
        int stop  = (int)ThresholdRange.RangeEnd;
        Task.Run(() =>
        {
            // An explicit slider write supersedes any in-flight "charge to 100 % once": clear the
            // override WITHOUT reverting (Deactivate, not Cancel) so its armed auto-revert can't
            // later clobber these fresh values with the pre-override thresholds. No-op when no
            // override is active — defence in depth that keeps the "explicit threshold cancels the
            // override" rule consistent with the presets path.
            TravelOverrideService.Deactivate();
            bool ok = ChargeThresholdService.SetThresholds(start, stop);
            RunOnUi(() =>
            {
                if (ok)
                {
                    // Threshold is now custom — clear any active preset name. Update() (not
                    // Current-mutate-then-Save) so a Reload() that swapped the settings object
                    // during the RPC gap above can't make this a lost write on a stale instance.
                    SettingsService.Update(s => s.ActivePreset = null);
                }
                else
                {
                    SmartChargeDetailText.Text = "Error — check driver";
                }
                // Only release the edit freeze if no newer edit started while this apply was in
                // flight; otherwise Refresh() snaps the sliders back to this superseded value and
                // the newer edit's debounced commit then reads (and re-applies) the revert.
                if (gen == _thresholdEditGeneration)
                {
                    _thresholdEditPending = false;
                    Refresh();
                }
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

    /// <summary>
    /// Colours the arc by charge state (TODO #34): green, amber, then orange as the level drops,
    /// with <paramref name="onAC"/> forcing blue regardless of level. Thresholds AND colour bytes
    /// come from <see cref="GaugePalette"/> — the same constants the tray icon's arc
    /// (<see cref="Helpers.IconGenerator"/>) renders from — so the gauge and the tray icon agree
    /// structurally, not by hand-synced literals.
    /// </summary>
    private void UpdateGaugeArc(int percent, bool onAC)
    {
        // Track geometry is constant and set in the constructor — only fill changes here.
        GaugeFill.Data = percent > 0
            ? BuildArcGeometry(GaugeCx, GaugeCy, GaugeRadius, GaugeStartAngle, GaugeSweep * percent / 100.0)
            : null;

        GaugeFill.Stroke = onAC
            ? AppColors.GaugeChargingBrush
            : percent switch
            {
                > GaugePalette.GreenAbovePct   => AppColors.GaugeGreenBrush,
                > GaugePalette.LowAtOrBelowPct => AppColors.GaugeMedBrush,
                _                              => AppColors.GaugeLowBrush
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
