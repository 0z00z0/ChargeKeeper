using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Power;
using Windows.Graphics;
using Windows.System.Power;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>Bigger, resizable pop-out of the battery-history graph, hosting the same
/// <see cref="BatteryHistoryGraphControl"/> as the dashboard. Frameless, and dismissed like the tray
/// popup: it closes itself on focus loss. Opened from a visible dashboard it grows out of that rect
/// and shrinks back into it on dismissal; with no origin rect it opens and closes flat.</summary>
public sealed partial class BatteryHistoryWindow : Window
{
    // Minimum keeps the 28px/36px axis-label columns plus a usable plot from being squeezed to nothing.
    private const int MinWidth  = 640;
    private const int MinHeight = 420;

    // Long enough to see the graph grow out of the dashboard, short enough not to lag the click.
    private const int AnimDurationMs = 340;
    private const int AnimTickMs     = 10;

    private readonly DispatcherTimer _refreshTimer;
    private DispatcherTimer? _animTimer;

    // Consumed on the FIRST Activated event: a clock started in the ctor runs its whole duration out
    // during realisation. A null _originRect also means there is nothing to retract into at close.
    private readonly RectInt32 _finalRect;
    private readonly RectInt32? _originRect;
    private bool _animStarted;

    // Set on Closed, so a stray animation tick can't touch a dead AppWindow.
    private bool _closing;

    // Set when a focus-loss dismissal begins: the window can deactivate again during its own retract.
    private bool _dismissing;

    // A Deactivated before the first real Activated is spurious — the window hasn't finished taking
    // focus. Treating it as a dismissal made a fast double-click open the pop-out and close it again.
    private bool _everActivated;

    /// <param name="originRect">The tray dashboard's on-screen rect (physical px) to animate open
    /// from, or null to place the window at its final rect directly.</param>
    public BatteryHistoryWindow(RectInt32? originRect = null)
    {
        InitializeComponent();
        Title = "Battery History — ChargeKeeper";
        ConfigureWindowChrome();

        _finalRect = ComputeFinalRect();
        if (originRect is { } origin)
        {
            // Place at the origin now, so the first painted frame is the small dashboard rect.
            _originRect = origin;
            AppWindow.MoveAndResize(origin);
        }
        else
        {
            AppWindow.MoveAndResize(_finalRect);
        }

        // Render immediately so the window doesn't show a blank canvas before the first tick.
        try { HistoryGraph.Render(); }
        catch (Exception ex) { AppLog.Error("BatteryHistoryWindow.Render", ex); }
        RefreshStats();
        ApplyGraphDisplay(SettingsService.Current.GraphDisplay);

        _refreshTimer       = new() { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) =>
        {
            try { HistoryGraph.Render(); }
            catch (Exception ex) { AppLog.Error("BatteryHistoryWindow.Render", ex); }
            RefreshStats();
        };
        _refreshTimer.Start();

        Activated += OnActivated;
        Closed    += (_, _) =>
        {
            _closing = true;
            _refreshTimer.Stop();
            _animTimer?.Stop();
        };
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_everActivated) return;   // spurious pre-activation deactivate — see field doc
            Dismiss();
            return;
        }

        _everActivated = true;

        // _originRect non-null ⇔ an open animation was requested; _animStarted latches it one-shot.
        if (!_animStarted && _originRect is { } openOrigin)
        {
            _animStarted = true;
            AnimateRect(openOrigin, _finalRect, HistoryGraph.Render);
        }
    }

    /// <summary>Escape takes the same path as clicking away, so the key introduces no third
    /// behaviour of its own.</summary>
    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Dismiss();
    }

    /// <summary>Retracts into the origin rect, then closes. Close, not Hide: App's singleton recreates
    /// the window cheaply, and with no title bar there is no other dismissal. Latched, because the
    /// window can deactivate again during its own retract.</summary>
    private void Dismiss()
    {
        if (_closing || _dismissing) return;
        _dismissing = true;

        if (_originRect is { } origin)
        {
            // Retract from wherever the window currently is — it's resizable, so not _finalRect.
            var current = new RectInt32(
                AppWindow.Position.X, AppWindow.Position.Y,
                AppWindow.Size.Width, AppWindow.Size.Height);
            AnimateRect(current, origin, Close);
        }
        else
        {
            Close();
        }
    }

    /// <summary>Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch: an
    /// exception inside a raw TryEnqueue callback bypasses Application.UnhandledException.</summary>
    private void RunOnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing) return;
        try { action(); }
        catch (Exception ex) { AppLog.Error("BatteryHistoryWindow.RunOnUi", ex); }
    });

    /// <summary>Refreshes POWER/REMAINING; only a cold adapter-wattage cache RPCs, off the UI thread.</summary>
    private void RefreshStats()
    {
        try
        {
            var report = Battery.AggregateBattery.GetReport();
            bool onAC  = BatteryStatsFormatter.IsOnAC(report.Status);
            int  rateMw = report.ChargeRateInMilliwatts ?? 0;

            int? watts = ChargerInfoService.CachedWattage;   // never RPCs — UI-thread safe
            PowerSourceText.Text   = BatteryStatsFormatter.FormatPowerSource(onAC, rateMw, watts);
            TimeRemainingText.Text = BatteryStatsFormatter.FormatTimeRemaining(
                report.ChargeRateInMilliwatts, report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);

            if (onAC && watts is null)
                Task.Run(() =>
                {
                    // Re-enter so the repaint reads a FRESH report, not the onAC/rateMw captured
                    // before the RPC.
                    if (ChargerInfoService.GetRatedWattage() is not null)
                        RunOnUi(RefreshStats);
                });
        }
        catch (Exception ex)
        {
            AppLog.Error("BatteryHistoryWindow.RefreshStats", ex);
        }
    }

    /// <summary>Applies the clicked Battery/System button, whose <c>Tag</c> names the
    /// <see cref="GraphDisplay"/>.</summary>
    private void OnGraphDisplayButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagName } ||
            !Enum.TryParse<GraphDisplay>(tagName, out var display))
            return;

        SettingsService.Update(s => s.GraphDisplay = display);
        ApplyGraphDisplay(display);
    }

    /// <summary>Shows the chosen graph and hides the other, highlights the matching button, and
    /// renders whichever control is now visible so the switch shows data immediately.</summary>
    private void ApplyGraphDisplay(GraphDisplay display)
    {
        HistoryGraph.Visibility     = display == GraphDisplay.Battery ? Visibility.Visible : Visibility.Collapsed;
        PerformanceGraph.Visibility = display == GraphDisplay.System  ? Visibility.Visible : Visibility.Collapsed;
        SetSelectedDisplayButton(display);

        if (display == GraphDisplay.Battery)
        {
            try { HistoryGraph.Render(); }
            catch (Exception ex) { AppLog.Error("BatteryHistoryWindow.Render", ex); }
        }
        else
        {
            PerformanceGraph.ApplySettings();
        }
    }

    /// <summary>Highlights the button for <paramref name="display"/>; deselecting clears the local
    /// value so its style wins. Mirrors BatteryHistoryGraphControl.SetSelectedScaleButton.</summary>
    private void SetSelectedDisplayButton(GraphDisplay display)
    {
        foreach (var button in GraphDisplayPanel.Children.OfType<Button>())
        {
            bool selected = button.Tag is string tagName &&
                             Enum.TryParse<GraphDisplay>(tagName, out var buttonDisplay) &&
                             buttonDisplay == display;
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

    private void ConfigureWindowChrome()
    {
        WindowChrome.ApplyPopup(this, resizable: true, alwaysOnTop: false);
        // A no-op on this frameless popup, but keeps the call site uniform with the other windows.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);
    }

    /// <summary>Final placement: ~70% × 65% of the monitor under the cursor with a DIP floor, centred
    /// there. Returns the OUTER rect, which the open animation interpolates. Not
    /// <see cref="NativeMethods.CentreRectOnCursorMonitor"/>: that caps a DIP target to the work area,
    /// whereas here the floor may win on a small screen.</summary>
    private RectInt32 ComputeFinalRect()
    {
        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();

        int w = Math.Max((int)(MinWidth  * scale), (int)((work.Right  - work.Left) * 0.70));
        int h = Math.Max((int)(MinHeight * scale), (int)((work.Bottom - work.Top)  * 0.65));

        return NativeMethods.CentreInWorkArea(work, w, h);
    }

    /// <summary>Animates the <see cref="AppWindow"/> rect on an ease-out curve, then invokes
    /// <paramref name="onComplete"/>. Any animation in flight is stopped first, so a click-away
    /// landing mid-open can't leave two timers fighting over the same AppWindow. Ticks deliberately
    /// don't redraw the graph — the control's own SizeChanged debounce absorbs them.</summary>
    private void AnimateRect(RectInt32 from, RectInt32 to, Action onComplete)
    {
        _animTimer?.Stop();
        AppWindow.MoveAndResize(from);   // ensure we start exactly at "from"

        long startMs = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AnimTickMs) };
        _animTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_closing) { timer.Stop(); return; }   // window torn down mid-animation

            // Wall-clock progress, not per-tick increments: DispatcherTimer ticks can be late.
            double t = Math.Min(1.0, (Environment.TickCount64 - startMs) / (double)AnimDurationMs);
            if (t >= 1.0)
            {
                timer.Stop();
                AppWindow.MoveAndResize(to);   // snap exactly to target — no rounding drift
                onComplete();
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3);   // ease-out cubic: fast start, gentle landing
            AppWindow.MoveAndResize(new RectInt32(
                Lerp(from.X,      to.X,      eased),
                Lerp(from.Y,      to.Y,      eased),
                Lerp(from.Width,  to.Width,  eased),
                Lerp(from.Height, to.Height, eased)));
        };
        timer.Start();
    }

    private static int Lerp(int from, int to, double t) => from + (int)Math.Round((to - from) * t);
}
