using ChargeKeeper.Helpers;
using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>Manages the "charge to 100 % once" travel override: parks the Smart Charge thresholds,
/// disables the cap so the battery reaches 100 %, then puts the parked pair back as soon as the
/// charge it was asked for is over — at full, or when the charger is removed, whichever comes first.
/// The whole record is persisted, so a restart or a crash resumes the same single charge rather than
/// leaving the cap off for every later one.</summary>
internal static class TravelOverrideService
{
    public static bool IsActive => SettingsService.Current.TravelOverrideActive;

    /// <summary>A charge to be full by a chosen time is set, in any of its phases.</summary>
    public static bool HasSchedule => SettingsService.Current.ChargeByTimeTarget is not null;

    public static ChargeByTimeRecord? Schedule => ChargeByTimeRecord.From(SettingsService.Current);

    /// <summary>False when Smart Charge was already off, so <see cref="Cancel"/> writes nothing.</summary>
    public static bool HasSavedRevertThresholds =>
        SettingsService.Current is { TravelOverrideRevertStart: not null, TravelOverrideRevertStop: not null };

    /// <summary>Whether the machine has charged under this lift. On disk rather than in memory: a
    /// lift armed on battery must still be waiting for its charger after a restart, and one whose
    /// charge had begun must still end when the charger turns out to be gone.</summary>
    public static bool ChargeStarted => SettingsService.Current.TravelOverrideChargeStarted;

    /// <summary>The thresholds to show and publish while the lift is in force — the parked pair,
    /// which is the setting the user actually chose and the one that comes back. Null when Smart
    /// Charge was already off at activation, so nothing is owed back and nothing is claimed.</summary>
    public static (int Start, int Stop)? ParkedThresholds =>
        SettingsService.Current is { TravelOverrideActive: true,
                                     TravelOverrideRevertStart: { } start,
                                     TravelOverrideRevertStop:  { } stop }
            ? (start, stop)
            : null;

    /// <summary>Raised on a background thread once an activation or revert has settled. The tray tooltip
    /// is not driven by a battery event, so it refreshes from here.</summary>
    public static event Action? StateChanged;

    public static string ActionLabel =>
        Schedule is { } timed ? $"✕  Cancel full by {ChargeByTimePolicy.Clock(timed.Target)}"
        : IsActive            ? "✕  Revert to charge threshold"
                              : "🔝  Charge to 100 % once";

    internal static void NotifyChanged() => StateChanged?.Invoke();

    // Follows each lift's charge to learn the taper from 80 % to full. Battery thread only.
    private static readonly ChargeTaperRecorder _taper = new();

    // Fire-once latch (0 = armed, 1 = revert dispatched). ApplyRevert clears TravelOverrideActive
    // asynchronously, so IsActive lags a few battery ticks behind the dispatch and would otherwise
    // let a second one through. Interlocked: OnBatteryReport runs on the MTA battery thread.
    private static int _revertDispatched;

    // Previous status, for the Charging→Idle edge. A race here at worst misses one edge, and the
    // pct≥100 fallback still reverts.
    private static BatteryStatus _lastStatus = BatteryStatus.NotPresent;

    /// <summary>Saves the current thresholds, then disables Smart Charge so the battery reaches 100 %.
    /// Changes nothing while a lift is already in force: reading the device then would park the lifted
    /// state and lose the pair owed back.</summary>
    public static void Activate(ActionCause cause) => Lift(cause, timed: null);

    /// <summary>The lift itself. <paramref name="timed"/> stamps a timed charge in the same write as
    /// the parked pair; without it a timed charge is dropped, because charging now supersedes it.</summary>
    internal static void Lift(ActionCause cause, Action<AppSettings>? timed)
    {
        Task.Run(() =>
        {
            var state = ChargeThresholdService.Read();

            // Update() so a Reload() during this Task's async gap cannot orphan the mutation.
            bool started = false, converted = false;
            SettingsService.Update(s =>
            {
                if (s.TravelOverrideActive)
                {
                    converted = timed is null && s.ChargeByTimeTarget is not null;
                    if (converted) ClearSchedule(s);
                    return;
                }
                started = true;

                // IsLimiting, not Start > 0: HP and Surface report Start as 0 by contract, and
                // testing it would leave nothing to restore on those machines.
                if (state is { IsLimiting: true })
                {
                    s.TravelOverrideRevertStart = state.Start;
                    s.TravelOverrideRevertStop  = state.Stop;
                }
                else
                {
                    s.TravelOverrideRevertStart = null;
                    s.TravelOverrideRevertStop  = null;
                }

                s.TravelOverrideActive        = true;
                s.TravelOverrideChargeStarted = false;   // armed; the charger may not be in yet
                if (timed is null) ClearSchedule(s); else timed(s);
            });

            if (converted)
            {
                AppLog.Info("TravelOverride: the timed charge becomes a charge to 100 % now" + cause.Clause);
                StateChanged?.Invoke();
                return;
            }
            if (!started)
            {
                AppLog.Info("TravelOverride: the cap is already lifted, so the request changes nothing" + cause.Clause);
                return;
            }

            // The latch belongs to this lift alone.
            Interlocked.Exchange(ref _revertDispatched, 0);

            // A rejected write leaves the override armed over an unchanged device — log it, or it
            // looks identical to a success.
            AppLog.Info((ChargeThresholdService.SetEnabled(false)
                ? "TravelOverride: the cap is lifted for one charge"
                : "TravelOverride: lifting the cap was rejected by the device — thresholds unchanged")
                + cause.Clause);

            StateChanged?.Invoke();   // refresh the tooltip now, don't wait for a battery event
        });
    }

    /// <summary>Ends a lift and any timed charge. One waiting for its start has the limit in force
    /// already, so only the record goes.</summary>
    public static void Cancel(ActionCause cause)
    {
        if (IsActive) { ApplyRevert(cause, keepSchedule: false); return; }
        if (!HasSchedule) return;
        SettingsService.Update(ClearSchedule);
        AppLog.Info("TravelOverride: the timed charge is cancelled; the limit is already in force" + cause.Clause);
        StateChanged?.Invoke();
    }

    /// <summary>Puts the parked pair back once, through the latch every ending shares.
    /// <paramref name="keepSchedule"/> keeps a timed charge waiting for its start.</summary>
    internal static void EndLift(ActionCause cause, bool keepSchedule)
    {
        if (Interlocked.CompareExchange(ref _revertDispatched, 1, 0) == 0)
            ApplyRevert(cause, keepSchedule);
    }

    internal static void DropSchedule(ActionCause cause)
    {
        SettingsService.Update(ClearSchedule);
        AppLog.Info("TravelOverride: the timed charge is dropped" + cause.Clause);
        StateChanged?.Invoke();
    }

    private static void ClearSchedule(AppSettings s)
    {
        s.ChargeByTimeTarget      = null;
        s.ChargeByTimeTesting     = false;
        s.ChargeByTimeStartAt     = null;
        s.ChargeByTimeExpectedPct = null;
        s.ChargeByTimeRateUnknown = false;
    }

    /// <summary>Clears the override without touching the thresholds, for when an explicit new choice
    /// supersedes it. Restoring the saved pair (what <see cref="Cancel"/> does) would clobber the
    /// caller's own write; leaving it armed would let the auto-revert clobber it later.</summary>
    public static void Deactivate()
    {
        if (!IsActive && !HasSchedule) return;
        ClearOverrideState(keepSchedule: false);
    }

    /// <summary>The single primitive every "apply a Start/Stop" caller funnels through.
    /// <see cref="Deactivate"/> must run FIRST: an armed auto-revert would otherwise clobber the new
    /// thresholds at the next full charge.</summary>
    public static bool ApplyExplicitThresholds(int start, int stop, ActionCause cause) =>
        // Valid non-zero thresholds enable Smart Charge by themselves, so no SetEnabled first.
        Supersede(() => ChargeThresholdService.SetThresholds(start, stop, cause));

    /// <summary>The same for a firmware charge mode, on a vendor that offers modes.</summary>
    public static bool ApplyMode(string id) => Supersede(() => ChargeThresholdService.SetMode(id));

    /// <summary>A person switching Smart Charge off, in any phase of a lift or a timed charge: the
    /// record goes with nothing owed back, and charging continues. A refused write keeps it.</summary>
    public static bool SwitchOff(ActionCause cause)
    {
        bool held = IsActive || HasSchedule;
        bool ok   = Supersede(() => ChargeThresholdService.SetEnabled(false));
        if (held)
            AppLog.Info((ok
                ? "TravelOverride: Smart Charge is off, so the lift and any timed charge are dropped and no limit returns"
                : "TravelOverride: switching Smart Charge off was rejected by the device, so the lift and any timed charge are kept")
                + cause.Clause);
        return ok;
    }

    private static bool Supersede(Func<bool> write)
    {
        // Snapshot before Deactivate clears it. The saved pair is the only record of the user's real
        // thresholds, so a rejected write has to put it back.
        var s = SettingsService.Current;
        var (wasActive, revertStart, revertStop, chargeStarted) =
            (s.TravelOverrideActive, s.TravelOverrideRevertStart, s.TravelOverrideRevertStop,
             s.TravelOverrideChargeStarted);
        var timed = (s.ChargeByTimeTarget, s.ChargeByTimeTesting, s.ChargeByTimeStartAt,
                     s.ChargeByTimeExpectedPct, s.ChargeByTimeRateUnknown);

        Deactivate();

        if (write()) return true;

        if (wasActive || timed.ChargeByTimeTarget is not null)
            SettingsService.Update(x =>
            {
                x.TravelOverrideActive        = wasActive;
                x.TravelOverrideRevertStart   = revertStart;
                x.TravelOverrideRevertStop    = revertStop;
                x.TravelOverrideChargeStarted = chargeStarted;
                (x.ChargeByTimeTarget, x.ChargeByTimeTesting, x.ChargeByTimeStartAt,
                 x.ChargeByTimeExpectedPct, x.ChargeByTimeRateUnknown) = timed;
            });
        return false;
    }

    /// <summary>Shared by <see cref="Deactivate"/> (clear only) and <see cref="ApplyRevert"/> (restore, then clear).</summary>
    private static void ClearOverrideState(bool keepSchedule)
    {
        SettingsService.Update(s =>
        {
            s.TravelOverrideActive        = false;
            s.TravelOverrideRevertStart   = null;
            s.TravelOverrideRevertStop    = null;
            s.TravelOverrideChargeStarted = false;
            if (!keepSchedule) ClearSchedule(s);
        });

        StateChanged?.Invoke();   // tray tooltip + menu resync immediately
    }

    /// <summary>Feeds one battery reading to <see cref="TravelOverridePolicy"/> and carries out what
    /// it decides. Every ending — the charge completing and the charger being removed — is read
    /// there, not here.</summary>
    public static void OnBatteryReport(int pct, BatteryStatus status, int? remainingMwh, int? fullMwh)
    {
        if (!IsActive)
        {
            _lastStatus = status;
            Interlocked.Exchange(ref _revertDispatched, 0);   // re-arm for the next activation
            _taper.Reset();
            return;
        }

        var last = _lastStatus;
        _lastStatus = status;
        var reading = new ChargeReading(AwakeClock.Mark(), pct, status, remainingMwh, fullMwh);
        _taper.Observe(reading);

        switch (TravelOverridePolicy.Decide(active: true, ChargeStarted, pct, status, last))
        {
            case TravelOverrideStep.RecordChargeStarted:
                // Straight to disk: a crash between here and the charger coming out would otherwise
                // leave the lift waiting for a charge that already happened.
                SettingsService.Update(s => s.TravelOverrideChargeStarted = true);
                break;

            // CAS the latch so only the first qualifying report dispatches the revert.
            case TravelOverrideStep.Revert when Interlocked.CompareExchange(ref _revertDispatched, 1, 0) == 0:
                // The policy decided the charge it was asked for is over; that reading is the cause.
                // Learned before the record is cleared; a reading of 100 % counts even unplugged.
                bool full = pct >= 100, completed = full || BatteryStatsFormatter.IsOnAC(status);
                if (completed) Learn(reading);
                ApplyRevert(full      ? "the battery level reading 100 %"
                          : completed ? "the charge the cap was lifted for completing"
                                      : "the charger being removed after the charge had begun", keepSchedule: false);
                break;
        }
    }

    private static void Learn(ChargeReading full)
    {
        var (sample, why) = _taper.Finish(full);
        if (sample is null)
        {
            AppLog.Info($"ChargeByTime: this charge to full taught nothing, {why}");
            return;
        }
        var kept = ChargeTaperStore.Installed.Add(sample);
        AppLog.Info($"ChargeByTime: learned a taper factor of {sample.Factor:0.000} over {sample.TaperMinutes} min " +
                    $"from 80 % to full; estimates now use {ChargeTaperStore.Median(kept):0.000}, the median of {kept.Count}");
    }

    private static void ApplyRevert(ActionCause cause, bool keepSchedule)
    {
        // Read synchronously, before the async gap below. Only the write side needs Update().
        var s           = SettingsService.Current;
        var revertStart = s.TravelOverrideRevertStart;
        var revertStop  = s.TravelOverrideRevertStop;
        Task.Run(() =>
        {
            try
            {
                if (revertStart is { } start && revertStop is { } stop)
                {
                    // Attempt both writes, then judge — enabling is not a precondition for the
                    // threshold write.
                    bool ok = ChargeThresholdService.SetEnabled(true);
                    ok     &= ChargeThresholdService.SetThresholds(start, stop, cause);
                    if (!ok)
                    {
                        // Keep flag and values: they are the only record of the user's real
                        // thresholds, and the device is still at 0/0. Re-arm here too — the flag
                        // stays true, so OnBatteryReport's re-arm never runs and one rejection would
                        // otherwise ignore every later completion edge.
                        AppLog.Info($"TravelOverride: putting {start}/{stop} back was rejected by the device, " +
                                    "so the lift is left in force and the saved thresholds kept" + cause.Clause);
                        Interlocked.Exchange(ref _revertDispatched, 0);
                        return;
                    }
                }
                // Nothing was saved, so Smart Charge stays disabled.

                ClearOverrideState(keepSchedule);
            }
            catch (Exception ex)
            {
                // Same reasoning as the rejection path: the override is still active, so
                // OnBatteryReport's re-arm never runs and a throw would disarm auto-revert for good.
                AppLog.Error("TravelOverrideService.ApplyRevert", ex);
                Interlocked.Exchange(ref _revertDispatched, 0);
            }
        });
    }
}
