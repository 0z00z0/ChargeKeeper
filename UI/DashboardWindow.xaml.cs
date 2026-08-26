using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Power;
using Windows.Foundation;
using Windows.System.Power;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// Borderless popup above the tray showing battery status and the vendor power features. It
/// auto-dismisses on focus loss and refreshes every 5 seconds while active.
/// </summary>
public sealed partial class DashboardWindow : Window
{
    private const int WindowWidth = 340;

    // What PresetButtonPanel is allowed before its first arrange: WindowWidth less RootGrid's 20 px
    // padding either side and the Smart Charge badge's 10 px either side. The same width the
    // travel-override button occupies, which is what the preset rows have to line up with.
    private const double PresetPanelWidth = WindowWidth - 40 - 20;

    // Arc gauge geometry: 100×100 px canvas, clock-face 135° start — 4:30, not 7 o'clock — and a
    // 270° clockwise sweep ending at 1:30, so the 90° gap sits on the right-hand side.
    private const double GaugeCx         = 50;
    private const double GaugeCy         = 50;
    // The largest radius that keeps the tick tips inside the canvas: they add 6 beyond it.
    private const double GaugeRadius     = 42;
    private const double GaugeStartAngle = 135;
    private const double GaugeSweep      = 270;

    // Margin between window edge and work-area boundary (DIPs, scaled per monitor).
    private const int EdgeMargin = 12;

    private readonly DispatcherTimer _refreshTimer;
    private readonly App             _app;

    // When the popup was last hidden — lets the tray click that auto-dismissed it avoid re-showing.
    private DateTime _hiddenAtUtc = DateTime.MinValue;

    // Guards OnThresholdRangeChanged against its own writes: the device sync and the min-gap
    // enforcement both set RangeStart/RangeEnd, which would re-enter it.
    private bool _updatingSliders = false;

    // Same for the badge switches and chips: a programmatic write raises the click's own event.
    private bool _updatingBadges = false;

    // The last span the user ran, so the switch resumes that one; the service keeps no history.
    private KeepAwakeRequest? _lastKeepAwake;

    // What KeepAwakePresetPanel is built from; rebuilding only on a change keeps the reconcile cheap.
    private IReadOnlyList<KeepAwakeRequest> _keepAwakeChips = [];

    // Same purpose for the lid-delay chips: the set only changes when the stored delay leaves the
    // quick list, so the 5 s reconcile must not rebuild the row underneath a pointer.
    private IReadOnlyList<int> _lidDelayChips = [];

    // Same for PresetButtonPanel: rebuilding on every 5 s tick would drop the button under the pointer.
    private IReadOnlyList<string> _presetButtonLabels = [];

    // The column/row shape PresetButtonPanel is currently arranged in, so a SizeChanged that does not
    // change it re-places nothing. Reset whenever the buttons are rebuilt: the new ones carry no
    // Grid.Row/Column and would otherwise all pile into the first cell.
    private (int Columns, int Rows) _presetGridShape;

    // Set from the first slider move until the debounced apply lands; freezes Refresh's slider sync.
    private bool _thresholdEditPending = false;

    // Bumped per slider change, so an apply completing after a newer edit knows not to clear the freeze.
    private int _thresholdEditGeneration;

    // Debounces auto-apply: each slider move restarts it; it fires once the user pauses.
    private readonly DispatcherTimer _thresholdApplyTimer;

    // Destroys the window once it has sat hidden long enough; never armed with _refreshTimer.
    private readonly DispatcherTimer _idleCloseTimer;

    public TimeSpan SinceHidden => DateTime.UtcNow - _hiddenAtUtc;

    public DashboardWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ConfigureThresholdRange();
        ConfigureWindowChrome();

        // The header mark is drawn at its own frame size rather than resampled from one asset.
        BrandMarkImage.Attach(BrandMark);

        // Track arc never changes — build it once here instead of every refresh tick.
        GaugeTrack.Data = BuildArcGeometry(GaugeCx, GaugeCy, GaugeRadius, GaugeStartAngle, GaugeSweep);

        // From the shared palette, so "charge limit" is the same brush here and in the history graph.
        GaugeStartTick.Stroke = AppColors.HistoryLimitBrush;
        GaugeStopTick.Stroke  = AppColors.HistoryLimitBrush;

        // The graph control has no reference to App/window-management — it only signals intent.
        HistoryGraph.ExpandRequested += (_, _) => _app.ShowHistoryWindow();

        // No room for these at 340px without crowding the plot; the pop-out window keeps them on.
        HistoryGraph.ShowGapMarkers    = false;
        HistoryGraph.ShowStressHeatmap = false;
        HistoryGraph.ShowCrosshair     = false;

        _refreshTimer       = new() { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => Refresh();

        _thresholdApplyTimer          = new() { Interval = TimeSpan.FromMilliseconds(700) };
        _thresholdApplyTimer.Tick    += (_, _) => CommitThresholds();

        _idleCloseTimer       = new() { Interval = DashboardIdlePolicy.IdleCloseAfter };
        _idleCloseTimer.Tick += (_, _) => CloseIfIdle();

        // A charge-control change made anywhere else must reach an open dashboard at once: the 5 s
        // poll cannot see state that isn't in the EC. Both fire off-thread, hence RunOnUi.
        ChargeControlService.StateChanged  += OnExternalStateChanged;
        TravelOverrideService.StateChanged += OnExternalStateChanged;

        // Keep Awake has its own RPC-free reconcile; neither event above covers it.
        KeepAwakeService.StateChanged += OnKeepAwakeStateChanged;

        // The panel's width is unknown when the buttons are built, and changes with the monitor's
        // DPI; the column count is recomputed from whatever it turns out to be.
        PresetButtonPanel.SizeChanged += (_, _) => LayoutPresetButtons();

        Activated += OnActivated;
        Closed    += (_, _) =>
        {
            _closed = true;   // gates RunOnUi: in-flight background reads must not touch a dead window
            _refreshTimer.Stop();
            _thresholdApplyTimer.Stop();
            // Also covers a destroy from below (a compositor reset), which can leave this armed.
            _idleCloseTimer.Stop();
            // Static events outlive this window, and App rebuilds a fresh one after every close.
            ChargeControlService.StateChanged  -= OnExternalStateChanged;
            TravelOverrideService.StateChanged -= OnExternalStateChanged;
            KeepAwakeService.StateChanged      -= OnKeepAwakeStateChanged;
        };
    }

    /// <summary>
    /// A charge-control change settled elsewhere. Refreshes only while on screen (it costs a blocking
    /// vendor RPC) and never during a slider edit, which <see cref="CommitThresholds"/> refreshes.
    /// </summary>
    private void OnExternalStateChanged() => RunOnUi(() =>
    {
        if (AppWindow.IsVisible && !_thresholdEditPending) Refresh();
    });

    /// <summary>A keep-awake session started, ended or expired. Reconciles unconditionally — no vendor RPC.</summary>
    private void OnKeepAwakeStateChanged() => RunOnUi(ApplyKeepAwakeBadge);

    /// <summary>
    /// Destroys the window after a long idle spell rather than holding its XAML tree and composition
    /// surface. App nulls its reference on Closed, so the next tray click rebuilds it.
    /// </summary>
    private void CloseIfIdle()
    {
        // One-shot: stop first, so nothing re-enters here whichever branch we take below.
        _idleCloseTimer.Stop();

        // Reclaiming idle memory must never be able to take the tray app down with it.
        try
        {
            if (_closed) return;   // destroyed from below already — Close() would throw on a dead window

            if (!DashboardIdlePolicy.ShouldClose(AppWindow.IsVisible, SinceHidden))
            {
                // Re-arm: DispatcherTimer promises no lower bound, so a tick can land early.
                if (!AppWindow.IsVisible) _idleCloseTimer.Start();
                return;
            }

            AppLog.Info($"Dashboard idle for {DashboardIdlePolicy.IdleCloseAfter:g} — closing to release its UI resources.");
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("DashboardWindow.CloseIfIdle", ex);
        }
    }

    // Set when the window closes (user action, or the framework destroying windows on a compositor
    // reset). Background reads marshalling back afterwards would throw, so RunOnUi drops them.
    private bool _closed;

    /// <summary>
    /// Sets the RangeSelector's bounds from code: assigning them in XAML throws a XamlParseException
    /// at LoadComponent on this Windows App SDK build. Guarded against a bogus apply on init.
    /// </summary>
    private void ConfigureThresholdRange() => WithSlidersSuppressed(() =>
    {
        ThresholdRange.Maximum       = 100;   // set Maximum first so Minimum never transiently exceeds it
        ThresholdRange.Minimum       = 5;
        ThresholdRange.StepFrequency = 5;
    });

    /// <summary>Raises <see cref="_updatingSliders"/> around a batch of programmatic RangeSelector
    /// writes, lowering it in a <c>finally</c> — the same shape <see cref="ApplyStatusBadges"/> uses,
    /// and for the same reason: a throw part-way through a hand-written pair latches the guard for
    /// the window's life, after which <see cref="OnThresholdRangeChanged"/> returns at its first line
    /// and no drag ever reaches the device.</summary>
    private void WithSlidersSuppressed(Action apply)
    {
        _updatingSliders = true;
        try { apply(); }
        finally { _updatingSliders = false; }
    }

    /// <summary>Immediate full refresh on a battery event, rather than waiting for the 5 s tick. UI thread only.</summary>
    internal void RefreshFromEvent() => Refresh();

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch: a throw inside a
    /// raw TryEnqueue callback tears the process down as an opaque stowed exception.
    /// </summary>
    private void RunOnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;   // window already destroyed — a stale callback has nothing to update
        try { action(); }
        catch (Exception ex) { AppLog.Error("DashboardWindow.RunOnUi", ex); }
    });

    public void ShowNearTray()
    {
        // A tick already queued can still arrive — CloseIfIdle's visibility check covers that.
        _idleCloseTimer.Stop();

        // Load data before making the window visible to avoid a "Loading…" flash.
        Refresh();

        // Size to the current content; ApplyStatusBadges re-places once the background read lands.
        PlaceWindow();

        AppWindow.Show();

        // Activate() fires the Activated event, which starts the refresh timer.
        Activate();
    }

    private void PlaceWindow()
    {
        // AppWindow works in physical pixels; the XAML content is in DIPs.
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

    /// <summary>Hides the window so a re-show is cheap; <see cref="_idleCloseTimer"/> reclaims it eventually.</summary>
    public void HideWindow()
    {
        _refreshTimer.Stop();
        _hiddenAtUtc = DateTime.UtcNow;
        // Restart, so each hide gets a full idle period measured from itself.
        _idleCloseTimer.Stop();
        _idleCloseTimer.Start();
        AppWindow.Hide();
    }

    private void ConfigureWindowChrome()
    {
        WindowChrome.ApplyPopup(this, resizable: false, alwaysOnTop: true);
        // Sets the taskbar/Alt-Tab icon; title-bar colouring is a no-op on this frameless popup.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);
    }

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

    private void Refresh()
    {
        // Battery info uses WinRT APIs that must stay on the UI thread.
        RefreshBatteryInfo();

        // In-process state only, and the remaining-time line needs this tick to count down.
        ApplyKeepAwakeBadge();

        // Settings plus a cached capability — no vendor RPC, so it belongs on this thread too.
        ApplyLidBadge();

        // The badge reads go through the vendor RPC — off-thread, then marshal back to apply.
        Task.Run(() =>
        {
            var chargeState      = ChargeThresholdService.Read();
            bool standbySupported = StandbyService.IsSupported;
            bool standbyOn        = StandbyService.IsRunning();
            RunOnUi(() => ApplyStatusBadges(chargeState, standbySupported, standbyOn));
        });
    }

    private void RefreshBatteryInfo()
    {
        try
        {
            var report = Battery.AggregateBattery.GetReport();

            int? pct = null;
            if (report.FullChargeCapacityInMilliwattHours is > 0 and { } full &&
                report.RemainingCapacityInMilliwattHours  is { } remaining)
            {
                pct = Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100);
            }

            BatteryPercentText.Text = pct.HasValue ? $"{pct}%" : "--";

            // The same call the tray icon path uses, so the two agree on when to show blue.
            bool onAC = BatteryStatsFormatter.IsOnAC(report.Status);
            UpdateGaugeArc(pct ?? 0, onAC);

            // Wattage is pop-out only: "AC Power (60W charger) · +45 W" overflows the 340px card.
            PowerSourceText.Text = BatteryStatsFormatter.FormatPowerSource(
                onAC, report.ChargeRateInMilliwatts ?? 0, adapterWattage: null);

            SetStatusGlyph(report.Status);

            // The label stays "REMAINING"; the value carries the direction, so it is never ambiguous.
            TimeRemainingText.Text = BatteryStatsFormatter.FormatTimeRemaining(
                report.ChargeRateInMilliwatts, report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);

            HistoryGraph.Render();
        }
        catch
        {
            BatteryPercentText.Text = "--";
            StatusGlyph.Text        = "!";
            StatusGlyph.Foreground  = AppColors.StatusUnknownBrush;
        }
    }

    /// <summary>Gauge-centre glyph and colour for the battery state; the tooltip carries the full word.</summary>
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

    /// <summary>
    /// Called on the UI thread after the background read. Guards the whole apply, since every switch
    /// write raises <c>Toggled</c>; try/finally so a throw part-way cannot latch the guard.
    /// </summary>
    private void ApplyStatusBadges(ChargeThresholdState? chargeState, bool standbySupported, bool standbyOn)
    {
        _updatingBadges = true;
        try { WriteStatusBadges(chargeState, standbySupported, standbyOn); }
        finally { _updatingBadges = false; }
    }

    private void WriteStatusBadges(ChargeThresholdState? chargeState, bool standbySupported, bool standbyOn)
    {
        // The same classifier the Settings page uses, so the two cannot drift. Hidden means no vendor
        // answered; Capable:false with a readable state keeps the badge and disables the switch.
        var surface = ThresholdCapabilityPolicy.Classify(chargeState, ChargeThresholdService.SupportsNumericThresholds);
        bool chargeVisible = surface != SmartChargeSurface.Hidden;
        SmartChargeBadge.Visibility = chargeVisible ? Visibility.Visible : Visibility.Collapsed;

        if (chargeVisible)
        {
            SetFeatureBadge(SmartChargeBadge, SmartChargeToggle, chargeState!.Enabled);
            SmartChargeToggle.IsEnabled = chargeState.Capable;
            SmartChargeDetailText.Text = (chargeState.Capable, chargeState.Enabled) switch
            {
                // Read-only BIOS setting: readable, but every write is refused.
                (false, _) => "Not supported",
                (true, true) when chargeState.HasStartThreshold
                    => $"{PresetLabel(chargeState)} · {chargeState.Start}% → {chargeState.Stop}%",
                // A mode-based vendor reports no start threshold, so a preset label means nothing here.
                (true, true) when chargeState.Stop > 0 => $"On — capped at about {chargeState.Stop} %",
                (true, true)  => "On — reading thresholds…",
                (true, false) => "Off — charges to 100%"
            };
        }

        // Each tick is decided independently: HP has no charge-start threshold and reports Start as 0,
        // so a shared condition would hide the stop tick — the one figure it does have.
        if (chargeState is { Capable: true, Enabled: true, Start: > 0 })
        {
            double startAngle = GaugeStartAngle + GaugeSweep * chargeState.Start / 100.0;
            GaugeStartTick.Data       = BuildTickGeometry(GaugeCx, GaugeCy, startAngle);
            GaugeStartTick.Visibility = Visibility.Visible;
        }
        else
        {
            GaugeStartTick.Visibility = Visibility.Collapsed;
        }

        if (chargeState is { Capable: true, Enabled: true, Stop: > 0 })
        {
            double stopAngle = GaugeStartAngle + GaugeSweep * chargeState.Stop / 100.0;
            GaugeStopTick.Data       = BuildTickGeometry(GaugeCx, GaugeCy, stopAngle);
            GaugeStopTick.Visibility = Visibility.Visible;
        }
        else
        {
            GaugeStopTick.Visibility = Visibility.Collapsed;
        }

        // Sync to the device only when the user isn't mid-edit, and only offer the picker where the
        // vendor takes arbitrary percentages — HP's coarse BIOS modes would ignore a dragged value.
        bool limitActive = chargeState is { Capable: true, Enabled: true };
        bool showSliders = limitActive && ChargeThresholdService.SupportsNumericThresholds;

        if (showSliders && !_thresholdEditPending && chargeState!.Start > 0 && chargeState.Stop > 0)
        {
            WithSlidersSuppressed(() =>
            {
                ThresholdRange.RangeStart = chargeState.Start;
                ThresholdRange.RangeEnd   = chargeState.Stop;
                StartValueText.Text = $"{chargeState.Start}%";
                StopValueText.Text  = $"{chargeState.Stop}%";
            });
        }
        ThresholdSliders.Visibility = showSliders ? Visibility.Visible : Visibility.Collapsed;

        if (limitActive && !ChargeThresholdService.SupportsNumericThresholds)
        {
            // Windows keeps reporting 100 % because HP lowers the reported full-charge capacity rather
            // than stopping the charge early — the note has to explain that, not just the number.
            ThresholdFixedNote.Text =
                $"Capped at about {chargeState!.Stop} % of design capacity. Windows still shows "
                + "100 % — this hardware lowers the reported full-charge capacity instead of "
                + "stopping early. Fixed limit, not an adjustable range; changes apply after a restart.";
            ThresholdFixedNote.Visibility = Visibility.Visible;
        }
        else
        {
            ThresholdFixedNote.Visibility = Visibility.Collapsed;
        }

        // Presets are start/stop percentages, so they need the same numeric surface the Settings
        // page gates on, and a vendor that refuses writes gets no activation button at all.
        bool showPresets = surface == SmartChargeSurface.Numeric && chargeState is { Capable: true };
        if (showPresets)
        {
            var presets = SettingsService.Current.Presets;
            BuildPresetButtons(presets);

            // Nothing is highlighted while the thresholds are no preset's — a travel override or
            // Smart Charge off included.
            string? activeName = ActivePresetPolicy.Match(presets, chargeState)?.Name;
            foreach (var button in PresetButtonPanel.Children.OfType<Button>())
            {
                bool isActive    = (string?)button.Tag == activeName;
                button.IsEnabled = !isActive;
                // Weight as well as colour: the filled chip must not be the only thing separating
                // the preset in use from the rest.
                button.FontWeight = isActive
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
        PresetButtonPanel.Visibility = showPresets ? Visibility.Visible : Visibility.Collapsed;

        // Shown whenever Smart Charge is capable, so it stays available to cancel while active.
        if (chargeState is { Capable: true })
        {
            TravelOverrideButton.Visibility = Visibility.Visible;
            TravelOverrideButton.Content = TravelOverrideService.ActionLabel;
        }
        else
        {
            TravelOverrideButton.Visibility = Visibility.Collapsed;
        }

        // A vendor with no standby scheduling hides the control rather than offering a dead switch.
        SmartStandbyBadge.Visibility = standbySupported ? Visibility.Visible : Visibility.Collapsed;
        if (standbySupported)
        {
            SetFeatureBadge(SmartStandbyBadge, SmartStandbyToggle, standbyOn);
            SmartStandbyDetailText.Text = standbyOn
                ? "Active — scheduling idle sleep"
                : "Off — always Modern Standby";
        }

        // Last: measuring earlier would size the window to a row that is about to disappear.
        if (AppWindow.IsVisible)
            PlaceWindow();
    }

    /// <summary>Which preset the device's current thresholds are, or "Custom" when none matches.</summary>
    private static string PresetLabel(ChargeThresholdState state) =>
        SettingsService.Read(s => ActivePresetPolicy.Match(s.Presets, state))?.Name ?? "Custom";

    /// <summary>(Re)builds the preset buttons. Returns untouched when the set hasn't changed, so the
    /// 5 s reconcile only repaints the highlight.</summary>
    private void BuildPresetButtons(IReadOnlyList<ThresholdPreset> presets)
    {
        var wanted = presets.Select(p => ThresholdPreset.FormatLabel(p.Name, p.Start, p.Stop)).ToList();
        if (wanted.SequenceEqual(_presetButtonLabels)) return;

        _presetButtonLabels = wanted;
        _presetGridShape    = default;
        PresetButtonPanel.Children.Clear();
        PresetButtonPanel.ColumnSpacing = PresetButtonLayout.Spacing;
        PresetButtonPanel.RowSpacing    = PresetButtonLayout.Spacing;

        // The button of the preset in use is disabled, so the DISABLED visual state is what paints
        // the marker — it overrides any Background/Foreground set on the button itself, which is why
        // the accent goes on these three template resources instead. Same brushes as the Settings
        // preset rows, so the two surfaces read alike. Set before the buttons are parented, so their
        // templates resolve them.
        PresetButtonPanel.Resources["ButtonBackgroundDisabled"]  = AppColors.AccentBrush;
        PresetButtonPanel.Resources["ButtonBorderBrushDisabled"] = AppColors.AccentBrush;
        PresetButtonPanel.Resources["ButtonForegroundDisabled"]  = AppColors.OnAccentBrush;

        foreach (var preset in presets)
        {
            var button = new Button
            {
                Tag                        = preset.Name,
                FontSize                   = 11,
                Padding                    = new Thickness(6, 2, 6, 2),
                MinWidth                   = 0,   // the default would spend width this popup hasn't got
                Height                     = 28,
                CornerRadius               = new CornerRadius(4),
                BorderThickness            = new Thickness(0),
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                VerticalAlignment          = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new TextBlock { Text = preset.Name, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            ToolTipService.SetToolTip(button, ThresholdPreset.FormatLabel(preset.Name, preset.Start, preset.Stop));
            button.Click += OnPresetButtonClick;
            PresetButtonPanel.Children.Add(button);
        }

        LayoutPresetButtons();
    }

    /// <summary>
    /// Places the buttons in equal-width columns spanning the whole panel, so every row — the last
    /// one included — ends flush with the travel-override button beneath. A last row holding fewer
    /// buttons than there are columns spreads them over the spare columns rather than leaving a gap.
    /// </summary>
    private void LayoutPresetButtons()
    {
        var buttons = PresetButtonPanel.Children.OfType<Button>().ToList();
        if (buttons.Count == 0) return;

        // ActualWidth is 0 until the first arrange, and the buttons are built before it; the fallback
        // is the same width the travel-override button gets, so the initial pass is already right.
        double available = PresetButtonPanel.ActualWidth > 0 ? PresetButtonPanel.ActualWidth : PresetPanelWidth;
        var shape = PresetButtonLayout.Choose(buttons.Count, available);
        if (shape == _presetGridShape) return;
        _presetGridShape = shape;

        PresetButtonPanel.ColumnDefinitions.Clear();
        PresetButtonPanel.RowDefinitions.Clear();
        for (int c = 0; c < shape.Columns; c++)
            PresetButtonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < shape.Rows; r++)
            PresetButtonPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int index = 0;
        for (int row = 0; row < shape.Rows; row++)
        {
            int inRow    = Math.Min(shape.Columns, buttons.Count - index);
            int baseSpan = shape.Columns / inRow;
            int extra    = shape.Columns % inRow;
            int column   = 0;
            for (int k = 0; k < inRow; k++, index++)
            {
                int span = baseSpan + (k < extra ? 1 : 0);
                Grid.SetRow(buttons[index], row);
                Grid.SetColumn(buttons[index], column);
                Grid.SetColumnSpan(buttons[index], span);
                column += span;
            }
        }
    }

    /// <summary>Applies a preset through the shared composition the tray and MQTT paths use, so every
    /// surface reflects it. Off the UI thread — the vendor write blocks.</summary>
    private void OnPresetButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;

        Task.Run(() =>
        {
            bool ok = false;
            try { ok = ChargeControlService.ApplyPresetByName(name); }
            catch (Exception ex) { AppLog.Error("DashboardWindow.OnPresetButtonClick", ex); }

            RunOnUi(() =>
            {
                if (!ok) SmartChargeDetailText.Text = "Error — check driver";
                Refresh();
            });
        });
    }

    /// <summary>Applies the badge colour and syncs its switch; the caller must hold <see cref="_updatingBadges"/>.</summary>
    private static void SetFeatureBadge(Border badge, ToggleSwitch toggle, bool on)
    {
        badge.Background = on ? AppColors.BadgeActiveBrush : AppColors.BadgeInactiveBrush;
        toggle.IsOn      = on;
    }

    /// <summary>
    /// Smart Charge on/off. Routes through the shared <see cref="ChargeControlService"/>, which fires
    /// StateChanged and so refreshes this window; only the throw path needs its own reconcile.
    /// </summary>
    private void OnSmartChargeToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;   // our own device sync, not a user action

        bool on = SmartChargeToggle.IsOn;
        Task.Run(() =>
        {
            try { ChargeControlService.SetSmartChargeEnabled(on); }
            catch (Exception ex)
            {
                AppLog.Error("DashboardWindow.OnSmartChargeToggled", ex);
                RunOnUi(Refresh);   // StateChanged never fired — put the switch back where the device is
            }
        });
    }

    /// <summary>
    /// Smart Standby on/off. There is no StateChanged for standby, so the trailing
    /// <see cref="Refresh"/> is what puts the switch back if the service control refused.
    /// </summary>
    private void OnSmartStandbyToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;

        bool on = SmartStandbyToggle.IsOn;
        Task.Run(() =>
        {
            try
            {
                // The bool is the only signal a refused service-control write gives.
                if (StandbyService.SetEnabled(on))
                    PowerLog.Event($"Smart Standby scheduling {(on ? "enabled" : "disabled")}", "dashboard toggle");
                else
                    PowerLog.Event($"Smart Standby scheduling was NOT {(on ? "enabled" : "disabled")} — the vendor write was refused",
                                   "dashboard toggle");
            }
            catch (Exception ex) { AppLog.Error("DashboardWindow.OnSmartStandbyToggled", ex); }
            finally { RunOnUi(Refresh); }
        });
    }

    // Second hit target for the switch beside it; inert when the vendor refuses writes.
    private void OnSmartChargeLabelTapped(object sender, TappedRoutedEventArgs e)
    {
        if (SmartChargeToggle.IsEnabled) SmartChargeToggle.IsOn = !SmartChargeToggle.IsOn;
    }

    private void OnSmartStandbyLabelTapped(object sender, TappedRoutedEventArgs e)
    {
        if (SmartStandbyToggle.IsEnabled) SmartStandbyToggle.IsOn = !SmartStandbyToggle.IsOn;
    }

    private void OnKeepAwakeLabelTapped(object sender, TappedRoutedEventArgs e) =>
        KeepAwakeToggle.IsOn = !KeepAwakeToggle.IsOn;   // never disabled: no vendor to refuse it

    /// <summary>Reconciles the whole Keep Awake badge, guarded like <see cref="ApplyStatusBadges"/> and for the same reason.</summary>
    private void ApplyKeepAwakeBadge()
    {
        _updatingBadges = true;
        try { WriteKeepAwakeBadge(); }
        finally { _updatingBadges = false; }
    }

    private void WriteKeepAwakeBadge()
    {
        var session = KeepAwakeService.Current;
        // Remember what is running, wherever it started, so the switch can resume it after an off.
        if (session is not null) _lastKeepAwake = session.Request;

        SetFeatureBadge(KeepAwakeBadge, KeepAwakeToggle, session is not null);
        KeepAwakeDetailText.Text = session is null
            ? "Off — normal sleep settings"
            : $"On — {KeepAwakePolicy.DescribeRemaining(DateTimeOffset.Now, session)}";

        BuildKeepAwakeChips();

        // KeepAwakeRequest is a record, so this compares the span itself, not where it started.
        foreach (var chip in KeepAwakePresetPanel.Children.OfType<ToggleButton>())
            chip.IsChecked = session is not null && Equals(chip.Tag, session.Request);
    }

    /// <summary>
    /// (Re)builds the chip row: the first four presets in Settings order plus a fixed "Net" — four is
    /// a width limit. Returns untouched when the set hasn't changed.
    /// </summary>
    private void BuildKeepAwakeChips()
    {
        List<KeepAwakeRequest> wanted =
        [
            .. SettingsService.Current.KeepAwakePresets.Take(4),
            new(KeepAwakeKind.UntilNetworkChange, null, null),
        ];
        if (wanted.SequenceEqual(_keepAwakeChips)) return;

        _keepAwakeChips = wanted;
        KeepAwakePresetPanel.Children.Clear();

        // Set before the chips are parented, so their templates resolve it.
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            KeepAwakePresetPanel.Resources[$"ToggleButtonBackgroundChecked{state}"] = AppColors.TimeScaleSelectedBrush;
            KeepAwakePresetPanel.Resources[$"ToggleButtonForegroundChecked{state}"] = AppColors.StatusChargingBrush;
        }

        foreach (var request in wanted)
        {
            var chip = new ToggleButton
            {
                Content         = KeepAwakePolicy.ShortLabel(request),
                Tag             = request,
                FontSize        = 11,
                Padding         = new Thickness(8, 3, 8, 3),
                MinWidth        = 0,   // the default would spend width this row hasn't got
                CornerRadius    = new CornerRadius(4),
                BorderThickness = new Thickness(0),
            };
            chip.Checked   += OnKeepAwakePresetChecked;
            chip.Unchecked += OnKeepAwakePresetUnchecked;
            KeepAwakePresetPanel.Children.Add(chip);
        }
    }

    /// <summary>Keep Awake on/off. Off→on resumes the last span that ran — the same ladder the tray toggle uses.</summary>
    private void OnKeepAwakeToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;   // our own sync, not a user action

        if (KeepAwakeToggle.IsOn)
            ActivateKeepAwake(_lastKeepAwake ?? KeepAwakePolicy.DefaultRequest(SettingsService.Current.KeepAwakePresets));
        else
            KeepAwakeService.Deactivate();
    }

    /// <summary><see cref="KeepAwakeService.Activate"/> is start-or-replace, so switching spans needs no cancel first.</summary>
    private void OnKeepAwakePresetChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;
        if (sender is ToggleButton { Tag: KeepAwakeRequest request }) ActivateKeepAwake(request);
    }

    /// <summary>Clicking the active chip cancels — a ToggleButton unchecking itself is that click.</summary>
    private void OnKeepAwakePresetUnchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;
        KeepAwakeService.Deactivate();
    }

    /// <summary>On the UI thread, unlike the two badges above: the service arms a timer, with no blocking RPC.</summary>
    private void ActivateKeepAwake(KeepAwakeRequest request)
    {
        _lastKeepAwake = request;
        try
        {
            // Raises StateChanged, whose handler reconciles the switch, the detail line and the chips.
            KeepAwakeService.Activate(request);
        }
        catch (Exception ex)
        {
            AppLog.Error("DashboardWindow.ActivateKeepAwake", ex);
            ApplyKeepAwakeBadge();   // StateChanged never fired — put the switch and chips back
        }
    }

    // Second hit target for the switch, as on the badges above.
    private void OnLidDelayLabelTapped(object sender, TappedRoutedEventArgs e) =>
        LidDelayToggle.IsOn = !LidDelayToggle.IsOn;

    /// <summary>Reconciles the whole Lid close badge, guarded like <see cref="ApplyStatusBadges"/> and for the same reason.</summary>
    private void ApplyLidBadge()
    {
        _updatingBadges = true;
        try { WriteLidBadge(); }
        finally { _updatingBadges = false; }
    }

    private void WriteLidBadge()
    {
        var s = SettingsService.Current;

        // Hidden where there is no lid — except while this app is still holding the override, which
        // has to stay switchable off. LidDashboardPolicy owns that rule.
        bool visible = LidDashboardPolicy.ShouldShow(LidDelayService.IsSupported, s.LidDelayEnabled, s.HasSavedLidAction);
        LidDelayBadge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        SetFeatureBadge(LidDelayBadge, LidDelayToggle, s.LidDelayEnabled);
        LidDelayDetailText.Text = LidDashboardPolicy.Describe(s.LidDelayEnabled, s.LidDelayMinutes,
                                                             s.LidDischargeEnabled, s.LidDischargeTargetPercent);

        BuildLidDelayChips(s.LidDelayMinutes);

        int? active = LidDashboardPolicy.ActiveChip(s.LidDelayEnabled, s.LidDelayMinutes);
        foreach (var chip in LidDelayPresetPanel.Children.OfType<ToggleButton>())
            chip.IsChecked = active is { } minutes && Equals(chip.Tag, minutes);
    }

    /// <summary>(Re)builds the delay chips. Returns untouched when the set hasn't changed.</summary>
    private void BuildLidDelayChips(int currentMinutes)
    {
        var wanted = LidDashboardPolicy.Chips(currentMinutes);
        if (wanted.SequenceEqual(_lidDelayChips)) return;

        _lidDelayChips = wanted;
        LidDelayPresetPanel.Children.Clear();

        // Set before the chips are parented, so their templates resolve it.
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            LidDelayPresetPanel.Resources[$"ToggleButtonBackgroundChecked{state}"] = AppColors.TimeScaleSelectedBrush;
            LidDelayPresetPanel.Resources[$"ToggleButtonForegroundChecked{state}"] = AppColors.StatusChargingBrush;
        }

        foreach (int minutes in wanted)
        {
            var chip = new ToggleButton
            {
                Content         = LidDashboardPolicy.ShortLabel(minutes),
                Tag             = minutes,
                FontSize        = 11,
                Padding         = new Thickness(8, 3, 8, 3),
                MinWidth        = 0,
                CornerRadius    = new CornerRadius(4),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(chip, $"Sleep {LidDashboardPolicy.ShortLabel(minutes)} after the lid closes");
            chip.Checked   += OnLidDelayChipChecked;
            chip.Unchecked += OnLidDelayChipUnchecked;
            LidDelayPresetPanel.Children.Add(chip);
        }
    }

    /// <summary>
    /// Lid-delay on/off. <see cref="LidDelayService.SetEnabled"/> owns the setting and the
    /// power-scheme write together, so this window never touches either directly; the reconcile after
    /// it is what puts the switch back when the scheme write was refused, since the setting is then
    /// left off rather than promising a delay the machine will not honour.
    /// </summary>
    private void OnLidDelayToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;   // our own sync, not a user action

        try { LidDelayService.SetEnabled(LidDelayToggle.IsOn); }
        catch (Exception ex) { AppLog.Error("DashboardWindow.OnLidDelayToggled", ex); }
        ApplyLidBadge();
    }

    /// <summary>Picking a delay also turns the feature on — the chip row is the quick way in, exactly
    /// as the keep-awake chips start a session.</summary>
    private void OnLidDelayChipChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;
        if (sender is not ToggleButton { Tag: int minutes }) return;

        try
        {
            SettingsService.Update(x => x.LidDelayMinutes = minutes);
            if (!SettingsService.Current.LidDelayEnabled) LidDelayService.SetEnabled(true);
        }
        catch (Exception ex) { AppLog.Error("DashboardWindow.OnLidDelayChipChecked", ex); }
        ApplyLidBadge();
    }

    /// <summary>Clicking the filled chip turns the delay off — a ToggleButton unchecking itself is that click.</summary>
    private void OnLidDelayChipUnchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingBadges) return;

        try { LidDelayService.SetEnabled(false); }
        catch (Exception ex) { AppLog.Error("DashboardWindow.OnLidDelayChipUnchecked", ex); }
        ApplyLidBadge();
    }

    /// <summary>Opens the Settings window. The popup auto-dismissing as it takes focus is deliberate.</summary>
    private void OnSettingsButton(object sender, RoutedEventArgs e) => _app.ShowSettingsWindow();

    /// <summary>
    /// RangeSelector keeps the thumbs from crossing but lets them end up equal, which
    /// <c>SetThresholds</c> rejects — so this enforces a 5-point gap by nudging the other thumb.
    /// </summary>
    private void OnThresholdRangeChanged(object sender, RangeChangedEventArgs e)
    {
        if (_updatingSliders) return;

        int start = (int)ThresholdRange.RangeStart;
        int stop  = (int)ThresholdRange.RangeEnd;

        if (stop - start < 5)
        {
            WithSlidersSuppressed(() =>
            {
                if (e.ChangedRangeProperty == RangeSelectorProperty.MinimumValue)
                {
                    // Push stop up; at the 100 ceiling, pull start back down instead.
                    stop = (int)(ThresholdRange.RangeEnd = Math.Min(start + 5, 100));
                    if (stop - start < 5) start = (int)(ThresholdRange.RangeStart = stop - 5);
                }
                else
                {
                    // Mirrored 5-point floor case for the stop thumb.
                    start = (int)(ThresholdRange.RangeStart = Math.Max(stop - 5, 5));
                    if (stop - start < 5) stop = (int)(ThresholdRange.RangeEnd = start + 5);
                }
            });
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

    /// <summary>Auto-applies the current slider values, debounced.</summary>
    private void CommitThresholds()
    {
        _thresholdApplyTimer.Stop();
        int gen   = _thresholdEditGeneration;   // this apply's edit; a newer drag bumps it
        int start = (int)ThresholdRange.RangeStart;
        int stop  = (int)ThresholdRange.RangeEnd;
        Task.Run(() =>
        {
            // Nothing here may skip the RunOnUi below: it releases _thresholdEditPending, and a latched
            // freeze stops Refresh syncing the sliders for the window's life.
            bool ok = false;
            try
            {
                // The shared composition the tray and MQTT paths use, so the drag reflects to them at once.
                ok = ChargeControlService.SetExplicitThresholds(start, stop);
            }
            catch (Exception ex) { AppLog.Error("DashboardWindow.CommitThresholds", ex); }

            RunOnUi(() =>
            {
                if (!ok)
                {
                    SmartChargeDetailText.Text = "Error — check driver";
                }
                // Only release the freeze if no newer edit started meanwhile, or Refresh snaps the
                // sliders back and the newer edit re-applies the revert.
                if (gen == _thresholdEditGeneration)
                {
                    _thresholdEditPending = false;
                    Refresh();
                }
            });
        });
    }

    private void OnTravelOverrideButton(object sender, RoutedEventArgs e)
    {
        if (TravelOverrideService.IsActive)
            TravelOverrideService.Cancel();
        else
            TravelOverrideService.Activate();

        // Re-read so the button label, badge, and sliders reflect the new state immediately.
        Refresh();
    }

    /// <summary>
    /// Colours the arc by charge level, with <paramref name="onAC"/> forcing blue. Thresholds and
    /// colours come from <see cref="GaugePalette"/>, as the tray icon's arc does.
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

    /// <summary>Short radial tick mark on the gauge arc at the given clock-face angle.</summary>
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

    /// <summary>Circular-arc geometry; angles follow clock-face convention (0° = 12 o'clock, clockwise).</summary>
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
