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

    // Cached tray icon state; -1 pct = not yet read.
    private (int Pct, bool Charging) _lastIconState = (-1, false);

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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

        // Start with the static red "L" icon; battery arc replaces it on first battery event.
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
        _menu = new TrayMenu(features, Shutdown);
        _trayIcon.ContextFlyout     = _menu.Flyout;
        _trayIcon.LeftClickCommand  = new RelayCommand(ToggleDashboard);
        _trayIcon.RightClickCommand = new RelayCommand(() => _menu!.RefreshState());

        _trayIcon.ForceCreate();
    }

    // ── Battery monitoring (dynamic icon + toast triggers) ────────────────────

    private void SubscribeBatteryEvents()
    {
        Battery.AggregateBattery.ReportUpdated += OnBatteryReportUpdated;
        // Trigger immediately with the current state so the icon is right from the start.
        OnBatteryReportUpdated(Battery.AggregateBattery, null!);
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

            // ── Dynamic tray icon ─────────────────────────────────────────────
            // Only re-render when something meaningful changed (avoids GDI churn every tick).
            if ((pct, charging) != _lastIconState)
            {
                _lastIconState = (pct, charging);
                UpdateTrayIcon(pct, charging);
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

    private void UpdateTrayIcon(int pct, bool charging)
    {
        try
        {
            var newIcon  = IconGenerator.RenderBatteryIcon(pct, charging);
            var oldIcon  = _currentBatteryIcon;
            _trayIcon!.Icon    = newIcon;
            _currentBatteryIcon = newIcon;
            oldIcon?.Dispose();
        }
        catch
        {
            // Icon rendering failure is non-fatal.
        }
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
                // Marshal to UI thread to update the tray tooltip / menu.
                _trayIcon?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_trayIcon is not null)
                        _trayIcon.ToolTipText = $"Lenovo Power Tray — update available: v{version}";
                    _menu?.SetUpdateBadge(version);
                });
            }).ConfigureAwait(false);
        });
    }

    // ── Dashboard ─────────────────────────────────────────────────────────────

    // A tray click that lands while the popup is open first deactivates the popup
    // (auto-hiding it); guard against immediately re-showing it from the same click.
    private const int ReopenGuardMs = 300;

    private void ToggleDashboard()
    {
        // Lazily create the window once and reuse it; subscribe Closed only at creation
        // so handlers don't accumulate on every click.
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow();
            _dashboard.Closed += (_, _) => _dashboard = null;
        }

        if (_dashboard.AppWindow.IsVisible)
            _dashboard.HideWindow();
        else if (_dashboard.SinceHidden.TotalMilliseconds > ReopenGuardMs)
            _dashboard.ShowNearTray();
        // else: this click is the same gesture that just auto-hid the popup — leave it hidden.
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Shutdown()
    {
        Battery.AggregateBattery.ReportUpdated -= OnBatteryReportUpdated;
        _currentBatteryIcon?.Dispose();
        ToastService.Cleanup();
        _trayIcon?.Dispose();
        Application.Current.Exit();
    }
}
