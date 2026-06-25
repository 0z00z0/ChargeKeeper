using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Windows.Devices.Power;
using Windows.System.Power;
using LenovoTray.Features;
using LenovoTray.Helpers;
using LenovoTray.Services;
using LenovoTray.UI;

namespace LenovoTray;

/// <summary>
/// Application entry point.  Owns the tray icon lifetime and coordinates the
/// dashboard popup and context menu.
/// </summary>
public partial class App : Application
{
    // Invisible WinUI 3 host — the framework exits when every window is closed.
    private Window?          _hostWindow;
    private TaskbarIcon?     _trayIcon;
    private DashboardWindow? _dashboard;
    private TrayMenu?        _menu;

    // Last known battery status — used to detect Charging→Idle transitions for toasts.
    private BatteryStatus _lastBatteryStatus = BatteryStatus.NotPresent;

    // Cached tray icon state; Pct = -1 means not yet read.
    private (int Pct, bool Charging) _lastIconState = (-1, false);

    // Guards the low-battery toast from firing repeatedly during the same discharge.
    // Reset with 5 % hysteresis so it re-fires on the next dip if the user charges briefly.
    private bool _lowBatteryWarningFired;

    public App()
    {
        InitializeComponent();

        // Last-resort diagnostics: log any unhandled managed exception to
        // %AppData%\LenovoPowerTray\crash.log before the process dies, so GUI crashes
        // (which surface only as an opaque 0xC000027B stowed exception in Event Viewer)
        // leave an actionable stack trace behind.
        UnhandledException += (_, e) =>
        {
            LogCrash("Application.UnhandledException", e.Exception);
            // Leave e.Handled = false: some failures aren't safely recoverable, and we'd
            // rather crash visibly than soldier on in a corrupt state.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LenovoPowerTray");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:u}] {source}\n{ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Opt native Win32 elements (the tray context menu) into system dark mode. Must run
        // before any UI is created so the menu HWND inherits the setting.
        NativeMethods.EnableDarkModeForNativeUi();

        // Capture the UI dispatcher while we're on the UI thread. Battery events fire on a
        // background thread and must marshal tray-icon updates back here (see UpdateTrayIcon).
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Configurable startup delay — keeps the app off the critical sign-in path on
        // machines where many elevated processes start simultaneously.
        int delay = SettingsService.Current.StartupDelaySeconds;
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(true);

        _hostWindow = new MainWindow();
        ToastService.Register();
        InitTrayIcon();
        SubscribeBatteryEvents();
        ScheduleUpdateCheck();
    }

    // ── Tray icon ─────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];

        // Start with the static red "L" icon; battery arc replaces it on the first battery event.
        var exeDir   = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var iconPath = IconGenerator.GenerateAndSaveTrayIcon(exeDir);
        _trayIcon.Icon = new System.Drawing.Icon(iconPath);

        // Left-click → dashboard. Right-click → native popup menu (refreshed first).
        IToggleFeature[] features =
        [
            new SmartChargeFeature(),
            new SmartStandbyFeature(),
            new AutoStartFeature(),
        ];
        _menu = new TrayMenu(features, Shutdown, ForceIconRefresh);
        _trayIcon.ContextFlyout     = _menu.Flyout;
        _trayIcon.LeftClickCommand  = new RelayCommand(ToggleDashboard);
        _trayIcon.RightClickCommand = new RelayCommand(() => _menu!.RefreshState());

        _trayIcon.ForceCreate();
    }

    // ── Battery monitoring (dynamic icon + toast triggers) ────────────────────

    private void SubscribeBatteryEvents()
    {
        Battery.AggregateBattery.ReportUpdated += OnBatteryReportUpdated;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Trigger immediately with the current state so the icon is right from the start.
        OnBatteryReportUpdated(Battery.AggregateBattery, null!);
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        // On resume the shell sometimes drops the tray icon WITHOUT broadcasting TaskbarCreated,
        // so H.NotifyIcon's built-in recovery never fires. A plain ForceCreate() can't help here:
        // its Create() early-returns while the library still believes the icon exists. Force a real
        // re-add by removing the stale registration first, then creating — the same TryRemove()+
        // Create() pair the library itself uses to recover from TaskbarCreated.
        _dispatcher?.TryEnqueue(() =>
        {
            try
            {
                if (_trayIcon is { } icon)
                {
                    icon.TrayIcon.TryRemove();
                    icon.TrayIcon.Create();
                }
            }
            catch (Exception ex)
            {
                LogCrash("OnPowerModeChanged", ex);
            }
            ForceIconRefresh();   // repaint the battery arc onto the (re)created icon
        });
    }

    private void OnBatteryReportUpdated(Battery sender, object args)
    {
        try
        {
            var report = sender.GetReport();

            // ── Compute percentage ────────────────────────────────────────────
            int pct = 0;
            if (report.FullChargeCapacityInMilliwattHours is > 0 and { } full &&
                report.RemainingCapacityInMilliwattHours  is { } remaining)
            {
                pct = Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100);
            }

            bool charging = report.Status is BatteryStatus.Charging or BatteryStatus.Idle;

            // ── Battery history ───────────────────────────────────────────────
            BatteryHistoryService.Record(pct);

            // ── Dynamic tray icon ─────────────────────────────────────────────
            // Only re-render when something meaningful changed (avoids GDI churn every tick).
            if ((pct, charging) != _lastIconState)
            {
                _lastIconState = (pct, charging);
                UpdateTrayIcon(pct, charging);
            }

            // ── Dashboard live update ──────────────────────────────────────────
            // Push an immediate refresh to the open dashboard so power connect/disconnect
            // and percentage changes are reflected at once, without waiting for the 5 s timer.
            if (_dashboard is not null)
            {
                // Re-read _dashboard on the UI thread (where the Closed handler nulls it). Touching a
                // window that closed after this tick captured it throws InvalidOperationException via
                // combase — fatal inside a raw dispatcher callback. RunOnUi also catches as a backstop.
                RunOnUi(() =>
                {
                    if (_dashboard is { } dash && dash.AppWindow.IsVisible)
                        dash.RefreshFromEvent();
                });
            }

            // ── Low-battery warning ───────────────────────────────────────────
            var s = SettingsService.Current;
            if (s.LowBatteryWarningEnabled &&
                report.Status == BatteryStatus.Discharging &&
                pct > 0 &&
                pct <= s.LowBatteryWarningPct &&
                !_lowBatteryWarningFired)
            {
                _lowBatteryWarningFired = true;
                ToastService.NotifyLowBattery(pct);
            }
            // Reset the guard with hysteresis so it can fire again after a partial charge.
            else if (pct > s.LowBatteryWarningPct + 5)
            {
                _lowBatteryWarningFired = false;
            }

            // ── Toast: charging complete ──────────────────────────────────────
            // Fire when the battery transitions from Charging → Idle (threshold or full).
            if (_lastBatteryStatus == BatteryStatus.Charging &&
                report.Status      == BatteryStatus.Idle)
            {
                var state   = ChargeThresholdService.Read();
                int stopPct = state is { Enabled: true, Stop: > 0 } ? state.Stop : 100;
                ToastService.NotifyChargeComplete(stopPct);
            }

            // ── Travel override revert ────────────────────────────────────────
            // Feed every reading to the service; it owns the "revert once charging completes"
            // decision (Charging→Idle edge, or Idle at 100 %) and the fire-once latch.
            TravelOverrideService.OnBatteryReport(pct, report.Status);

            // ── Tray tooltip ──────────────────────────────────────────────────
            _lastOnAC           = charging;   // Charging or Idle ⇒ on AC (same expression as the icon)
            _lastRateMW         = report.ChargeRateInMilliwatts ?? 0;
            _lastThresholdState = ChargeThresholdService.Read();
            UpdateTooltip(pct, report.RemainingCapacityInMilliwattHours,
                               report.FullChargeCapacityInMilliwattHours);

            // ── Toast: AC connected ───────────────────────────────────────────
            if (_lastBatteryStatus == BatteryStatus.Discharging &&
                report.Status      == BatteryStatus.Charging)
            {
                ToastService.NotifyChargingStarted();
            }

            _lastBatteryStatus = report.Status;
        }
        catch
        {
            // Battery API failure is non-fatal — the tray icon just stays as-is.
        }
    }

    private System.Drawing.Icon? _currentBatteryIcon;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    // Tooltip state — rebuilt on every battery tick and pushed to the tray icon.
    private string  _lastTooltip             = "";
    private string? _updateAvailableVersion;
    private int     _lastRateMW;   // milliwatts; positive = charging, negative = draining
    private bool    _lastOnAC;
    private ChargeThresholdState? _lastThresholdState;
    private static readonly string _appVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    private void UpdateTrayIcon(int pct, bool charging)
    {
        // The tray icon is a UI object and must be mutated on the UI thread. Battery
        // ReportUpdated fires on a background (MTA) thread, so marshal the whole
        // render → swap → dispose onto the dispatcher. Mutating/disposing the icon off-thread
        // races with the shell and faults the native tray/GDI handle — an access violation that
        // bypasses managed try/catch and kills the process (observed when unplugging AC power).
        if (_dispatcher is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() => UpdateTrayIcon(pct, charging));
            return;
        }

        try
        {
            var mode    = SettingsService.Current.IconMode;
            var newIcon = IconGenerator.RenderBatteryIcon(pct, charging, mode);
            var oldIcon = _currentBatteryIcon;
            _trayIcon!.Icon     = newIcon;
            _currentBatteryIcon = newIcon;
            oldIcon?.Dispose();
        }
        catch
        {
            // Icon rendering failure is non-fatal.
        }
    }

    /// <summary>
    /// Forces an immediate tray icon re-render using the last known battery state.
    /// Called when the icon mode is toggled from the tray menu or settings panel.
    /// </summary>
    internal void ForceIconRefresh()
    {
        if (_lastIconState.Pct >= 0)
            UpdateTrayIcon(_lastIconState.Pct, _lastIconState.Charging);
    }

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch. An exception
    /// thrown inside a DispatcherQueue callback is NOT surfaced to Application.UnhandledException —
    /// it tears the process down as an opaque 0xC000027B stowed exception (nothing reaches crash.log).
    /// That is the root cause of the "tray icon vanishes" reports: e.g. a battery tick refreshing a
    /// dashboard window that just closed throws InvalidOperationException via combase. Catching here
    /// keeps the tray alive; the failure is logged instead of fatal.
    /// </summary>
    private void RunOnUi(Action action)
    {
        var dq = _dispatcher;
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { LogCrash("RunOnUi", ex); }
        });
    }

    private void UpdateTooltip(int pct, int? remainingMwh, int? fullMwh)
    {
        var lines = new System.Text.StringBuilder();

        // ⚡ Lenovo Power Tray  v1.0.x
        lines.Append($"⚡ Lenovo Power Tray  v{_appVersion}");

        // ⚡ AC · 75%  ·  +45 W   (on AC)
        // 🔋 75%  ·  −18 W        (on battery)
        // Glyph follows the power source so it never contradicts the AC label, and the rate is
        // shown only in its expected direction via the shared formatter (mW below 1 W, real −).
        string chargeIcon = _lastOnAC ? "⚡" : "🔋";
        lines.Append(_lastOnAC
            ? $"\n{chargeIcon} AC · {pct}%"
            : $"\n{chargeIcon} {pct}%");
        string? rate = (_lastOnAC && _lastRateMW > 0) || (!_lastOnAC && _lastRateMW < 0)
            ? PowerFormat.SignedRate(_lastRateMW)
            : null;
        if (rate is not null)
            lines.Append($"  ·  {rate}");

        // ⏱ ~2h 15m remaining  /  ⏱ ~45m to full
        if (Math.Abs(_lastRateMW) >= 100 && remainingMwh is { } rem && fullMwh is > 0 and { } full)
        {
            double h = _lastRateMW < 0
                ? rem        / (double)Math.Abs(_lastRateMW)
                : (full-rem) / (double)_lastRateMW;
            if (h > 0 && h < 99)
            {
                var ts    = TimeSpan.FromHours(h);
                var tstr  = ts.TotalHours >= 1 ? $"~{(int)ts.TotalHours}h {ts.Minutes:D2}m" : $"~{ts.Minutes}m";
                var label = _lastRateMW < 0 ? "remaining" : "to full";
                lines.Append($"\n⏱ {tstr} {label}");
            }
        }

        // ⚡ Charging to 100%   OR   ⚙ Smart Charge: 70–80%
        if (TravelOverrideService.IsActive)
            lines.Append("\n⚡ Charging to 100%");
        else if (_lastThresholdState is { Enabled: true, Start: > 0, Stop: > 0 } sc)
            lines.Append($"\n⚙ Smart Charge: {sc.Start}–{sc.Stop}%");

        // ⬆ Update available: vX.Y.Z
        if (_updateAvailableVersion is { } uv)
            lines.Append($"\n⬆ Update available: v{uv}");

        var tooltip = lines.ToString();

        // NOTIFYICONDATA.szTip holds at most 127 UTF-16 chars (+ NUL); clamp so the shell doesn't
        // silently truncate. Don't split a surrogate pair (the emoji glyphs are 2 code units each).
        const int MaxTipLength = 127;
        if (tooltip.Length > MaxTipLength)
        {
            int cut = MaxTipLength - 1;                       // leave room for the ellipsis
            if (char.IsHighSurrogate(tooltip[cut - 1])) cut--;
            tooltip = string.Concat(tooltip.AsSpan(0, cut), "…");
        }

        if (tooltip == _lastTooltip) return;
        _lastTooltip = tooltip;

        RunOnUi(() =>
        {
            if (_trayIcon is not null)
                _trayIcon.ToolTipText = tooltip;
        });
    }

    // ── Update check ──────────────────────────────────────────────────────────

    private void ScheduleUpdateCheck()
    {
        // Fire-and-forget: delay 30 s so the check doesn't slow down the cold-start path.
        // The async lambda ensures the inner CheckAsync Task is awaited (ContinueWith would
        // have returned Task<Task> and orphaned the HTTP request).
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await UpdateCheckService.CheckAsync(version =>
            {
                _updateAvailableVersion = version;
                // Refresh tooltip to include the update notice; refresh menu badge on UI thread.
                UpdateTooltip(_lastIconState.Pct < 0 ? 0 : _lastIconState.Pct, null, null);
                RunOnUi(() => _menu?.SetUpdateBadge(version));
            }).ConfigureAwait(false);
        });
    }

    // ── Dashboard ─────────────────────────────────────────────────────────────

    // A tray click that lands while the popup is open first deactivates the popup
    // (auto-hiding it); guard against immediately re-showing it from the same click.
    private const int ReopenGuardMs = 300;

    private void ToggleDashboard()
    {
        // Guard the whole open path: a failure building or showing the popup must not take
        // down the tray app. Log it and stay alive so the menu/icon keep working.
        try
        {
            // Lazily create the window once and reuse it; subscribe Closed only at creation
            // so handlers don't accumulate on every click.
            if (_dashboard is null)
            {
                _dashboard = new DashboardWindow(this);
                _dashboard.Closed += (_, _) => _dashboard = null;
            }

            if (_dashboard.AppWindow.IsVisible)
                _dashboard.HideWindow();
            else if (_dashboard.SinceHidden.TotalMilliseconds > ReopenGuardMs)
                _dashboard.ShowNearTray();
            // else: this click is the same gesture that just auto-hid the popup — leave it hidden.
        }
        catch (Exception ex)
        {
            LogCrash("ToggleDashboard", ex);
            _dashboard = null;   // drop the half-built window so the next click retries cleanly
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Shutdown()
    {
        Battery.AggregateBattery.ReportUpdated -= OnBatteryReportUpdated;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _currentBatteryIcon?.Dispose();
        ToastService.Cleanup();
        _trayIcon?.Dispose();
        Application.Current.Exit();
    }
}
