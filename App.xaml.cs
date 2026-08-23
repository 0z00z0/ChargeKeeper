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
/// Application entry point. Owns the tray icon lifetime and coordinates the dashboard popup and
/// context menu.
/// </summary>
public partial class App : Application
{
    // Invisible WinUI 3 host — the framework exits when every window is closed.
    private Window?              _hostWindow;

    // Completes when the display subsystem is settled enough to create windows; the tray icon is
    // deliberately not behind this gate. RunContinuationsAsynchronously stops a parked awaiter
    // resuming INLINE at the TrySetResult call site, nested inside OnLaunched.
    private readonly TaskCompletionSource _windowsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task WindowsReady => _windowsReady.Task;
    private TaskbarIcon?         _trayIcon;
    private DashboardWindow?     _dashboard;
    private BatteryHistoryWindow? _historyWindow;
    private SettingsWindow?      _settings;
    private TrayMenu?            _menu;

    // Last known battery status — used to detect Charging→Idle transitions for toasts.
    private BatteryStatus _lastBatteryStatus = BatteryStatus.NotPresent;

    // Keeps the _last* battery fields coherent across the MQTT snapshot thread, the history sampler
    // and OnBatteryReportUpdated. Held only for the read-or-publish of the fields — never across a
    // vendor RPC or an MQTT publish.
    private readonly System.Threading.Lock _batteryReportLock = new();

    // Cached tray icon state; Pct = -1 means not yet read.
    private (int Pct, bool Charging) _lastIconState = (-1, false);

    // Fire-once latch, reset with 5 % hysteresis so a brief charge re-arms it.
    private bool _lowBatteryWarningFired;

    // Fire-once latch for the opposite end, re-armed the moment the level falls back below.
    private bool _highBatteryWarningFired;

    // A GPU fault during a power transition kills the compositor connection, and WinUI then tears
    // the process down as a CLEAN exit with nothing in any log. These flags let OnProcessExit tell
    // that apart from the two legitimate exits — tray-menu Exit and Windows logoff/shutdown.
    private static volatile bool _intentionalExit;
    private static volatile bool _sessionEnding;
    private static readonly DateTime _processStartUtc = DateTime.UtcNow;

    // Parsed once in Program.Main and handed in, so Main's launch decisions and OnLaunched's read
    // the same answer rather than two parses that can drift.
    private readonly StartupArgs _startup;

    // Upper bound for the hand-editable startup delay; 60 s is the top preset Settings offers.
    private const int MaxStartupDelaySeconds = 60;

    internal App(StartupArgs startup)
    {
        _startup = startup;

        InitializeComponent();

        // A tray app's lifetime is anchored to the tray icon, not to a XAML window: a compositor
        // reset can destroy every window from below, and OnLastWindowClose would then end the
        // process. The dashboard recreates itself lazily on the next tray click.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // GUI crashes surface only as an opaque 0xC000027B stowed exception in Event Viewer, so log
        // the managed exception before the process dies.
        UnhandledException += (_, e) =>
        {
            LogCrash("Application.UnhandledException", e.Exception);
            // Leave e.Handled = false: crashing visibly beats running corrupt.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>
    /// Fires on every CLEAN teardown, never on a hard kill such as an installer's taskkill — which
    /// is what makes it safe to relaunch from. An exit that is neither user-initiated nor a logoff
    /// is the silent compositor-loss teardown, and gets a replacement instance.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        var uptime = DateTime.UtcNow - _processStartUtc;
        AppLog.Info($"ProcessExit: clean teardown after {uptime:hh\\:mm\\:ss} " +
                    $"(intentional={_intentionalExit}, sessionEnding={_sessionEnding}).");

        if (_intentionalExit || _sessionEnding) return;

        // Crash-loop guard: at most 3 auto-relaunches per 10 minutes. Deliberately not gated on
        // uptime as well — a GPU-reset teardown can hit a process that is only seconds old.
        if (!TryRecordRelaunch())
        {
            AppLog.Info("Not relaunching: 3 auto-relaunches within 10 minutes — giving up.");
            return;
        }

        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe, StartupArgs.AutoRelaunchArg) { UseShellExecute = false });
            AppLog.Info("Unexpected teardown — relaunched a fresh instance.");
        }
        catch (Exception ex)
        {
            AppLog.Error("OnProcessExit.Relaunch", ex);
        }
    }

    /// <summary>
    /// Sliding-window rate limiter for the self-heal relaunch: false once 3 relaunches have happened
    /// within 10 minutes. Timestamps live in a file because each check runs in a NEW process.
    /// </summary>
    private static bool TryRecordRelaunch()
    {
        try
        {
            var path = AppPaths.DataFile("relaunch-history.txt");

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

    private static void LogCrash(string source, Exception? ex) => AppLog.Error(source, ex);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The /debug command and "should this probe resurrect the app?" are settled in Program.Main,
        // before WinUI loads — do not reintroduce either check here.
        bool watchdogStart = _startup.IsWatchdogProbe;

        // Must come before any window or tray icon is created. A watchdog probe that got this far
        // already holds the lock, and neither path may acquire twice: a Mutex is re-entrant per
        // owning thread, so a second WaitOne would bump the recursion count rather than fail.
        if (!SingleInstance.IsHeld &&
            !await SingleInstance.TryAcquireAsync(_startup.SingleInstanceAttempts).ConfigureAwait(true))
        {
            AppLog.Info("Another instance already holds the single-instance lock — exiting.");
            _intentionalExit = true;   // else OnProcessExit relaunches this duplicate exit, forever
            Application.Current.Exit();
            return;
        }

        if (watchdogStart)
            AppLog.Info("Watchdog relaunch: no live instance found — restoring the tray app.");
        else
            WatchdogTask.TryClearHoldMarker();   // any deliberate start re-arms resurrection

        // Minidump-on-crash (WER LocalDumps) follows the intent /debug stores; "off" actively
        // disarms, because the registration is an HKLM key that outlives the process. Backgrounded:
        // it only has to be armed before a FUTURE crash, not before the tray icon appears.
        _ = Task.Run(() =>
        {
            string dumpDir = AppPaths.DataFile("dumps");
            CrashDumps.ApplyPolicy(dumpDir);
            CrashDumps.TryDisarmSilentExitMonitor();
            CrashDumps.TryCleanupOldDumps(dumpDir);
            WatchdogTask.TryEnsureTasks();
        });

        // Must run before any UI is created so the tray menu's native HWND inherits the setting.
        NativeMethods.EnableDarkModeForNativeUi();

        // Battery events fire on a background thread and must marshal tray-icon updates back here.
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Its presence next to a ProcessExit line is what tells the silent-death mechanisms apart.
        _dispatcher.ShutdownStarting += (_, _) =>
            AppLog.Info("DispatcherQueue.ShutdownStarting — framework-initiated teardown.");

        // Logoff/shutdown must not trigger the self-heal relaunch in OnProcessExit.
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        // Deliberately ahead of both waits below: the waits guard a hazard the icon does not share.
        // A tray icon is a message-only HWND plus a Shell_NotifyIcon registration, not a window, and
        // its menu is a native Win32 PopupMenu — nothing a recovering display subsystem can pull away.
        InitTrayIcon();

        // A fresh instance created right after a GPU-reset teardown, an unlock or a resume can die
        // to the same reset it was born from: give the display subsystem a moment first.
        if (watchdogStart || _startup.IsAutoRelaunch)
        {
            PowerLog.Event("Display settle: holding window creation for 5 s",
                           watchdogStart ? "watchdog relaunch" : "auto-relaunch after a display teardown");
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }

        // Keeps the app off the critical sign-in path; clamped because settings.json is hand-editable.
        int delay = Math.Clamp(SettingsService.Current.StartupDelaySeconds, 0, MaxStartupDelaySeconds);
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(true);

        // Exit is reachable from the moment InitTrayIcon returns, so the two waits above are the one
        // window in which Shutdown() can run BEFORE the startup it is tearing down. Everything below
        // would re-subscribe, re-arm and reconnect exactly what Shutdown just released.
        if (_intentionalExit)
        {
            AppLog.Info("Exit was chosen during the startup wait — abandoning the rest of startup.");
            return;
        }

        // Opened BEFORE the first window is created, so a tray click parked on the gate may proceed.
        _windowsReady.TrySetResult();
        PowerLog.Event("Display settle: complete, windows may be created", "startup gate opened");

        _hostWindow = new MainWindow();
        _hostWindow.Closed += (_, _) => AppLog.Info("Host window closed.");
        SubscribeBatteryEvents();
        StartHistorySampling();
        ScheduleUpdateCheck();
        // Before the first evaluation: a rule keyed on the routed adapter can match the wrong place,
        // and applying its preset is exactly what this drops the rule to avoid.
        SettingsService.ClearRulesKeyedOnTheRoutedAdapter();
        NetworkLocationService.Start();
        KeepAwakeService.Start();
        // Also the crash-recovery point: puts the user's own Windows lid-close action back if a
        // previous run died with it still overridden.
        LidDelayService.Start();

        // Before the publisher reads the port: an upgraded install carries the old 1883 default, and
        // connecting on it once would remember it as the endpoint that works.
        SettingsService.RetireTheDefaultMqttPort();

        // Home Assistant MQTT publisher. Inert unless HomeAssistantEnabled and a broker host are set.
        _ha = new HomeAssistantService(AppInfo.Version);
        // Publishes live values on every (re)connect. Set BEFORE ApplySettings, which may start
        // connecting — and invoke this — right away. Runs on the MQTT thread, so the fields are
        // snapshotted under the lock; the caller publishes outside this call.
        _ha.CurrentStateProvider = () =>
        {
            using (_batteryReportLock.EnterScope())
            {
                if (_lastIconState.Pct < 0) return null;   // no reading yet — publish nothing
                return HaStateBuilder.Build(
                    _lastIconState.Pct, _lastRateMW, _lastIconState.Charging, _lastBatteryStatus, _lastThresholdState,
                    ChargerInfoService.CachedWattage, _lastRemainingMwh, _lastFullMwh, _lastDesignMwh, _lastLowPowerMode,
                    SettingsService.Read(s => s.Presets.ToList()));
            }
        };
        // The settings, network and diagnostic snapshot, and the vendor gates the announcement is
        // filtered through. Both run on the MQTT threads.
        _ha.CurrentSurfaceProvider = () => HaSurfaceReader.Read(AppInfo.Version);
        _ha.CapabilityProvider     = HaSurfaceReader.Capabilities;
        _ha.ApplySettings(SettingsService.Current);
        // "Reload settings from disk" must reach the live MQTT client too — the Settings window's
        // reload only refreshes what it displays.
        SettingsService.Reloaded += () => _ha?.ApplySettings(SettingsService.Current);
        // The settings payload has no battery tick of its own, so every source that can move one of
        // its values publishes it. Unchanged payloads are deduped, so a redundant signal costs nothing.
        SettingsService.Changed             += () => _ha?.PublishSurfaceNow();
        KeepAwakeService.StateChanged       += () => _ha?.PublishSurfaceNow();
        NetworkLocationService.LocationChanged += _ => _ha?.PublishSurfaceNow();
    }

    private HomeAssistantService? _ha;

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];

        // Start with the static ChargeKeeper mark; the battery arc replaces it on the first event.
        // Guarded because nothing above this on the startup path catches: a disk fault would kill
        // the process before the tray icon exists, and the self-heal would relaunch into it again.
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            _trayIcon.Icon = new System.Drawing.Icon(IconGenerator.GenerateAndSaveTrayIcon(exeDir));
        }
        catch (Exception ex)
        {
            AppLog.Error("InitTrayIcon.BrandIcon", ex);
            // The in-memory renderer needs no disk at all.
            try { _trayIcon.Icon = IconGenerator.RenderBatteryIcon(0, false, SettingsService.Current.IconMode); }
            catch (Exception fallbackEx) { AppLog.Error("InitTrayIcon.FallbackIcon", fallbackEx); }
        }

        // A second left-click inside the double-click window opens Settings instead.
        IToggleFeature[] features = [new AutoStartFeature()];
        _menu = new TrayMenu(features, Shutdown, ForceIconRefresh, onOpenSettings: ShowSettingsWindow,
                             windowsReady: WindowsReady);
        _trayIcon.ContextFlyout     = _menu.Flyout;
        _trayIcon.LeftClickCommand  = new RelayCommand(ToggleDashboard);
        _trayIcon.RightClickCommand = new RelayCommand(() => _menu!.RefreshState());

        _trayIcon.ForceCreate();
    }

    private void SubscribeBatteryEvents()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // The tray slot size is DPI-dependent and the render is gated on battery ticks, so without
        // this the arc stays rescaled until the next battery event.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // Travel-override toggles aren't battery events, so rebuild the tooltip on the service's own
        // state change — otherwise it stays stuck on "Charging to 100 %" after a revert.
        TravelOverrideService.StateChanged += RefreshTooltip;

        // Seed the baseline from a forced read, THEN subscribe — in that order, so the first real
        // event cannot overlap the seed. Off the UI thread, so the battery read and the vendor RPCs
        // stay off the cold-start path.
        _ = Task.Run(() =>
        {
            // Registration leads the seed: a toast raised before the notification platform is
            // registered is silently dropped.
            ToastService.Register();
            // Exit is reachable from the tray menu while this runs, and Shutdown's -= would then
            // precede the += below, seeding against a disposed tray icon and MQTT service.
            if (_intentionalExit) return;
            OnBatteryReportUpdated(Battery.AggregateBattery, null!);
            Battery.AggregateBattery.ReportUpdated += OnBatteryReportUpdated;
        });
    }

    private System.Threading.Timer? _historyTimer;

    private void StartHistorySampling()
    {
        // LoadWindow scans up to 14 days of CSV — real disk I/O that must not run on the UI thread.
        // The fixed cadence afterwards is what makes downtime visible as a gap in the timeline.
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
            // Runs on a timer pool thread, so snapshot the fields together — a row must not pair
            // this tick's SoC with the previous tick's limit and power. Record does disk I/O and
            // must stay outside the lock.
            int pct; int? limit; int rate;
            using (_batteryReportLock.EnterScope())
            {
                if (_lastIconState.Pct < 0) return;   // no battery reading yet — nothing to log
                pct   = _lastIconState.Pct;
                limit = _lastThresholdState is { Enabled: true, Stop: > 0 } t ? t.Stop : null;
                rate  = _lastRateMW;
            }

            var gap = BatteryHistoryService.Record(pct, limit, rate);
            if (gap is { } g) CheckDrainAnomaly(g);
        }
        catch (Exception ex)
        {
            AppLog.Error("SampleHistory", ex);
        }
    }

    /// <summary>
    /// Raises the overnight-drain toast when a just-detected downtime gap shows a genuine
    /// over-threshold drain. The decision itself lives in <see cref="DrainAnomalyPolicy"/>.
    /// </summary>
    private static void CheckDrainAnomaly(DowntimeGapInfo gap)
    {
        var s = SettingsService.Current;
        if (DrainAnomalyPolicy.ShouldWarn(s.DrainAnomalyWarningEnabled, gap.SocDropPercent, gap.GapDuration, s.DrainAnomalyPercentPerHour))
            ToastService.NotifyDrainAnomaly(gap.SocDropPercent, gap.GapDuration);
    }

    private static System.Threading.Timer? _shutdownCancelledProbe;

    private static void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        _sessionEnding = true;
        PowerLog.Event($"Session ending: {e.Reason}", "Windows sign-out, restart or shutdown");
        // A restart or sign-out does not go through Shutdown(), so the Windows lid-close action
        // would otherwise stay overridden for as long as the app is not running.
        LidDelayService.Stop();

        // Windows raises no event when another app vetoes the shutdown, so still being alive a
        // while later is the only detector — and _sessionEnding would otherwise suppress the
        // self-heal relaunch for the rest of the session.
        _shutdownCancelledProbe?.Dispose();
        _shutdownCancelledProbe = new System.Threading.Timer(_ =>
        {
            _sessionEnding = false;
            LidDelayService.Start();
        }, null, TimeSpan.FromSeconds(30), System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // Every transition is logged: this timeline is what correlates a later silent teardown
        // with a power event.
        PowerLog.Event($"Windows power mode: {e.Mode}", "system power notification");
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;

        // A charger swap while asleep produces no AC→battery transition to invalidate on.
        ChargerInfoService.Invalidate();

        // The socket can survive the OS suspend while the broker already dropped us via keep-alive,
        // flipping every sensor to "unavailable" — reconnect rather than wait out the backoff.
        _ha?.OnPowerResume();

        // A keep-awake expiry that elapsed while suspended never fires: the timer's due time passes
        // in suspended wall-clock time.
        KeepAwakeService.OnPowerResume();

        // The shell sometimes drops the tray icon WITHOUT broadcasting TaskbarCreated, so
        // H.NotifyIcon's recovery never fires and ForceCreate() early-returns while the library
        // still believes the icon exists. Remove the stale registration first, then create.
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

            int pct = 0;
            if (report.FullChargeCapacityInMilliwattHours is > 0 and { } full &&
                report.RemainingCapacityInMilliwattHours  is { } remaining)
            {
                pct = Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100);
            }

            bool charging = BatteryStatsFormatter.IsOnAC(report.Status);

            // SoC history rides _historyTimer's fixed cadence instead, which is what makes downtime
            // show as a gap. Capacity history touches none of the _last* fields, so it stays outside
            // the lock.
            if (report.FullChargeCapacityInMilliwattHours is > 0 and { } fullChargeMwh)
                BatteryCapacityHistoryService.RecordIfNewDay(fullChargeMwh, report.DesignCapacityInMilliwattHours);

            // Vendor RPCs stay OUTSIDE the lock — the MQTT snapshot takes it too, and holding it
            // across a blocking EC call would stall publishing.
            var thresholdState = ChargeThresholdService.Read();
            if (charging) ChargerInfoService.GetRatedWattage();

            // Critical section: a coherent edge-detect and _last* publish, so no reader sees a torn
            // mix of two ticks. It spans no vendor RPC and never blocks — the toasts and the MQTT
            // publish are deferred to after the lock releases.
            HaState haSnapshot;
            bool fireLowBattery = false;
            int? highBatteryWarnAtPct = null;   // the configured level, carried out of the lock
            bool fireChargingStarted = false;
            int? chargeCompleteStopPct = null;
            bool? powerSourceEdge = null;   // true = now on AC; logged outside the lock
            using (_batteryReportLock.EnterScope())
            {
                // Gated to avoid GDI churn on every tick.
                if ((pct, charging) != _lastIconState)
                {
                    _lastIconState = (pct, charging);
                    UpdateTrayIcon(pct, charging);
                }

                // Refresh the open dashboard at once rather than waiting for its own 5 s timer.
                if (_dashboard is not null)
                {
                    // Re-read _dashboard on the UI thread, where the Closed handler nulls it:
                    // touching a window that closed since this tick captured it throws via combase.
                    RunOnUi(() =>
                    {
                        if (_dashboard is { } dash && dash.AppWindow.IsVisible)
                            dash.RefreshFromEvent();
                    });
                }

                var s = SettingsService.Current;
                if (s.LowBatteryWarningEnabled &&
                    report.Status == BatteryStatus.Discharging &&
                    pct > 0 &&
                    pct <= s.LowBatteryWarningPct &&
                    !_lowBatteryWarningFired)
                {
                    _lowBatteryWarningFired = true;
                    fireLowBattery = true;   // fired outside the lock (see below)
                }
                // Reset the guard with hysteresis so it can fire again after a partial charge.
                else if (pct > s.LowBatteryWarningPct + 5)
                {
                    _lowBatteryWarningFired = false;
                }

                // The threshold state decides whether a high level is news: within the cap it is
                // the cap working, above it the cap is not holding.
                if (HighBatteryWarningPolicy.ShouldWarn(s.HighBatteryWarningEnabled, pct,
                        s.HighBatteryWarningPct, _highBatteryWarningFired, thresholdState))
                {
                    _highBatteryWarningFired = true;
                    highBatteryWarnAtPct = s.HighBatteryWarningPct;   // fired outside the lock (see below)
                }
                else if (HighBatteryWarningPolicy.ClearsLatch(pct, s.HighBatteryWarningPct))
                {
                    _highBatteryWarningFired = false;
                }

                if (_lastBatteryStatus == BatteryStatus.Charging &&
                    report.Status      == BatteryStatus.Idle)
                {
                    chargeCompleteStopPct = thresholdState is { Enabled: true, Stop: > 0 } ? thresholdState.Stop : 100;
                }

                // The service owns the "revert once charging completes" decision and dispatches any
                // EC revert on its own background Task.
                TravelOverrideService.OnBatteryReport(pct, report.Status);

                _lastRateMW         = report.ChargeRateInMilliwatts ?? 0;
                _lastThresholdState = thresholdState;                          // hoisted read above
                _lastRemainingMwh   = report.RemainingCapacityInMilliwattHours;
                _lastFullMwh        = report.FullChargeCapacityInMilliwattHours;
                _lastDesignMwh      = report.DesignCapacityInMilliwattHours;
                _lastLowPowerMode   = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
                UpdateTooltip(pct, _lastRemainingMwh, _lastFullMwh);

                // Built here for a coherent _last* view; published below, outside the lock.
                haSnapshot = HaStateBuilder.Build(
                    pct, _lastRateMW, charging, report.Status, _lastThresholdState, ChargerInfoService.CachedWattage,
                    _lastRemainingMwh, _lastFullMwh, _lastDesignMwh, _lastLowPowerMode,
                    SettingsService.Read(s => s.Presets.ToList()));

                if (_lastBatteryStatus == BatteryStatus.Discharging &&
                    report.Status      == BatteryStatus.Charging)
                {
                    fireChargingStarted = true;   // fired outside the lock (see below)
                }

                // Unplugged: the next AC session may be a different adapter.
                if (_lastBatteryStatus != BatteryStatus.Discharging &&
                    report.Status      == BatteryStatus.Discharging)
                {
                    ChargerInfoService.Invalidate();
                }

                // Only the EDGE, and only from a real previous reading — NotPresent is the
                // pre-first-report seed, and calling that "charger disconnected" would put a fiction
                // at the top of every log.
                if (_lastBatteryStatus != BatteryStatus.NotPresent &&
                    BatteryStatsFormatter.IsOnAC(_lastBatteryStatus) != charging)
                {
                    powerSourceEdge = charging;
                }

                _lastBatteryStatus = report.Status;
            }

            // Outside the lock for the same reason the toasts are: the log write is file I/O.
            if (powerSourceEdge is { } onAc)
                PowerLog.Event($"Power source: now on {(onAc ? "AC" : "battery")}, battery {pct} %",
                               onAc ? "charger connected" : "charger disconnected");

            // ToastService.Notify* does a synchronous WinRT/COM Show; the decisions and the latch
            // above were taken under the lock, so only the Show is deferred.
            if (fireLowBattery)                    ToastService.NotifyLowBattery(pct);
            if (highBatteryWarnAtPct is { } warnAt) ToastService.NotifyHighBattery(pct, warnAt);
            if (chargeCompleteStopPct is { } stop) ToastService.NotifyChargeComplete(stop);
            if (fireChargingStarted)               ToastService.NotifyChargingStarted();

            _ha?.PublishState(haSnapshot);
        }
        catch (Exception ex)
        {
            // Non-fatal, but logged: this handler owns the icon, the toasts and the MQTT publish, so
            // a fault partway through drops all of them for that tick.
            LogCrash("OnBatteryReportUpdated", ex);
        }
    }

    private System.Drawing.Icon? _currentBatteryIcon;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    private string  _lastTooltip             = "";
    private string? _updateAvailableVersion;
    private int     _lastRateMW;   // milliwatts; positive = charging, negative = draining
    private int?    _lastRemainingMwh;   // cached so RefreshTooltip can rebuild without a battery event
    private int?    _lastFullMwh;
    private int?    _lastDesignMwh;      // design capacity — the battery-health denominator
    private bool    _lastLowPowerMode;   // Windows Energy Saver active
    private ChargeThresholdState? _lastThresholdState;

    private void UpdateTrayIcon(int pct, bool charging)
    {
        // UI thread only — ReportUpdated fires on an MTA thread, and mutating or disposing the icon
        // off-thread faults the native tray/GDI handle, an access violation that bypasses managed
        // try/catch and kills the process.
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
    /// The tray slot size is DPI-dependent, so a display topology or DPI change drops the cached
    /// slot size and repaints the icon at the new resolution.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Helpers.IconGenerator.InvalidateSlotSizeCache();
        ForceIconRefresh();
    }

    /// <summary>Forces an immediate tray icon re-render from the last known battery state.</summary>
    internal void ForceIconRefresh()
    {
        if (_lastIconState.Pct >= 0)
            UpdateTrayIcon(_lastIconState.Pct, _lastIconState.Charging);
    }

    /// <summary>
    /// Rebuilds the tray tooltip from the last cached reading, for the changes that arrive without a
    /// battery event (the travel-override activate/revert). Re-reads the charge threshold, so a
    /// just-restored Smart Charge limit shows immediately.
    /// </summary>
    internal void RefreshTooltip()
    {
        // The vendor RPC stays OUTSIDE the lock — never hold _batteryReportLock across an EC call.
        var threshold = ChargeThresholdService.Read();

        // Then take the lock, so this off-thread writer does not pair the new threshold with a
        // previous tick's battery fields.
        int pct; int? remaining, full;
        using (_batteryReportLock.EnterScope())
        {
            _lastThresholdState = threshold;
            pct       = _lastIconState.Pct < 0 ? 0 : _lastIconState.Pct;
            remaining = _lastRemainingMwh;
            full      = _lastFullMwh;
        }
        UpdateTooltip(pct, remaining, full);
    }

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch: an exception
    /// inside a DispatcherQueue callback does NOT reach Application.UnhandledException, and tears the
    /// process down as an opaque 0xC000027B stowed exception instead.
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

        // A tray tooltip is plain text, so a colour emoji is the only way to carry the brand teal.
        lines.Append($"💠 ChargeKeeper  v{AppInfo.Version}");

        // U+FE0E forces the bolt to its text/outline form, so it renders bright like the ⚙/⏱/⬆
        // outlines rather than as a dark colour emoji on the dark tooltip background.
        string chargeIcon = _lastIconState.Charging ? "⚡︎" : "🔋";
        // Adapter wattage rides the "AC" label — it is a property of the power source, not a new
        // stat — and only shows on AC, where the cache is warm.
        string acLabel = ChargerInfoService.CachedWattage is { } watts ? $"AC ({watts}W)" : "AC";
        lines.Append(_lastIconState.Charging
            ? $"\n{chargeIcon} {acLabel} · {pct}%"
            : $"\n{chargeIcon} {pct}%");
        string? rate = (_lastIconState.Charging && _lastRateMW > 0) || (!_lastIconState.Charging && _lastRateMW < 0)
            ? PowerFormat.SignedRate(_lastRateMW)
            : null;
        if (rate is not null)
            lines.Append($"  ·  {rate}");

        string timeText = BatteryStatsFormatter.FormatTimeRemaining(_lastRateMW, remainingMwh, fullMwh);
        if (timeText != "—")
            lines.Append($"\n⏱ {timeText}");

        // A mode-based vendor (HP, Surface) reports Start as 0 by contract, so it gets a cap rather
        // than a range.
        if (TravelOverrideService.IsActive)
            lines.Append("\n🔝 Charging to 100%");
        else if (_lastThresholdState is { IsLimiting: true } sc)
            lines.Append(sc.HasStartThreshold ? $"\n⚙ Smart Charge: {sc.Start}–{sc.Stop}%"
                                              : $"\n⚙ Smart Charge: to {sc.Stop}%");

        if (_updateAvailableVersion is { } uv)
            lines.Append($"\n⬆ Update available: v{uv}");

        var tooltip = lines.ToString();

        // NOTIFYICONDATA.szTip holds at most 127 UTF-16 chars (+ NUL); clamp so the shell doesn't
        // silently truncate, without splitting a surrogate pair.
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

    /// <summary>How often the background check re-asks GitHub after the first one.</summary>
    internal static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private void ScheduleUpdateCheck()
    {
        // Delayed 30 s so the check doesn't slow the cold-start path, then repeated daily: this is
        // the whole background update mechanism, so a machine left signed in has to keep checking.
        // The async lambda is what makes the inner CheckAsync awaited — ContinueWith would return
        // Task<Task> and orphan the request.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            while (true)
            {
                await UpdateCheckService.Shared.CheckAsync(version =>
                {
                    _updateAvailableVersion = version;
                    // Pass the cached capacities: nulls here would drop the "remaining" line and
                    // latch the shortened text into _lastTooltip.
                    UpdateTooltip(_lastIconState.Pct < 0 ? 0 : _lastIconState.Pct, _lastRemainingMwh, _lastFullMwh);
                    RunOnUi(() => _menu?.SetUpdateBadge(version));
                }).ConfigureAwait(false);

                await Task.Delay(UpdateCheckInterval).ConfigureAwait(false);
            }
        });
    }

    // A tray click that lands while the popup is open first deactivates it, so guard against
    // immediately re-showing the popup from that same click.
    private const int ReopenGuardMs = 300;

    // True while a tray click is parked on the settle gate. UI thread only, so no locking.
    private bool _clickParkedOnGate;

    // When the previous tray left-click arrived, for TrayClickPolicy's double-click test. Null once
    // a pair has resolved, so a third rapid click starts a fresh pair. UI thread only.
    private DateTimeOffset? _lastTrayClickAt;

    // async void is safe here: this is an ICommand handler and the try/catch spans the await, which
    // is the settle gate and is normally already complete.
    private async void ToggleDashboard()
    {
        // Stamped BEFORE the settle gate below: a double-click is about how fast the USER clicked,
        // and the gate can park a click for seconds on a watchdog/auto-relaunch start.
        var now      = DateTimeOffset.Now;
        var previous = _lastTrayClickAt;
        _lastTrayClickAt = now;

        // A failure building or showing the popup must not take down the tray app.
        try
        {
            if (!WindowsReady.IsCompleted)
            {
                // Park the FIRST click and drop the rest: replaying them all in order would read as
                // open-then-hide. ReopenGuardMs cannot absorb it — the second click would take the
                // IsVisible branch and never reach the guard.
                if (_clickParkedOnGate) return;
                _clickParkedOnGate = true;
                try     { await WindowsReady.ConfigureAwait(true); }
                finally { _clickParkedOnGate = false; }
            }

            // Subscribe Closed only at creation so handlers don't accumulate on every click.
            if (_dashboard is null)
            {
                _dashboard = new DashboardWindow(this);
                _dashboard.Closed += (_, _) =>
                {
                    AppLog.Info("Dashboard window closed.");
                    _dashboard = null;
                };
            }

            switch (TrayClickPolicy.Decide(now, previous, NativeMethods.DoubleClickTime,
                                           _dashboard.AppWindow.IsVisible, _dashboard.SinceHidden,
                                           TimeSpan.FromMilliseconds(ReopenGuardMs)))
            {
                case TrayClickAction.HideDashboard:
                    _dashboard.HideWindow();
                    break;

                case TrayClickAction.OpenDashboard:
                    _dashboard.ShowNearTray();
                    break;

                case TrayClickAction.OpenSettingsAndHideDashboard:
                    // Ends the pair, so a third rapid click is a fresh first click.
                    _lastTrayClickAt = null;
                    // Hidden BEFORE Settings is activated: the dashboard is IsAlwaysOnTop and would
                    // otherwise fight the new window for z-order at the same corner of the screen.
                    if (_dashboard.AppWindow.IsVisible) _dashboard.HideWindow();
                    ShowSettingsWindow();
                    break;

                // TrayClickAction.None: the same gesture that just auto-hid the popup.
            }
        }
        catch (Exception ex)
        {
            LogCrash("ToggleDashboard", ex);
            _dashboard = null;   // drop the half-built window so the next click retries cleanly
        }
    }

    /// <summary>
    /// Opens the resizable battery-history graph window, or focuses it if already open. The window
    /// dismisses itself on focus loss, so Closed is what keeps the singleton reference honest.
    /// </summary>
    internal void ShowHistoryWindow()
    {
        // Guarded like its two siblings: BatteryHistoryWindow renders its graph in the constructor,
        // and a throw from this XAML event handler would reach Application.UnhandledException,
        // which deliberately leaves Handled = false.
        try
        {
            if (_historyWindow is not null)
            {
                _historyWindow.Activate();
                return;
            }

            // Captured BEFORE HideWindow below: the pop-out animates open from the dashboard's rect,
            // and null places the window at its final rect directly.
            Windows.Graphics.RectInt32? origin = null;
            if (_dashboard is { } dash && dash.AppWindow.IsVisible)
            {
                origin = new Windows.Graphics.RectInt32(
                    dash.AppWindow.Position.X, dash.AppWindow.Position.Y,
                    dash.AppWindow.Size.Width, dash.AppWindow.Size.Height);

                // Hidden now rather than via its own Deactivated handler: the dashboard is
                // IsAlwaysOnTop and would keep fighting the pop-out for z-order at the same rect.
                dash.HideWindow();
            }

            _historyWindow = new BatteryHistoryWindow(origin);
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("ShowHistoryWindow", ex);
            _historyWindow = null;   // drop the half-built window so the next click retries cleanly
        }
    }

    /// <summary>
    /// Opens the Settings window, or focuses it if already open. Unlike the dashboard and the
    /// history pop-out this is a normal titled window that stays open until the user closes it.
    /// </summary>
    internal async void ShowSettingsWindow()
    {
        try
        {
            await WindowsReady.ConfigureAwait(true);   // see ToggleDashboard for why this is async void

            if (_settings is not null)
            {
                _settings.RefreshAllSections();   // pick up any change made while it sat in the background
                _settings.Activate();
                return;
            }

            _settings = new SettingsWindow(_menu!,
                onHomeAssistantChanged: () => _ha?.ApplySettings(SettingsService.Current),
                onDiscoveryChanged: () => _ha?.RepublishDiscovery());
            _settings.Closed += (_, _) => _settings = null;
            _settings.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("ShowSettingsWindow", ex);
            _settings = null;   // drop the half-built window so the next click retries cleanly
        }
    }

    private void Shutdown()
    {
        _intentionalExit = true;          // tells OnProcessExit this teardown is legitimate
        WatchdogTask.WriteHoldMarker();   // and tells the watchdog task to stay down
        AppLog.Info("User exit via tray menu.");

        Battery.AggregateBattery.ReportUpdated -= OnBatteryReportUpdated;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        TravelOverrideService.StateChanged -= RefreshTooltip;
        NetworkLocationService.Stop();
        LidDelayService.Stop();   // hands the Windows lid-close action back before we go
        _ha?.Dispose();           // goes offline in HA but keeps the retained discovery
        _currentBatteryIcon?.Dispose();
        ToastService.Cleanup();
        _trayIcon?.Dispose();
        Application.Current.Exit();
    }
}
