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
/// are never re-captured while stored (that would save our own override as if it were theirs), and
/// <see cref="Start"/> puts them back whenever it finds them stored with the feature off. Restoring
/// also clears them only once the write has actually succeeded.
/// </para>
/// <para>
/// The OS hold uses its own holder thread rather than <see cref="KeepAwakeService"/>: that service
/// has a single current session, so borrowing it would silently cancel a keep-awake the user started
/// by hand. The per-thread rule for <c>SetThreadExecutionState</c> is the same one documented there.
/// </para>
/// </summary>
internal static class LidDelayService
{
    // Guards _delayPending + _timer + _lidSeeded + the registration handle. Nothing here raises
    // events, so there is no "invoke outside the lock" dance to match KeepAwakeService's.
    private static readonly System.Threading.Lock _sync = new();

    private static System.Threading.Timer? _timer;
    private static bool   _delayPending;
    private static bool   _lidSeeded;      // has the registration replay been consumed?
    private static IntPtr _lidRegistration = IntPtr.Zero;

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
        switch (LidDelayPolicy.DecideStartup(s.LidDelayEnabled, s.HasSavedLidAction))
        {
            case LidActionOverride.CaptureAndOverride: CaptureAndOverride(); break;
            case LidActionOverride.ReapplyOverride:    ApplyOverrideOnly();  break;
            case LidActionOverride.Restore:
                AppLog.Info("LidDelay: the lid-close action was left overridden by a previous run — restoring it.");
                RestoreSavedAction();
                break;
        }

        if (SettingsService.Current.LidDelayEnabled) Subscribe();
    }

    /// <summary>
    /// Releases everything this service owns and puts the user's lid-close action back. Called from
    /// the app's clean shutdown; a crash is covered by <see cref="Start"/> instead.
    /// </summary>
    public static void Stop()
    {
        CancelDelay();
        Unsubscribe();
        if (SettingsService.Current.HasSavedLidAction) RestoreSavedAction();
    }

    /// <summary>
    /// Turns the feature on or off, overriding or restoring the Windows lid-close action to match.
    /// Returns false when the power scheme could not be written, in which case the setting is left OFF
    /// rather than claiming a delay the machine will not honour.
    /// </summary>
    public static bool SetEnabled(bool enable)
    {
        if (enable)
        {
            var s = SettingsService.Current;
            bool ok = s.HasSavedLidAction ? ApplyOverrideOnly() : CaptureAndOverride();
            if (!ok)
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
        bool restored = RestoreSavedAction();
        AppLog.Info(restored
            ? "LidDelay: off, the Windows lid-close action is back to its own value."
            : "LidDelay: off, but the Windows lid-close action could not be restored — retrying at next start.");
        return restored;
    }

    // ── Power-scheme override ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads the user's own lid-close actions, PERSISTS them, and only then overrides them. The order
    /// is the whole crash-safety story: the values reach disk before the setting they describe is
    /// changed, so there is no window in which the override exists and the original does not.
    /// </summary>
    private static bool CaptureAndOverride()
    {
        if (NativeMethods.ReadLidCloseAction() is not { } original)
        {
            AppLog.Info("LidDelay: no lid-close action in the active power scheme (no lid?) — nothing to override.");
            return false;
        }

        SettingsService.Update(s =>
        {
            s.LidDelaySavedAcAction = (int)original.Ac;
            s.LidDelaySavedDcAction = (int)original.Dc;
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
        bool ok = NativeMethods.WriteLidCloseAction(
            NativeMethods.LIDACTION_DO_NOTHING, NativeMethods.LIDACTION_DO_NOTHING);
        if (!ok) AppLog.Error("LidDelayService.ApplyOverrideOnly: the power scheme write failed", null);
        return ok;
    }

    /// <summary>
    /// Writes the user's own lid-close actions back and forgets them. The saved values are cleared
    /// ONLY after a successful write — a failed restore must stay owed, so the next
    /// <see cref="Start"/> tries again rather than losing the setting for good.
    /// </summary>
    private static bool RestoreSavedAction()
    {
        var s = SettingsService.Current;
        if (!s.HasSavedLidAction) return true;

        // A half-written pair is still worth restoring: fall back to "sleep", the Windows default for
        // a lid close, rather than leaving that side parked on our override.
        uint ac = (uint)(s.LidDelaySavedAcAction ?? 1);
        uint dc = (uint)(s.LidDelaySavedDcAction ?? 1);

        if (!NativeMethods.WriteLidCloseAction(ac, dc))
        {
            AppLog.Error("LidDelayService.RestoreSavedAction: could not put the lid-close action back", null);
            return false;
        }

        SettingsService.Update(x =>
        {
            x.LidDelaySavedAcAction = null;
            x.LidDelaySavedDcAction = null;
        });
        return true;
    }

    // ── Lid subscription ──────────────────────────────────────────────────────────

    private static void Subscribe()
    {
        IntPtr registration;
        lock (_sync)
        {
            if (_lidRegistration != IntPtr.Zero) return;
            _lidSeeded = false;   // the next callback is the registration replay
            // Registering under the lock on purpose: Windows fires the seeding callback immediately,
            // and OnLidState must not observe _lidSeeded before it has been reset.
            registration = _lidRegistration = NativeMethods.RegisterLidNotification(OnLidState);
        }
        if (registration == IntPtr.Zero)
            AppLog.Error("LidDelayService.Subscribe: could not subscribe to the lid switch", null);
    }

    private static void Unsubscribe()
    {
        IntPtr registration;
        lock (_sync)
        {
            registration     = _lidRegistration;
            _lidRegistration = IntPtr.Zero;
        }
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
        lock (_sync)
        {
            action = LidDelayPolicy.OnTimerFired(SettingsService.Current.LidDelayEnabled, _delayPending,
                                                 KeepAwakeService.Current is not null);
            if (action != LidDelayAction.None) ClearLocked();
        }

        switch (action)
        {
            case LidDelayAction.Suspend:
                AppLog.Info("LidDelay: delay elapsed with the lid still closed — suspending.");
                // Off this timer thread: SetSuspendState does not return until the machine resumes.
                Task.Run(() => { if (!NativeMethods.Suspend()) AppLog.Error("LidDelayService.Suspend failed", null); });
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
