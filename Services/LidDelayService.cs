using System.Collections.Concurrent;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Waits N minutes after the lid closes before letting the machine sleep. The rules live in the pure
/// <see cref="LidDelayPolicy"/>; this owns the power-scheme override, the lid subscription, the OS
/// hold and the suspend.
/// </summary>
/// <remarks>
/// Lid close is a power-policy action, not an idle timeout, so <c>SetThreadExecutionState</c> cannot
/// delay it: the only mechanism is to park the user's own lid-close action on "do nothing" while the
/// feature is on, hold the machine awake, then suspend explicitly. Those values must reach disk
/// BEFORE the scheme is written and are never re-captured while stored, or a crash strands the laptop
/// on "do nothing" — which Windows hides from Settings on Modern Standby machines. Lid actions are
/// per-scheme, so the scheme is stored with them and every write targets it explicitly. The OS hold
/// uses its own holder thread rather than <see cref="KeepAwakeService"/>'s single current session,
/// which borrowing would cancel.
/// </remarks>
internal static class LidDelayService
{
    // Guards _delayPending + _timer + _lidSeeded + _generation + _started.
    private static readonly System.Threading.Lock _sync = new();

    // Separate from _sync because Subscribe holds it across RegisterLidNotification, during which
    // Windows delivers the seeding callback — and that callback takes _sync.
    private static readonly System.Threading.Lock _subscribeSync = new();

    private static System.Threading.Timer? _timer;
    private static bool   _delayPending;
    private static bool   _delayElapsed;   // the timer ran; only a discharge target can still hold
    private static bool   _lidSeeded;      // has the registration replay been consumed?
    private static long   _generation;     // bumped by anything that invalidates a queued suspend
    private static IntPtr _lidRegistration = IntPtr.Zero;

    // The discharge target outstanding for the current lid close, and the newest battery reading.
    // The reading is kept so arming can judge a machine that is already at its target, rather than
    // holding until the level happens to move again.
    private static readonly LidDischargeWatch _discharge = new();
    private static (int Percent, bool Charging)? _lastBattery;

    // The override this process applied, and the values it displaced. Authoritative over
    // settings.json while it is set — see OnSettingsReloaded.
    private static (Guid Scheme, uint Ac, uint Dc)? _appliedOverride;

    private static readonly BlockingCollection<uint> _holdRequests = new();
    private static Thread? _holder;

    private static bool _started;

    // Hardware, so it is asked once: the dashboard reconciles its Lid close section every refresh.
    private static bool? _lidPresent;

    /// <summary>
    /// Whether this machine has a lid to delay. A failed capability query counts as present — hiding
    /// the feature on a laptop is worse than offering it on a machine that will never close a lid.
    /// </summary>
    public static bool IsSupported => _lidPresent ??= NativeMethods.LidPresent() ?? true;

    /// <summary>
    /// Called once at startup, and also the crash-recovery entry point: it runs the
    /// <see cref="LidDelayPolicy.DecideStartup"/> table first, so a lid action left overridden by a
    /// dead process is put back even if the user never opens Settings again.
    /// </summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }

        var s = SettingsService.Current;
        if (!s.LidDelayEnabled && s.HasSavedLidAction)
            PowerLog.Event("Lid-close action was left overridden by a previous run — restoring it",
                           "crash recovery at startup");

        Reconcile();
        SettingsService.Reloaded += OnSettingsReloaded;
    }

    /// <summary>
    /// Releases everything this service owns and puts the user's lid-close action back — the exact
    /// inverse of <see cref="Start"/>, so a later Start can re-arm it. Called from clean shutdown and
    /// from logoff/restart; a crash is covered by <see cref="Start"/> instead.
    /// </summary>
    public static void Stop()
    {
        // Dropped along with the rest: OnSettingsReloaded reconciles, and a reload reaching a stopped
        // service would re-apply the override with no Stop left to undo it.
        SettingsService.Reloaded -= OnSettingsReloaded;
        CancelDelay();
        Unsubscribe();
        if (SettingsService.Current.HasSavedLidAction) RestoreSavedAction();
        lock (_sync) { _started = false; }
    }

    /// <summary>
    /// Returns false only from the enable path, meaning the power scheme could not be written and the
    /// setting was left off rather than promising a delay the machine will not honour. Disabling
    /// always returns true; a failed restore stays owed to the next <see cref="Start"/>.
    /// </summary>
    public static bool SetEnabled(bool enable)
    {
        if (enable)
        {
            var s = SettingsService.Current;
            if (!(s.HasSavedLidAction ? ApplyOverrideOnly() : CaptureAndOverride()))
            {
                AppLog.Info("LidDelay: could not change the Windows lid-close action — leaving the feature off.");
                return false;
            }
            SettingsService.Update(x => x.LidDelayEnabled = true);
            Subscribe();
            PowerLog.Event($"Lid-close delay on, {SettingsService.Current.LidDelayMinutes} min",
                           "the setting was turned on");
            return true;
        }

        SettingsService.Update(x => x.LidDelayEnabled = false);
        CancelDelay();
        Unsubscribe();
        PowerLog.Event(RestoreSavedAction()
            ? "Lid-close delay off, the Windows lid-close action is back to its own value"
            : "Lid-close delay off, but the Windows lid-close action could not be restored — retrying at next start",
            "the setting was turned off");
        return true;
    }

    /// <summary>
    /// Turns the discharge target on or off. Paired with its runtime effect rather than left to a
    /// plain settings write, for the same reason <see cref="SetEnabled"/> is: switching it off while
    /// a target is outstanding must drop that target, or the hold outlives the feature.
    /// </summary>
    public static void SetDischargeEnabled(bool enable)
    {
        SettingsService.Update(s => s.LidDischargeEnabled = enable);
        if (enable)
        {
            PowerLog.Event($"Lid-close discharge target on, {SettingsService.Current.LidDischargeTargetPercent} %",
                           "the setting was turned on");
            return;
        }

        bool wasWatching;
        lock (_sync)
        {
            wasWatching = _discharge.IsWatching;
            _discharge.Disarm();
        }
        PowerLog.Event("Lid-close discharge target off", "the setting was turned off");
        if (wasWatching) Complete();
    }

    /// <summary>
    /// Feeds the newest battery reading to an outstanding discharge target, and keeps it for the next
    /// lid close. The stop condition is the charge level, never a "power is connected" reading:
    /// connected power may deliver less than the machine draws, so the battery can drain while
    /// plugged in, and a connectivity test would hold that machine awake indefinitely.
    /// </summary>
    public static void OnBatteryReport(int percent, bool isCharging)
    {
        LidDischargeDecision decision;
        lock (_sync)
        {
            _lastBattery = (percent, isCharging);
            decision = _discharge.OnReading(percent, isCharging);
        }

        switch (decision)
        {
            case LidDischargeDecision.TargetReached:
                PowerLog.Event($"Battery reached its lid-close target at {percent} %",
                               "the discharge target was met");
                break;
            case LidDischargeDecision.Charging:
                PowerLog.Event($"Lid-close discharge target given up at {percent} %",
                               "the battery is charging, so the target cannot be reached");
                break;
            default:
                return;
        }

        // No-op while the wait itself is still running — the timer decides then, and finds the watch
        // already released.
        Complete();
    }

    /// <summary>Brings the power scheme and the lid subscription in line with the stored settings.
    /// Shared by <see cref="Start"/> and the settings-reload path.</summary>
    private static void Reconcile()
    {
        switch (LidDelayPolicy.DecideStartup(SettingsService.Current.LidDelayEnabled,
                                             SettingsService.Current.HasSavedLidAction))
        {
            case LidActionOverride.CaptureAndOverride: CaptureAndOverride(); break;
            case LidActionOverride.ReapplyOverride:    ApplyOverrideOnly();  break;
            case LidActionOverride.Restore:            RestoreSavedAction(); break;
        }

        if (SettingsService.Current.LidDelayEnabled) Subscribe();
        else { CancelDelay(); Unsubscribe(); }
    }

    /// <summary>
    /// The in-memory record wins over the reloaded file: settings.json roams, so it can arrive from
    /// another machine with no saved lid action while this machine's scheme is still parked on "do
    /// nothing", and believing it would lose the user's original for good.
    /// </summary>
    private static void OnSettingsReloaded()
    {
        if (_appliedOverride is { } applied && !SettingsService.Current.HasSavedLidAction)
        {
            AppLog.Info("LidDelay: reloaded settings carried no saved lid action while an override is live — " +
                        "restoring the record from this session.");
            SettingsService.Update(s =>
            {
                s.LidDelaySavedAcAction = (int)applied.Ac;
                s.LidDelaySavedDcAction = (int)applied.Dc;
                s.LidDelaySavedScheme   = applied.Scheme.ToString();
            });
        }
        Reconcile();
    }

    /// <summary>The stored scheme, or the active one for a settings file written before the scheme
    /// was tracked. Null when none can be resolved at all.</summary>
    private static Guid? SchemeForSavedValues() =>
        Guid.TryParse(SettingsService.Current.LidDelaySavedScheme, out var stored)
            ? stored
            : NativeMethods.ReadActiveLidCloseAction()?.Scheme;

    /// <summary>
    /// The order matters: the user's own values must reach disk before the setting they describe
    /// changes, or a crash in between strands the machine on "do nothing".
    /// </summary>
    private static bool CaptureAndOverride()
    {
        if (NativeMethods.ReadActiveLidCloseAction() is not { } original)
        {
            AppLog.Info("LidDelay: no lid-close action in the active power scheme (no lid?) — nothing to override.");
            return false;
        }

        SettingsService.Update(s =>
        {
            s.LidDelaySavedAcAction = (int)original.Ac;
            s.LidDelaySavedDcAction = (int)original.Dc;
            s.LidDelaySavedScheme   = original.Scheme.ToString();
        });

        return ApplyOverrideOnly();
    }

    /// <summary>
    /// Parks both lid-close actions on "do nothing" without touching the saved originals. Both AC and
    /// DC are set because Windows re-evaluates the policy for the current power source.
    /// </summary>
    private static bool ApplyOverrideOnly()
    {
        if (SchemeForSavedValues() is not { } scheme)
        {
            AppLog.Error("LidDelayService.ApplyOverrideOnly: no power scheme to write to", null);
            return false;
        }

        if (!NativeMethods.WriteLidCloseAction(scheme,
                NativeMethods.LIDACTION_DO_NOTHING, NativeMethods.LIDACTION_DO_NOTHING))
        {
            AppLog.Error("LidDelayService.ApplyOverrideOnly: the power scheme write failed", null);
            return false;
        }

        var s = SettingsService.Current;
        _appliedOverride = (scheme, (uint)(s.LidDelaySavedAcAction ?? 1), (uint)(s.LidDelaySavedDcAction ?? 1));
        return true;
    }

    /// <summary>
    /// The saved values are cleared only after a successful write, so a failed restore stays owed to
    /// the next <see cref="Start"/> rather than losing the setting for good.
    /// </summary>
    private static bool RestoreSavedAction()
    {
        var s = SettingsService.Current;
        if (!s.HasSavedLidAction) return true;

        if (SchemeForSavedValues() is not { } scheme)
        {
            AppLog.Error("LidDelayService.RestoreSavedAction: no power scheme to write to", null);
            return false;
        }

        // A half-written pair still beats leaving one side parked on the override: fall back to
        // "sleep", the Windows default for a lid close.
        uint ac = (uint)(s.LidDelaySavedAcAction ?? 1);
        uint dc = (uint)(s.LidDelaySavedDcAction ?? 1);

        if (!NativeMethods.WriteLidCloseAction(scheme, ac, dc))
        {
            AppLog.Error("LidDelayService.RestoreSavedAction: could not put the lid-close action back", null);
            return false;
        }

        _appliedOverride = null;
        SettingsService.Update(x =>
        {
            x.LidDelaySavedAcAction = null;
            x.LidDelaySavedDcAction = null;
            x.LidDelaySavedScheme   = null;
        });
        return true;
    }

    private static void Subscribe()
    {
        lock (_subscribeSync)
        {
            if (_lidRegistration != IntPtr.Zero) return;
            lock (_sync) { _lidSeeded = false; }   // the next callback is the registration replay

            // Registered without _sync held: Windows delivers the seeding callback during this call,
            // and that callback takes _sync.
            var registration = NativeMethods.RegisterLidNotification(OnLidState);
            if (registration == IntPtr.Zero)
            {
                AppLog.Error("LidDelayService.Subscribe: could not subscribe to the lid switch", null);
                return;
            }
            _lidRegistration = registration;
        }
    }

    private static void Unsubscribe()
    {
        IntPtr registration;
        lock (_subscribeSync)
        {
            registration     = _lidRegistration;
            _lidRegistration = IntPtr.Zero;
        }
        // Outside the lock: this is the call that would deadlock against an in-flight callback.
        NativeMethods.UnregisterLidNotification(registration);
    }

    /// <summary>Lid-switch callback — arrives on an OS thread, so it must not block.</summary>
    private static void OnLidState(bool closed)
    {
        LidDelayAction action;
        bool first;
        lock (_sync)
        {
            first = !_lidSeeded;
            _lidSeeded = true;
            // Any lid open invalidates a queued suspend, including one already decided on but not yet
            // run — by then _delayPending is false and the policy has nothing left to cancel.
            if (!closed) _generation++;
            action = LidDelayPolicy.OnLidState(closed ? LidState.Closed : LidState.Opened,
                                               SettingsService.Current.LidDelayEnabled, _delayPending, first);
        }

        // Logged whatever the policy decided, replay included: a lid event the feature ignored is
        // what someone asking "why didn't it sleep" needs to see.
        PowerLog.Event($"Lid {(closed ? "closed" : "opened")}",
                       first ? "lid-switch registration replay (initial state, not a real transition)"
                             : "lid switch");

        switch (action)
        {
            case LidDelayAction.StartDelay:
                StartDelay();
                LockIfConfigured();
                break;
            case LidDelayAction.Cancel:
                CancelDelay();
                PowerLog.Event("Lid-close delay cancelled, the machine stays awake", "lid reopened");
                break;
        }
    }

    /// <summary>
    /// Locks the workstation as the lid closes. The delay window is exactly the period the machine
    /// sits awake with the lid shut, so the lock belongs here, not beside the suspend at its end.
    /// </summary>
    private static void LockIfConfigured()
    {
        var s = SettingsService.Current;
        if (!LidDelayPolicy.ShouldLockOnLidClose(s.LidDelayEnabled, s.LidDelayLockOnClose,
                                                 KeepAwakeService.Current is not null))
            return;

        if (NativeMethods.LockComputer())
        {
            PowerLog.Event("Computer locked", "the lid closed with the lid-close delay on");
        }
        else
        {
            PowerLog.Event("Lock was refused by Windows", "LockWorkStation returned false");
            AppLog.Error("LidDelayService.LockComputer failed", null);
        }
    }

    private static void StartDelay()
    {
        var s = SettingsService.Current;
        var delay = LidDelayPolicy.DelayFor(s.LidDelayMinutes);
        int? watchingFor = null;
        lock (_sync)
        {
            // Re-checked under the lock: OnLidState decided and then released it, so two concurrent
            // close notifications can both have been told to start, and the second would restart
            // the countdown.
            if (_delayPending) return;
            EnsureHolder();
            _delayPending = true;
            _delayElapsed = false;

            // Armed only where a battery reading exists, and judged against it at once: a machine
            // already at its target must not wait for a level that has no reason to move, and a
            // machine with no battery at all could never release a watch once it held.
            if (s.LidDischargeEnabled && _lastBattery is { } reading)
            {
                _discharge.Arm(s.LidDischargeTargetPercent);
                if (_discharge.OnReading(reading.Percent, reading.Charging) == LidDischargeDecision.Hold)
                    watchingFor = _discharge.Target;
            }

            _holdRequests.Add(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => OnTimerFired(), null, delay, Timeout.InfiniteTimeSpan);
        }
        PowerLog.Event($"Lid-close delay armed — suspending in {delay.TotalMinutes:0} min unless the lid reopens",
                       "lid closed with the lid-close delay on");
        if (watchingFor is { } target)
            PowerLog.Event($"Sleep also waits for the battery to reach {target} %",
                           "lid closed with a discharge target set");
    }

    private static void OnTimerFired()
    {
        lock (_sync) _delayElapsed = true;
        Complete();
    }

    /// <summary>
    /// Decides what happens now the wait is over: suspend, hold on for an outstanding discharge
    /// target, or release the hold without sleeping. Reached from the timer, and again from whatever
    /// ends a hold — a battery reading or the target being switched off.
    /// </summary>
    private static void Complete()
    {
        LidDelayAction action;
        long gen;
        lock (_sync)
        {
            // Before the wait is up the timer still owns the decision; suspending here would cut the
            // delay short for a machine that merely reached its target early.
            if (!_delayElapsed) return;

            action = LidDelayPolicy.OnTimerFired(SettingsService.Current.LidDelayEnabled, _delayPending,
                                                 KeepAwakeService.Current is not null, _discharge.IsWatching);
            if (action is LidDelayAction.Suspend or LidDelayAction.Cancel) ClearLocked();
            gen = _generation;
        }

        switch (action)
        {
            case LidDelayAction.Suspend:
                PowerLog.Event("Suspending the machine",
                               "the lid-close delay elapsed with the lid still closed");
                // Off this timer thread: SetSuspendState does not return until the machine resumes.
                Task.Run(() =>
                {
                    // The lid can be opened between the decision above and this running, by which
                    // point _delayPending is false and nothing else would stop the suspend.
                    lock (_sync)
                    {
                        if (_generation != gen)
                        {
                            PowerLog.Event("Suspend abandoned", "the lid was opened before it ran");
                            return;
                        }
                    }
                    if (!NativeMethods.Suspend())
                    {
                        PowerLog.Event("Suspend was refused by Windows", "SetSuspendState returned false");
                        AppLog.Error("LidDelayService.Suspend failed", null);
                    }
                });
                break;
            case LidDelayAction.Cancel:
                PowerLog.Event("Lid-close delay elapsed but the machine was not suspended",
                               "a keep-awake session is holding it awake");
                break;
            case LidDelayAction.Hold:
                PowerLog.Event("Lid-close delay elapsed but the machine was not suspended",
                               "the battery has not drained to its target yet");
                break;
        }
    }

    private static void CancelDelay()
    {
        lock (_sync)
        {
            _generation++;   // invalidates a suspend that was already decided on
            _discharge.Disarm();
            if (!_delayPending) return;
            ClearLocked();
        }
    }

    // Callers hold _sync.
    private static void ClearLocked()
    {
        _delayPending = false;
        _delayElapsed = false;
        _discharge.Disarm();
        _timer?.Dispose();
        _timer = null;
        // Clearing must happen on the thread that made the request — post it, don't call it here.
        if (_holder is not null) _holdRequests.Add(NativeMethods.ES_CONTINUOUS);
    }

    private static void EnsureHolder()
    {
        if (_holder is not null) return;
        // Background thread: process exit tears it down, which releases the execution state anyway.
        _holder = new Thread(HolderLoop) { IsBackground = true, Name = "LidDelay" };
        _holder.Start();
    }

    private static void HolderLoop()
    {
        foreach (uint flags in _holdRequests.GetConsumingEnumerable())
        {
            try
            {
                NativeMethods.SetThreadExecutionState(flags);
                // Logged here, not at the request sites: this is when the OS learns about the hold.
                PowerLog.Event(flags == NativeMethods.ES_CONTINUOUS
                                   ? "OS keep-awake hold released"
                                   : "OS keep-awake hold taken",
                               "lid-close delay");
            }
            catch (Exception ex) { AppLog.Error("LidDelayService.SetThreadExecutionState", ex); }
        }
    }
}
