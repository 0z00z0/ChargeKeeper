using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Windows.Devices.Power;
using Windows.System.Power;
using ChargeKeeper.Features;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ChargeKeeper.UI;

namespace ChargeKeeper;

/// <summary>
/// Application entry point.  Owns the tray icon lifetime and coordinates the
/// dashboard popup and context menu.
/// </summary>
public partial class App : Application
{
    // Invisible WinUI 3 host — the framework exits when every window is closed.
    private Window?              _hostWindow;
    private TaskbarIcon?         _trayIcon;
    private DashboardWindow?     _dashboard;
    private BatteryHistoryWindow? _historyWindow;
    private TrayMenu?            _menu;

    // Last known battery status — used to detect Charging→Idle transitions for toasts.
    private BatteryStatus _lastBatteryStatus = BatteryStatus.NotPresent;

    // Cached tray icon state; Pct = -1 means not yet read.
    private (int Pct, bool Charging) _lastIconState = (-1, false);

    // Guards the low-battery toast from firing repeatedly during the same discharge.
    // Reset with 5 % hysteresis so it re-fires on the next dip if the user charges briefly.
    private bool _lowBatteryWarningFired;

    // ── Teardown forensics + self-heal ─────────────────────────────────────────
    // Diagnosed 2026-07-02: on AC unplug (and standby), this machine's Intel GPU can fault during
    // the power transition (LiveKernelEvent 141). The compositor connection under the app dies and
    // WinUI tears the process down as a CLEAN exit — no exception reaches any managed handler, no
    // WER record, nothing in any log. The flags below let OnProcessExit tell that silent framework
    // teardown apart from the two legitimate exits (tray-menu Exit, Windows logoff/shutdown) and
    // relaunch a fresh instance for the illegitimate one.
    private static volatile bool _intentionalExit;
    private static volatile bool _sessionEnding;
    private static readonly DateTime _processStartUtc = DateTime.UtcNow;

    // Watchdog probes (see WatchdogTask.cs) start this exe every ~5 minutes; when the app is
    // alive or was deliberately exited, the probe must exit without leaving a trace in app.log —
    // 288 "duplicate instance" + "ProcessExit" pairs a day would bury the real forensics.
    private static volatile bool _quietExit;

    // ── Single-instance guard ───────────────────────────────────────────────────
    // Diagnosed 2026-07-03: after install, the elevated post-install launch can go unnoticed (the
    // UAC prompt isn't always where the user is looking), so they launch the app again themselves
    // — nothing stopped a second process from running alongside the first. Two instances would
    // both claim the tray icon and write to history.csv with no cross-process locking. Held for
    // the whole process lifetime; Windows releases it automatically on termination (clean exit,
    // crash, or kill) — no explicit Release() needed, which also sidesteps Mutex's normal
    // same-thread-release requirement (ProcessExit handlers aren't guaranteed to run on the
    // thread that acquired it).
    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = "Local\\ChargeKeeper.SingleInstance";

    /// <summary>
    /// Retries a non-blocking mutex acquire for a few seconds before giving up. A single instant
    /// check isn't enough: the self-heal relaunch (<see cref="OnProcessExit"/>) spawns a new
    /// process while the OLD one may still be a few milliseconds from fully terminating and
    /// releasing the mutex — an instant WaitOne(0) would then wrongly treat that legitimate
    /// relaunch as "already running" and exit.
    /// </summary>
    private static async Task<bool> AcquireSingleInstanceLockAsync(int attempts = 15)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
            if (_singleInstanceMutex.WaitOne(TimeSpan.Zero))
                return true;

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            await Task.Delay(200).ConfigureAwait(true);
        }
        return false;
    }

    public App()
    {
        // Must run before ANYTHING touches %AppData%\ChargeKeeper: the first AppLog write (or a
        // settings/history read) would create the new folder, and Directory.Move refuses to move
        // onto an existing destination — which would strand the user's settings + battery history
        // in the old folder forever.
        MigrateLegacyAppDataFolder();

        InitializeComponent();

        // THE key lifetime decision for a tray app (confirmed by app.log forensics 2026-07-02):
        // during a GPU/compositor reset (AC unplug, standby), the framework can destroy ALL our
        // windows from below — and with the default OnLastWindowClose policy it then tears the
        // whole process down as a clean exit ("Dashboard window closed" → "Host window closed" →
        // "DispatcherQueue.ShutdownStarting" within the same second). A tray app's lifetime must
        // be anchored to the tray icon (a Win32 construct, not a XAML window), so only an explicit
        // Application.Exit() — the tray menu's Exit — may end the process. The dashboard already
        // recreates itself lazily on the next tray click when its window has been destroyed.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Last-resort diagnostics: log any unhandled managed exception to
        // %AppData%\ChargeKeeper\app.log before the process dies, so GUI crashes
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

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>
    /// One-time migration for the rename from Lenovo Power Tray to ChargeKeeper: moves the old
    /// <c>%AppData%\LenovoPowerTray</c> folder to <c>%AppData%\ChargeKeeper</c> so settings,
    /// battery history, and logs survive the upgrade. Runs only when the old folder exists and the
    /// new one doesn't (i.e. exactly once); a failure is logged and never crashes startup — the
    /// app then simply starts with fresh defaults, same as a clean install.
    /// </summary>
    private static void MigrateLegacyAppDataFolder()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir  = Path.Combine(appData, "LenovoPowerTray");
            var newDir  = Path.Combine(appData, "ChargeKeeper");
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;

            Directory.Move(oldDir, newDir);
            AppLog.Info("Migrated legacy %AppData%\\LenovoPowerTray folder to %AppData%\\ChargeKeeper.");
        }
        catch (Exception ex)
        {
            // Logged only AFTER the move attempt — AppLog itself creates the new folder.
            AppLog.Error("MigrateLegacyAppDataFolder", ex);
        }
    }

    /// <summary>
    /// Fires on every CLEAN process teardown (it does NOT fire for hard kills like taskkill or a
    /// native access violation — which is exactly what makes it the right hook: an installer's
    /// taskkill must not trigger a relaunch that races the file copy). If the exit was neither
    /// user-initiated nor a logoff/shutdown, it is the silent compositor-loss teardown described
    /// above — spawn a replacement instance. The dying process is already elevated, so the child
    /// inherits elevation without a UAC prompt.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        if (_quietExit) return;

        var uptime = DateTime.UtcNow - _processStartUtc;
        AppLog.Info($"ProcessExit: clean teardown after {uptime:hh\\:mm\\:ss} " +
                    $"(intentional={_intentionalExit}, sessionEnding={_sessionEnding}).");

        if (_intentionalExit || _sessionEnding) return;

        // Crash-loop guard: allow at most 3 auto-relaunches per 10 minutes, tracked in a small
        // state file. (A minimum-uptime gate was tried first and misfired: a GPU-reset teardown
        // can hit a process that is only seconds old — e.g. launch, open dashboard, unplug —
        // and the young-process rule wrongly suppressed the one relaunch that mattered.)
        if (!TryRecordRelaunch())
        {
            AppLog.Info("Not relaunching: 3 auto-relaunches within 10 minutes — giving up.");
            return;
        }

        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe, AutoRelaunchArg) { UseShellExecute = false });
            AppLog.Info("Unexpected teardown — relaunched a fresh instance.");
        }
        catch (Exception ex)
        {
            AppLog.Error("OnProcessExit.Relaunch", ex);
        }
    }

    private const string AutoRelaunchArg = "--auto-relaunch";

    /// <summary>
    /// Sliding-window rate limiter for the self-heal relaunch: returns false once 3 relaunches
    /// have happened within the last 10 minutes. Timestamps persist in a file because each check
    /// runs in a NEW process — in-memory state can't span the relaunch chain it is limiting.
    /// </summary>
    private static bool TryRecordRelaunch()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChargeKeeper", "relaunch-history.txt");

            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
            var recent = new List<long>();
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                    if (long.TryParse(line, out var ts) && ts >= cutoff)
                        recent.Add(ts);

            if (recent.Count >= 3) return false;

            recent.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, recent.Select(t => t.ToString()));
            return true;
        }
        catch
        {
            // If the bookkeeping itself fails, err on the side of bringing the tray back.
            return true;
        }
    }

    // Delegates to the shared AppLog (originally this method wrote crash.log directly; AppLog
    // generalised that into an Info/Error log so major non-fatal events get a trail too).
    private static void LogCrash(string source, Exception? ex) => AppLog.Error(source, ex);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Watchdog probes must respect a deliberate tray-menu Exit: the hold-marker written by
        // Shutdown() keeps them from resurrecting an app the user chose to stop.
        bool watchdogStart = Environment.GetCommandLineArgs().Contains(WatchdogTask.WatchdogArg);
        if (watchdogStart && WatchdogTask.HoldMarkerExists)
        {
            _intentionalExit = true;
            _quietExit = true;
            Application.Current.Exit();
            return;
        }

        // Must be the very first thing: exit before any window or tray icon is created if another
        // instance already holds the lock. Watchdog probes use a single instant attempt — the 3s
        // retry below exists for the self-heal relaunch race, and a probe finding a live instance
        // is the expected steady state, not a race worth waiting out.
        if (!await AcquireSingleInstanceLockAsync(watchdogStart ? 1 : 15).ConfigureAwait(true))
        {
            if (!watchdogStart)
                AppLog.Info("Another instance already holds the single-instance lock — exiting.");
            _intentionalExit = true;   // must be set — otherwise OnProcessExit's self-heal relaunches
                                        // this "duplicate" exit, and the relaunch detects a duplicate
                                        // too, looping forever
            _quietExit = watchdogStart;
            Application.Current.Exit();
            return;
        }

        if (watchdogStart)
            AppLog.Info("Watchdog relaunch: no live instance found — restoring the tray app.");
        else
            WatchdogTask.TryClearHoldMarker();   // any deliberate start re-arms resurrection

        // Capture a minidump if the app dies from a fault that bypasses every managed handler
        // below (the "vanished tray icon, zero trace anywhere" signature seen 2026-07-03 and
        // 2026-07-05 — no ProcessExit line, no app.log entry, no WER report at all). LocalDumps
        // covers unhandled faults; the SilentProcessExit monitor covers fault-less external
        // terminations (the undock-kill signature confirmed 2026-07-06/07-08). See CrashDumps.cs
        // for the full story. Backgrounded: it only needs to be armed before some FUTURE crash,
        // not before the rest of startup (window/tray-icon creation below) proceeds — registry
        // I/O here would otherwise add unaccounted latency to the exact "is the app actually
        // running yet" window this app's history has repeatedly had trouble with.
        _ = Task.Run(() =>
        {
            string dumpDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChargeKeeper", "dumps");
            CrashDumps.TryRegisterLocalDumps(dumpDir);
            CrashDumps.TryRegisterSilentExitMonitor(dumpDir);
            WatchdogTask.TryEnsureTasks();
        });

        // Opt native Win32 elements (the tray context menu) into system dark mode. Must run
        // before any UI is created so the menu HWND inherits the setting.
        NativeMethods.EnableDarkModeForNativeUi();

        // Capture the UI dispatcher while we're on the UI thread. Battery events fire on a
        // background thread and must marshal tray-icon updates back here (see UpdateTrayIcon).
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Teardown forensics: this event fires when the XAML framework itself initiates process
        // teardown (last window closed — or, the case we're hunting, the compositor connection
        // dying under the app during a GPU reset). Its presence/absence in app.log next to a
        // ProcessExit line tells the silent-death mechanisms apart.
        _dispatcher.ShutdownStarting += (_, _) =>
            AppLog.Info("DispatcherQueue.ShutdownStarting — framework-initiated teardown.");

        // Logoff/shutdown must not trigger the self-heal relaunch in OnProcessExit.
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        // When OnProcessExit resurrected us after a GPU-reset teardown — or a watchdog probe is
        // restoring us right after an unlock/resume — the display subsystem may still be
        // mid-recovery: give it a moment before creating windows and the tray icon, or the
        // fresh instance can die to the same reset it was born from.
        if (watchdogStart || Environment.GetCommandLineArgs().Contains(AutoRelaunchArg))
        {
            if (!watchdogStart)
                AppLog.Info("Started via auto-relaunch; waiting 5s for the display subsystem to settle.");
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }

        // Configurable startup delay — keeps the app off the critical sign-in path on
        // machines where many elevated processes start simultaneously.
        int delay = SettingsService.Current.StartupDelaySeconds;
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(true);

        _hostWindow = new MainWindow();
        _hostWindow.Closed += (_, _) => AppLog.Info("Host window closed.");
        // InitTrayIcon first so the tray icon appears as early as possible: registering the
        // notification platform (ToastService.Register — a COM/registry call, tens–hundreds of ms
        // cold for an unpackaged app) doesn't need to gate icon creation. Kept BEFORE
        // SubscribeBatteryEvents so a startup low-battery toast (raised by the forced first battery
        // read there) can still fire.
        InitTrayIcon();
        ToastService.Register();
        SubscribeBatteryEvents();
        StartHistorySampling();
        ScheduleUpdateCheck();
        NetworkLocationService.Start();   // TODO #31 — TrayMenu subscribes to LocationChanged for the auto-apply reaction

        // TODO #28 — Home Assistant MQTT publisher. Inert unless HomeAssistantEnabled AND a broker
        // host are set in settings.json; OnBatteryReportUpdated feeds it state, Shutdown disposes it.
        _ha = new HomeAssistantService(AppInfo.Version);
        _ha.ApplySettings(SettingsService.Current);
    }

    private HomeAssistantService? _ha;

    // ── Tray icon ─────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];

        // Start with the static ChargeKeeper mark; battery arc replaces it on the first battery event.
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
        // Travel-override toggles aren't battery events, so rebuild the tooltip on the service's
        // own state change — otherwise it stays stuck on "Charging to 100 %" after a revert.
        TravelOverrideService.StateChanged += RefreshTooltip;
        // Trigger immediately with the current state so the icon is right from the start — but on a
        // background thread (this is called on the UI thread during startup, whereas the event
        // normally fires on an MTA thread). The handler already marshals its icon swap + dashboard
        // refresh via UpdateTrayIcon/RunOnUi, so running it off-thread keeps the battery read + two
        // Lenovo RPCs + the capacity-file read it does out of the startup UI-thread path. The static
        // brand icon is already showing, so there's no visible gap while this completes.
        _ = Task.Run(() => OnBatteryReportUpdated(Battery.AggregateBattery, null!));
    }

    // ── History sampling ──────────────────────────────────────────────────────

    private System.Threading.Timer? _historyTimer;

    private void StartHistorySampling()
    {
        // LoadWindow does a full CSV scan (up to 14 days of rows) — real disk I/O that must not
        // run on the UI thread during startup, or a large history file could visibly delay launch.
        // Prime the in-memory window from disk so the dashboard shows history immediately after a
        // restart, then sample at a fixed cadence so downtime is visible as a gap in the timeline.
        Task.Run(() =>
        {
            var span   = SettingsService.Current.GraphTimeScale.ToTimeSpan();
            var loaded = BatteryHistoryService.LoadWindow(span);
            AppLog.Info($"History sampling started: span={span}, {loaded.Count} sample(s) loaded from disk.");

            int interval = BatteryHistoryService.SampleIntervalSeconds;
            _historyTimer = new System.Threading.Timer(
                _ => SampleHistory(), null, TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
        });
    }

    private void SampleHistory()
    {
        try
        {
            if (_lastIconState.Pct < 0) return;   // no battery reading yet — nothing to log
            int? limit = _lastThresholdState is { Enabled: true, Stop: > 0 } t ? t.Stop : null;
            var gap = BatteryHistoryService.Record(_lastIconState.Pct, limit, _lastRateMW);
            if (gap is { } g) CheckDrainAnomaly(g);
        }
        catch (Exception ex)
        {
            AppLog.Error("SampleHistory", ex);
        }
    }

    /// <summary>
    /// Overnight-drain anomaly (TODO #26): fires a toast when a just-detected downtime gap shows a
    /// genuine, trustworthy over-threshold drain. The actual decision (noise floors + rate check)
    /// lives in the pure, unit-tested <see cref="DrainAnomalyPolicy"/>; this only reads settings and
    /// raises the toast.
    /// </summary>
    private static void CheckDrainAnomaly(DowntimeGapInfo gap)
    {
        var s = SettingsService.Current;
        if (DrainAnomalyPolicy.ShouldWarn(s.DrainAnomalyWarningEnabled, gap.SocDropPercent, gap.GapDuration, s.DrainAnomalyPercentPerHour))
            ToastService.NotifyDrainAnomaly(gap.SocDropPercent, gap.GapDuration);
    }

    private static void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        _sessionEnding = true;
        AppLog.Info($"SessionEnding: {e.Reason}.");
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // Log every transition — the timeline around these lines is what lets a later silent
        // teardown be correlated with a power event (see the self-heal notes at the top).
        AppLog.Info($"PowerModeChanged: {e.Mode}.");
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;

        // A charger swap while asleep produces no AC→battery transition to invalidate on, so drop
        // the cached adapter wattage on resume too — the next on-AC read re-queries whatever's
        // attached now (TODO #41).
        ChargerInfoService.Invalidate();

        // On resume the shell sometimes drops the tray icon WITHOUT broadcasting TaskbarCreated,
        // so H.NotifyIcon's built-in recovery never fires. A plain ForceCreate() can't help here:
        // its Create() early-returns while the library still believes the icon exists. Force a real
        // re-add by removing the stale registration first, then creating — the same TryRemove()+
        // Create() pair the library itself uses to recover from TaskbarCreated.
        RunOnUi(() =>
        {
            if (_trayIcon is { } icon)
            {
                icon.TrayIcon.TryRemove();
                icon.TrayIcon.Create();
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

            bool charging = BatteryStatsFormatter.IsOnAC(report.Status);

            // ── Battery history ───────────────────────────────────────────────
            // Sampled on a fixed cadence by _historyTimer (see SampleHistory), NOT per battery
            // event — a regular cadence is what lets downtime show up as a gap in the graph.

            // ── Battery capacity history (TODO #24) ─────────────────────────────
            // Unlike the SoC history above, this rides the battery-report EVENT rather than a
            // dedicated timer — RecordIfNewDay is a cheap no-op after the first success each day,
            // and this event already fires often enough (multiple times an hour) that a separate
            // timer would add nothing but a second thing to start/stop at shutdown.
            if (report.FullChargeCapacityInMilliwattHours is > 0 and { } fullChargeMwh)
                BatteryCapacityHistoryService.RecordIfNewDay(fullChargeMwh, report.DesignCapacityInMilliwattHours);

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
            _lastOnAC           = charging;   // IsOnAC — shared with the icon expression above
            _lastRateMW         = report.ChargeRateInMilliwatts ?? 0;
            _lastThresholdState = ChargeThresholdService.Read();
            _lastRemainingMwh   = report.RemainingCapacityInMilliwattHours;
            _lastFullMwh        = report.FullChargeCapacityInMilliwattHours;
            // Only meaningful while an adapter is attached (TODO #41); the read is memoized inside
            // ChargerInfoService, so per-event calls here are cheap after the first.
            _lastAdapterWattage = charging ? ChargerInfoService.GetRatedWattage() : null;
            UpdateTooltip(pct, _lastRemainingMwh, _lastFullMwh);

            // ── Home Assistant publish (TODO #28) ─────────────────────────────
            // No-op unless the MQTT publisher is enabled+connected; threshold/adapter fields are
            // omitted (→ HA "unknown") when Smart Charge is off / no wattage support.
            var th = _lastThresholdState;
            _ha?.PublishState(new HaState(
                Soc:          pct,
                PowerMw:      _lastRateMW,
                OnAc:         charging,
                ChargeStart:  th is { Capable: true, Enabled: true, Start: > 0 } ? th.Start : null,
                ChargeStop:   th is { Capable: true, Enabled: true, Stop:  > 0 } ? th.Stop  : null,
                AdapterWatts: _lastAdapterWattage));

            // ── Toast: AC connected ───────────────────────────────────────────
            if (_lastBatteryStatus == BatteryStatus.Discharging &&
                report.Status      == BatteryStatus.Charging)
            {
                ToastService.NotifyChargingStarted();
            }

            // ── Charger-wattage cache invalidation ────────────────────────────
            // Unplugged: drop ChargerInfoService's memoized adapter wattage, so the next AC
            // session re-reads whatever adapter is attached then — it may be a different charger.
            if (_lastBatteryStatus != BatteryStatus.Discharging &&
                report.Status      == BatteryStatus.Discharging)
            {
                ChargerInfoService.Invalidate();
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
    private int?    _lastRemainingMwh;   // cached so RefreshTooltip can rebuild without a battery event
    private int?    _lastFullMwh;
    private int?    _lastAdapterWattage; // AC adapter rated wattage (TODO #41), null until known
    private ChargeThresholdState? _lastThresholdState;

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
    /// Rebuilds the tray tooltip from the last cached battery reading — used when something that
    /// affects the tooltip changes outside of a battery event (the travel-override activate/revert),
    /// so it doesn't stay stuck on a stale line until the next ReportUpdated fires. Re-reads the
    /// charge threshold so a just-restored Smart Charge limit shows immediately. Safe off the UI
    /// thread: it only builds a string, then UpdateTooltip marshals the assignment via RunOnUi.
    /// </summary>
    internal void RefreshTooltip()
    {
        _lastThresholdState = ChargeThresholdService.Read();
        UpdateTooltip(_lastIconState.Pct < 0 ? 0 : _lastIconState.Pct, _lastRemainingMwh, _lastFullMwh);
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

        // 💠 ChargeKeeper  v1.0.x
        // 💠 is the brand mark in ZeroZero's signature teal (#27e0c8-ish). A tray tooltip is plain
        // text — no per-glyph colour — so a colour emoji is the only way to carry brand colour, and
        // the bright cyan-teal reads clearly on the dark Win11 tooltip background.
        lines.Append($"💠 ChargeKeeper  v{AppInfo.Version}");

        // ⚡ AC · 75%  ·  +45 W   (on AC — the bolt forced to its TEXT/outline form via U+FE0E so it
        //                          renders bright like the ⚙/⏱/⬆ outlines; the colour plug 🔌 was a
        //                          dark emoji that nearly vanished on the dark tooltip background)
        // 🔋 75%  ·  −18 W        (on battery)
        // Glyph follows the power source so it never contradicts the AC label, and the rate is
        // shown only in its expected direction via the shared formatter (mW below 1 W, real −).
        string chargeIcon = _lastOnAC ? "⚡︎" : "🔋";   // ⚡ + U+FE0E = outline (text) presentation
        // Adapter wattage (TODO #41) rides along in the "AC" label itself — "AC (65W)" — rather
        // than as a separate line, since it's a property of the power SOURCE, not a new stat.
        string acLabel = _lastAdapterWattage is { } watts ? $"AC ({watts}W)" : "AC";
        lines.Append(_lastOnAC
            ? $"\n{chargeIcon} {acLabel} · {pct}%"
            : $"\n{chargeIcon} {pct}%");
        string? rate = (_lastOnAC && _lastRateMW > 0) || (!_lastOnAC && _lastRateMW < 0)
            ? PowerFormat.SignedRate(_lastRateMW)
            : null;
        if (rate is not null)
            lines.Append($"  ·  {rate}");

        // ⏱ ~2h 15m remaining  /  ⏱ ~45m to full — the same estimate the two windows' REMAINING
        // stat shows, via the shared formatter (noise floor, >99h cap, and h/m text all live there;
        // this used to be a third hand-rolled copy that had already drifted on zero-padding).
        string timeText = BatteryStatsFormatter.FormatTimeRemaining(_lastRateMW, remainingMwh, fullMwh);
        if (timeText != "—")
            lines.Append($"\n⏱ {timeText}");

        // 🔝 Charging to 100%   OR   ⚙ Smart Charge: 70–80%
        if (TravelOverrideService.IsActive)
            lines.Append("\n🔝 Charging to 100%");
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
                _dashboard.Closed += (_, _) =>
                {
                    AppLog.Info("Dashboard window closed.");
                    _dashboard = null;
                };
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

    // ── Battery history pop-out ──────────────────────────────────────────────────

    /// <summary>
    /// Opens the bigger, resizable battery-history graph window, or focuses it if already open.
    /// Mirrors TrayMenu's AboutWindow singleton pattern (create once, Activate() thereafter) rather
    /// than DashboardWindow's hide/show toggle — the window closes itself on focus loss (popup-style
    /// dismissal), so instances are short-lived and Closed keeps the singleton reference honest.
    /// </summary>
    internal void ShowHistoryWindow()
    {
        if (_historyWindow is not null)
        {
            _historyWindow.Activate();
            return;
        }

        // Capture the dashboard's on-screen rect (physical px) NOW — it's read before HideWindow()
        // below. The pop-out animates open from this rect ("the dashboard's graph grows into a
        // window"); null (no visible dashboard) skips the animation and places the window at its
        // final rect directly.
        Windows.Graphics.RectInt32? origin = null;
        if (_dashboard is { } dash && dash.AppWindow.IsVisible)
        {
            origin = new Windows.Graphics.RectInt32(
                dash.AppWindow.Position.X, dash.AppWindow.Position.Y,
                dash.AppWindow.Size.Width, dash.AppWindow.Size.Height);

            // Hide the dashboard NOW rather than waiting for its own Deactivated handler to react
            // to the new window taking focus. The dashboard is IsAlwaysOnTop=true; left visible,
            // it can keep fighting the freshly-activated (non-topmost) pop-out for z-order/focus
            // at the exact same on-screen rect, which was observed as the pop-out opening and then
            // immediately closing again (its own focus-loss dismissal misfiring within the same
            // instant). Hiding it up front removes that contender entirely.
            dash.HideWindow();
        }

        _historyWindow = new BatteryHistoryWindow(origin);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Activate();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Shutdown()
    {
        _intentionalExit = true;   // tells OnProcessExit this teardown is legitimate — no relaunch
        WatchdogTask.WriteHoldMarker();   // and tells the watchdog task the same — stay down
        AppLog.Info("User exit via tray menu.");

        Battery.AggregateBattery.ReportUpdated -= OnBatteryReportUpdated;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        TravelOverrideService.StateChanged -= RefreshTooltip;
        NetworkLocationService.Stop();
        _ha?.Dispose();   // goes offline in HA but keeps the retained discovery (device persists)
        _currentBatteryIcon?.Dispose();
        ToastService.Cleanup();
        _trayIcon?.Dispose();
        Application.Current.Exit();
    }
}
