using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Waits N minutes after the lid closes before letting the machine sleep. The rules live in the pure
/// <see cref="LidDelayPolicy"/>; this owns the power-scheme override, the lid subscription, the OS
/// hold and the suspend.
/// </summary>
/// <remarks>
/// Lid delay is a power-policy action, not an idle timeout, so <c>SetThreadExecutionState</c> cannot
/// delay it: the only mechanism is to park the user's own lid-close action on "do nothing" while the
/// feature is on, hold the machine awake, then suspend explicitly. Those values must reach disk
/// BEFORE the scheme is written and are never re-captured while stored, or a crash strands the laptop
/// on "do nothing" — which Windows hides from Settings on Modern Standby machines. Lid actions are
/// per-scheme, so the scheme is stored with them and every write targets it explicitly. The OS hold
/// runs on its own <see cref="ExecutionStateHolder"/> instance: the holder thread is a class shared
/// with <see cref="KeepAwakeService"/>, but each service owns the session running on it, and
/// borrowing <see cref="KeepAwakeService"/>'s running session — rather than merely its thread type —
/// is what a hand-off or a network change would cancel.
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
    private static bool   _lidSeeded;      // has the registration replay been consumed?

    // True between a start that found the lid already shut and the lid next opening. Over that span
    // Windows owns the lid-close action again: no wait can be resumed, and leaving the override in
    // place would park the action on "do nothing" with nobody serving the close.
    private static bool   _handedBack;

    // The lid as the switch last reported it, kept beyond the end of a wait. A sleep a keep-awake
    // session suppressed is served when that session ends, and by then no wait is running, so
    // _delayPending can no longer answer whether the lid is still shut.
    private static bool   _lidClosed;

    // The value the previous notification carried and the instant it arrived, kept only so the next
    // one can say whether it changed anything. Nothing de-duplicates against them: a repeat arms a
    // fresh wait exactly as a transition does, and naming it is what this records.
    private static bool?           _lastLidPayload;
    private static DateTimeOffset? _lastNotificationAt;

    // A wait whose conditions were met while a keep-awake session held the machine awake. The sleep
    // is owed rather than cancelled: the condition arrived, and only the session stopped it being
    // acted on. Cleared when it is served, when the lid opens, and when the feature goes off.
    private static bool _sleepOwed;

    // The two conditions of the current lid close: whether each was set when it started, and whether
    // it has arrived. Whichever arrives first ends the wait — see LidDelayPolicy.WaitIsOver.
    private static bool _timeSet, _timeArrived, _targetSet, _targetArrived;

    // Whether the battery target was withdrawn mid-wait because a charger put it out of reach. Kept
    // apart from _targetSet because the two states it separates are otherwise identical: a wait that
    // never had a condition is over, and a wait whose only condition was taken away is not.
    private static bool _targetGivenUp;

    // What the current lid close says while it runs and at its end. The timer reports on the delay;
    // the battery reports off the readings that arrive anyway.
    private static readonly LidWaitTrail _trail = new();
    private static System.Threading.Timer? _heartbeat;
    private static DateTimeOffset _waitStartedAt;

    // The delay the current wait armed with, so the published countdown reports the timer that is
    // actually running. A setting changed mid-wait moves neither the timer nor this.
    private static TimeSpan _waitDelay;

    // Wall clock and awake time taken together as the wait arms. Elapsed time alone cannot say
    // whether the machine was held awake or slept through most of the wait — the process resumes and
    // carries on counting either way.
    private static AwakeMark _waitClock;

    private static long   _generation;     // bumped by anything that invalidates a queued suspend
    private static IntPtr _lidRegistration = IntPtr.Zero;

    // The discharge target outstanding for the current lid close, and the newest battery reading.
    // The reading is kept so arming can judge a machine that is already at its target, rather than
    // holding until the level happens to move again.
    private static readonly LidDischargeWatch _discharge = new();

    // The temperature safeguard. Armed with the hold and stood down with it, never a background
    // monitor: the hold is what the application does to the machine, so the ceiling belongs to it.
    private static readonly LidThermalWatch _thermal = new();
    private static bool _thermalEnded;
    private static (int Percent, bool Charging)? _lastBattery;

    // The override this process applied, and the values it displaced. Authoritative over
    // settings.json while it is set — see OnSettingsReloaded.
    private static (Guid Scheme, uint Ac, uint Dc)? _appliedOverride;

    private static readonly ExecutionStateHolder _holder = new("LidDelay", "lid-close delay",
        _ => "OS keep-awake hold taken", $"{nameof(LidDelayService)}.SetThreadExecutionState");

    private static bool _started;

    // Hardware, so it is asked once: the dashboard reconciles its Lid delay section every refresh.
    private static bool? _lidPresent;

    /// <summary>Raised (off the UI thread) whenever the feature is switched on or off, including when
    /// it stands itself down after a lid close reached sleep. Every surface showing the switch follows
    /// this one signal rather than reading the setting on a timer.</summary>
    public static event Action? StateChanged;

    /// <summary>
    /// Raised (on the OS callback thread) for a real lid movement, carrying what the delivery was.
    /// Only this service holds the subscription, and Windows permits one per program, so anything
    /// else that wants to know a lid moved follows this. A subscriber must not block: the callback
    /// this runs on is the one a lid-close wait is armed from.
    /// </summary>
    /// <remarks>Every delivery is raised, repeats and the value delivered at registration included,
    /// so the reading of what counts as a movement stays in one place —
    /// <see cref="ScriptTriggerPolicy.ForLid"/> — rather than being decided here as well.</remarks>
    public static event Action<LidEventKind>? LidNotification;

    /// <summary>
    /// Whether this machine has a lid to delay. A failed capability query counts as present — hiding
    /// the feature on a laptop is worse than offering it on a machine that will never close a lid.
    /// </summary>
    public static bool IsSupported => _lidPresent ??= NativeMethods.LidPresent() ?? true;

    /// <summary>The wait as it stands, for the published surface. Composed under the lock so the
    /// state and the instant it would sleep at cannot describe two different moments.</summary>
    /// <remarks>Nothing is precomputed: the sleep instant is the moment the wait armed plus the
    /// delay it armed with, and the countdown over it is taken fresh by whoever reads this.</remarks>
    public static LidWaitSnapshot WaitNow()
    {
        lock (_sync)
        {
            var state = LidWaitStates.From(SettingsService.Current.LidDelayEnabled,
                                           _delayPending, _timeSet, _targetSet);
            return new LidWaitSnapshot(
                state,
                _delayPending && _timeSet ? _waitStartedAt + _waitDelay : null);
        }
    }

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
            PowerLog.Event("Lid-delay was left overridden by a previous run — restoring it",
                           "crash recovery at startup");

        Reconcile();
        SettingsService.Reloaded += OnSettingsReloaded;
        // A sleep a session suppressed is served when that session ends, and this is the only signal
        // that says so — the session's own expiry, its being switched off, and the network it was
        // tied to going away all arrive through it.
        KeepAwakeService.StateChanged += OnKeepAwakeStateChanged;
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
        KeepAwakeService.StateChanged -= OnKeepAwakeStateChanged;
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
    /// <param name="cause">Why it changed, for the power trail. Null means the user, because every
    /// entry point but <see cref="TurnOffIfDue"/> and a refused suspend is one.</param>
    public static bool SetEnabled(bool enable, string? cause = null)
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
            PowerLog.Event($"Lid delay on, {SettingsService.Current.LidDelayMinutes} min",
                           cause ?? "the setting was turned on");
            RaiseStateChanged();
            return true;
        }

        SettingsService.Update(x => x.LidDelayEnabled = false);
        CancelDelay();
        Unsubscribe();
        PowerLog.Event(RestoreSavedAction()
            ? "Lid delay off, the Windows lid-close action is back to its own value"
            : "Lid delay off, but the Windows lid-close action could not be restored — retrying at next start",
            cause ?? "the setting was turned off");
        RaiseStateChanged();
        return true;
    }

    /// <summary>
    /// Stands the feature down now a lid close is over, when the user asked for a one-off delay and
    /// this close is one that counts. <see cref="LidDelayPolicy.ShouldTurnOffAfterLidClose"/> holds the
    /// expiry-versus-interruption rule; this only supplies the outcome and performs the write.
    /// </summary>
    /// <remarks>Never called from the lid-switch callback: <see cref="SetEnabled"/> unsubscribes, and
    /// unregistering from inside a callback deadlocks against the callback still running.</remarks>
    /// <returns>Whether it stood the feature down.</returns>
    private static bool TurnOffIfDue(LidDelayOutcome outcome)
    {
        if (!LidDelayPolicy.ShouldTurnOffAfterLidClose(SettingsService.Current.LidDelayOffAfterSleep, outcome))
            return false;

        PowerLog.Say(LidWaitTrail.SwitchedOffBeforeSleeping);
        SetEnabled(false, "the delay was set to run once and this lid close is over");
        return true;
    }

    // Never let a subscriber's failure escape: one raise site sits on the suspend task, where an
    // escaped exception terminates the process, and the window subscribers touch the UI.
    private static void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { AppLog.Error("LidDelayService.StateChanged", ex); }
    }

    /// <summary>
    /// Turns the battery-level condition on or off. Paired with its runtime effect rather than left
    /// to a plain settings write, for the same reason <see cref="SetEnabled"/> is: switching it off
    /// while a target is outstanding must drop that condition from the current wait, or the machine
    /// keeps waiting for a level nothing is watching for.
    /// </summary>
    public static void SetDischargeEnabled(bool enable)
    {
        SettingsService.Update(s => s.LidDischargeEnabled = enable);
        if (enable)
        {
            PowerLog.Event($"Lid-delay battery target on, {SettingsService.Current.LidDischargeTargetPercent} %",
                           "the setting was turned on");
            return;
        }

        bool wasWatching;
        lock (_sync)
        {
            wasWatching = _discharge.IsWatching;
            _discharge.Disarm();
            if (wasWatching) _targetSet = false;
        }
        PowerLog.Event("Lid-delay battery target off", "the setting was turned off");
        if (wasWatching) Complete();
    }

    /// <summary>
    /// Turns the time condition on or off. A plain settings write: unlike the battery target it owns
    /// no watch, and the timer of a wait already running is left to run out on its own terms.
    /// </summary>
    /// <summary>
    /// The one way the lid-close delay length is written. Every surface that offers it — the
    /// Settings page, the dashboard chip and the Home Assistant number — comes through here, so the
    /// configured wait is knowable from the power trail at any point without toggling the feature.
    /// </summary>
    /// <remarks>
    /// The trail used to name the length in two places — at switch-on and at the lid close — with
    /// nothing recorded in between, so a length changed mid-life read as the arming code arming
    /// something other than what was configured. The two are indistinguishable in a record that
    /// carries only the endpoints, which is what this closes.
    /// <para>Both the stored value and the span it arms are named, because they differ wherever the
    /// stored one falls outside <see cref="LidDelayPolicy.MinMinutes"/>…<see cref="LidDelayPolicy.MaxMinutes"/>.</para>
    /// </remarks>
    /// <param name="surface">Where the write came from, in the words a reader of the trail knows the
    /// surface by.</param>
    public static void SetDelayMinutes(int minutes, string surface)
    {
        int previous = SettingsService.Current.LidDelayMinutes;
        if (previous == minutes) return;

        SettingsService.Update(s => s.LidDelayMinutes = minutes);

        int arms = (int)LidDelayPolicy.DelayFor(minutes).TotalMinutes;
        PowerLog.Event(
            arms == minutes
                ? $"Lid-delay length {previous} min → {minutes} min"
                : $"Lid-delay length {previous} min → {minutes} min, which arms {arms} min",
            $"changed from {surface}");
    }

    public static void SetTimeEnabled(bool enable)
    {
        SettingsService.Update(s => s.LidDelayTimeEnabled = enable);
        PowerLog.Event(enable
            ? $"Lid-delay timer on, {SettingsService.Current.LidDelayMinutes} min"
            : "Lid-delay timer off",
            enable ? "the setting was turned on" : "the setting was turned off");
    }

    /// <summary>
    /// Feeds the newest battery reading to an outstanding discharge target, and keeps it for the next
    /// lid close. The stop condition is the charge level, never a "power is connected" reading:
    /// connected power may deliver less than the machine draws, so the battery can drain while
    /// plugged in, and a connectivity test would hold that machine awake indefinitely.
    /// </summary>
    /// <summary>
    /// One gated temperature reading, from the application's own fixed-cadence history tick. Ends
    /// the hold and sleeps the machine where a ceiling is armed and the reading has reached it.
    /// </summary>
    /// <remarks>
    /// A machine held awake with its lid shut and then carried in a bag has nowhere to send its
    /// heat, and the hold is what keeps it awake — so ending the hold on a temperature the
    /// application can read is the application's own responsibility rather than Windows'.
    /// <para>A missing reading stands the ceiling down rather than firing it: a value that is not
    /// there is not a hot machine.</para>
    /// </remarks>
    public static void OnThermalReading(double? celsius)
    {
        LidThermalDecision decision;
        lock (_sync)
        {
            decision = _thermal.OnReading(celsius);
            if (decision is LidThermalDecision.CeilingReached)
            {
                _thermalEnded = true;
                _trail.Arrived(LidWaitEnd.TooHot);
            }
        }

        if (decision is not LidThermalDecision.CeilingReached) return;

        double reached = celsius ?? 0;
        PowerLog.Event($"The machine reached {reached:0.#} °C with the lid shut",
                       "the lid-close temperature ceiling ended the wait early");

        // Nobody sees a notification inside a closed bag, so the notice is left for the next wake.
        SettingsService.Update(s =>
        {
            s.LidThermalSleptAtCelsius = reached;
            s.LidThermalSleptAtUtc     = DateTimeOffset.UtcNow;
        });

        Complete();
    }

    public static void OnBatteryReport(int percent, bool isCharging)
    {
        LidDischargeDecision decision;
        var charger = LidChargerResponse.Nothing;
        string? progress = null;
        lock (_sync)
        {
            _lastBattery = (percent, isCharging);
            decision = _discharge.OnReading(percent, isCharging);
            // Reaching the target is that condition arriving; a pack taking charge means it can
            // never arrive, so the condition is dropped rather than counted as met. Counting it as
            // met would sleep a plugged-in machine the moment its lid closed.
            if (decision is LidDischargeDecision.TargetReached)
            {
                _targetArrived = true;
                _trail.Arrived(LidWaitEnd.BatteryTarget);
            }
            // Withdrawn, and recorded as withdrawn: a condition that became unreachable is not one
            // that was satisfied, so it must not end the wait in sleep. Either answer below keeps
            // the machine awake — the switch chooses between standing down and waiting on.
            if (decision is LidDischargeDecision.Charging)
            {
                _targetSet     = false;
                _targetGivenUp = true;
                charger = LidDelayPolicy.OnChargerConnected(
                    SettingsService.Current.LidDelayOffWhenCharging, _delayPending);
            }

            // Hold means a target is still outstanding, which only happens inside a wait.
            if (decision is LidDischargeDecision.Hold) progress = _trail.OnBatteryReading(percent);
        }

        if (progress is not null) PowerLog.Say(progress);

        switch (decision)
        {
            case LidDischargeDecision.TargetReached:
                PowerLog.Event($"Battery reached its lid-close target at {percent} %",
                               "the battery target was met");
                break;
            case LidDischargeDecision.Charging:
                PowerLog.Event($"Lid-delay battery target given up at {percent} %",
                               "the battery is charging, so the target cannot be reached");
                break;
            default:
                return;
        }

        if (charger is LidChargerResponse.StandDown)
        {
            StandDownOnCharger(percent);
            return;
        }

        Complete();
    }

    /// <summary>
    /// Ends the wait a connected charger has taken the battery target away from, without sleeping,
    /// and switches the feature off so Windows' own lid-close action serves the next close. The
    /// notice is shown as it happens rather than held for the next wake: the machine is awake, which
    /// is the fact the notice exists to state.
    /// </summary>
    /// <remarks>Safe to call <see cref="SetEnabled"/> from here — this runs on the battery-report
    /// thread, not the lid callback whose unregistration would deadlock against itself.</remarks>
    private static void StandDownOnCharger(int percent)
    {
        string? ended;
        lock (_sync)
        {
            // The lid can have reopened between the reading and this running, which ends the wait on
            // its own terms and leaves nothing to stand down from.
            if (!_delayPending) return;
            _generation++;                    // invalidates a suspend that was already decided on
            _trail.Arrived(LidWaitEnd.ChargerConnected);
            ended = _trail.End(percent);
            ClearLocked();
        }

        PowerLog.Say(ended);
        PowerLog.Say(LidWaitTrail.SwitchedOffOnChargerConnected);
        AppChangeLog.Record(AppChange.WaitEndedOnACharger);
        SetEnabled(false, "a charger was connected while the lid was shut");
        ToastService.NotifyLidDelayStoodDown(percent);
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

    /// <summary>
    /// Lid-switch callback — arrives on an OS thread, so it must not block. Takes the byte Windows
    /// delivered rather than a reading of it: the trail used to record the conclusion, which is
    /// indistinguishable from a correct one whatever produced it.
    /// </summary>
    /// <remarks>The idle reading is taken first and before anything else, because
    /// <see cref="LockIfConfigured"/> stops the session tick advancing and the reading would then
    /// describe the lock rather than the moment the notification arrived.</remarks>
    private static void OnLidState(byte payload)
    {
        var sinceInput = NativeMethods.SinceLastInput();
        var now = DateTimeOffset.Now;
        bool closed = payload == LidEventLog.ClosedPayload;

        LidDelayAction action;
        bool first;
        bool droppedOwedSleep = false;
        LidEventObservation observation;
        lock (_sync)
        {
            first = !_lidSeeded;
            observation = new LidEventObservation(
                Payload:            payload,
                // The replay carries no previous value even where an earlier subscription left one:
                // it is the state at registration, not a transition from anything.
                Kind:               LidEventLog.KindOf(closed, first ? null : _lastLidPayload),
                SincePrevious:      first || _lastNotificationAt is null ? null : now - _lastNotificationAt,
                SinceInput:         sinceInput,
                NearOwnSchemeWrite: LidEventLog.WithinTheWindow(NativeMethods.LastSchemeActivatedAt, now),
                NearDisplayChange:  LidEventLog.DisplayChangedRecently(now),
                BatteryPercent:     _lastBattery?.Percent,
                BatteryCharging:    _lastBattery?.Charging);
            _lastLidPayload     = closed;
            _lastNotificationAt = now;
            _lidSeeded          = true;
            _lidClosed          = closed;
            // Any lid open invalidates a queued suspend, including one already decided on but not yet
            // run — by then _delayPending is false and the policy has nothing left to cancel.
            if (!closed) _generation++;
            // A sleep a session was holding back is dropped rather than kept: reopening the lid takes
            // away the one piece of evidence the deferred sleep rests on.
            if (!closed && _sleepOwed) { _sleepOwed = false; droppedOwedSleep = true; }
            action = LidDelayPolicy.OnLidState(closed ? LidState.Closed : LidState.Opened,
                                               SettingsService.Current.LidDelayEnabled, _delayPending, first,
                                               _handedBack);
        }

        // Logged whatever the policy decided, replay included: a lid event the feature ignored is
        // what someone asking "why didn't it sleep" needs to see. The observation rides on this one
        // entry rather than lines of its own, so an ordinary day costs nothing.
        PowerLog.Event(LidEventLog.What(observation), LidEventLog.Cause(observation));

        // The only two lines this adds, and both are silent forever on a machine whose lid behaves.
        if (LidEventLog.RepeatLine(observation) is { } repeated) PowerLog.Say(repeated);
        if (LidEventLog.SchemeWriteLine(observation) is { } echoed) PowerLog.Say(echoed);

        LidEventLog.Record(observation, now);

        // Raised before the wait is acted on: a subscriber does its own work on a thread of its own,
        // and a lid close is followed by a suspend this must not be queued behind.
        LidNotification?.Invoke(observation.Kind);

        if (droppedOwedSleep) PowerLog.Say(LidWaitTrail.OwedSleepDroppedOnLidOpen);

        switch (action)
        {
            case LidDelayAction.StartDelay:
                // Locked first: with neither condition set the wait ends inside StartDelay, and a
                // lock queued behind that suspend would land on a machine already asleep.
                LockIfConfigured();
                StartDelay();
                break;
            case LidDelayAction.Cancel:
                // The lid reopening is the one moment a whole wait can be measured end to end, and
                // the reading is what separates a wait that was held from one that was slept through.
                PowerLog.Event("Lid delay cancelled, the machine stays awake",
                               SleepGap.AddTo("lid reopened", CancelDelay()));
                break;

            case LidDelayAction.HandBackUntilTheLidOpens:
                lock (_sync) _handedBack = true;
                PowerLog.Event(RestoreSavedAction()
                    ? "No lid-close wait — the Windows lid-close action is back until the lid next opens"
                    : "No lid-close wait, and the Windows lid-close action could not be put back",
                    "the application started with the lid already shut, so there is no wait to resume");
                break;

            case LidDelayAction.TakeTheOverrideBack:
                lock (_sync) _handedBack = false;
                PowerLog.Event(CaptureAndOverride()
                    ? "Lid-close waits are being served again"
                    : "Lid-close waits could not be taken back — Windows still owns the lid-close action",
                    "the lid opened after a start with it already shut");
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
        bool armedTimer;
        LidTargetArm targetArm;
        int? thermalCeiling;
        int targetPercent = LidDischargeWatch.Clamp(s.LidDischargeTargetPercent);
        int? levelAtClose;
        lock (_sync)
        {
            // Re-checked under the lock: OnLidState decided and then released it, so two concurrent
            // close notifications can both have been told to start, and the second would restart
            // the countdown.
            if (_delayPending) return;
            _holder.EnsureStarted();
            _delayPending  = true;
            _timeArrived   = false;
            _targetArrived = false;
            _targetGivenUp = false;
            _timeSet       = s.LidDelayTimeEnabled;
            _waitStartedAt = DateTimeOffset.Now;
            _waitDelay     = delay;
            _waitClock     = AwakeClock.Mark();

            // Set only where a battery reading exists, and judged against it at once: a machine
            // already at its target must not wait for a level that has no reason to move, and a
            // machine with no battery at all could never release a watch once it held, which under
            // first-to-arrive would leave the clock as the only condition anyway.
            _targetSet   = s.LidDischargeEnabled && _lastBattery is not null;
            levelAtClose = _lastBattery?.Percent;

            _trail.Start(_timeSet, (int)delay.TotalMinutes,
                         _targetSet ? targetPercent : null,
                         _lastBattery?.Percent);

            LidDischargeDecision? decision = null;
            if (_targetSet && _lastBattery is { } reading)
            {
                _discharge.Arm(s.LidDischargeTargetPercent);
                decision = _discharge.OnReading(reading.Percent, reading.Charging);
                switch (decision)
                {
                    case LidDischargeDecision.Hold:          break;
                    case LidDischargeDecision.TargetReached: _targetArrived = true;
                                                             _trail.Arrived(LidWaitEnd.BatteryTarget); break;
                    // Already charging as the lid closes: the target can never arrive, so it is no
                    // condition at all and the clock, if set, carries the wait on its own.
                    default:                                 _targetSet     = false; break;
                }
            }

            targetArm = LidTargetArming.Decide(s.LidDischargeEnabled, _lastBattery is not null, decision);

            // The temperature safeguard, armed with the hold. Only where the machine offers a
            // reading the gate has already vouched for: a ceiling watching a value that never
            // arrives is a safeguard that cannot act, and one watching a stuck value would sleep a
            // working machine the moment it armed.
            _thermalEnded = false;
            thermalCeiling = s.LidThermalCeilingEnabled && ThermalStatusService.PublishableCelsius is not null
                           ? LidThermalWatch.Clamp(s.LidThermalCeilingCelsius)
                           : null;
            if (thermalCeiling is { } ceiling) _thermal.Arm(ceiling);

            _holder.Post(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
            _timer?.Dispose();
            _timer = null;
            armedTimer = _timeSet;
            if (armedTimer)
                _timer = new System.Threading.Timer(_ => OnTimerFired(), null, delay, Timeout.InfiniteTimeSpan);

            // The progress report runs only for the condition it reports on: with no delay set there
            // is no elapsed fraction to report, and the battery reports off its own readings.
            _heartbeat?.Dispose();
            _heartbeat = armedTimer
                ? new System.Threading.Timer(_ => OnHeartbeat(), null,
                                             LidWaitTrail.TimeReportInterval, LidWaitTrail.TimeReportInterval)
                : null;
        }

        // Both directions, like the battery target below: a wait recorded only when its timer armed
        // reads as a wait that had one, and the difference decides what the machine was waiting for.
        if (armedTimer)
            PowerLog.Event($"Lid-delay timer armed — suspending in {delay.TotalMinutes:0} min unless the lid reopens",
                           "lid closed with the lid-delay timer on");
        else
            PowerLog.Event("No delay timer on this lid close", "the timer condition is off");

        // Recorded in every direction, including the ones where nothing was armed: a target that was
        // configured and never armed is otherwise indistinguishable from one quietly holding.
        var (what, why) = LidTargetArming.Describe(targetArm, targetPercent, levelAtClose);
        PowerLog.Event(what, why);

        if (thermalCeiling is { } armedCeiling)
            PowerLog.Event($"Sleep also comes if the machine reaches {armedCeiling} °C",
                           "lid closed with the temperature ceiling on");
        else if (SettingsService.Current.LidThermalCeilingEnabled)
            PowerLog.Event("No temperature ceiling on this lid close",
                           "this machine offers no reading that has been shown to be trustworthy");
        else
            PowerLog.Event("No temperature ceiling on this lid close", "the setting is off");

        AppChangeLog.Record(AppChange.LidClosed);

        // Whichever condition already stands satisfied ends the wait here, including the case where
        // neither was set at all.
        Complete();
    }

    private static void OnTimerFired()
    {
        lock (_sync)
        {
            _timeArrived = true;
            _trail.Arrived(LidWaitEnd.DelayElapsed);
        }
        Complete();
    }

    /// <summary>
    /// Says how far the delay has got. Elapsed time is measured from the moment the wait started
    /// rather than counted in ticks, so a coalesced or delayed timer reports the time that actually
    /// passed instead of the time it was supposed to fire at.
    /// </summary>
    private static void OnHeartbeat()
    {
        string? progress;
        SleepGap? gap;
        lock (_sync)
        {
            if (!_delayPending) return;
            progress = _trail.OnElapsed(DateTimeOffset.Now - _waitStartedAt);
            gap      = AwakeClock.Since(_waitClock);
        }

        // The reading rides on the report that was due anyway: a heartbeat saying "fifteen minutes
        // into the delay" is exactly the line that has to stop being read as fifteen minutes held.
        if (progress is not null) PowerLog.Say(SleepGap.AddSentenceTo(progress, gap));
    }

    /// <summary>
    /// Decides what the wait's current state means: suspend, keep waiting, or release the hold
    /// without sleeping. Reached from arming, from the timer, and from whatever settles the battery
    /// target — a reading or the condition being switched off.
    /// </summary>
    private static void Complete()
    {
        LidDelayAction action;
        long gen;
        string? ended = null;
        LidWaitEnd endedBy = LidWaitEnd.NothingToWaitFor;
        SleepGap? gap = null;
        lock (_sync)
        {
            bool over = LidDelayPolicy.WaitIsOver(_timeSet, _timeArrived, _targetSet, _targetArrived,
                                                  _thermalEnded, _targetGivenUp);
            action = LidDelayPolicy.OnWaitProgress(SettingsService.Current.LidDelayEnabled, _delayPending,
                                                   KeepAwakeService.Current is not null, over);
            if (action is LidDelayAction.Suspend or LidDelayAction.Cancel
                       or LidDelayAction.SuspendWhenTheSessionEnds)
            {
                // A suppressed sleep ends the wait here like any other: the condition arrived, so the
                // timer, the watches and the hold have nothing left to do, and leaving them running
                // would keep reporting progress through a wait that is over. What outlives the wait
                // is the one flag saying the sleep it earned has still to be served.
                if (action is LidDelayAction.SuspendWhenTheSessionEnds) _sleepOwed = true;
                // Composed before ClearLocked wipes what it describes.
                ended   = _trail.End(_lastBattery?.Percent);
                endedBy = _trail.EndedBy;
                gap     = AwakeClock.Since(_waitClock);
                ClearLocked();
            }
            gen = _generation;
        }

        // First, and outside the lock: this is the line whose absence is meant to prove the
        // application was not the one that acted, so nothing that can block may precede it. The
        // awake reading travels on it rather than on a line of its own — the closing line is where
        // "held for two hours" and "slept for most of it" have to stop looking alike.
        if (ended is not null)
        {
            PowerLog.Say(SleepGap.AddSentenceTo(ended, gap));
            AppChangeLog.Record(AppChangeLog.From(endedBy));
        }

        switch (action)
        {
            case LidDelayAction.Suspend:
                PowerLog.Event("Suspending the machine",
                               "a lid-close condition was reached with the lid still closed");
                SuspendOffThisThread(gen);
                break;

            case LidDelayAction.SuspendWhenTheSessionEnds:
                PowerLog.Event("A lid-close condition was reached but the machine was not suspended",
                               "a keep-awake session is holding it awake");
                // The stand-down is not taken here: this lid close has not slept yet, and only a
                // close that reaches sleep expires a one-off delay. It is taken when the sleep the
                // session is holding back is finally served.
                PowerLog.Say(LidWaitTrail.SleepOwedUntilTheSessionEnds);
                break;

            case LidDelayAction.Cancel:
                PowerLog.Event("A lid-close condition was reached but the machine was not suspended",
                               "lid handling was switched off while the lid was shut");
                TurnOffIfDue(LidDelayOutcome.StoppedShort);
                break;
        }
    }

    /// <summary>
    /// Serves the sleep a keep-awake session was holding back, now that session has ended. Reached
    /// from <see cref="KeepAwakeService.StateChanged"/>, which is raised for a session starting as
    /// well as ending — <see cref="LidDelayPolicy.ShouldCompleteSuppressedWait"/> is what tells the
    /// two apart, along with everything else that has to still hold at this moment.
    /// </summary>
    private static void OnKeepAwakeStateChanged()
    {
        long gen;
        lock (_sync)
        {
            if (!LidDelayPolicy.ShouldCompleteSuppressedWait(_sleepOwed,
                    SettingsService.Current.LidDelayEnabled, _lidClosed,
                    KeepAwakeService.Current is not null))
                return;

            _sleepOwed = false;
            gen = _generation;
        }

        PowerLog.Say(LidWaitTrail.SleepServedWhenTheSessionEnded);
        PowerLog.Event("Suspending the machine",
                       "the keep-awake session that was holding back a lid-close sleep has ended, " +
                       "and the lid is still shut");
        SuspendOffThisThread(gen);
    }

    /// <summary>
    /// Puts the machine to sleep for a wait that has ended, on a thread of its own:
    /// <c>SetSuspendState</c> does not return until the machine resumes, and every caller here is a
    /// timer callback or an event raise that must not block.
    /// </summary>
    /// <param name="generation">The value <see cref="_generation"/> had when the suspend was decided
    /// on. Anything that invalidates a queued suspend — the lid opening above all — moves it, and a
    /// moved value abandons this one.</param>
    private static void SuspendOffThisThread(long generation)
    {
        Task.Run(() =>
        {
            // The lid can be opened between the decision and this running, by which point
            // _delayPending is false and nothing else would stop the suspend.
            bool abandoned;
            lock (_sync) abandoned = _generation != generation;
            if (abandoned)
            {
                PowerLog.Event("Suspend abandoned", "the lid was opened before it ran");
                TurnOffIfDue(LidDelayOutcome.LidReopened);
                return;
            }

            // Before the suspend, never after it: SetSuspendState does not return until the machine
            // resumes, so a stand-down placed after it is never written on a machine that never
            // resumes with the application running, and the one-off delay would still be armed for
            // the next lid close. Putting the user's own lid action back with the lid already shut
            // cannot re-trigger it — Windows acts on the transition, not on the state.
            bool stoodDown = TurnOffIfDue(LidDelayOutcome.Slept);

            if (!NativeMethods.Suspend())
            {
                PowerLog.Event("Suspend was refused by Windows", "SetSuspendState returned false");
                AppLog.Error("LidDelayService.Suspend failed", null);
                // The stand-down was taken for a sleep that did not happen, and only a lid close that
                // reached sleep expires the setting.
                if (stoodDown)
                    SetEnabled(true, "the suspend the one-off delay stood down for was refused");
            }
        });
    }

    /// <summary>Ends the wait without sleeping. Returns how much of the cancelled wait the machine
    /// was awake for, or null where there was no wait to cancel or the platform gave no reading.
    /// </summary>
    private static SleepGap? CancelDelay()
    {
        SleepGap? gap;
        lock (_sync)
        {
            _generation++;   // invalidates a suspend that was already decided on
            _discharge.Disarm();
            _thermal.Disarm();
            // Dropped with the wait, and before the early return: this is also the path a feature
            // switched off takes, and a feature that is off has no sleep left to serve.
            _sleepOwed = false;
            if (!_delayPending) return null;
            // Read before ClearLocked, which is what ends the wait being measured.
            gap = AwakeClock.Since(_waitClock);
            ClearLocked();
        }

        // Outside the lock, and only where a wait was actually cancelled: a lid opening with
        // nothing running changes nothing.
        AppChangeLog.Record(AppChange.LidOpened);
        return gap;
    }

    // Callers hold _sync.
    private static void ClearLocked()
    {
        _delayPending  = false;
        _timeSet       = false;
        _timeArrived   = false;
        _targetSet     = false;
        _targetArrived = false;
        _targetGivenUp = false;
        _waitDelay     = TimeSpan.Zero;
        _trail.Clear();
        _discharge.Disarm();
        _thermal.Disarm();
        _thermalEnded = false;
        _timer?.Dispose();
        _timer = null;
        _heartbeat?.Dispose();
        _heartbeat = null;
        // Clearing must happen on the thread that made the request — post it, don't call it here.
        if (_holder.IsStarted) _holder.Post(NativeMethods.ES_CONTINUOUS);
    }
}
