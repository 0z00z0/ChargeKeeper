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

    // Cached tray icon state; Pct = -1 means not yet read. This is the last READING, which the MQTT
    // snapshot and the tooltip also depend on — never the record of what the icon is showing.
    private (int Pct, PowerState State, PowerFlow? Flow) _lastIconState = (-1, PowerState.Discharging, null);

    // What the tray icon is actually showing. Kept apart from _lastIconState so a repaint that never
    // landed cannot be recorded as applied and dedupe every later tick at the same reading away.
    private readonly TrayIconLatch _iconLatch = new();

    // Fire-once latch, reset with 5 % hysteresis so a brief charge re-arms it.
    private bool _lowBatteryWarningFired;

    // Holds the "already warned" line to one per latch: the alternative is a line on every tick
    // from the warning level down to empty.
    private bool _lowSuppressionLogged;

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

        // Here rather than in Shutdown or OnSessionEnding: this is the one point every clean exit
        // passes through — the tray Exit, a sign-out, and the silent compositor-loss teardown that
        // nothing else announces. A shutdown another app then vetoes never reaches it, so the line
        // cannot claim a stop that did not happen.
        if (StartupHealth.State == MonitoringState.Watching)
        {
            PowerLog.Say(HealthMessages.MonitoringStopped);
            StartupHealth.MarkStopped();
        }

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
            // After the tasks, never before: the sweep declines while one still starts from the
            // retired folder, so it has to read what TryEnsureTasks has just written.
            LegacyInstallSweep.TryRun();
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

        // Everything that makes the application useful hangs off this call, and a throw inside it
        // used to abandon start-up in silence behind a tray icon that looked normal. Whatever
        // fails, the log and the icon now say the battery is not being watched.
        try
        {
            StartMonitoring();
        }
        catch (Exception ex)
        {
            AppLog.Error("OnLaunched.StartMonitoring", ex);
            ReportStartupFailed();
        }
    }

    /// <summary>
    /// The second half of start-up: the host window, the battery subscription, sampling and every
    /// background service. Split from <see cref="OnLaunched"/> so one guard covers all of it.
    /// </summary>
    private void StartMonitoring()
    {
        _hostWindow = new MainWindow();
        _hostWindow.Closed += (_, _) => AppLog.Info("Host window closed.");
        SubscribeBatteryEvents();
        // Off the UI thread: the counter's first read in a fresh process costs roughly 30 s, which
        // StartMonitoring must not block on. SampleHistory() reads through it once warmed.
        Task.Run(ThermalZoneReader.WarmUp);
        StartHistorySampling();
        StartPerformanceSampling();
        ScheduleUpdateCheck();
        // Before the first evaluation: a rule keyed on the routed adapter can match the wrong place,
        // and applying its preset is exactly what this drops the rule to avoid.
        SettingsService.ClearRulesKeyedOnTheRoutedAdapter();
        NetworkLocationService.Start();
        KeepAwakeService.Start();
        // Also the crash-recovery point: puts the user's own Windows lid-close action back if a
        // previous run died with it still overridden.
        LidDelayService.Start();

        // The MQTT publisher. Inert unless the module's own settings say publishing is on and a
        // broker host is set; the move of that block out of settings.json runs inside the ctor,
        // before anything reads it.
        //
        // The live snapshot is read on the MQTT threads, so the fields are taken under the battery
        // lock; the caller publishes outside this call. Null means no reading yet, which the entities
        // publish as "unknown" rather than as a fabricated zero.
        _mqtt = new MqttPublisher(AppInfo.Version, () =>
        {
            using (_batteryReportLock.EnterScope())
            {
                if (_lastIconState.Pct < 0) return null;
                return LiveStateBuilder.Build(
                    _lastIconState.Pct, _lastRateMw ?? 0, _lastIconState.State != PowerState.Discharging,
                    _lastBatteryStatus, _lastThresholdState,
                    ChargerInfoService.CachedWattage, _lastRemainingMwh, _lastFullMwh, _lastDesignMwh, _lastLowPowerMode,
                    SettingsService.Read(s => s.Presets.ToList()));
            }
        });
        // The settings surface has no battery tick of its own, so every source that can move one of
        // its values signals it. Unchanged payloads are deduped, so a redundant signal costs nothing.
        // A broker setting is not one of these: it lives in the module's own file, whose store raises
        // its own change event straight at the connection.
        //
        // Both handlers below redo a whole surface, so both ask whether the change reached one first;
        // the broker's remembered endpoint alone is written back on every successful connect. A
        // reload raises the same event, so "reload settings from disk" needs no subscription of its
        // own even though it can move every published setting at once.
        SettingsService.ChangeCommitted     += c => { if (c.IsMaterial) _mqtt?.PublishSurfaceNow(); };
        // The tray style is one of those values, and an icon-mode command from Home Assistant has no
        // battery tick of its own. The latch carries the style, so this repaints only when it moved.
        SettingsService.ChangeCommitted     += c => { if (c.IsMaterial) RepaintTrayIconFromLastReading(); };
        KeepAwakeService.StateChanged       += () => _mqtt?.PublishSurfaceNow();
        NetworkLocationService.LocationChanged += _ => _mqtt?.PublishSurfaceNow();
    }

    private MqttPublisher? _mqtt;

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];

        // The identity the shell files this icon's settings under, including whether it sits in the
        // visible area or behind the overflow chevron. Left unset it is a hash of the executable's
        // full path, so moving the install folder reads to Windows as a different icon and drops
        // the position back to the chevron. Set before the icon is created, and never after
        // CustomName, which writes the library's own derived value back over it.
        //
        // Guarded on its own rather than with the creation below: an identity that cannot be
        // applied is a position that may not be remembered, which is no reason to leave the tray
        // with no icon at all.
        try
        {
            _trayIcon.Id = TrayIconIdentity.Value;
        }
        catch (Exception ex)
        {
            AppLog.Error("InitTrayIcon.Identity", ex);
        }

        // Start with the seed mark, drawn on the tray's own maximised geometry so the slot does not
        // change shape when the battery arc replaces it on the first event.
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
            try { _trayIcon.Icon = IconGenerator.RenderBatteryIcon(0, PowerState.Discharging, SettingsService.Current.IconMode); }
            catch (Exception fallbackEx) { AppLog.Error("InitTrayIcon.FallbackIcon", fallbackEx); }
        }

        // A second left-click inside the double-click window opens Settings instead.
        IToggleFeature[] features = [new AutoStartFeature()];
        _menu = new TrayMenu(features, Shutdown, ForceIconRefresh, onOpenSettings: ShowSettingsWindow,
                             windowsReady: WindowsReady);
        _trayIcon.ContextFlyout     = _menu.Flyout;
        _trayIcon.LeftClickCommand  = new RelayCommand(ToggleDashboard);
        _trayIcon.RightClickCommand = new RelayCommand(() => _menu!.RefreshState());

        // Passed explicitly: the parameter defaults to true, and H.NotifyIcon then puts the WHOLE
        // process into IDLE_PRIORITY_CLASS plus EcoQoS throttling for the rest of its life —
        // nothing reverses it. The tray icon's own left-click path hops through a threading timer,
        // so under load the menu arrives seconds late or not at all.
        //
        // Guarded: the shell refuses the Shell_NotifyIcon registration when it is still coming up
        // after a boot, and that throw used to abandon the whole of startup. The icon is a way to
        // reach the app, not what the app is for, so a refusal must not stop the battery being
        // watched. H.NotifyIcon re-adds the icon itself on the shell's TaskbarCreated broadcast.
        try
        {
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            PowerLog.Say(HealthMessages.TrayIconMissing);
            AppLog.Error("InitTrayIcon.ForceCreate", ex);
        }
    }

    /// <summary>
    /// Records a start-up that did not finish: the readable line, the state every tray repaint
    /// consults, and the warning mark itself. Reachable from the launch handler and from the
    /// battery-subscription task, which runs after it has returned.
    /// </summary>
    private void ReportStartupFailed()
    {
        PowerLog.Say(HealthMessages.MonitoringDidNotStart);
        StartupHealth.MarkFailed();
        _degradedTrayShown = false;
        RunOnUi(ApplyDegradedTrayPresentation);
    }

    /// <summary>Records that the watch is live, with the reading it started from and the levels it
    /// will warn at — the line that separates a working instance from a dead one at a glance.</summary>
    private void ReportMonitoringStarted()
    {
        int pct; PowerState state;
        using (_batteryReportLock.EnterScope())
            (pct, state) = (_lastIconState.Pct, _lastIconState.State);

        var s = SettingsService.Current;
        PowerLog.Say(HealthMessages.MonitoringStarted(
            Math.Max(pct, 0), state,
            s.LowBatteryWarningEnabled,  s.LowBatteryWarningPct,
            s.HighBatteryWarningEnabled, s.HighBatteryWarningPct));
        StartupHealth.MarkWatching();

        // The low-battery latch is in memory only, so a restart re-arms a warning that was already
        // given. Unrecorded, a second warning at the same level reads as a defect.
        if (s.LowBatteryWarningEnabled)
            AppLog.Info(NotificationMessages.LowWarningResetByRestart(s.LowBatteryWarningPct));
    }

    // What the tray icon is showing while degraded. Kept apart from _iconLatch, which records
    // battery readings and is bypassed entirely in that state.
    private bool _degradedTrayShown;

    /// <summary>
    /// Puts the warning mark and its plain-language tooltip on the tray icon. UI thread only, for
    /// the same reason <see cref="UpdateTrayIcon"/> is.
    /// </summary>
    private void ApplyDegradedTrayPresentation()
    {
        if (_trayIcon is not { } icon) return;

        try
        {
            var warning = IconGenerator.RenderWarningIcon();
            var previous = _currentBatteryIcon;
            icon.Icon           = warning;
            _currentBatteryIcon = warning;
            previous?.Dispose();

            icon.ToolTipText   = HealthMessages.DegradedTooltip;
            _lastTooltip       = HealthMessages.DegradedTooltip;
            _degradedTrayShown = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("ApplyDegradedTrayPresentation", ex);
        }
    }

    private void SubscribeBatteryEvents()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // The tray slot size is DPI-dependent and the render is gated on battery ticks, so without
        // this the arc stays rescaled until the next battery event.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // A light/dark flip is NOT a display event — the shell broadcasts WM_SETTINGCHANGE
        // ("ImmersiveColorSet"), which SystemEvents files under UserPreferenceChanged. Without this
        // the icon keeps the old outline strength until a display change or a restart.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // Travel-override toggles aren't battery events, so rebuild the tooltip on the service's own
        // state change — otherwise it stays stuck on "Charging to 100 %" after a revert.
        TravelOverrideService.StateChanged += RefreshTooltip;
        // Same reason for the screen-hold line: starting, ending or re-posting a session is not a
        // battery event, so without this the line outlives the hold it describes.
        KeepAwakeService.StateChanged += RefreshTooltip;

        // Seed the baseline from a forced read, THEN subscribe — in that order, so the first real
        // event cannot overlap the seed. Off the UI thread, so the battery read and the vendor RPCs
        // stay off the cold-start path.
        _ = Task.Run(() =>
        {
            // Nothing observes this Task, so without the catch a throw is completely silent: the
            // Battery.AggregateBattery read and the += below both sit outside
            // OnBatteryReportUpdated's own try, and either faulting means no seed, no subscription,
            // no battery event ever, and the icon left on the startup mark.
            try
            {
                // Registration leads the seed: a toast raised before the notification platform is
                // registered is silently dropped.
                ToastService.Register();
                // Exit is reachable from the tray menu while this runs, and Shutdown's -= would then
                // precede the += below, seeding against a disposed tray icon and MQTT service.
                if (_intentionalExit) return;
                OnBatteryReportUpdated(Battery.AggregateBattery, null!);
                Battery.AggregateBattery.ReportUpdated += OnBatteryReportUpdated;
                ReportMonitoringStarted();
            }
            catch (Exception ex)
            {
                LogCrash("SubscribeBatteryEvents.Seed", ex);
                // The subscription IS the watch, so a fault here is the failure the tray must show,
                // even though the launch handler returned successfully some time ago.
                ReportStartupFailed();
            }
        });
    }

    private PerformanceSampler? _performanceSampler;

    /// <summary>
    /// Brings the self-measurement sampler up and keeps it in step with the settings. Off is the
    /// default and off schedules nothing, so an installation that never turns this on pays for one
    /// object and no timers.
    /// </summary>
    private void StartPerformanceSampling()
    {
        _performanceSampler = new PerformanceSampler(new SystemPerformanceProbe(), new PerformanceHistorySink());

        // Every settings write raises Changed, not only these two; Apply is a no-op when neither has
        // moved, so an unrelated save does not restart the timers or lose the processor baseline.
        SettingsService.Changed  += ApplyPerformanceSettings;
        SettingsService.Reloaded += ApplyPerformanceSettings;
        ApplyPerformanceSettings();
    }

    private void ApplyPerformanceSettings()
    {
        try
        {
            var s = SettingsService.Current;
            bool wasSampling = _performanceSampler?.IsSampling ?? false;
            _performanceSampler?.Apply(s.PerformanceGraphEnabled, s.PerformanceSampleRate);

            // Switching off drops the live window too, so switching back on starts a fresh stretch
            // rather than drawing a line across the gap. The file is untouched either way.
            if (wasSampling && !s.PerformanceGraphEnabled)
            {
                PerformanceHistoryService.Flush();
                PerformanceHistoryService.ClearWindow();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("App.ApplyPerformanceSettings", ex);
        }
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
            // Independent of the battery reading below, and taken first: the thermal zone is on the
            // machine whether or not a battery report has arrived yet, and the plausibility gate it
            // feeds needs this tick's cadence regardless.
            ThermalStatusService.Sample();

            // Runs on a timer pool thread, so snapshot the fields together — a row must not pair
            // this tick's SoC with the previous tick's limit and power. Record does disk I/O and
            // must stay outside the lock.
            int pct; int? limit; int rate; PowerState state;
            using (_batteryReportLock.EnterScope())
            {
                if (_lastIconState.Pct < 0) return;   // no battery reading yet — nothing to log
                pct   = _lastIconState.Pct;
                limit = _lastThresholdState is { Enabled: true, Stop: > 0 } t ? t.Stop : null;
                rate  = _lastRateMw ?? 0;
                state = _lastIconState.State;
            }

            var gap = BatteryHistoryService.Record(pct, limit, rate, state);
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

    /// <summary>The last reading the application took, or null when none has arrived yet.</summary>
    private int? LastKnownLevel()
    {
        using (_batteryReportLock.EnterScope())
            return _lastIconState.Pct < 0 ? null : _lastIconState.Pct;
    }

    /// <summary>
    /// The waking half of the sleep pair: how long the machine was away and what the battery did
    /// meanwhile. The level is read fresh rather than taken from the cache — the cached one is from
    /// before the suspend, and reporting it would state the drain as nothing.
    /// </summary>
    private static void ReportWake()
    {
        var at = DateTimeOffset.Now;

        // Off the notification thread: a resume broadcast is on a deadline and the battery read is
        // a vendor call.
        _ = Task.Run(() =>
        {
            int? levelNow = null;
            try
            {
                levelNow = PercentFrom(Battery.AggregateBattery.GetReport());
            }
            catch (Exception ex)
            {
                // A missing level costs the sentence its second half, not the sentence.
                AppLog.Error("ReportWake.Read", ex);
            }

            if (SleepWatch.Wake(at, levelNow) is { } sentence) PowerLog.Say(sentence);
        });
    }

    /// <summary>State of charge from a battery report, or null when the report carries no usable
    /// capacity pair. The one place the percentage is derived, so no two readers can disagree.</summary>
    private static int? PercentFrom(BatteryReport report) =>
        report.FullChargeCapacityInMilliwattHours is > 0 and { } full &&
        report.RemainingCapacityInMilliwattHours  is { } remaining
            ? Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100)
            : null;

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // Every transition is logged: this timeline is what correlates a later silent teardown
        // with a power event.
        PowerLog.Event($"Windows power mode: {e.Mode}", "system power notification");

        // The sleep half of the pair. The cached reading is used rather than a fresh one: a suspend
        // notification is on a deadline, and a vendor read here would delay the machine going down.
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            SleepWatch.RecordSleep(DateTimeOffset.Now, LastKnownLevel());
            PowerLog.Say(SleepWatch.WentToSleep);
            return;
        }

        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;

        // Without this pair, downtime and a stopped application are the same shape in the log: a
        // gap in the samples with nothing to say which it was.
        ReportWake();

        // A charger swap while asleep produces no AC→battery transition to invalidate on.
        ChargerInfoService.Invalidate();

        // The socket can survive the OS suspend while the broker already dropped us via keep-alive,
        // flipping every sensor to "unavailable" — reconnect rather than wait out the backoff.
        _mqtt?.OnPowerResume();

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

            int pct = PercentFrom(report) ?? 0;

            // Windows separates the two mains states itself: Charging is taking charge, Idle is
            // connected and not. The gauge is painted from all three; the on-AC flag stays for
            // everything that only asks which power source is in use.
            var  powerState = PowerStates.From(report.Status);
            bool charging   = BatteryStatsFormatter.IsOnAC(report.Status);

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
            LiveState liveSnapshot;
            bool fireLowBattery = false;
            // The low-battery decision, carried out of the lock so the log write is file I/O the
            // lock does not span. Exactly one of the three is ever set on a tick.
            bool lowRepeatSuppressed = false;
            bool lowWarningReArmed   = false;
            int  lowWarnAtPct        = 0;
            int? highBatteryWarnAtPct = null;   // the configured level, carried out of the lock
            bool fireChargingStarted = false;
            int? chargeCompleteStopPct = null;
            bool? powerSourceEdge = null;   // true = now on AC; logged outside the lock
            using (_batteryReportLock.EnterScope())
            {
                // The rate is taken here rather than with the other _last* fields further down: the
                // icon is painted from it on the next line, and a rate assigned after that would
                // paint the previous tick's flow.
                _lastRateMw = report.ChargeRateInMilliwatts;
                var flow = PowerFlows.From(_lastRateMw);

                _lastIconState = (pct, powerState, flow);
                // Still gated to avoid GDI churn on every tick, but inside UpdateTrayIcon and against
                // what was last PAINTED — a repaint that never landed leaves the gate open.
                UpdateTrayIcon(pct, powerState, flow);

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
                lowWarnAtPct = s.LowBatteryWarningPct;
                bool lowLevelReached = s.LowBatteryWarningEnabled &&
                                       report.Status == BatteryStatus.Discharging &&
                                       pct > 0 &&
                                       pct <= s.LowBatteryWarningPct;
                if (lowLevelReached && !_lowBatteryWarningFired)
                {
                    _lowBatteryWarningFired = true;
                    fireLowBattery = true;   // fired outside the lock (see below)
                    _lowSuppressionLogged = false;
                }
                // Below the level with the warning already given. Recorded ONCE per latch: a
                // discharge from the level to empty is dozens of ticks, and one line answers "why
                // was there no second warning" for all of them.
                else if (lowLevelReached && !_lowSuppressionLogged)
                {
                    _lowSuppressionLogged = true;
                    lowRepeatSuppressed   = true;
                }
                // Reset the guard with hysteresis so it can fire again after a partial charge.
                else if (pct > s.LowBatteryWarningPct + 5)
                {
                    // Only news when a warning was actually outstanding; a machine sitting on
                    // charge would otherwise announce a re-arm on every tick.
                    lowWarningReArmed       = _lowBatteryWarningFired;
                    _lowBatteryWarningFired = false;
                    _lowSuppressionLogged   = false;
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

                _lastThresholdState = thresholdState;                          // hoisted read above
                _lastRemainingMwh   = report.RemainingCapacityInMilliwattHours;
                _lastFullMwh        = report.FullChargeCapacityInMilliwattHours;
                _lastDesignMwh      = report.DesignCapacityInMilliwattHours;
                _lastLowPowerMode   = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
                UpdateTooltip(pct, _lastRemainingMwh, _lastFullMwh);

                // Built here for a coherent _last* view; published below, outside the lock.
                liveSnapshot = LiveStateBuilder.Build(
                    pct, _lastRateMw ?? 0, charging, report.Status, _lastThresholdState, ChargerInfoService.CachedWattage,
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

            // Outside the lock: an outstanding lid-close discharge target can end here, and ending it
            // dispatches the suspend.
            LidDelayService.OnBatteryReport(pct, report.Status == BatteryStatus.Charging);

            // Outside the lock for the same reason the toasts are: the log write is file I/O.
            if (powerSourceEdge is { } onAc)
                PowerLog.Event($"Power source: now on {(onAc ? "AC" : "battery")}, battery {pct} %",
                               onAc ? "charger connected" : "charger disconnected");

            // The decision, recorded before the attempt to show it. Together with ToastService's own
            // shown/refused line this separates "never attempted" from "attempted and failed" —
            // the distinction the whole path was missing.
            if (fireLowBattery)
                AppLog.Info(NotificationMessages.LowThresholdCrossed(lowWarnAtPct, pct));
            else if (lowRepeatSuppressed)
                AppLog.Info(NotificationMessages.LowRepeatSuppressed(lowWarnAtPct, pct));
            else if (lowWarningReArmed)
                AppLog.Info(NotificationMessages.LowWarningReArmed(pct));

            // ToastService.Notify* does a synchronous WinRT/COM Show; the decisions and the latch
            // above were taken under the lock, so only the Show is deferred.
            if (fireLowBattery)                    ToastService.NotifyLowBattery(pct);
            if (highBatteryWarnAtPct is { } warnAt) ToastService.NotifyHighBattery(pct, warnAt);
            if (chargeCompleteStopPct is { } stop) ToastService.NotifyChargeComplete(stop);
            if (fireChargingStarted)               ToastService.NotifyChargingStarted();

            _mqtt?.PublishState(liveSnapshot);
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
    // Milliwatts; positive = charging, negative = draining. Nullable because an absent reading is
    // not a rate of zero: the tray's flow mark must draw nothing rather than claim "at rest".
    private int?    _lastRateMw;
    private int?    _lastRemainingMwh;   // cached so RefreshTooltip can rebuild without a battery event
    private int?    _lastFullMwh;
    private int?    _lastDesignMwh;      // design capacity — the battery-health denominator
    private bool    _lastLowPowerMode;   // Windows Energy Saver active
    private ChargeThresholdState? _lastThresholdState;

    private void UpdateTrayIcon(int pct, PowerState state, PowerFlow? flow)
    {
        // A start-up that failed must never paint a battery reading. The icon is the only thing
        // most people ever look at, and a normal-looking one is exactly what made an instance that
        // watched nothing indistinguishable from a working one.
        if (StartupHealth.IsDegraded)
        {
            if (!_degradedTrayShown) RunOnUi(ApplyDegradedTrayPresentation);
            return;
        }

        // Re-read on whichever thread gets here, so a repaint that waited in the queue draws the
        // style and the thresholds in force now rather than the ones set when it was posted. The
        // threshold state is already cached from this tick's ChargeThresholdService.Read.
        var request = new TrayIconRequest(pct, state, SettingsService.Current.IconMode,
                                          _lastThresholdState, flow);
        if (!_iconLatch.NeedsRepaint(request)) return;

        // UI thread only — ReportUpdated fires on an MTA thread, and mutating or disposing the icon
        // off-thread faults the native tray/GDI handle, an access violation that bypasses managed
        // try/catch and kills the process.
        if (_dispatcher is { } dq && !dq.HasThreadAccess)
        {
            // A refused enqueue is a repaint that will never happen; the latch is untouched either
            // way, so the next tick tries again rather than deduping against an icon that never moved.
            if (!dq.TryEnqueue(() => UpdateTrayIcon(pct, state, flow)))
                AppLog.Error("UpdateTrayIcon.Enqueue", new InvalidOperationException(
                    "The dispatcher queue refused the tray-icon repaint; the icon still shows the previous state."));
            return;
        }

        try
        {
            var newIcon = IconGenerator.RenderBatteryIcon(request.Pct, request.State, request.Mode,
                                                          request.Threshold, request.Flow);
            var oldIcon = _currentBatteryIcon;
            _trayIcon!.Icon     = newIcon;
            _currentBatteryIcon = newIcon;
            oldIcon?.Dispose();
            // Only here: the latch records what the tray icon is showing, not what was asked for.
            _iconLatch.MarkPainted(request);
        }
        catch (Exception ex)
        {
            // Non-fatal, but no longer silent — a swallowed failure used to leave the stale icon in
            // place with nothing anywhere to say why.
            AppLog.Error("UpdateTrayIcon", ex);
        }
    }

    /// <summary>Repaints from the last reading, for the sources that change the icon without a
    /// battery event of their own. Deduped by the latch, so an unrelated change costs nothing.</summary>
    private void RepaintTrayIconFromLastReading()
    {
        var (pct, state, flow) = TrayIconLatch.ReadingOrUnknown(_lastIconState);
        UpdateTrayIcon(pct, state, flow);
    }

    /// <summary>
    /// The tray slot size is DPI-dependent, so a display topology or DPI change drops the cached
    /// slot size and repaints the icon at the new resolution. The taskbar's light/dark setting is
    /// dropped on the same event: it decides the glyph's outline strength, and this is the display
    /// notification the shell does raise.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Helpers.IconGenerator.InvalidateSlotSizeCache();
        Helpers.IconGenerator.InvalidateThemeCache();
        ForceIconRefresh();
    }

    /// <summary>
    /// Repaints the tray icon when the shell's light/dark setting flips, which decides the glyph's
    /// outline strength and the arc's empty track. Three categories can carry it: General is where
    /// WM_SETTINGCHANGE "ImmersiveColorSet" lands (no SPI code, so it falls to the default), Color
    /// comes from WM_SYSCOLORCHANGE and VisualStyle from WM_THEMECHANGED. General is also the
    /// catch-all for every unmapped setting, so the repaint is gated on the theme value having
    /// actually moved rather than on the event arriving.
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (!Helpers.IconGenerator.CategoryCanCarryThemeChange(e.Category)) return;
        if (!Helpers.IconGenerator.RefreshThemeCacheIfChanged()) return;

        // SystemEvents raises on its own hidden-window thread, so the repaint has to be marshalled.
        // RunOnUi and never a bare TryEnqueue: a throw inside a raw dispatcher callback does not
        // reach Application.UnhandledException and kills the process as a stowed 0xC000027B.
        RunOnUi(ForceIconRefresh);
    }

    /// <summary>Forces an immediate tray icon re-render from the last known battery state, or from
    /// the unknown state when no battery report has arrived yet — the style change, the slot-size
    /// change and the tray recreate that call this all have to show without waiting for a tick.</summary>
    internal void ForceIconRefresh()
    {
        // A degraded instance repaints the warning mark, not a reading — the slot size and the
        // taskbar theme move under it just the same.
        if (StartupHealth.IsDegraded)
        {
            _degradedTrayShown = false;
            RunOnUi(ApplyDegradedTrayPresentation);
            return;
        }

        // The pixels changed without the request changing, so the latch has to be dropped first.
        _iconLatch.Invalidate();
        RepaintTrayIconFromLastReading();
    }

    /// <summary>
    /// Rebuilds the tray tooltip and the icon from the last cached reading, for the changes that
    /// arrive without a battery event (the travel-override activate/revert). Re-reads the charge
    /// threshold, so a just-restored Smart Charge limit shows in both immediately.
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
        // The icon carries the threshold marks too, and the threshold is what just moved. Deduped by
        // the latch, so an unchanged one costs nothing.
        RepaintTrayIconFromLastReading();
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
        // Same reason as UpdateTrayIcon: while start-up has failed the tooltip says so and nothing
        // else, so a hover cannot report a battery level nothing is watching.
        if (StartupHealth.IsDegraded) return;

        var lines = new System.Text.StringBuilder();

        // A tray tooltip is plain text, so a colour emoji is the only way to carry the brand teal.
        lines.Append($"💠 ChargeKeeper  v{AppInfo.Version}");

        // U+FE0E forces the bolt to its text/outline form, so it renders bright like the ⚙/⏱/⬆
        // outlines rather than as a dark colour emoji on the dark tooltip background.
        bool onAc = _lastIconState.State != PowerState.Discharging;
        string chargeIcon = onAc ? "⚡︎" : "🔋";
        // Adapter wattage rides the "AC" label — it is a property of the power source, not a new
        // stat — and only shows on AC, where the cache is warm.
        string acLabel = ChargerInfoService.CachedWattage is { } watts ? $"AC ({watts}W)" : "AC";
        lines.Append(onAc
            ? $"\n{chargeIcon} {acLabel} · {pct}%"
            : $"\n{chargeIcon} {pct}%");
        int rateMw = _lastRateMw ?? 0;
        string? rate = (onAc && rateMw > 0) || (!onAc && rateMw < 0)
            ? PowerFormat.SignedRate(rateMw)
            : null;
        if (rate is not null)
            lines.Append($"  ·  {rate}");

        string timeText = BatteryStatsFormatter.FormatTimeRemaining(_lastRateMw, remainingMwh, fullMwh);
        if (timeText != "—")
            lines.Append($"\n⏱ {timeText}");

        // A mode-based vendor (HP, Surface) reports Start as 0 by contract, so it gets a cap rather
        // than a range.
        if (TravelOverrideService.IsActive)
            lines.Append("\n🔝 Charging to 100%");
        else if (_lastThresholdState is { IsLimiting: true } sc)
            lines.Append(sc.HasStartThreshold ? $"\n⚙ Smart Charge: {sc.Start}–{sc.Stop}%"
                                              : $"\n⚙ Smart Charge: to {sc.Stop}%");

        // The screen hold is the one keep-awake state that costs real power, so it is the only part
        // of the session the tooltip carries. U+FE0E keeps the sun an outline, like the glyphs above.
        if (KeepAwakeService.Current is not null && SettingsService.Current.KeepAwakeDisplayOn)
            lines.Append("\n☀︎ Keep Awake: screen on");

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

            _settings = new SettingsWindow(_menu!, _mqtt);
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
        // SystemEvents holds its handlers in a static list, so an app that forgets this one stays
        // rooted for the life of the process.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        TravelOverrideService.StateChanged -= RefreshTooltip;
        KeepAwakeService.StateChanged -= RefreshTooltip;
        SettingsService.Changed  -= ApplyPerformanceSettings;
        SettingsService.Reloaded -= ApplyPerformanceSettings;
        // Stops both timers and writes out whatever the last second collected.
        _performanceSampler?.Dispose();
        PerformanceHistoryService.Flush();
        NetworkLocationService.Stop();
        LidDelayService.Stop();   // hands the Windows lid-close action back before we go
        _mqtt?.Dispose();         // publishes offline, and leaves the document standing
        _currentBatteryIcon?.Dispose();
        ToastService.Cleanup();
        _trayIcon?.Dispose();
        Application.Current.Exit();
    }
}
