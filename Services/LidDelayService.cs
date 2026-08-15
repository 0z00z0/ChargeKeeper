using System.Collections.Concurrent;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Waits N minutes after the lid closes before letting the machine sleep (issue #90). The rules live
/// in the pure <see cref="LidDelayPolicy"/>; this owns the power-scheme override, the lid
/// subscription, the OS hold and the suspend.
/// <para>
/// WHY THIS TOUCHES A WINDOWS SETTING AT ALL: <c>SetThreadExecutionState</c> — the whole mechanism
/// behind <see cref="KeepAwakeService"/> — cannot help here. Lid close is a power-policy ACTION, not
/// an idle timeout, and the API is documented as unable to "prevent the user from putting the
/// computer to sleep". The only way to delay it is to override the user's own lid-close action to
/// "do nothing" for as long as the feature is on, hold the machine awake for the delay, and then
/// suspend explicitly. Nothing lighter works.
/// </para>
/// <para>
/// CRASH SAFETY IS THE POINT. An app that dies with the override in place leaves a laptop that
/// silently stops sleeping on lid close — a battery-draining regression the user never asked for,
/// and one they cannot even undo through Settings, because Windows HIDES the lid-close action on
/// Modern Standby machines. So: the user's own values are persisted BEFORE anything is written, they
/// are never re-captured while stored (that would save our own override as if it were theirs),
/// <see cref="Start"/> puts them back whenever it finds them stored with the feature off, and a
/// restore clears the record only once the write has actually succeeded.
/// </para>
/// <para>
/// Lid actions are PER-SCHEME, so the scheme they were captured from is stored with them and every
/// write targets it explicitly. Writing "whichever plan is active now" would, after a power-plan
/// switch, clobber the second plan's setting while leaving the first parked on the override.
/// </para>
/// <para>
/// The OS hold uses its own holder thread rather than <see cref="KeepAwakeService"/>: that service
/// has a single current session, so borrowing it would silently cancel a keep-awake the user started
/// by hand. The per-thread rule for <c>SetThreadExecutionState</c> is the same one documented there.
/// </para>
/// </summary>
internal static class LidDelayService
{
    // Guards _delayPending + _timer + _lidSeeded + _generation. Nothing here raises events, so there
    // is no "invoke outside the lock" dance to match KeepAwakeService's.
    private static readonly System.Threading.Lock _sync = new();

    // Separate from _sync because Subscribe holds it across RegisterLidNotification, during which
    // Windows delivers the seeding callback — and that callback takes _sync. Splitting the two keeps
    // the registering thread (often the UI thread) from holding a lock the callback needs.
    private static readonly System.Threading.Lock _subscribeSync = new();

    private static System.Threading.Timer? _timer;
    private static bool   _delayPending;
    private static bool   _lidSeeded;      // has the registration replay been consumed?
    private static long   _generation;     // bumped by anything that invalidates a queued suspend
    private static IntPtr _lidRegistration = IntPtr.Zero;

    // The override this process actually applied, and the values it displaced. AUTHORITATIVE over
    // settings.json while it is set — see OnSettingsReloaded.
    private static (Guid Scheme, uint Ac, uint Dc)? _appliedOverride;

    private static readonly BlockingCollection<uint> _holdRequests = new();
    private static Thread? _holder;

    private static bool _started;

    /// <summary>Whether a lid-close delay is currently counting down.</summary>
    public static bool IsDelayPending { get { lock (_sync) return _delayPending; } }

    /// <summary>
    /// Reconciles the power scheme with the stored settings and, when the feature is on, subscribes to
    /// the lid switch. Called once at startup next to <see cref="KeepAwakeService.Start"/>.
    /// <para>This is the crash-recovery entry point: it runs the <see cref="LidDelayPolicy.DecideStartup"/>
    /// table BEFORE anything else, so a lid action left overridden by a dead process is put back on the
    /// next launch even if the user never opens Settings again.</para>
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
            AppLog.Info("LidDelay: the lid-close action was left overridden by a previous run — restoring it.");

        Reconcile();
        SettingsService.Reloaded += OnSettingsReloaded;
    }

    /// <summary>
    /// Releases everything this service owns and puts the user's lid-close action back. Called from
    /// the app's clean shutdown and from logoff/restart; a crash is covered by <see cref="Start"/>.
    /// </summary>
    public static void Stop()
    {
        CancelDelay();
        Unsubscribe();
        if (SettingsService.Current.HasSavedLidAction) RestoreSavedAction();
    }

    /// <summary>
    /// Turns the feature on or off, overriding or restoring the Windows lid-close action to match.
    /// <para>Returns false ONLY from the enable path, meaning the power scheme could not be written and
    /// the setting was left off rather than claiming a delay the machine will not honour. The disable
    /// path always returns true: the setting is off either way, and a failed restore stays owed to the
    /// next <see cref="Start"/> rather than being the caller's problem.</para>
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
            AppLog.Info($"LidDelay: on, {SettingsService.Current.LidDelayMinutes} min after the lid closes.");
            return true;
        }

        SettingsService.Update(x => x.LidDelayEnabled = false);
        CancelDelay();
        Unsubscribe();
        AppLog.Info(RestoreSavedAction()
            ? "LidDelay: off, the Windows lid-close action is back to its own value."
            : "LidDelay: off, but the Windows lid-close action could not be restored — retrying at next start.");
        return true;
    }

    /// <summary>
    /// Brings the power scheme and the lid subscription in line with the stored settings. Shared by
    /// <see cref="Start"/> and the settings-reload path so "what state should we be in" is decided in
    /// exactly one place.
    /// </summary>
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
    /// Re-reconciles after "Reload settings from file" swaps <see cref="SettingsService.Current"/>.
    /// <para>The in-memory record wins here. Reload replaces the whole settings object with whatever is
    /// on disk, and settings.json roams (roaming AppData / OneDrive), so the file can legitimately
    /// arrive from another machine carrying NO saved lid action while THIS machine's scheme is still
    /// parked on "do nothing". Believing the file there would lose the user's original for good: the
    /// next capture would read our own override and persist it as if it were theirs.</para>
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

    // ── Power-scheme override ─────────────────────────────────────────────────────

    /// <summary>
    /// The scheme the saved values belong to: the stored one, or the active one for a settings file
    /// written before the scheme was tracked. Null when no scheme can be resolved at all.
    /// </summary>
    private static Guid? SchemeForSavedValues() =>
        Guid.TryParse(SettingsService.Current.LidDelaySavedScheme, out var stored)
            ? stored
            : NativeMethods.ReadActiveLidCloseAction()?.Scheme;

    /// <summary>
    /// Reads the user's own lid-close actions, PERSISTS them with their scheme, and only then
    /// overrides them. The order is the whole crash-safety story: the values reach disk before the
    /// setting they describe is changed, so there is no window in which the override exists and the
    /// original does not.
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
    /// Parks both lid-close actions on "do nothing" WITHOUT touching the saved originals — the path
    /// taken whenever values are already stored. Re-capturing there would persist this very override
    /// as the user's own setting.
    /// <para>Both AC and DC are set because Windows re-evaluates the policy for the current power
    /// source: leaving DC alone would let the machine sleep on lid close the moment it is unplugged.</para>
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
    /// Writes the user's own lid-close actions back into the scheme they came from, and forgets them.
    /// The saved values are cleared ONLY after a successful write — a failed restore must stay owed,
    /// so the next <see cref="Start"/> tries again rather than losing the setting for good.
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

        // A half-written pair is still worth restoring: fall back to "sleep", the Windows default for
        // a lid close, rather than leaving that side parked on our override.
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

    // ── Lid subscription ──────────────────────────────────────────────────────────

    private static void Subscribe()
    {
        lock (_subscribeSync)
        {
            if (_lidRegistration != IntPtr.Zero) return;
            lock (_sync) { _lidSeeded = false; }   // the next callback is the registration replay

            // Registered WITHOUT _sync held: Windows delivers the seeding callback during this call,
            // and that callback takes _sync. Resetting _lidSeeded above is all the ordering the seed
            // actually needs.
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
        // Outside the lock: this is the call that would genuinely deadlock against an in-flight callback.
        NativeMethods.UnregisterLidNotification(registration);
    }

    /// <summary>Lid-switch callback — arrives on an OS thread, so it must not block.</summary>
    private static void OnLidState(bool closed)
    {
        LidDelayAction action;
        lock (_sync)
        {
            bool first = !_lidSeeded;
            _lidSeeded = true;
            // Any lid OPEN invalidates a queued suspend, including one already decided on but not yet
            // run — at which point _delayPending is false and the policy has nothing left to cancel.
            if (!closed) _generation++;
            action = LidDelayPolicy.OnLidState(closed ? LidState.Closed : LidState.Opened,
                                               SettingsService.Current.LidDelayEnabled, _delayPending, first);
        }

        switch (action)
        {
            case LidDelayAction.StartDelay: StartDelay(); break;
            case LidDelayAction.Cancel:
                CancelDelay();
                AppLog.Info("LidDelay: lid reopened — the machine stays awake.");
                break;
        }
    }

    // ── Delay window ──────────────────────────────────────────────────────────────

    private static void StartDelay()
    {
        var delay = LidDelayPolicy.DelayFor(SettingsService.Current.LidDelayMinutes);
        lock (_sync)
        {
            // Re-checked under the lock: OnLidState decided and then released it, so two concurrent
            // close notifications can both have been told to start. Without this the second restarts
            // the countdown — exactly what the policy's duplicate-close rule exists to prevent.
            if (_delayPending) return;
            EnsureHolder();
            _delayPending = true;
            _holdRequests.Add(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => OnTimerFired(), null, delay, Timeout.InfiniteTimeSpan);
        }
        AppLog.Info($"LidDelay: lid closed — sleeping in {delay.TotalMinutes:0} min unless it is reopened.");
    }

    private static void OnTimerFired()
    {
        LidDelayAction action;
        long gen;
        lock (_sync)
        {
            action = LidDelayPolicy.OnTimerFired(SettingsService.Current.LidDelayEnabled, _delayPending,
                                                 KeepAwakeService.Current is not null);
            if (action != LidDelayAction.None) ClearLocked();
            gen = _generation;
        }

        switch (action)
        {
            case LidDelayAction.Suspend:
                AppLog.Info("LidDelay: delay elapsed with the lid still closed — suspending.");
                // Off this timer thread: SetSuspendState does not return until the machine resumes.
                Task.Run(() =>
                {
                    // The lid can be opened between the decision above and this running, by which
                    // point _delayPending is already false and nothing else would stop the suspend.
                    lock (_sync)
                    {
                        if (_generation != gen)
                        {
                            AppLog.Info("LidDelay: lid opened before the suspend ran — not sleeping.");
                            return;
                        }
                    }
                    if (!NativeMethods.Suspend()) AppLog.Error("LidDelayService.Suspend failed", null);
                });
                break;
            case LidDelayAction.Cancel:
                AppLog.Info("LidDelay: delay elapsed but something else is holding the machine awake — not sleeping.");
                break;
        }
    }

    private static void CancelDelay()
    {
        lock (_sync)
        {
            _generation++;   // invalidates a suspend that was already decided on
            if (!_delayPending) return;
            ClearLocked();
        }
    }

    // Callers hold _sync.
    private static void ClearLocked()
    {
        _delayPending = false;
        _timer?.Dispose();
        _timer = null;
        // Clearing must happen on the thread that made the request — post it, don't call it here.
        if (_holder is not null) _holdRequests.Add(NativeMethods.ES_CONTINUOUS);
    }

    // ── OS hold ───────────────────────────────────────────────────────────────────

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
            try { NativeMethods.SetThreadExecutionState(flags); }
            catch (Exception ex) { AppLog.Error("LidDelayService.SetThreadExecutionState", ex); }
        }
    }
}
